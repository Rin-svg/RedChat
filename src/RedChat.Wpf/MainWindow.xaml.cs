using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;
using Microsoft.Win32;
using NAudio;
using NAudio.Wave;
using RedChat.Presentation;
using RedChat.Wpf.Views;
using RedChat.Wpf.Calls;
using Button = System.Windows.Controls.Button;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace RedChat.Wpf;

/// <summary>Fenêtre principale ; le code-behind ne gère que les interactions propres à WPF.</summary>
public partial class MainWindow : Window, IDisposable
{
    private readonly Func<Task> _shutdown;
    private readonly MainViewModel _viewModel;
    private readonly PersonalProfileViewModel _personalProfile;
    private readonly ICollectionView _conversationView;
    private readonly ICollectionView _contactView;
    private readonly RedChat.Core.FavoriteConversations _favorites;
    private readonly Forms.NotifyIcon? _trayIcon;
    private readonly Forms.ContextMenuStrip? _trayMenu;
    private bool _notificationsEnabled = true;
    private string? _pendingNotificationConversationId;
    private bool _friendsOnly;
    private bool _favoritesOnly;
    private bool _detailsVisible;
    private bool _closing;
    private bool _allowClose;
    private bool _trayResourcesDisposed;
    private WaveIn? _voiceRecorder;
    private WaveFileWriter? _voiceWriter;
    private TaskCompletionSource? _voiceStopped;
    private string? _voiceTemporaryPath;
    private bool _voiceStopRequested;
    private readonly MediaPlayer _voicePlayer = new();
    /// <summary>Délai laissé à la file d'écriture pour émettre « End » avant la coupure du réseau à la fermeture (best effort).</summary>
    private static readonly TimeSpan CallEndFlushDelay = TimeSpan.FromMilliseconds(300);

    private readonly object _callMediaGate = new();
    private Task _callMediaDisposeTask = Task.CompletedTask;
    private WebRtcMediaEngine? _callMediaEngine;
    private Window? _callVideoWindow;
    private System.Windows.Controls.Image? _callVideoImage;
    private WriteableBitmap? _remoteVideoBitmap;
    private Border? _callLocalPreviewFrame;
    private System.Windows.Controls.Image? _callLocalPreviewImage;
    private WriteableBitmap? _localVideoBitmap;

    // Une seule image locale en attente dans le dispatcher : la caméra produit ~30 images/s et l'interface
    // ne doit jamais accumuler de retard (les images en trop sont simplement abandonnées).
    private int _localFramePending;

    /// <summary>Largeur de la vignette de la caméra locale dans la fenêtre d'appel vidéo.</summary>
    private const double LocalPreviewWidth = 180;

    /// <summary>Crée la fenêtre autour du modèle de vue déjà assemblé.</summary>
    public MainWindow(MainViewModel viewModel, PersonalProfileViewModel personalProfile, Func<Task> shutdown)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _personalProfile = personalProfile ?? throw new ArgumentNullException(nameof(personalProfile));
        WelcomeLanding.DataContext = _viewModel;
        WelcomeLanding.EnterRequested += OnLandingEnterRequested;
        WelcomeLanding.ScanToggleRequested += OnLandingScanToggleRequested;
        WelcomeLanding.ConnectPeerRequested += OnLandingConnectPeerRequested;
        SelfStatusCard.DataContext = _personalProfile;
        _viewModel.UpdateSelfProfile(_personalProfile.CurrentProfile);
        _personalProfile.ProfileSaved += OnSelfProfileSaved;
        _favorites = _viewModel.Favorites;
        _favorites.Changed += OnFavoritesChanged;
        DataContext = _viewModel;
        _shutdown = shutdown ?? throw new ArgumentNullException(nameof(shutdown));
        _conversationView = CollectionViewSource.GetDefaultView(_viewModel.Conversations);
        _conversationView.Filter = FilterConversation;
        _contactView = CollectionViewSource.GetDefaultView(_viewModel.Contacts);
        _contactView.Filter = FilterContact;
        _viewModel.Contacts.CollectionChanged += OnContactsCollectionChanged;
        _viewModel.IncomingMessageReceived += OnIncomingMessageReceived;
        _viewModel.Calls.SessionChanged += OnCallSessionChanged;
        _viewModel.Calls.SignalReceived += OnCallSignalReceived;
        Forms.ToolStripMenuItem notificationsItem = new("Notifications activées")
        {
            CheckOnClick = true,
            Checked = true,
        };
        notificationsItem.CheckedChanged += (_, _) => _notificationsEnabled = notificationsItem.Checked;
        _trayMenu = new Forms.ContextMenuStrip();
        _trayMenu.Items.Add("Ouvrir RedChat", null, (_, _) => _ = Dispatcher.BeginInvoke(new Action(RestoreFromTray)));
        _trayMenu.Items.Add(notificationsItem);
        _trayMenu.Items.Add(new Forms.ToolStripSeparator());
        _trayMenu.Items.Add("Quitter", null, (_, _) => _ = Dispatcher.BeginInvoke(new Action(Close)));
        _trayIcon = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "RedChat — messagerie privée",
            ContextMenuStrip = _trayMenu,
            Visible = true,
        };
        _trayIcon.BalloonTipClicked += OnTrayBalloonTipClicked;
        _trayIcon.DoubleClick += OnTrayIconDoubleClick;
    }

    // audit-ok: gestionnaire de l’événement Closing WPF ; l’exception de fermeture est capturée localement.
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        if (_closing)
        {
            return;
        }

        _closing = true;
        try
        {
            if (_viewModel is null)
            {
                return;
            }

            // Appel en cours : prévenir le pair AVANT de couper le réseau (EndCall est sans effet s'il n'y a pas d'appel).
            // Le paquet « End » passe par une file d'écriture : on laisse un court délai pour qu'il parte.
            // Si malgré tout il se perd, le pair termine l'appel de lui-même à la perte de connexion (CallSignalingService).
            bool callWasLive = _viewModel.Calls.ActiveSession is
            {
                State: RedChat.Infrastructure.CallSessionState.OutgoingRinging
                    or RedChat.Infrastructure.CallSessionState.IncomingRinging
                    or RedChat.Infrastructure.CallSessionState.Accepted,
            };
            _viewModel.Calls.EndCall();
            await DisposeCallMediaEngineAsync();
            if (callWasLive)
            {
                await Task.Delay(CallEndFlushDelay);
            }

            await StopAndDiscardVoiceRecordingAsync();
            _voicePlayer.Stop();
            _voicePlayer.Close();
            _viewModel.Dispose();
            await _shutdown();
        }
        catch (Exception ex)
        {
            AppLogger.Write(ex);
            MessageBox.Show("La fermeture a rencontré un problème. RedChat va quand même se fermer ; vérifiez le journal local.",
                "RedChat — fermeture", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            if (_viewModel is not null)
            {
                _viewModel.Contacts.CollectionChanged -= OnContactsCollectionChanged;
                _viewModel.IncomingMessageReceived -= OnIncomingMessageReceived;
                _viewModel.Calls.SessionChanged -= OnCallSessionChanged;
                _viewModel.Calls.SignalReceived -= OnCallSignalReceived;
            }
            await DisposeCallMediaEngineAsync();
            if (_favorites is not null)
            {
                _favorites.Changed -= OnFavoritesChanged;
            }
            if (_personalProfile is not null)
            {
                _personalProfile.ProfileSaved -= OnSelfProfileSaved;
            }
            if (WelcomeLanding is not null)
            {
                WelcomeLanding.EnterRequested -= OnLandingEnterRequested;
                WelcomeLanding.ScanToggleRequested -= OnLandingScanToggleRequested;
                WelcomeLanding.ConnectPeerRequested -= OnLandingConnectPeerRequested;
            }
            Dispose();
            _allowClose = true;
            Close();
        }
    }

    /// <summary>Libère les ressources Windows Forms utilisées par l'icône de zone de notification.</summary>
    public void Dispose()
    {
        if (_trayResourcesDisposed)
        {
            return;
        }

        _trayResourcesDisposed = true;
        if (_trayIcon is not null)
        {
            _trayIcon.BalloonTipClicked -= OnTrayBalloonTipClicked;
            _trayIcon.DoubleClick -= OnTrayIconDoubleClick;
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }
        _trayMenu?.Dispose();
        GC.SuppressFinalize(this);
    }

    private void OnIncomingMessageReceived(object? sender, IncomingMessageNotificationEventArgs e)
    {
        if (!_notificationsEnabled || _trayIcon is null)
        {
            return;
        }

        _pendingNotificationConversationId = e.ConversationId;
        string title = e.ConversationName.Length > 63 ? e.ConversationName[..63] : e.ConversationName;
        _trayIcon.BalloonTipTitle = title;
        _trayIcon.BalloonTipText = "Nouveau message reçu.";
        _trayIcon.ShowBalloonTip(5000);
    }

    private void OnTrayBalloonTipClicked(object? sender, EventArgs e) =>
        _ = Dispatcher.BeginInvoke(new Action(OpenPendingNotificationConversation));

    private void OnTrayIconDoubleClick(object? sender, EventArgs e) =>
        _ = Dispatcher.BeginInvoke(new Action(RestoreFromTray));

    private void RestoreFromTray()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Show();
        Activate();
    }

    private void OpenPendingNotificationConversation()
    {
        string? conversationId = _pendingNotificationConversationId;
        if (conversationId is null)
        {
            RestoreFromTray();
            return;
        }

        _pendingNotificationConversationId = null;
        RestoreFromTray();
        SearchBox.Text = string.Empty;
        ShowChats_Click(this, new RoutedEventArgs());
        _viewModel.RefreshConversations();
        ConversationViewModel? conversation = _viewModel.Conversations.FirstOrDefault(item => item.Info.Id == conversationId);
        if (conversation is null)
        {
            return;
        }

        _viewModel.SelectedConversation = conversation;
        ConversationList.SelectedItem = conversation;
        ConversationList.ScrollIntoView(conversation);
    }

    private void ShowStatuses_Click(object sender, RoutedEventArgs e)
    {
        WelcomeLanding.Visibility = Visibility.Collapsed;
        StatusCenterWindow window = new(_viewModel) { Owner = this };
        window.ShowDialog();
    }

    private void OnSelfProfileSaved(object? sender, EventArgs e) =>
        _viewModel.UpdateSelfProfile(_personalProfile.CurrentProfile);

    private void ContactAvatar_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: { } item })
        {
            return;
        }

        string? peerId = item switch
        {
            ConversationViewModel conversation when !conversation.Info.IsGroup => conversation.Info.Id,
            ContactViewModel contact => contact.PeerId,
            PersonalProfileViewModel profile => profile.PeerId,
            _ => null,
        };

        if (string.IsNullOrWhiteSpace(peerId))
        {
            return;
        }

        e.Handled = true;
        bool isOwnStatus = peerId == _viewModel.LocalPeerId;
        if (!isOwnStatus && !_viewModel.ReceivedStatuses.Any(status =>
            status.OwnerPeerId == peerId && status.ExpiresAt > DateTimeOffset.UtcNow))
        {
            return;
        }

        StatusCenterWindow window = new(_viewModel, peerId) { Owner = this };
        window.ShowDialog();
    }

    private void OpenSelfConversation_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _viewModel.OpenSelfConversation();
        ShowChats_Click(this, new RoutedEventArgs());
        ConversationList.SelectedItem = _viewModel.SelectedConversation;
        ConversationList.ScrollIntoView(_viewModel.SelectedConversation);
        e.Handled = true;
    }

    private void StickerPicker_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedConversation is null)
        {
            MessageBox.Show("Ouvre d'abord une conversation pour envoyer un sticker.", "RedChat — stickers", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        StickerPickerPopup.IsOpen = true;
    }

    private void Sticker_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string sticker } || _viewModel.SelectedConversation is not { } conversation)
        {
            return;
        }

        conversation.Draft = sticker;
        conversation.SendCommand.Execute(null);
        StickerPickerPopup.IsOpen = false;
    }

    private void RemoveContact_Click(object sender, RoutedEventArgs e)
    {
        ContactViewModel? contact = ResolveSelectedAddressBookContact();
        if (contact is null)
        {
            MessageBox.Show("Sélectionne d'abord un contact.", "RedChat — contacts", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        MessageBoxResult answer = MessageBox.Show(
            $"Retirer {contact.DisplayLabel} de ton carnet ? L'historique de la conversation sera conservé.",
            "RedChat — supprimer le contact", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer == MessageBoxResult.Yes)
        {
            _viewModel.RemoveContact(contact.PeerId);
            ContactDetailsPanel.Visibility = Visibility.Collapsed;
            _contactView.Refresh();
        }
    }

    private void RemoveConversation_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedConversation is not { } conversation)
        {
            MessageBox.Show("Sélectionne d'abord une discussion ou un groupe.", "RedChat — suppression", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string kind = conversation.Info.IsGroup ? "ce groupe" : "cette discussion";
        MessageBoxResult answer = MessageBox.Show(
            $"Supprimer {kind} de cet appareil ? Les autres membres ne seront pas affectés. Les messages en attente de cette conversation seront supprimés de ta file locale.",
            "RedChat — supprimer", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer == MessageBoxResult.Yes)
        {
            _viewModel.RemoveConversation(conversation.Info.Id);
            ContactDetailsPanel.Visibility = Visibility.Collapsed;
            DetailsColumn.Width = new GridLength(0);
            _detailsVisible = false;
            _conversationView.Refresh();
        }
    }

    private void ForwardMessage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: RedChat.Core.ChatMessage message })
        {
            return;
        }

        ForwardMessageViewModel forward = _viewModel.CreateForwardViewModel(message);
        if (forward.Targets.Count == 0)
        {
            MessageBox.Show("Aucune autre conversation n'est disponible. Ouvre ou crée d'abord une discussion.", "RedChat — transférer", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ForwardMessageWindow dialog = new(forward) { Owner = this };
        if (dialog.ShowDialog() == true && forward.ForwardedMessage is not null)
        {
            _conversationView.Refresh();
        }
    }

    private void CreateGroup_Click(object sender, RoutedEventArgs e)
    {
        CreateGroupViewModel vm = _viewModel.CreateGroupViewModel();
        CreateGroupWindow dialog = new(vm) { Owner = this };
        if (dialog.ShowDialog() == true && vm.CreatedGroup is { } group)
        {
            _viewModel.RefreshConversations();
            _viewModel.SelectedConversation = _viewModel.Conversations.FirstOrDefault(c => c.Info.Id == group.Id);
            _conversationView.Refresh();
        }
    }

    private void UseDiscoveredPeer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: DiscoveredPeerViewModel peer })
        {
            _viewModel.ConnectEndpoint = peer.Endpoint;
        }
    }

    private async void Attachment_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedConversation is not { } conversation)
        {
            return;
        }

        OpenFileDialog dialog = new()
        {
            Title = conversation.IsViewOnceMode ? "Envoyer une photo ou vidéo à vue unique" : "Joindre un fichier à la conversation",
            Filter = "Photos et vidéos|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.mp4;*.m4v;*.mov;*.avi;*.wmv|Tous les fichiers (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            bool viewOnce = conversation.IsViewOnceMode;
            if (viewOnce && !IsImageExtension(Path.GetExtension(dialog.FileName)) && !IsVideoExtension(Path.GetExtension(dialog.FileName)))
            {
                throw new InvalidDataException("La vue unique ne prend en charge que les photos et vidéos.");
            }

            await _viewModel.SendAttachmentAsync(conversation.Info.Id, dialog.FileName, viewOnce);
            conversation.IsViewOnceMode = false;
            MessageBox.Show(viewOnce
                    ? "La photo ou vidéo a été envoyée en vue unique."
                    : "Le transfert du fichier a été placé dans la file d'envoi.",
                "RedChat — pièce jointe",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException or InvalidDataException)
        {
            MessageBox.Show(ex.Message, "RedChat — envoi impossible", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }


    private async void VoiceRecord_Click(object sender, RoutedEventArgs e)
    {
        if (_voiceRecorder is null)
        {
            if (_viewModel.SelectedConversation is null)
            {
                MessageBox.Show("Ouvre d'abord une conversation.", "RedChat — message vocal", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                StartVoiceRecording();
                if (sender is Button button)
                {
                    button.Content = "■";
                    button.ToolTip = "Terminer et envoyer le message vocal";
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or MmException)
            {
                await StopAndDiscardVoiceRecordingAsync();
                MessageBox.Show($"Impossible de démarrer l'enregistrement : {ex.Message}", "RedChat — message vocal", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            return;
        }

        if (sender is Button stopButton)
        {
            stopButton.IsEnabled = false;
        }

        try
        {
            string? recordedPath = await FinishVoiceRecordingAsync();
            if (recordedPath is null)
            {
                MessageBox.Show("Aucun son n'a été enregistré.", "RedChat — message vocal", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string conversationId = _viewModel.SelectedConversation?.Info.Id
                ?? throw new InvalidOperationException("La conversation n'est plus sélectionnée.");
            await _viewModel.SendAttachmentAsync(conversationId, recordedPath);
            _viewModel.ArchiveOutgoingVoice(recordedPath);
            MessageBox.Show("Message vocal envoyé et conservé dans l'historique.", "RedChat — message vocal", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException)
        {
            MessageBox.Show(ex.Message, "RedChat — envoi du message vocal", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            if (sender is Button button)
            {
                button.IsEnabled = true;
                button.Content = "🎙";
                button.ToolTip = "Enregistrer / terminer un message vocal";
            }

            DeleteVoiceTemporaryFile();
        }
    }

    private void StartVoiceRecording()
    {
        string path = Path.Combine(Path.GetTempPath(), $"redchat-voice-{Guid.NewGuid():N}.wav");
        WaveFormat format = new(16000, 16, 1);
        WaveFileWriter writer = new(path, format);
        WaveIn recorder = new() { WaveFormat = format, BufferMilliseconds = 100 };
        _voiceTemporaryPath = path;
        _voiceWriter = writer;
        _voiceRecorder = recorder;
        _voiceStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _voiceStopRequested = false;
        recorder.DataAvailable += OnVoiceDataAvailable;
        recorder.RecordingStopped += OnVoiceRecordingStopped;
        try
        {
            recorder.StartRecording();
        }
        catch
        {
            recorder.DataAvailable -= OnVoiceDataAvailable;
            recorder.RecordingStopped -= OnVoiceRecordingStopped;
            recorder.Dispose();
            writer.Dispose();
            _voiceRecorder = null;
            _voiceWriter = null;
            _voiceStopped = null;
            DeleteVoiceTemporaryFile();
            throw;
        }
    }

    private void OnVoiceDataAvailable(object? sender, WaveInEventArgs e)
    {
        WaveFileWriter? writer = _voiceWriter;
        if (writer is null || e.BytesRecorded <= 0)
        {
            return;
        }

        // 16 kHz, mono, 16 bits : cinq minutes représentent environ 9,2 Mio.
        const long maxRecordingBytes = 16_000L * 2 * 300;
        if (writer.Position + e.BytesRecorded > maxRecordingBytes)
        {
            if (!_voiceStopRequested && sender is WaveIn recorder)
            {
                _voiceStopRequested = true;
                recorder.StopRecording();
            }

            return;
        }

        writer.Write(e.Buffer, 0, e.BytesRecorded);
    }

    private void OnVoiceRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            _voiceStopped?.TrySetException(e.Exception);
        }
        else
        {
            _voiceStopped?.TrySetResult();
        }
    }

    private async Task<string?> FinishVoiceRecordingAsync()
    {
        WaveIn? recorder = _voiceRecorder;
        if (recorder is null)
        {
            return null;
        }

        TaskCompletionSource stopped = _voiceStopped ?? throw new InvalidOperationException("État d'enregistrement invalide.");
        try
        {
            if (!_voiceStopRequested)
            {
                _voiceStopRequested = true;
                recorder.StopRecording();
            }

            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(8));
        }
        finally
        {
            recorder.DataAvailable -= OnVoiceDataAvailable;
            recorder.RecordingStopped -= OnVoiceRecordingStopped;
            recorder.Dispose();
            _voiceRecorder = null;
            _voiceStopped = null;
            _voiceWriter?.Dispose();
            _voiceWriter = null;
        }

        string? path = _voiceTemporaryPath;
        return path is not null && File.Exists(path) && new FileInfo(path).Length > 44 ? path : null;
    }

    private async Task StopAndDiscardVoiceRecordingAsync()
    {
        if (_voiceRecorder is not null)
        {
            try
            {
                await FinishVoiceRecordingAsync();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or MmException)
            {
                AppLogger.Write(ex);
            }
        }

        _voiceWriter?.Dispose();
        _voiceWriter = null;
        DeleteVoiceTemporaryFile();
    }

    private void DeleteVoiceTemporaryFile()
    {
        string? path = _voiceTemporaryPath;
        _voiceTemporaryPath = null;
        if (path is null)
        {
            return;
        }

        try { File.Delete(path); }
        catch (IOException ex) { AppLogger.Write(ex); }
        catch (UnauthorizedAccessException ex) { AppLogger.Write(ex); }
    }

    private void PlayVoice_Click(object sender, RoutedEventArgs e)
    {
        string? path = (sender as FrameworkElement)?.DataContext is VoiceMessageViewModel voice
            ? voice.FilePath
            : _viewModel.VoiceMessages.FirstOrDefault()?.FilePath;
        if (path is null || !File.Exists(path))
        {
            MessageBox.Show("Ce message vocal n'est plus disponible sur cet appareil.", "RedChat — lecture", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            _voicePlayer.Stop();
            _voicePlayer.Open(new Uri(path, UriKind.Absolute));
            _voicePlayer.Play();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UriFormatException)
        {
            MessageBox.Show($"Impossible de lire ce message vocal : {ex.Message}", "RedChat — lecture", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ConversationSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        _conversationView.Refresh();
        _contactView.Refresh();
        if (_friendsOnly)
        {
            ContactsEmptyState.Visibility = _contactView.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            ConversationEmptyState.Visibility = _conversationView.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private bool FilterContact(object item)
    {
        if (item is not ContactViewModel contact)
        {
            return false;
        }

        string query = SearchBox.Text.Trim();
        return query.Length == 0
            || contact.DisplayLabel.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || contact.PeerId.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void OnContactsCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_friendsOnly)
        {
            ContactsEmptyState.Visibility = _contactView.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void ConversationList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        bool isFavorite = _viewModel.SelectedConversation is { } conversation
            && _favorites.Contains(conversation.Info.Id);
        FavoriteConversationButton.Content = isFavorite ? "★" : "☆";
        FavoriteConversationButton.ToolTip = isFavorite ? "Retirer des favoris" : "Ajouter aux favoris";
        ApplySelectedConversationWallpaper();
    }

    private void ViewOnceMessage_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not RedChat.Core.ChatMessage message
            || !message.CanOpenViewOnce)
        {
            return;
        }

        MessageBox.Show(this, message.Text, "RedChat — message à vue unique",
            MessageBoxButton.OK, MessageBoxImage.Information);
        try
        {
            _viewModel.ConsumeViewOnceMessage(message.ConversationId, message.Id);
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(this, ex.Message, "RedChat — vue unique", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenReceivedAttachment_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not RedChat.Infrastructure.AttachmentReceivedEventArgs attachment)
        {
            return;
        }

        if (!File.Exists(attachment.FilePath))
        {
            MessageBox.Show(this, "Ce fichier n'est plus disponible sur cet appareil.",
                "RedChat — pièce jointe", MessageBoxButton.OK, MessageBoxImage.Information);
            _ = _viewModel.ReceivedAttachments.Remove(attachment);
            return;
        }

        string extension = Path.GetExtension(attachment.FileName);
        bool isVideo = IsVideoExtension(extension);
        bool isImage = IsImageExtension(extension);
        if (!isVideo && !isImage)
        {
            MessageBox.Show(this, $"Le fichier a été enregistré ici :\n{attachment.FilePath}",
                "RedChat — pièce jointe", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        bool displayed = false;
        try
        {
            Window viewer;
            if (isVideo)
            {
                MediaElement media = new()
                {
                    Source = new Uri(attachment.FilePath, UriKind.Absolute),
                    LoadedBehavior = MediaState.Manual,
                    UnloadedBehavior = MediaState.Stop,
                    Stretch = Stretch.Uniform,
                };
                media.MediaOpened += (_, _) => media.Play();
                viewer = new Window
                {
                    Title = attachment.IsViewOnce ? "RedChat — vidéo à vue unique" : attachment.FileName,
                    Width = 900,
                    Height = 650,
                    MinWidth = 480,
                    MinHeight = 320,
                    Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(8, 13, 22)),
                    Content = media,
                    Owner = this,
                };
                viewer.Closed += (_, _) =>
                {
                    media.Stop();
                    media.Close();
                };
            }
            else
            {
                BitmapImage bitmap = new();
                using (FileStream stream = File.OpenRead(attachment.FilePath))
                {
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.DecodePixelWidth = 1600;
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                    bitmap.Freeze();
                }

                viewer = new Window
                {
                    Title = attachment.IsViewOnce ? "RedChat — photo à vue unique" : attachment.FileName,
                    Width = 900,
                    Height = 650,
                    MinWidth = 360,
                    MinHeight = 270,
                    Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(8, 13, 22)),
                    Content = new System.Windows.Controls.Image { Source = bitmap, Stretch = Stretch.Uniform },
                    Owner = this,
                };
            }

            displayed = true;
            _ = viewer.ShowDialog();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or UriFormatException or InvalidOperationException)
        {
            MessageBox.Show(this, ex.Message, "RedChat — affichage impossible",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            if (displayed && attachment.IsViewOnce)
            {
                try
                {
                    _ = _viewModel.ConsumeViewOnceAttachment(attachment);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    MessageBox.Show(this, ex.Message, "RedChat — vue unique",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }
    }

    private static bool IsImageExtension(string extension) =>
        extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".gif", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase);

    private static bool IsVideoExtension(string extension) =>
        extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".m4v", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".mov", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".avi", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".wmv", StringComparison.OrdinalIgnoreCase);

    private void ApplySelectedConversationWallpaper()
    {
        if (_viewModel.SelectedConversation is not { } conversation)
        {
            return;
        }

        conversation.SetWallpaperPath(FindConversationWallpaperPath(conversation.Info.Id));
    }

    private static string GetConversationWallpaperKey(string conversationId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(conversationId)));

    private static string GetConversationWallpaperDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RedChat", "ConversationWallpapers");

    private static string GetConversationWallpaperRemovalMarker(string conversationId) =>
        Path.Combine(GetConversationWallpaperDirectory(), GetConversationWallpaperKey(conversationId) + ".removed");

    private static string? FindConversationWallpaperPath(string conversationId)
    {
        string directory = GetConversationWallpaperDirectory();
        if (!Directory.Exists(directory) || File.Exists(GetConversationWallpaperRemovalMarker(conversationId)))
        {
            return null;
        }

        string key = GetConversationWallpaperKey(conversationId);
        string legacyPath = Path.Combine(directory, key + ".png");
        return Directory.EnumerateFiles(directory, key + "-*.png")
            .Append(legacyPath)
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private void SetConversationWallpaper_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedConversation is not { } conversation)
        {
            return;
        }

        OpenFileDialog dialog = new()
        {
            Title = "Choisir le fond d’écran de cette conversation",
            Filter = "Images PNG ou JPEG|*.png;*.jpg;*.jpeg",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            FileInfo source = new(dialog.FileName);
            if (source.Length is <= 0 or > 20 * 1024 * 1024)
            {
                throw new InvalidDataException("L’image doit peser moins de 20 Mo.");
            }

            BitmapImage bitmap = new();
            using (FileStream stream = File.OpenRead(dialog.FileName))
            {
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
                bitmap.StreamSource = stream;
                bitmap.DecodePixelWidth = 1920;
                bitmap.EndInit();
                bitmap.Freeze();
            }

            if (bitmap.PixelWidth is < 32 or > 8192
                || bitmap.PixelHeight is < 32 or > 8192
                || (long)bitmap.PixelWidth * bitmap.PixelHeight > 30_000_000)
            {
                throw new InvalidDataException("Les dimensions de l’image sont trop grandes ou trop petites.");
            }

            PngBitmapEncoder encoder = new();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            string directory = GetConversationWallpaperDirectory();
            Directory.CreateDirectory(directory);
            string fileId = Guid.NewGuid().ToString("N");
            string destination = Path.Combine(directory, $"{GetConversationWallpaperKey(conversation.Info.Id)}-{fileId}.png");
            string temporary = destination + ".tmp";
            using (FileStream output = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                encoder.Save(output);
            }
            File.Move(temporary, destination);
            string removalMarker = GetConversationWallpaperRemovalMarker(conversation.Info.Id);
            if (File.Exists(removalMarker))
            {
                File.Delete(removalMarker);
            }
            conversation.SetWallpaperPath(destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or FormatException or ArgumentException)
        {
            MessageBox.Show(ex.Message, "RedChat — fond d’écran", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RemoveConversationWallpaper_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedConversation is not { HasWallpaper: true } conversation)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(GetConversationWallpaperDirectory());
            File.WriteAllText(GetConversationWallpaperRemovalMarker(conversation.Info.Id), string.Empty);
            conversation.SetWallpaperPath(null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(ex.Message, "RedChat — fond d’écran", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ContactList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        bool hasSelection = ContactsList.SelectedItem is ContactViewModel;
        OpenContactButton.IsEnabled = hasSelection;
        if (hasSelection)
        {
            _detailsVisible = true;
            ContactDetailsPanel.Visibility = Visibility.Visible;
            DetailsColumn.Width = new GridLength(258);
        }
    }

    private void OpenSelectedContact_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedAddressBookContact is not { } contact)
        {
            return;
        }

        try
        {
            _viewModel.OpenAddressBookContact(contact.PeerId);
            ShowChats_Click(sender, e);
            ConversationList.SelectedItem = _viewModel.SelectedConversation;
            _conversationView.Refresh();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            MessageBox.Show(ex.Message, "RedChat — contact", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void ShowChats_Click(object sender, RoutedEventArgs e)
    {
        WelcomeLanding.Visibility = Visibility.Collapsed;
        _friendsOnly = false;
        _favoritesOnly = false;
        ChatsTab.Background = (System.Windows.Media.Brush)FindResource("Accent");
        ChatsTab.Foreground = System.Windows.Media.Brushes.White;
        FriendsTab.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(28, 43, 64));
        FriendsTab.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(175, 192, 216));
        ListHeading.Text = "DISCUSSIONS RÉCENTES";
        ConversationList.Visibility = Visibility.Visible;
        ContactsList.Visibility = Visibility.Collapsed;
        SelfStatusCard.Visibility = Visibility.Collapsed;
        OpenContactButton.Visibility = Visibility.Collapsed;
        CreateGroupButton.Visibility = Visibility.Visible;
        ConversationEmptyState.Visibility = _viewModel.Conversations.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ContactsEmptyState.Visibility = Visibility.Collapsed;
        _conversationView.Refresh();
    }

    private void ShowFriends_Click(object sender, RoutedEventArgs e)
    {
        WelcomeLanding.Visibility = Visibility.Collapsed;
        _friendsOnly = true;
        _favoritesOnly = false;
        FriendsTab.Background = (System.Windows.Media.Brush)FindResource("Accent");
        FriendsTab.Foreground = System.Windows.Media.Brushes.White;
        ChatsTab.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(28, 43, 64));
        ChatsTab.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(175, 192, 216));
        ListHeading.Text = "CARNET D'AMIS";
        ConversationList.Visibility = Visibility.Collapsed;
        ContactsList.Visibility = Visibility.Visible;
        SelfStatusCard.Visibility = Visibility.Visible;
        OpenContactButton.Visibility = Visibility.Visible;
        OpenContactButton.IsEnabled = _viewModel.SelectedAddressBookContact is not null;
        CreateGroupButton.Visibility = Visibility.Collapsed;
        ConversationEmptyState.Visibility = Visibility.Collapsed;
        _contactView.Refresh();
        ContactsEmptyState.Visibility = _contactView.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool FilterConversation(object item)
    {
        if (item is not ConversationViewModel conversation)
        {
            return false;
        }

        if (_friendsOnly && conversation.Info.IsGroup)
        {
            return false;
        }

        string query = SearchBox.Text.Trim();
        if (_favoritesOnly && !_favorites.Contains(conversation.Info.Id))
        {
            return false;
        }

        return query.Length == 0
            || conversation.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || conversation.LastMessagePreview.Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }


    private void ChangeAvatar_Click(object sender, RoutedEventArgs e)
    {
        ContactViewModel? contact = ResolveSelectedAddressBookContact();
        if (contact is null)
        {
            MessageBox.Show("Sélectionne d'abord un contact dans le carnet ou ouvre sa conversation privée.",
                "RedChat — avatar", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        OpenFileDialog dialog = new()
        {
            Title = "Choisir une photo de contact",
            Filter = "Images PNG ou JPEG|*.png;*.jpg;*.jpeg",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            FileInfo source = new(dialog.FileName);
            if (source.Length is <= 0 or > 5 * 1024 * 1024)
            {
                throw new InvalidDataException("L'image doit peser moins de 5 Mo.");
            }

            BitmapImage bitmap = new();
            using (FileStream stream = File.OpenRead(dialog.FileName))
            {
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
                bitmap.StreamSource = stream;
                bitmap.DecodePixelWidth = 512;
                bitmap.EndInit();
                bitmap.Freeze();
            }

            if (bitmap.PixelWidth is < 32 or > 4096 || bitmap.PixelHeight is < 32 or > 4096)
            {
                throw new InvalidDataException("Les dimensions de l'image doivent être comprises entre 32 et 4096 pixels.");
            }

            PngBitmapEncoder encoder = new();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RedChat", "Avatars");
            Directory.CreateDirectory(directory);
            string destination = Path.Combine(directory, contact.PeerId + ".png");
            string temporary = destination + ".tmp";
            using (FileStream output = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                encoder.Save(output);
            }
            File.Move(temporary, destination, true);
            contact.NotifyAvatarChanged();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or FormatException or ArgumentException)
        {
            MessageBox.Show(ex.Message, "RedChat — avatar", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void RemoveAvatar_Click(object sender, RoutedEventArgs e)
    {
        ContactViewModel? contact = ResolveSelectedAddressBookContact();
        if (contact is null)
        {
            MessageBox.Show("Sélectionne d'abord un contact.", "RedChat — avatar",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (contact.AvatarPath is not { } path)
        {
            MessageBox.Show("Ce contact n'a pas encore de photo de profil.", "RedChat — avatar",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            File.Delete(path);
            contact.NotifyAvatarChanged();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(ex.Message, "RedChat — avatar", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }


    private void OnFavoritesChanged(object? sender, EventArgs e)
    {
        _conversationView.Refresh();
        bool isFavorite = _viewModel.SelectedConversation is { } conversation
            && _favorites.Contains(conversation.Info.Id);
        FavoriteConversationButton.Content = isFavorite ? "★" : "☆";
        FavoriteConversationButton.ToolTip = isFavorite ? "Retirer des favoris" : "Ajouter aux favoris";
        if (_favoritesOnly)
        {
            ConversationEmptyState.Visibility = _conversationView.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void ShowFavorites_Click(object sender, RoutedEventArgs e)
    {
        WelcomeLanding.Visibility = Visibility.Collapsed;
        _friendsOnly = false;
        _favoritesOnly = true;
        ChatsTab.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(28, 43, 64));
        ChatsTab.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(175, 192, 216));
        FriendsTab.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(28, 43, 64));
        FriendsTab.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(175, 192, 216));
        ListHeading.Text = "CONVERSATIONS FAVORITES";
        ConversationList.Visibility = Visibility.Visible;
        ContactsList.Visibility = Visibility.Collapsed;
        SelfStatusCard.Visibility = Visibility.Collapsed;
        OpenContactButton.Visibility = Visibility.Collapsed;
        CreateGroupButton.Visibility = Visibility.Visible;
        ContactsEmptyState.Visibility = Visibility.Collapsed;
        _conversationView.Refresh();
        ConversationEmptyState.Visibility = _conversationView.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ToggleFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedConversation is not { } conversation)
        {
            MessageBox.Show("Ouvre d'abord une conversation pour l'ajouter aux favoris.", "RedChat — favoris",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            bool isFavorite = _favorites.Toggle(conversation.Info.Id);
            FavoriteConversationButton.Content = isFavorite ? "★" : "☆";
            FavoriteConversationButton.ToolTip = isFavorite ? "Retirer des favoris" : "Ajouter aux favoris";
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(ex.Message, "RedChat — favoris", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        _conversationView.Refresh();
        if (_favoritesOnly)
        {
            ConversationEmptyState.Visibility = _conversationView.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void Notifications_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.RefreshConversations();
        ConversationViewModel? unread = _viewModel.Conversations
            .Where(c => c.UnreadCount > 0)
            .OrderByDescending(c => c.LastActivityUnixMs)
            .FirstOrDefault();

        if (unread is null)
        {
            MessageBox.Show("Tu es à jour : aucun message non lu.", "RedChat — notifications",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ShowChats_Click(sender, e);
        _viewModel.SelectedConversation = unread;
        ConversationList.SelectedItem = unread;
        MessageBox.Show($"{unread.UnreadCount} message(s) non lu(s) dans « {unread.DisplayName} ». ",
            "RedChat — notifications", MessageBoxButton.OK, MessageBoxImage.Information);
    }


    private void ShowMyProfile_Click(object sender, RoutedEventArgs e)
    {
        WelcomeLanding.Visibility = Visibility.Collapsed;
        MyProfileWindow dialog = new(_personalProfile) { Owner = this };
        _ = dialog.ShowDialog();
    }

    private void ShowLanding_Click(object sender, RoutedEventArgs e) =>
        WelcomeLanding.Visibility = Visibility.Visible;

    private void OnLandingEnterRequested(object? sender, EventArgs e)
    {
        WelcomeLanding.Visibility = Visibility.Collapsed;
        ShowChats_Click(this, new RoutedEventArgs());
    }

    private void OnLandingScanToggleRequested(object? sender, EventArgs e)
    {
        if (_viewModel.IsLanScanActive)
        {
            _viewModel.StopLanScan();
        }
        else
        {
            _viewModel.StartLanScan();
        }
        WelcomeLanding.UpdateScanButtonLabel(_viewModel.IsLanScanActive);
    }

    private async void OnLandingConnectPeerRequested(DiscoveredPeerViewModel peer)
    {
        try
        {
            _ = await _viewModel.ConnectDiscoveredPeerAsync(peer.PeerId);
            WelcomeLanding.Visibility = Visibility.Collapsed;
            ShowChats_Click(this, new RoutedEventArgs());
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "RedChat — connexion au pair",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        string dataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RedChat");
        Directory.CreateDirectory(dataFolder);
        MessageBoxResult result = MessageBox.Show(
            "Les données locales de RedChat sont conservées dans le dossier de ton profil Windows.\n\n" +
            "Oui : ouvrir le dossier des données (profil, historique protégé et avatars).\n" +
            "Non : ouvrir uniquement le dossier des avatars.\n" +
            "Annuler : fermer.",
            "RedChat — paramètres et données", MessageBoxButton.YesNoCancel, MessageBoxImage.Information);
        if (result == MessageBoxResult.Cancel)
        {
            return;
        }

        string target = result == MessageBoxResult.Yes ? dataFolder : Path.Combine(dataFolder, "Avatars");
        Directory.CreateDirectory(target);
        string explorerPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "explorer.exe");
        ProcessStartInfo startInfo = new(explorerPath)
        {
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(target);
        Process.Start(startInfo);
    }

    private void MetaAi_Click(object sender, RoutedEventArgs e)
    {
        MetaAiWindow window = new() { Owner = this };
        window.ShowDialog();
    }

    private void OnCallSessionChanged(object? sender, RedChat.Infrastructure.CallSessionChangedEventArgs e)
    {
        if (e.Session is { State: RedChat.Infrastructure.CallSessionState.Accepted } accepted)
        {
            WebRtcMediaEngine engine;
            lock (_callMediaGate)
            {
                if (_callMediaEngine is null || _callMediaEngineSessionId != accepted.CallId)
                {
                    _callMediaEngine = new WebRtcMediaEngine(_viewModel.Calls, accepted);
                    _callMediaEngine.StatusChanged += OnCallMediaStatusChanged;
                    _callMediaEngine.MediaError += OnCallMediaError;
                    if (accepted.Media == RedChat.Core.CallMediaKind.Video)
                    {
                        _callMediaEngine.RemoteVideoFrameAvailable += ShowRemoteVideoFrame;
                        _callMediaEngine.LocalVideoFrameAvailable += ShowLocalVideoFrame;
                    }
                    _callMediaEngineSessionId = accepted.CallId;
                }
                engine = _callMediaEngine;
            }
            // Le ViewModel pilote micro/caméra via ICallMediaControls ; l'attache se fait sur le thread de l'interface.
            _ = Dispatcher.BeginInvoke(new Action(() => _viewModel.Call.AttachMedia(engine)));
            if (accepted.IsOutgoing)
            {
                _ = StartOutgoingMediaAsync(engine);
            }
        }
        else if (e.Session is { State: RedChat.Infrastructure.CallSessionState.Ended or RedChat.Infrastructure.CallSessionState.Rejected or RedChat.Infrastructure.CallSessionState.Busy })
        {
            _ = DisposeCallMediaEngineAsync();
        }
    }

    private string? _callMediaEngineSessionId;

    private async void OnCallSignalReceived(object? sender, RedChat.Infrastructure.CallSignalReceivedEventArgs e)
    {
        WebRtcMediaEngine? engine;
        lock (_callMediaGate)
        {
            engine = _callMediaEngine;
        }
        if (engine is null)
        {
            return;
        }
        try
        {
            await engine.HandleSignalAsync(e);
        }
        catch (Exception ex)
        {
            OnCallMediaError($"Signal média refusé : {ex.Message}");
        }
    }

    private async Task StartOutgoingMediaAsync(WebRtcMediaEngine engine)
    {
        try
        {
            await engine.StartOutgoingAsync();
        }
        catch (Exception ex)
        {
            OnCallMediaError($"Impossible de démarrer l’appel média : {ex.Message}");
        }
    }

    // Les messages média s'affichent dans le bandeau d'appel (CallViewModel) : plus aucune boîte de dialogue modale.
    private void OnCallMediaStatusChanged(string message) => _viewModel.Call.ReportMediaStatus(message);

    private void OnCallMediaError(string message) => _viewModel.Call.ReportMediaError(message);

    /// <summary>
    /// Libère le moteur média. Renvoie la libération réellement en cours : si l'événement SessionChanged l'a déjà
    /// lancée en tâche de fond, l'appelant (fermeture de la fenêtre) l'attend quand même au lieu de revenir aussitôt.
    /// </summary>
    private Task DisposeCallMediaEngineAsync()
    {
        lock (_callMediaGate)
        {
            WebRtcMediaEngine? engine = _callMediaEngine;
            _callMediaEngine = null;
            _callMediaEngineSessionId = null;
            if (engine is not null)
            {
                _callMediaDisposeTask = DisposeMediaEngineCoreAsync(engine);
            }

            return _callMediaDisposeTask;
        }
    }

    private async Task DisposeMediaEngineCoreAsync(WebRtcMediaEngine engine)
    {
        engine.RemoteVideoFrameAvailable -= ShowRemoteVideoFrame;
        engine.LocalVideoFrameAvailable -= ShowLocalVideoFrame;
        try
        {
            await engine.DisposeAsync();
        }
        catch (Exception ex)
        {
            // Ne jamais laisser une erreur de libération bloquer la fermeture ni rester non observée.
            AppLogger.Write(ex);
        }

        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            _viewModel.Call.AttachMedia(null);
            _callVideoWindow?.Close();
            ResetCallVideoState();
        }));
    }

    /// <summary>Contrôle l'image (règle testée dans <see cref="VideoFrameGuard"/>) et son format Bgr avant tout accès au bitmap.</summary>
    private static bool IsUsableBgrFrame(byte[]? pixels, uint width, uint height, int stride, SIPSorceryMedia.Abstractions.VideoPixelFormatsEnum pixelFormat) =>
        pixels is not null
        && pixelFormat == SIPSorceryMedia.Abstractions.VideoPixelFormatsEnum.Bgr
        && VideoFrameGuard.IsUsableBgr24(pixels.Length, width, height, stride);

    private static void WriteFrame(ref WriteableBitmap? bitmap, byte[] pixels, uint width, uint height, int stride)
    {
        int frameWidth = (int)width;
        int frameHeight = (int)height;
        if (bitmap is null || bitmap.PixelWidth != frameWidth || bitmap.PixelHeight != frameHeight)
        {
            bitmap = new WriteableBitmap(frameWidth, frameHeight, 96, 96, PixelFormats.Bgr24, null);
        }

        bitmap.WritePixels(new Int32Rect(0, 0, frameWidth, frameHeight), pixels, stride, 0);
    }

    private void ShowRemoteVideoFrame(byte[] pixels, uint width, uint height, int stride, SIPSorceryMedia.Abstractions.VideoPixelFormatsEnum pixelFormat)
    {
        if (!IsUsableBgrFrame(pixels, width, height, stride, pixelFormat))
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            EnsureCallVideoWindow();
            WriteFrame(ref _remoteVideoBitmap, pixels, width, height, stride);
            if (_callVideoImage is not null)
            {
                _callVideoImage.Source = _remoteVideoBitmap;
            }
        }));
    }

    private void ShowLocalVideoFrame(byte[] pixels, uint width, uint height, int stride, SIPSorceryMedia.Abstractions.VideoPixelFormatsEnum pixelFormat)
    {
        if (!IsUsableBgrFrame(pixels, width, height, stride, pixelFormat)
            || _viewModel.Call.IsCameraPaused
            || Interlocked.Exchange(ref _localFramePending, 1) != 0)
        {
            return;
        }

        // Le tampon de la caméra peut être réutilisé par la bibliothèque dès le retour du rappel : on le copie.
        byte[] copy = new byte[(int)((long)stride * height)];
        Buffer.BlockCopy(pixels, 0, copy, 0, copy.Length);
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                if (_viewModel.Call.IsCameraPaused)
                {
                    return;
                }

                EnsureCallVideoWindow();
                WriteFrame(ref _localVideoBitmap, copy, width, height, stride);
                if (_callLocalPreviewImage is not null && _callLocalPreviewFrame is not null)
                {
                    _callLocalPreviewImage.Source = _localVideoBitmap;
                    _callLocalPreviewFrame.Visibility = Visibility.Visible;
                }
            }
            finally
            {
                _ = Interlocked.Exchange(ref _localFramePending, 0);
            }
        }));
    }

    /// <summary>
    /// Crée (une seule fois) la fenêtre d'appel vidéo : image distante plein cadre + vignette locale en bas à droite,
    /// affichée en miroir comme dans toutes les applications d'appel. Doit être appelée sur le thread de l'interface.
    /// </summary>
    private void EnsureCallVideoWindow()
    {
        if (_callVideoWindow is not null)
        {
            return;
        }

        _callVideoImage = new System.Windows.Controls.Image { Stretch = Stretch.Uniform };
        _callLocalPreviewImage = new System.Windows.Controls.Image
        {
            Stretch = Stretch.Uniform,
            RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(-1, 1),
        };
        _callLocalPreviewFrame = new Border
        {
            Width = LocalPreviewWidth,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            VerticalAlignment = System.Windows.VerticalAlignment.Bottom,
            Margin = new Thickness(12),
            BorderThickness = new Thickness(2),
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(200, 30, 45)),
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 0, 0)),
            Visibility = Visibility.Collapsed,
            Child = _callLocalPreviewImage,
        };
        Grid content = new();
        _ = content.Children.Add(_callVideoImage);
        _ = content.Children.Add(_callLocalPreviewFrame);

        Window window = new()
        {
            Title = "RedChat — appel vidéo",
            Width = 720,
            Height = 520,
            MinWidth = 360,
            MinHeight = 270,
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(8, 13, 22)),
            Content = content,
            Owner = this,
        };
        // Caméra mise en pause : la dernière image resterait figée, on masque donc la vignette.
        void OnCallPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(CallViewModel.IsCameraPaused) && _callLocalPreviewFrame is not null && _viewModel.Call.IsCameraPaused)
            {
                _callLocalPreviewFrame.Visibility = Visibility.Collapsed;
            }
        }

        _viewModel.Call.PropertyChanged += OnCallPropertyChanged;
        window.Closed += (_, _) =>
        {
            _viewModel.Call.PropertyChanged -= OnCallPropertyChanged;
            ResetCallVideoState();
        };
        _callVideoWindow = window;
        window.Show();
    }

    private void ResetCallVideoState()
    {
        _callVideoWindow = null;
        _callVideoImage = null;
        _remoteVideoBitmap = null;
        _callLocalPreviewFrame = null;
        _callLocalPreviewImage = null;
        _localVideoBitmap = null;
    }

    private void ToggleDetails_Click(object sender, RoutedEventArgs e)
    {
        if (!_detailsVisible && ResolveSelectedAddressBookContact() is null)
        {
            MessageBox.Show("Pour afficher les détails, sélectionne un contact du carnet ou ouvre une conversation privée avec lui.",
                "RedChat — détails du contact", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _detailsVisible = !_detailsVisible;
        ContactDetailsPanel.Visibility = _detailsVisible ? Visibility.Visible : Visibility.Collapsed;
        DetailsColumn.Width = _detailsVisible ? new GridLength(258) : new GridLength(0);
    }

    private ContactViewModel? ResolveSelectedAddressBookContact()
    {
        if (_viewModel.SelectedAddressBookContact is { } selected)
        {
            return selected;
        }

        if (_viewModel.SelectedConversation is { Info.IsGroup: false } conversation)
        {
            ContactViewModel? matching = _viewModel.Contacts.FirstOrDefault(c => c.PeerId == conversation.Info.Id);
            if (matching is not null)
            {
                _viewModel.SelectedAddressBookContact = matching;
                return matching;
            }
        }

        if (ContactsList.SelectedItem is ContactViewModel listContact)
        {
            _viewModel.SelectedAddressBookContact = listContact;
            return listContact;
        }

        return null;
    }

    private void SaveContactName_Click(object sender, RoutedEventArgs e)
    {
        ContactViewModel? contact = ResolveSelectedAddressBookContact();
        if (contact is null)
        {
            MessageBox.Show("Aucun contact n'est sélectionné. Choisis d'abord un contact dans le carnet.",
                "RedChat — nom local", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        contact.DisplayName = ContactNameBox.Text;
        contact.RenameCommand.Execute(null);
        if (contact.Error is null)
        {
            MessageBox.Show("Le nom local a été enregistré. La sauvegarde sur cet appareil est automatique.",
                "RedChat — nom local", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show(contact.Error, "RedChat — nom local", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ToggleContactVerification_Click(object sender, RoutedEventArgs e)
    {
        ContactViewModel? contact = ResolveSelectedAddressBookContact();
        if (contact is null)
        {
            MessageBox.Show("Aucun contact n'est sélectionné.", "RedChat — vérification",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        contact.ToggleVerifiedCommand.Execute(null);
        MessageBox.Show(contact.IsVerified ? "Contact marqué comme vérifié." : "Vérification retirée.",
            "RedChat — vérification", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void EmojiPicker_Click(object sender, RoutedEventArgs e)
    {
        EmojiPickerPopup.IsOpen = !EmojiPickerPopup.IsOpen;
    }

    private void Emoji_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string emoji } || _viewModel.SelectedConversation is not { } conversation)
        {
            return;
        }

        conversation.Draft += emoji;
        EmojiPickerPopup.IsOpen = false;
    }

    private void Draft_TextChanged(object sender, TextChangedEventArgs e) => _viewModel.SelectedConversation?.NotifyTyping();

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void About_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show("RedChat\nMessagerie privée pair-à-pair sur réseau local.\n\nLes messages sont chiffrés entre les appareils.",
            "À propos de RedChat", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
