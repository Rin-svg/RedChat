using System.Collections.ObjectModel;
using RedChat.Core;

namespace RedChat.Presentation;

/// <summary>Prépare le transfert d'un message vers une autre conversation : choix de la destination, recherche et envoi.</summary>
public sealed class ForwardMessageViewModel : ObservableObject
{
    private const int PreviewMaxChars = 120;

    private readonly ChatService _chat;
    private readonly ChatMessage _message;
    private readonly List<ConversationViewModel> _candidates;
    private string _search = string.Empty;
    private ConversationViewModel? _selectedTarget;
    private string? _error;
    private ChatMessage? _forwardedMessage;

    /// <summary>Crée le modèle de vue pour un message et les conversations proposées comme destination.</summary>
    /// <param name="chat">Service de conversation qui effectuera l'envoi.</param>
    /// <param name="message">Message à transférer.</param>
    /// <param name="conversations">Conversations connues ; celle du message d'origine est retirée de la liste.</param>
    public ForwardMessageViewModel(ChatService chat, ChatMessage message, IEnumerable<ConversationViewModel> conversations)
    {
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        _message = message ?? throw new ArgumentNullException(nameof(message));
        ArgumentNullException.ThrowIfNull(conversations);
        _candidates = conversations
            .Where(c => !string.Equals(c.Info.Id, message.ConversationId, StringComparison.Ordinal))
            .ToList();
        Targets = new ObservableCollection<ConversationViewModel>(_candidates);
        ForwardCommand = new RelayCommand(Forward, SetError, () => CanForward);
    }

    /// <summary>Conversations proposées, filtrées par la recherche.</summary>
    public ObservableCollection<ConversationViewModel> Targets { get; }

    /// <summary>Aperçu du texte transféré (tronqué pour l'affichage).</summary>
    public string PreviewText => _message.Text.Length <= PreviewMaxChars ? _message.Text : _message.Text[..(PreviewMaxChars - 1)] + "…";

    /// <summary>Texte de recherche appliqué au nom des conversations.</summary>
    public string Search
    {
        get => _search;
        set
        {
            if (SetProperty(ref _search, value ?? string.Empty))
            {
                ApplyFilter();
            }
        }
    }

    /// <summary>Conversation de destination choisie, ou null.</summary>
    public ConversationViewModel? SelectedTarget
    {
        get => _selectedTarget;
        set
        {
            if (SetProperty(ref _selectedTarget, value))
            {
                Error = null;
                ForwardCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>Dernière erreur de transfert.</summary>
    public string? Error
    {
        get => _error;
        private set => SetProperty(ref _error, value);
    }

    /// <summary>Message créé dans la conversation de destination, ou null avant le transfert.</summary>
    public ChatMessage? ForwardedMessage
    {
        get => _forwardedMessage;
        private set => SetProperty(ref _forwardedMessage, value);
    }

    /// <summary>Vrai si une destination est choisie et si la règle de transfert l'autorise.</summary>
    public bool CanForward => SelectedTarget is not null && MessageForwardPolicy.Validate(_message, SelectedTarget.Info.Id) is null;

    /// <summary>Commande de transfert vers la destination choisie.</summary>
    public RelayCommand ForwardCommand { get; }

    private void ApplyFilter()
    {
        string term = Search.Trim();
        IEnumerable<ConversationViewModel> matches = term.Length == 0
            ? _candidates
            : _candidates.Where(c => c.DisplayName.Contains(term, StringComparison.CurrentCultureIgnoreCase));

        Targets.Clear();
        foreach (ConversationViewModel conversation in matches)
        {
            Targets.Add(conversation);
        }

        if (SelectedTarget is not null && !Targets.Contains(SelectedTarget))
        {
            SelectedTarget = null;
        }
    }

    private void Forward()
    {
        if (SelectedTarget is null)
        {
            throw new InvalidOperationException("Choisis d'abord une conversation de destination.");
        }

        ForwardedMessage = _chat.ForwardMessage(_message.ConversationId, _message.Id, SelectedTarget.Info.Id);
        Error = null;
    }

    private void SetError(Exception exception) => Error = exception.Message;
}