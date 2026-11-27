using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RedChat.Wpf;

namespace RedChat.Wpf.Views;

public partial class MetaAiWindow : Window, IDisposable
{
    private const int MaxHistoryMessages = 20;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _isSending;
    private int _disposed;

    /// <summary>Initialise la fenêtre de conversation avec le modèle local.</summary>
    public MetaAiWindow()
    {
        InitializeComponent();
        DataContext = this;
        Messages.Add(new MetaAiMessage(
            false,
            "Bonjour ! Je suis Llama 3.2 1B, exécuté localement avec Ollama. Ouvre « Installation… » si Ollama n’est pas encore configuré.",
            false));
        StatusText.Text = "Assistant local — installe Ollama et le modèle llama3.2:1b pour commencer.";
    }

    /// <summary>Messages de la conversation temporaire affichés par l'interface.</summary>
    public ObservableCollection<MetaAiMessage> Messages { get; } = new();

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        if (_isSending)
        {
            return;
        }

        string prompt = PromptBox.Text.Trim();
        if (prompt.Length == 0)
        {
            return;
        }

        PromptBox.Clear();
        Messages.Add(new MetaAiMessage(true, prompt, false));
        SetSending(true);
        StatusText.Text = "Llama réfléchit…";
        ScrollToLatest();
        try
        {
            List<(string Role, string Content)> history = Messages
                .Where(message => !message.IsError && message.Content.Length <= 4000)
                .Skip(1)
                .TakeLast(MaxHistoryMessages)
                .Select(message => (message.IsUser ? "user" : "assistant", message.Content))
                .ToList();
            string answer = await LocalAiClient.CompleteAsync(history, _lifetime.Token);
            Messages.Add(new MetaAiMessage(false, answer, false));
            StatusText.Text = "Réponse générée localement par Llama 3.2 1B.";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidDataException or System.Text.Json.JsonException)
        {
            string error = exception is HttpRequestException { StatusCode: null }
                ? "Impossible de joindre Ollama sur cet appareil. Installe Ollama, lance « ollama run llama3.2:1b », puis réessaie."
                : exception.Message;
            Messages.Add(new MetaAiMessage(false, $"Erreur : {error}", true));
            StatusText.Text = "La requête locale a échoué.";
        }
        finally
        {
            SetSending(false);
            ScrollToLatest();
        }
    }

    private void Setup_Click(object sender, RoutedEventArgs e)
    {
        SetupOverlay.Visibility = Visibility.Visible;
    }

    private void CloseSetup_Click(object sender, RoutedEventArgs e)
    {
        SetupOverlay.Visibility = Visibility.Collapsed;
    }

    private void PromptBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift)
        {
            e.Handled = true;
            Send_Click(sender, e);
        }
    }

    private void SetSending(bool isSending)
    {
        _isSending = isSending;
        SendButton.IsEnabled = !isSending;
        PromptBox.IsEnabled = !isSending;
    }

    private void ScrollToLatest()
    {
        if (MessagesList.Items.Count > 0)
        {
            MessagesList.ScrollIntoView(MessagesList.Items[^1]);
        }
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        Dispose();
        base.OnClosed(e);
    }

    /// <summary>Annule toute requête réseau en cours et libère son jeton d'annulation.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel();
        _lifetime.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>Message affiché dans la conversation IA en mémoire.</summary>
public sealed class MetaAiMessage(bool isUser, string content, bool isError)
{
    /// <summary>Indique que le message vient de l'utilisateur.</summary>
    public bool IsUser { get; } = isUser;

    /// <summary>Texte affiché dans la bulle.</summary>
    public string Content { get; } = content;

    /// <summary>Indique que le message est une erreur.</summary>
    public bool IsError { get; } = isError;

    /// <summary>Nom affiché au-dessus du message.</summary>
    public string SenderLabel => IsUser ? "Vous" : IsError ? "Assistant · erreur" : "Llama 3.2 1B";

    /// <summary>Initiales affichées dans l’avatar de conversation.</summary>
    public string AvatarLabel => IsUser ? "VO" : "IA";

    /// <summary>Heure locale de création du message.</summary>
    public string TimeLabel { get; } = DateTime.Now.ToString("HH:mm", System.Globalization.CultureInfo.CurrentCulture);
}
