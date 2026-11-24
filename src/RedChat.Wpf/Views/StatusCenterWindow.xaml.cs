using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using RedChat.Core;
using RedChat.Presentation;
using RedChat.Infrastructure;
using WpfProgressBar = System.Windows.Controls.ProgressBar;

namespace RedChat.Wpf.Views;

/// <summary>Centre de statuts local : publication texte/photo/vidéo et expiration après 24 heures.</summary>
public partial class StatusCenterWindow : Window
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);
    private static readonly TimeSpan PhotoDisplayDuration = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan UnknownVideoDisplayDuration = TimeSpan.FromSeconds(30);
    private const int MaxMediaBytes = 20 * 1024 * 1024;
    private readonly string _storePath;
    private readonly MainViewModel _viewModel;
    private readonly string? _ownerPeerId;
    private readonly bool _isOwnStatusViewer;
    private readonly ObservableCollection<LocalStatusItem> _statuses = new();
    private readonly ObservableCollection<ReceivedStatusViewModel> _contactStatuses = new();
    private readonly DispatcherTimer _expiryTimer;
    private readonly DispatcherTimer _viewerTimer;
    private readonly HashSet<string> _viewedStatusIds = new(StringComparer.Ordinal);
    private ReceivedStatusViewModel[] _viewerStatuses = Array.Empty<ReceivedStatusViewModel>();
    private WpfProgressBar[] _progressBars = Array.Empty<WpfProgressBar>();
    private Stopwatch? _segmentClock;
    private TimeSpan _segmentDuration;
    private string? _currentVideoPath;
    private byte[]? _selectedMedia;
    private string? _selectedMediaName;
    private string? _selectedMediaType;

    /// <summary>Initialise le centre de statuts et le relie au modèle de vue principal.</summary>
    /// <param name="viewModel">Modèle de vue principal contenant les contacts et statuts reçus.</param>
    /// <param name="ownerPeerId">Identifiant du contact dont les statuts doivent être affichés, ou null pour le centre général.</param>
    public StatusCenterWindow(MainViewModel viewModel, string? ownerPeerId = null)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _ownerPeerId = ownerPeerId;
        _isOwnStatusViewer = ownerPeerId is not null && ownerPeerId == viewModel.LocalPeerId;
        InitializeComponent();
        _viewerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _viewerTimer.Tick += OnViewerTimerTick;
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RedChat", "Statuses");
        Directory.CreateDirectory(directory);
        _storePath = Path.Combine(directory, "local-statuses.json");
        StatusesList.ItemsSource = _statuses;
        ReceivedStatusesList.ItemsSource = _ownerPeerId is null ? _viewModel.ReceivedStatuses : _contactStatuses;
        AudienceList.ItemsSource = _viewModel.Contacts;
        if (_ownerPeerId is not null)
        {
            ContactViewModel? contact = _isOwnStatusViewer
                ? null
                : _viewModel.Contacts.FirstOrDefault(item => item.PeerId == _ownerPeerId);
            string contactName = _isOwnStatusViewer ? "vous" : contact?.DisplayLabel ?? "ce contact";
            Title = _isOwnStatusViewer ? "RedChat — Mes statuts" : $"RedChat — Statuts de {contactName}";
            Width = 440;
            Height = 720;
            MinWidth = 360;
            MinHeight = 600;
            ReceivedStatusesHeader.Text = _isOwnStatusViewer ? "Mes statuts" : $"Statuts de {contactName}";
            if (!_isOwnStatusViewer)
            {
                foreach (ReceivedStatusViewModel status in _viewModel.ReceivedStatuses.Where(item => item.OwnerPeerId == _ownerPeerId).OrderByDescending(item => item.CreatedAt))
                {
                    _contactStatuses.Add(status);
                }
            }
            PublishPanel.Visibility = Visibility.Collapsed;
            LocalStatusesPanel.Visibility = Visibility.Collapsed;
            FooterNotice.Visibility = Visibility.Collapsed;
            Grid.SetRow(ReceivedStatusesPanel, 0);
            Grid.SetRowSpan(ReceivedStatusesPanel, 2);
            if (!_isOwnStatusViewer)
            {
                _viewModel.ReceivedStatuses.CollectionChanged += OnReceivedStatusesChanged;
            }
        }
        UpdateReceivedEmptyState();
        if (_ownerPeerId is not null)
        {
            RefreshStatusViewer();
        }
        LoadStatuses();
        if (_ownerPeerId is not null)
        {
            RefreshStatusViewer();
        }
        _viewModel.LocalStatusViewed += OnLocalStatusViewed;
        _expiryTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _expiryTimer.Tick += OnExpiryTimerTick;
        _expiryTimer.Start();
        Closed += (_, _) =>
        {
            _expiryTimer.Stop();
            _viewerTimer.Stop();
            ClearCurrentVideo();
            _viewModel.LocalStatusViewed -= OnLocalStatusViewed;
            if (!_isOwnStatusViewer)
            {
                _viewModel.ReceivedStatuses.CollectionChanged -= OnReceivedStatusesChanged;
            }
        };
    }

    private void OnReceivedStatusesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_ownerPeerId is null)
        {
            return;
        }

        _contactStatuses.Clear();
        foreach (ReceivedStatusViewModel status in _viewModel.ReceivedStatuses
            .Where(item => item.OwnerPeerId == _ownerPeerId)
            .OrderByDescending(item => item.CreatedAt))
        {
            _contactStatuses.Add(status);
        }
        UpdateReceivedEmptyState();
        RefreshStatusViewer();
    }

    private void UpdateReceivedEmptyState()
    {
        int count = _ownerPeerId is null
            ? _viewModel.ReceivedStatuses.Count
            : _isOwnStatusViewer ? _statuses.Count : _contactStatuses.Count;
        if (_isOwnStatusViewer)
        {
            EmptyReceivedNotice.Text = "Tu n'as publié aucun statut pour le moment.";
        }
        EmptyReceivedNotice.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        StatusViewer.Visibility = _ownerPeerId is not null && count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ReceivedStatusesList.Visibility = _ownerPeerId is null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshStatusViewer()
    {
        if (_ownerPeerId is null)
        {
            return;
        }

        _viewerStatuses = _isOwnStatusViewer
            ? _statuses.Where(item => item.ExpiresAt > DateTimeOffset.UtcNow)
                .OrderBy(item => item.CreatedAt)
                .Select(CreateOwnStatusViewModel)
                .ToArray()
            : _contactStatuses.OrderBy(item => item.CreatedAt).ToArray();
        UpdateReceivedEmptyState();
        ProgressSegmentsPanel.Children.Clear();
        ProgressSegmentsPanel.ColumnDefinitions.Clear();
        _progressBars = new WpfProgressBar[_viewerStatuses.Length];
        for (int i = 0; i < _progressBars.Length; i++)
        {
            ProgressSegmentsPanel.ColumnDefinitions.Add(new ColumnDefinition());
            WpfProgressBar bar = new()
            {
                Minimum = 0,
                Maximum = 100,
                Height = 3,
                Margin = new Thickness(0, 0, i == _progressBars.Length - 1 ? 0 : 4, 0),
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(92, 92, 92)),
                Foreground = System.Windows.Media.Brushes.White,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            };
            Grid.SetColumn(bar, i);
            ProgressSegmentsPanel.Children.Add(bar);
            _progressBars[i] = bar;
        }

        if (_viewerStatuses.Length == 0)
        {
            _viewerTimer.Stop();
            ClearCurrentVideo();
            return;
        }

        ShowStatus(0);
    }

    private void ShowStatus(int index)
    {
        if (index < 0 || index >= _viewerStatuses.Length)
        {
            if (index >= _viewerStatuses.Length)
            {
                Close();
            }
            return;
        }

        _viewerTimer.Stop();
        ClearCurrentVideo();
        ReceivedStatusViewModel status = _viewerStatuses[index];
        StatusViewer.DataContext = status;
        ViewerSender.Text = status.SenderName;
        ViewerTime.Text = $"{status.CreatedAt.ToLocalTime():dd/MM à HH:mm} · {index + 1}/{_viewerStatuses.Length}";
        for (int i = 0; i < _progressBars.Length; i++)
        {
            _progressBars[i].Value = i < index ? 100 : 0;
        }

        if (!_isOwnStatusViewer && _viewedStatusIds.Add(status.Id)
            && !_viewModel.MarkReceivedStatusViewed(status.OwnerPeerId, status.Id))
        {
            MessageBox.Show("Le reçu de consultation n'a pas pu être envoyé : ce contact n'est pas connecté.",
                "RedChat — Statuts", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        if (status.Status.ContentKind == StatusContentKind.Video && status.MediaBytes is { Length: > 0 } bytes)
        {
            ViewerImage.Visibility = Visibility.Collapsed;
            ViewerVideo.Visibility = Visibility.Visible;
            string extension = bytes.Length >= 4 && bytes[0] == 0x1A && bytes[1] == 0x45 && bytes[2] == 0xDF && bytes[3] == 0xA3
                ? ".webm"
                : ".mp4";
            _currentVideoPath = Path.Combine(Path.GetTempPath(), $"redchat-status-{Guid.NewGuid():N}{extension}");
            try
            {
                File.WriteAllBytes(_currentVideoPath, bytes);
                ViewerVideo.Source = new Uri(_currentVideoPath, UriKind.Absolute);
                ViewerVideo.Position = TimeSpan.Zero;
                ViewerVideo.Play();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                ViewerVideo.Visibility = Visibility.Collapsed;
                ClearCurrentVideo();
                MessageBox.Show($"Impossible de lire cette vidéo : {ex.Message}", "RedChat — Statuts", MessageBoxButton.OK, MessageBoxImage.Warning);
                ShowStatus(index + 1);
            }
        }
        else
        {
            ViewerVideo.Visibility = Visibility.Collapsed;
            ViewerImage.Visibility = status.MediaBytes is null ? Visibility.Collapsed : Visibility.Visible;
            StartStatusTimer(PhotoDisplayDuration);
        }
    }

    private void StartStatusTimer(TimeSpan duration)
    {
        _segmentDuration = duration > TimeSpan.Zero ? duration : UnknownVideoDisplayDuration;
        _segmentClock = Stopwatch.StartNew();
        _viewerTimer.Start();
    }

    private void OnViewerTimerTick(object? sender, EventArgs e)
    {
        if (_segmentClock is null || _progressBars.Length == 0)
        {
            return;
        }

        double progress = Math.Clamp(_segmentClock.Elapsed.TotalMilliseconds / _segmentDuration.TotalMilliseconds, 0, 1);
        int index = Array.FindIndex(_viewerStatuses, status => ReferenceEquals(status, StatusViewer.DataContext));
        if (index >= 0)
        {
            _progressBars[index].Value = progress * 100;
        }
        if (progress >= 1)
        {
            ShowStatus(index + 1);
        }
    }

    private void ViewerVideo_MediaOpened(object sender, RoutedEventArgs e)
    {
        TimeSpan duration = ViewerVideo.NaturalDuration.HasTimeSpan
            ? ViewerVideo.NaturalDuration.TimeSpan
            : UnknownVideoDisplayDuration;
        StartStatusTimer(duration);
    }

    private void ViewerVideo_MediaEnded(object sender, RoutedEventArgs e)
    {
        int index = Array.FindIndex(_viewerStatuses, status => ReferenceEquals(status, StatusViewer.DataContext));
        ShowStatus(index + 1);
    }

    private void ViewerVideo_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        ViewerVideo.Visibility = Visibility.Collapsed;
        ClearCurrentVideo();
        MessageBox.Show($"Impossible de lire cette vidéo : {e.ErrorException.Message}", "RedChat — Statuts", MessageBoxButton.OK, MessageBoxImage.Warning);
        int index = Array.FindIndex(_viewerStatuses, status => ReferenceEquals(status, StatusViewer.DataContext));
        ShowStatus(index + 1);
    }

    private void ClearCurrentVideo()
    {
        ViewerVideo.Stop();
        ViewerVideo.Close();
        ViewerVideo.Source = null;
        if (_currentVideoPath is null)
        {
            return;
        }

        try
        {
            File.Delete(_currentVideoPath);
            _currentVideoPath = null;
        }
        catch (FileNotFoundException)
        {
            _currentVideoPath = null;
        }
        catch (IOException)
        {
            MessageBox.Show("Le fichier temporaire de lecture vidéo n'a pas pu être supprimé.", "RedChat — Statuts", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (UnauthorizedAccessException)
        {
            MessageBox.Show("Accès refusé lors de la suppression du fichier temporaire vidéo.", "RedChat — Statuts", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void PreviousStatus_Click(object sender, RoutedEventArgs e)
    {
        int index = Array.FindIndex(_viewerStatuses, status => ReferenceEquals(status, StatusViewer.DataContext));
        ShowStatus(Math.Max(0, index - 1));
    }

    private void NextStatus_Click(object sender, RoutedEventArgs e)
    {
        int index = Array.FindIndex(_viewerStatuses, status => ReferenceEquals(status, StatusViewer.DataContext));
        ShowStatus(index + 1);
    }

    private void CloseViewer_Click(object sender, RoutedEventArgs e) => Close();

    private ReceivedStatusViewModel CreateOwnStatusViewModel(LocalStatusItem item)
    {
        StatusContentKind contentKind = item.ContentType switch
        {
            "Photo PNG" or "Photo JPEG" => StatusContentKind.Photo,
            "Vidéo MP4" or "Vidéo WebM" => StatusContentKind.Video,
            _ => StatusContentKind.Text,
        };
        EphemeralStatus status = new()
        {
            Id = item.Id,
            OwnerPeerId = _viewModel.LocalPeerId,
            ContentKind = contentKind,
            Text = item.Text,
            MediaBytes = item.MediaBytes,
            CreatedAt = item.CreatedAt,
            ExpiresAt = item.ExpiresAt,
        };
        return new ReceivedStatusViewModel(status, _viewModel.SelfProfile.DisplayName);
    }

    private void OnExpiryTimerTick(object? sender, EventArgs e)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        bool localChanged = false;
        foreach (LocalStatusItem item in _statuses.Where(item => item.ExpiresAt <= now).ToArray())
        {
            _statuses.Remove(item);
            localChanged = true;
        }

        _viewModel.PruneExpiredReceivedStatuses();

        if (localChanged)
        {
            SaveStatuses();
            if (_isOwnStatusViewer)
            {
                RefreshStatusViewer();
            }
        }
    }

    private void ReceivedStatusesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReceivedStatusesList.SelectedItem is not ReceivedStatusViewModel selected)
        {
            return;
        }

        if (!_viewModel.MarkReceivedStatusViewed(selected.OwnerPeerId, selected.Id))
        {
            MessageBox.Show("Le reçu de consultation n'a pas pu être envoyé : ce contact n'est pas connecté.",
                "RedChat — Statuts", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        ReceivedStatusesList.SelectedItem = null;
    }

    private void OnLocalStatusViewed(object? sender, StatusViewedEventArgs e)
    {
        int index = -1;
        for (int i = 0; i < _statuses.Count; i++)
        {
            if (_statuses[i].Id == e.StatusId)
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            return;
        }

        LocalStatusItem item = _statuses[index];
        if (item.ExpiresAt <= DateTimeOffset.UtcNow
            || !_viewModel.Contacts.Any(contact => contact.PeerId == e.ViewerPeerId)
            || (item.AudienceMode == StatusAudienceMode.AllExcept && item.AudiencePeerIds.Contains(e.ViewerPeerId, StringComparer.Ordinal))
            || (item.AudienceMode == StatusAudienceMode.OnlySelected && !item.AudiencePeerIds.Contains(e.ViewerPeerId, StringComparer.Ordinal))
            || item.ViewedByPeerIds.Contains(e.ViewerPeerId, StringComparer.Ordinal))
        {
            return;
        }
        _statuses[index] = item with { ViewedByPeerIds = item.ViewedByPeerIds.Append(e.ViewerPeerId).Distinct(StringComparer.Ordinal).ToArray() };
        _ = SaveStatuses();
    }

    private void DeleteStatus_Click(object sender, RoutedEventArgs e)
    {
        if (StatusesList.SelectedItem is not LocalStatusItem selected)
        {
            MessageBox.Show("Sélectionne d’abord un de tes statuts.", "RedChat — Statuts", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        MessageBoxResult confirmation = MessageBox.Show(
            "Supprimer ce statut sur ton appareil et demander sa suppression chez tous les contacts qui étaient autorisés à le recevoir ? Les demandes destinées aux contacts hors ligne seront conservées jusqu'à l'expiration du statut.",
            "RedChat — Supprimer le statut", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        string[] recipients = selected.AuthorizedPeerIds ?? _viewModel.Contacts
            .Where(contact => selected.AudienceMode switch
            {
                StatusAudienceMode.AllContacts => true,
                StatusAudienceMode.AllExcept => !selected.AudiencePeerIds.Contains(contact.PeerId, StringComparer.Ordinal),
                StatusAudienceMode.OnlySelected => selected.AudiencePeerIds.Contains(contact.PeerId, StringComparer.Ordinal),
                _ => false,
            })
            .Select(contact => contact.PeerId).ToArray();

        try
        {
            _statuses.Remove(selected);
            if (!SaveStatuses())
            {
                _statuses.Insert(0, selected);
                return;
            }
            int sent = _viewModel.DeleteLocalStatus(selected.Id, recipients, selected.ExpiresAt);
            int offline = Math.Max(0, recipients.Length - sent);
            MessageBox.Show($"Statut supprimé localement. {sent} demande(s) mise(s) en file réseau ; {offline} demande(s) conservée(s) pour la reconnexion ou en attente de livraison.",
                "RedChat — Statuts", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or ProfileStoreException)
        {
            MessageBox.Show($"La suppression locale a été effectuée, mais la synchronisation n'a pas pu être entièrement enregistrée : {ex.Message}", "RedChat — Statuts", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ChooseMedia_Click(object sender, RoutedEventArgs e)
    {
        OpenFileDialog dialog = new()
        {
            Title = "Choisir une photo ou une vidéo",
            Filter = "Photos et vidéos|*.png;*.jpg;*.jpeg;*.mp4;*.webm|Images|*.png;*.jpg;*.jpeg|Vidéos|*.mp4;*.webm",
            CheckFileExists = true,
            Multiselect = false,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            FileInfo info = new(dialog.FileName);
            if (info.Length is <= 0 or > MaxMediaBytes)
            {
                MessageBox.Show("Le média doit peser entre 1 octet et 20 Mio.", "RedChat — Statuts", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string extension = info.Extension.ToLowerInvariant();
            byte[] bytes = File.ReadAllBytes(dialog.FileName);
            string? type = extension switch
            {
                ".png" when HasPngSignature(bytes) => "Photo PNG",
                ".jpg" or ".jpeg" when HasJpegSignature(bytes) => "Photo JPEG",
                ".mp4" when HasMp4Signature(bytes) => "Vidéo MP4",
                ".webm" when HasWebmSignature(bytes) => "Vidéo WebM",
                _ => null,
            };

            if (type is null)
            {
                MessageBox.Show("Le contenu du fichier ne correspond pas à un format photo/vidéo pris en charge.", "RedChat — Statuts", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _selectedMedia = bytes;
            _selectedMediaName = Path.GetFileName(dialog.FileName);
            _selectedMediaType = type;
            SelectedMediaLabel.Text = $"{type} : {_selectedMediaName} ({info.Length / 1024d:0} Ko)";
        }
        catch (IOException)
        {
            MessageBox.Show("Impossible de lire le média sélectionné.", "RedChat — Statuts", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (UnauthorizedAccessException)
        {
            MessageBox.Show("Accès refusé au média sélectionné.", "RedChat — Statuts", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ClearMedia_Click(object sender, RoutedEventArgs e)
    {
        _selectedMedia = null;
        _selectedMediaName = null;
        _selectedMediaType = null;
        SelectedMediaLabel.Text = "Aucun média sélectionné";
    }

    private void Publish_Click(object sender, RoutedEventArgs e)
    {
        string text = StatusTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(text) && _selectedMedia is null)
        {
            MessageBox.Show("Écris un texte ou choisis une photo/vidéo avant de publier.", "RedChat — Statuts", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        StatusAudienceMode mode = AudienceModeBox.SelectedIndex switch
        {
            1 => StatusAudienceMode.AllExcept,
            2 => StatusAudienceMode.OnlySelected,
            _ => StatusAudienceMode.AllContacts,
        };
        string[] selectedPeerIds = mode == StatusAudienceMode.AllContacts
            ? Array.Empty<string>()
            : AudienceList.SelectedItems.Cast<ContactViewModel>()
                .Select(contact => contact.PeerId).Distinct(StringComparer.Ordinal).ToArray();
        if (mode != StatusAudienceMode.AllContacts && selectedPeerIds.Length == 0)
        {
            MessageBox.Show("Sélectionne au moins un contact pour cette règle de confidentialité.", "RedChat — Statuts", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DateTimeOffset created = DateTimeOffset.UtcNow;
        string statusId = Guid.NewGuid().ToString("D");
        StatusAudience audience = new()
        {
            Mode = mode,
            PeerIds = new HashSet<string>(selectedPeerIds, StringComparer.Ordinal),
        };
        EphemeralStatus status;
        try
        {
            StatusContentKind contentKind = _selectedMedia is null
                ? StatusContentKind.Text
                : _selectedMediaType is "Photo PNG" or "Photo JPEG" ? StatusContentKind.Photo : StatusContentKind.Video;
            status = new EphemeralStatus
            {
                Id = statusId, OwnerPeerId = _viewModel.LocalPeerId, ContentKind = contentKind,
                Text = text, MediaBytes = _selectedMedia?.ToArray(), CreatedAt = created,
                ExpiresAt = created + Lifetime, Audience = audience,
            }.Validate(created, isLocalOwner: true);
        }
        catch (InvalidDataException ex)
        {
            MessageBox.Show($"Le statut n'est pas valide : {ex.Message}", "RedChat — Statuts", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string[] authorizedPeerIds = _viewModel.Contacts
            .Where(contact => audience.Allows(contact.PeerId))
            .Select(contact => contact.PeerId).Distinct(StringComparer.Ordinal).ToArray();
        LocalStatusItem localItem = new()
        {
            Id = status.Id, Text = status.Text, CreatedAt = status.CreatedAt, ExpiresAt = status.ExpiresAt,
            ContentType = _selectedMediaType ?? "Texte", MediaName = _selectedMediaName,
            MediaBase64 = _selectedMedia is null ? null : Convert.ToBase64String(_selectedMedia),
            AudienceMode = mode, AudiencePeerIds = selectedPeerIds, AuthorizedPeerIds = authorizedPeerIds,
        };
        _statuses.Insert(0, localItem);
        if (!SaveStatuses())
        {
            _statuses.Remove(localItem);
            return;
        }

        try
        {
            LanNode.StatusPublishResult result = _viewModel.PublishStatus(status, _selectedMediaName);
            string message = result.QueuedContacts > 0
                ? $"Statut enregistré. {result.QueuedContacts} contact(s) autorisé(s) ont été mis en file d'envoi. {result.FailedContacts} contact(s) n'ont pas été transmis (hors ligne ou file réseau indisponible)."
                : "Statut enregistré sur cet appareil, mais aucun contact autorisé n'est actuellement connecté.";
            MessageBox.Show(message, "RedChat — Statuts", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or InvalidOperationException)
        {
            MessageBox.Show($"Le statut est conservé localement, mais l'envoi réseau a échoué : {ex.Message}", "RedChat — Statuts", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        StatusTextBox.Clear();
        ClearMedia_Click(this, new RoutedEventArgs());
        AudienceList.UnselectAll();
        AudienceModeBox.SelectedIndex = 0;
    }

    private void LoadStatuses()
    {
        try
        {
            if (File.Exists(_storePath))
            {
                List<LocalStatusItem>? loaded = JsonSerializer.Deserialize<List<LocalStatusItem>>(File.ReadAllText(_storePath));
                foreach (LocalStatusItem item in (loaded ?? new()).Where(IsValidStoredItem).Where(item => item.ExpiresAt > DateTimeOffset.UtcNow).OrderByDescending(item => item.CreatedAt))
                {
                    _statuses.Add(item);
                }
            }
        }
        catch (JsonException)
        {
            MessageBox.Show("Le fichier des statuts locaux est illisible. Les publications ne seront pas chargées.", "RedChat — Statuts", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (IOException)
        {
            MessageBox.Show("Impossible de lire les statuts locaux.", "RedChat — Statuts", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        SaveStatuses();
    }

    private bool SaveStatuses()
    {
        try
        {
            LocalStatusItem[] active = _statuses.Where(IsValidStoredItem).Where(item => item.ExpiresAt > DateTimeOffset.UtcNow).ToArray();
            string temporary = _storePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(active));
            File.Move(temporary, _storePath, true);
            _statuses.Clear();
            foreach (LocalStatusItem item in active)
            {
                _statuses.Add(item);
            }

            return true;
        }
        catch (IOException)
        {
            MessageBox.Show("Impossible d'enregistrer le statut sur cet appareil.", "RedChat — Statuts", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            MessageBox.Show("Accès refusé lors de l'enregistrement du statut.", "RedChat — Statuts", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private static bool IsValidStoredItem(LocalStatusItem item)
    {
        if (!Guid.TryParse(item.Id, out _) || item.Text is null || item.Text.Length > 700
            || item.CreatedAt == default || item.ExpiresAt != item.CreatedAt + Lifetime
            || !Enum.IsDefined(item.AudienceMode) || item.AudiencePeerIds is null || item.AudiencePeerIds.Length > 256
            || item.AudiencePeerIds.Distinct(StringComparer.Ordinal).Count() != item.AudiencePeerIds.Length
            || item.AudiencePeerIds.Any(peerId => !InputValidator.IsValidPeerId(peerId))
            || (item.AuthorizedPeerIds is not null && (item.AuthorizedPeerIds.Length > 256
                || item.AuthorizedPeerIds.Distinct(StringComparer.Ordinal).Count() != item.AuthorizedPeerIds.Length
                || item.AuthorizedPeerIds.Any(peerId => !InputValidator.IsValidPeerId(peerId))))
            || item.ViewedByPeerIds is null || item.ViewedByPeerIds.Length > 256
            || item.ViewedByPeerIds.Distinct(StringComparer.Ordinal).Count() != item.ViewedByPeerIds.Length
            || item.ViewedByPeerIds.Any(peerId => !InputValidator.IsValidPeerId(peerId))
            || (item.AudienceMode == StatusAudienceMode.AllContacts && item.AudiencePeerIds.Length != 0)
            || (item.AudienceMode != StatusAudienceMode.AllContacts && item.AudiencePeerIds.Length == 0))
        {
            return false;
        }

        if (item.MediaBase64 is null)
        {
            // Un ancien enregistrement peut ne pas avoir ContentType dans le JSON.
            return (string.IsNullOrEmpty(item.ContentType) || item.ContentType == "Texte")
                && !string.IsNullOrWhiteSpace(item.Text);
        }

        try
        {
            byte[] bytes = Convert.FromBase64String(item.MediaBase64);
            if (bytes.Length is 0 or > MaxMediaBytes)
            {
                return false;
            }
            return item.ContentType switch
            {
                "Photo PNG" => HasPngSignature(bytes),
                "Photo JPEG" => HasJpegSignature(bytes),
                "Vidéo MP4" => HasMp4Signature(bytes),
                "Vidéo WebM" => HasWebmSignature(bytes),
                _ => false,
            };
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool HasPngSignature(ReadOnlySpan<byte> b) => b.Length >= 8 && b[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
    private static bool HasJpegSignature(ReadOnlySpan<byte> b) => b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF;
    private static bool HasMp4Signature(ReadOnlySpan<byte> b) => b.Length >= 12 && b[4] == (byte)'f' && b[5] == (byte)'t' && b[6] == (byte)'y' && b[7] == (byte)'p';
    private static bool HasWebmSignature(ReadOnlySpan<byte> b) => b.Length >= 4 && b[0] == 0x1A && b[1] == 0x45 && b[2] == 0xDF && b[3] == 0xA3;

    // Propriétés avec valeurs par défaut : les anciens JSON de la phase 4 ne contenaient
    // que Id, Text, CreatedAt et ExpiresAt. System.Text.Json conserve alors "Texte"
    // et null pour les champs média, ce qui permet une migration transparente.
    private sealed record LocalStatusItem
    {
        public string Id { get; init; } = string.Empty;
        public string Text { get; init; } = string.Empty;
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset ExpiresAt { get; init; }
        public string ContentType { get; init; } = "Texte";
        public string? MediaName { get; init; }
        public string? MediaBase64 { get; init; }
        public StatusAudienceMode AudienceMode { get; init; } = StatusAudienceMode.AllContacts;
        public string[] AudiencePeerIds { get; init; } = Array.Empty<string>();
        public string[]? AuthorizedPeerIds { get; init; }
        public string[] ViewedByPeerIds { get; init; } = Array.Empty<string>();

        public string ViewCountLabel => $"Consultations : {ViewedByPeerIds.Length}";
        public string ContentLabel => ContentType;
        public byte[]? MediaBytes
        {
            get
            {
                if (string.IsNullOrWhiteSpace(MediaBase64))
                {
                    return null;
                }
                try { return Convert.FromBase64String(MediaBase64); }
                catch (FormatException) { return null; }
            }
        }
        public string MediaLabel => MediaName is null ? string.Empty : $"Fichier : {MediaName}";
        public string ExpiresLabel => $"Publié le {CreatedAt.ToLocalTime():dd/MM à HH:mm} · expire le {ExpiresAt.ToLocalTime():dd/MM à HH:mm}";
    }
}
