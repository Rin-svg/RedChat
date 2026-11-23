using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using RedChat.Presentation;
using Clipboard = System.Windows.Clipboard;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace RedChat.Wpf.Views;

/// <summary>Éditeur WPF du profil personnel public.</summary>
public partial class MyProfileWindow : Window
{
    private readonly PersonalProfileViewModel _viewModel;

    /// <summary>Crée l'éditeur à partir du profil personnel courant.</summary>
    public MyProfileWindow(PersonalProfileViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DisplayNameBox.Text = viewModel.DisplayName;
        StatusBox.Text = viewModel.Status;
        AboutBox.Text = viewModel.About;
        PeerIdText.Text = viewModel.PeerId;
        RefreshAvatar();
    }

    private void ChooseAvatar_Click(object sender, RoutedEventArgs e)
    {
        OpenFileDialog dialog = new()
        {
            Title = "Choisir ta photo de profil",
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
            FileInfo file = new(dialog.FileName);
            if (file.Length is <= 0 or > 512 * 1024)
            {
                throw new InvalidDataException("La photo doit peser au maximum 512 Kio.");
            }

            // audit-ok: la taille est bornée à 512 Kio juste au-dessus (FileInfo.Length) avant toute lecture.
            byte[] bytes = File.ReadAllBytes(dialog.FileName);
            BitmapImage image = DecodeImage(bytes);
            _viewModel.AvatarBytes = bytes;
            AvatarPreview.Source = image;
            AvatarInitials.Visibility = Visibility.Collapsed;
            FeedbackText.Text = "Photo chargée. Enregistre le profil pour la conserver.";
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or FormatException)
        {
            FeedbackText.Text = ex.Message;
        }
    }

    private void RemoveAvatar_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.RemoveAvatarCommand.Execute(null);
        RefreshAvatar();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.DisplayName = DisplayNameBox.Text;
        _viewModel.Status = StatusBox.Text;
        _viewModel.About = AboutBox.Text;
        _viewModel.SaveCommand.Execute(null);
        FeedbackText.Text = _viewModel.Error ?? _viewModel.Notice ?? string.Empty;
        if (_viewModel.Error is null)
        {
            DialogResult = true;
        }
    }

    private void CopyPeerId_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(_viewModel.PeerId);
        FeedbackText.Text = "PeerId copié.";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void RefreshAvatar()
    {
        byte[]? bytes = _viewModel.AvatarBytes;
        if (bytes is null)
        {
            AvatarPreview.Source = null;
            AvatarInitials.Visibility = Visibility.Visible;
            return;
        }

        AvatarPreview.Source = DecodeImage(bytes);
        AvatarInitials.Visibility = Visibility.Collapsed;
    }

    private static BitmapImage DecodeImage(byte[] bytes)
    {
        BitmapImage image = new();
        using MemoryStream stream = new(bytes, writable: false);
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.DecodePixelWidth = 256;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
