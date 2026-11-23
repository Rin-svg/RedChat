using System.Collections.ObjectModel;
using RedChat.Core;

namespace RedChat.Presentation;

/// <summary>État et actions d'une conversation affichée dans l'interface.</summary>
public sealed class ConversationViewModel : ObservableObject, IDisposable
{
    private readonly ChatService _chat;
    private readonly ContactBook _contacts;
    private readonly IUiDispatcher _dispatcher;
    private readonly ITimer _typingTimer;
    private string _draft = string.Empty;
    private string? _replyToId;
    private string? _editingMessageId;
    private string _editDraft = string.Empty;
    private bool _isPeerTyping;
    private string? _error;
    private readonly TimeProvider _clock;
    private long _lastTypingUnixMs;
    private bool _disposed;

    /// <summary>Crée le modèle de vue et s'abonne aux changements du service.</summary>
    public ConversationViewModel(ConversationInfo info, ChatService chat, ContactBook contacts, IUiDispatcher dispatcher, TimeProvider? clock = null)
    {
        Info = info ?? throw new ArgumentNullException(nameof(info));
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        _contacts = contacts ?? throw new ArgumentNullException(nameof(contacts));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _clock = clock ?? TimeProvider.System;
        _typingTimer = _clock.CreateTimer(OnTypingTimeout, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        Messages = new ObservableCollection<ChatMessage>(_chat.GetMessages(Info.Id));
        SendCommand = new RelayCommand(Send, SetError, () => CanSend);
        SaveEditCommand = new RelayCommand(SaveEdit, SetError, () => _editingMessageId is not null);
        CancelEditCommand = new RelayCommand(CancelEdit);
        CancelReplyCommand = new RelayCommand(CancelReply);
        DeleteCommand = new RelayCommand(Delete, SetError);
        ReplyCommand = new RelayCommand(Reply, SetError);
        BeginEditCommand = new RelayCommand(BeginEditFromCommand, SetError);
        ReactCommand = new RelayCommand(React, SetError);
        LikeCommand = new RelayCommand(Like, SetError);
        _chat.Changed += OnChatChanged;
        _contacts.Changed += OnContactsChanged;
    }

    /// <summary>Description immuable de la conversation.</summary>
    public ConversationInfo Info { get; }

    /// <summary>Messages de la conversation, dans l'ordre chronologique de réception.</summary>
    public ObservableCollection<ChatMessage> Messages { get; }

    /// <summary>Nom affiché (surnom local prioritaire, sinon profil public, ou titre du groupe).</summary>
    public string DisplayName => Info.IsGroup ? Info.Title ?? "Groupe" : _contacts.DisplayLabel(Info.Id);

    /// <summary>Chemin de la photo importée localement pour ce contact (elle prime sur la photo publiée par le contact), ou null.</summary>
    public string? AvatarPath
    {
        get
        {
            if (Info.IsGroup)
            {
                return null;
            }

            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RedChat", "Avatars", Info.Id + ".png");
            return File.Exists(path) ? path : null;
        }
    }

    /// <summary>
    /// Photo publiée par le contact dans son profil public (PNG/JPEG déjà validé par le Core), ou null (groupe, pas de photo).
    /// Une copie est renvoyée : l'appelant ne peut pas modifier le carnet de contacts.
    /// </summary>
    public byte[]? RemoteAvatarBytes => Info.IsGroup ? null : _contacts.Find(Info.Id)?.PublicProfile?.AvatarBytes?.ToArray();

    /// <summary>Initiales utilisées comme avatar de remplacement lorsque le contact ne possède pas de photo.</summary>
    public string AvatarInitials
    {
        get
        {
            string[] parts = DisplayName.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0)
            {
                return "RC";
            }

            string first = parts[0];
            string last = parts.Length > 1 ? parts[^1] : string.Empty;
            return last.Length == 0
                ? first[..Math.Min(2, first.Length)].ToUpperInvariant()
                : string.Concat(char.ToUpperInvariant(first[0]), char.ToUpperInvariant(last[0]));
        }
    }

    /// <summary>Dernier message, pour l'aperçu de la liste des conversations.</summary>
    public string LastMessagePreview => Messages.Count == 0 ? "Aucun message" : Preview(Messages[^1]);

    /// <summary>Date Unix millisecondes du dernier message.</summary>
    public long LastActivityUnixMs => Messages.Count == 0 ? 0 : Messages[^1].SentAtUnixMs;

    /// <summary>Nombre de messages entrants non lus.</summary>
    public int UnreadCount => Messages.Count(m => !m.IsOutgoing && !m.IsReadByMe);

    /// <summary>Vrai si le correspondant envoie actuellement un indicateur de frappe.</summary>
    public bool IsPeerTyping
    {
        get => _isPeerTyping;
        private set
        {
            if (SetProperty(ref _isPeerTyping, value))
            {
                OnPropertyChanged(nameof(TypingLabel));
            }
        }
    }

    /// <summary>Libellé court de l'indicateur de frappe.</summary>
    public string TypingLabel => IsPeerTyping ? "écrit…" : string.Empty;

    /// <summary>Brouillon en cours de saisie.</summary>
    public string Draft
    {
        get => _draft;
        set
        {
            if (SetProperty(ref _draft, value ?? string.Empty))
            {
                SendCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>Identifiant du message auquel le prochain envoi répond, le cas échéant.</summary>
    public string? ReplyToId
    {
        get => _replyToId;
        private set => SetProperty(ref _replyToId, value);
    }

    /// <summary>Texte actuellement modifié.</summary>
    public string EditDraft
    {
        get => _editDraft;
        set => SetProperty(ref _editDraft, value ?? string.Empty);
    }

    /// <summary>Identifiant du message en cours d'édition.</summary>
    public string? EditingMessageId
    {
        get => _editingMessageId;
        private set
        {
            if (SetProperty(ref _editingMessageId, value))
            {
                SaveEditCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>Dernière erreur d'action, destinée à un bandeau d'interface.</summary>
    public string? Error
    {
        get => _error;
        private set => SetProperty(ref _error, value);
    }

    /// <summary>Commande d'envoi du brouillon.</summary>
    public RelayCommand SendCommand { get; }

    /// <summary>Commande de validation d'une édition.</summary>
    public RelayCommand SaveEditCommand { get; }

    /// <summary>Commande d'annulation d'une édition.</summary>
    public RelayCommand CancelEditCommand { get; }

    /// <summary>Commande d'annulation de la réponse préparée.</summary>
    public RelayCommand CancelReplyCommand { get; }

    /// <summary>Commande de suppression d'un message sortant (paramètre : identifiant).</summary>
    public RelayCommand DeleteCommand { get; }

    /// <summary>Commande de réaction (paramètre : <see cref="MessageReactionRequest"/>).</summary>
    public RelayCommand ReactCommand { get; }

    /// <summary>Commande de réponse (paramètre : <see cref="ChatMessage"/>).</summary>
    public RelayCommand ReplyCommand { get; }

    /// <summary>Commande d'ouverture de l'éditeur (paramètre : <see cref="ChatMessage"/>).</summary>
    public RelayCommand BeginEditCommand { get; }

    /// <summary>Commande de réaction rapide « J'aime » (paramètre : identifiant de message).</summary>
    public RelayCommand LikeCommand { get; }

    /// <summary>Vrai si le brouillon satisfait déjà la validation métier.</summary>
    public bool CanSend => InputValidator.IsValidMessageText(Draft);

    /// <summary>Rafraîchit les messages depuis l'instantané thread-safe du service.</summary>
    public void RefreshMessages()
    {
        IReadOnlyList<ChatMessage> latest = _chat.GetMessages(Info.Id);
        Messages.Clear();
        foreach (ChatMessage message in latest)
        {
            Messages.Add(message);
        }

        OnPropertyChanged(nameof(LastMessagePreview));
        OnPropertyChanged(nameof(LastActivityUnixMs));
        OnPropertyChanged(nameof(UnreadCount));
    }

    /// <summary>Marque les messages entrants comme lus au moment où la conversation devient visible.</summary>
    public void MarkRead() => _chat.MarkRead(Info.Id);

    /// <summary>Envoie un indicateur « écrit… » au maximum une fois toutes les deux secondes.</summary>
    public void NotifyTyping()
    {
        long now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        if (now - _lastTypingUnixMs >= 2_000)
        {
            _lastTypingUnixMs = now;
            _chat.SendTyping(Info.Id);
        }
    }

    /// <summary>Prépare une réponse à un message.</summary>
    public void ReplyTo(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.ConversationId != Info.Id || message.IsDeleted)
        {
            throw new ArgumentException("Ce message ne peut pas recevoir de réponse.", nameof(message));
        }

        ReplyToId = message.Id;
    }

    /// <summary>Ouvre l'éditeur d'un de nos messages.</summary>
    public void BeginEdit(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.ConversationId != Info.Id || !message.IsOutgoing || message.IsDeleted)
        {
            throw new ArgumentException("Seuls vos messages non supprimés peuvent être modifiés.", nameof(message));
        }

        EditingMessageId = message.Id;
        EditDraft = message.Text;
    }

    /// <summary>Libère les abonnements pour éviter de conserver les vues fermées en mémoire.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _chat.Changed -= OnChatChanged;
        _contacts.Changed -= OnContactsChanged;
        _typingTimer.Dispose();
    }

    private void Send()
    {
        if (!CanSend)
        {
            throw new ArgumentException("Le message est vide, trop long ou contient des caractères interdits.");
        }

        _ = _chat.SendText(Info.Id, Draft, ReplyToId);
        Draft = string.Empty;
        ReplyToId = null;
        Error = null;
    }

    private void SaveEdit()
    {
        if (EditingMessageId is null)
        {
            return;
        }

        _chat.EditMessage(Info.Id, EditingMessageId, EditDraft);
        CancelEdit();
    }

    private void CancelEdit()
    {
        EditingMessageId = null;
        EditDraft = string.Empty;
    }

    private void CancelReply() => ReplyToId = null;

    private void Reply(object? parameter)
    {
        if (parameter is not ChatMessage message)
        {
            throw new ArgumentException("Message de réponse invalide.");
        }

        ReplyTo(message);
    }

    private void BeginEditFromCommand(object? parameter)
    {
        if (parameter is not ChatMessage message)
        {
            throw new ArgumentException("Message à modifier invalide.");
        }

        BeginEdit(message);
    }

    private void Like(object? parameter)
    {
        if (parameter is not string messageId || !InputValidator.IsValidGuid(messageId))
        {
            throw new ArgumentException("Identifiant de message invalide.");
        }

        _chat.React(Info.Id, messageId, "👍");
    }

    private void Delete(object? parameter)
    {
        if (parameter is not string messageId || !InputValidator.IsValidGuid(messageId))
        {
            throw new ArgumentException("Identifiant de message invalide.");
        }

        _chat.DeleteMessage(Info.Id, messageId);
    }

    private void React(object? parameter)
    {
        if (parameter is not MessageReactionRequest request || !InputValidator.IsValidGuid(request.MessageId))
        {
            throw new ArgumentException("Demande de réaction invalide.");
        }

        _chat.React(Info.Id, request.MessageId, request.Emoji);
    }

    private void OnChatChanged(object? sender, ChatChangedEventArgs e)
    {
        if (e.ConversationId != Info.Id)
        {
            return;
        }

        _dispatcher.Post(() =>
        {
            if (_disposed)
            {
                return;
            }

            if (e.Kind == ChatChangeKind.Typing)
            {
                IsPeerTyping = true;
                _typingTimer.Change(TimeSpan.FromSeconds(4), Timeout.InfiniteTimeSpan);
            }

            RefreshMessages();
        });
    }

    private void OnContactsChanged(object? sender, EventArgs e) => _dispatcher.Post(() =>
    {
        if (!_disposed)
        {
            OnPropertyChanged(nameof(DisplayName));
            OnPropertyChanged(nameof(AvatarInitials));
            OnPropertyChanged(nameof(AvatarPath));
            OnPropertyChanged(nameof(RemoteAvatarBytes));
        }
    });

    private void OnTypingTimeout(object? state) => _dispatcher.Post(() =>
    {
        if (!_disposed)
        {
            IsPeerTyping = false;
        }
    });

    private void SetError(Exception exception)
    {
        Error = exception.Message;
    }

    private static string Preview(ChatMessage message)
    {
        if (message.IsDeleted)
        {
            return "Message supprimé";
        }

        string text = message.Text.Replace('\r', ' ').Replace('\n', ' ');
        return text.Length <= 80 ? text : text[..77] + "…";
    }
}

/// <summary>Paramètre typé d'une commande de réaction.</summary>
/// <param name="MessageId">Identifiant du message ciblé.</param>
/// <param name="Emoji">Réaction courte à appliquer.</param>
public sealed record MessageReactionRequest(string MessageId, string Emoji);
