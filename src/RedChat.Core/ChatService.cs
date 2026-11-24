using System.Collections.ObjectModel;

namespace RedChat.Core;

/// <summary>
/// Logique de conversation : envoi/réception, accusés, réactions, édition, suppression, groupes v1, file d'attente hors ligne.
/// Aucun accès réseau ni disque ici : tout passe par <see cref="ITransport"/> et l'horloge injectée (donc entièrement testable).
/// <para>SÉCURITÉ : <c>senderPeerId</c> passé à <see cref="Receive"/> est l'identité AUTHENTIFIÉE par le handshake, jamais une valeur
/// lue dans le paquet. Chaque action reçue est autorisée (seul l'auteur modifie/supprime ; seul un destinataire accuse réception ;
/// seul un membre écrit dans un groupe). Toute violation lève <see cref="ProtocolViolationException"/> : l'appelant coupe la connexion.</para>
/// </summary>
public sealed partial class ChatService
{
    /// <summary>Nombre maximal de paquets en attente par pair hors ligne (évite une croissance mémoire sans fin).</summary>
    public const int MaxOutboxPerPeer = 1000;

    private sealed class Conv
    {
        public Conv(ConversationInfo info) => Info = info;

        public ConversationInfo Info { get; }

        public List<ChatMessage> Messages { get; } = new();
    }

    private readonly object _lock = new();
    private readonly string _self;
    private readonly ITransport _transport;
    private readonly TimeProvider _clock;
    private readonly Dictionary<string, Conv> _convs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<Packet>> _outbox = new(StringComparer.Ordinal);

    /// <summary>Crée le service pour l'identité locale <paramref name="selfPeerId"/>.</summary>
    public ChatService(string selfPeerId, ITransport transport, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        if (!InputValidator.IsValidPeerId(selfPeerId))
        {
            throw new ArgumentException("PeerId local invalide.", nameof(selfPeerId));
        }

        _self = selfPeerId;
        _transport = transport;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Levé APRÈS libération du verrou interne : un abonné peut donc rappeler le service sans interblocage.</summary>
    public event EventHandler<ChatChangedEventArgs>? Changed;

    /// <summary>
    /// Levé après chaque opération qui a pu modifier l'état durable (historique ou file d'attente), même si aucun événement
    /// <see cref="Changed"/> n'a été produit (ex. accusé mis en file pour un pair hors ligne). Sert à déclencher la sauvegarde.
    /// Levé APRÈS libération du verrou ; un abonné ne doit pas lever d'exception.
    /// </summary>
    public event EventHandler? StateChanged;

    // ───────────────────────────── Lecture ─────────────────────────────

    /// <summary>Instantané des conversations.</summary>
    public IReadOnlyList<ConversationInfo> GetConversations()
    {
        lock (_lock)
        {
            return _convs.Values.Select(c => c.Info).ToArray();
        }
    }

    /// <summary>Instantané des messages d'une conversation, dans l'ordre d'arrivée (liste vide si inconnue).</summary>
    public IReadOnlyList<ChatMessage> GetMessages(string conversationId)
    {
        lock (_lock)
        {
            return _convs.TryGetValue(conversationId, out Conv? c) ? c.Messages.ToArray() : Array.Empty<ChatMessage>();
        }
    }

    /// <summary>Nombre de paquets en attente pour un pair.</summary>
    public int PendingCount(string peerId)
    {
        lock (_lock)
        {
            return _outbox.TryGetValue(peerId, out Queue<Packet>? q) ? q.Count : 0;
        }
    }

    // ───────────────────────────── Actions locales ─────────────────────────────

    /// <summary>Envoie un texte. <paramref name="conversationId"/> = PeerId du contact ou GUID d'un groupe connu.</summary>
    public ChatMessage SendText(string conversationId, string text, string? replyToId = null, bool isViewOnce = false)
    {
        if (!InputValidator.IsValidMessageText(text))
        {
            throw new ArgumentException("Texte invalide.", nameof(text));
        }

        if (replyToId is not null && !InputValidator.IsValidGuid(replyToId))
        {
            throw new ArgumentException("Identifiant de réponse invalide.", nameof(replyToId));
        }

        List<Action> events = new();
        ChatMessage msg;
        lock (_lock)
        {
            Conv conv = GetOrCreateOutgoingConv(conversationId, events);
            if (isViewOnce && (conv.Info.IsGroup || conv.Info.IsSelfConversation))
            {
                throw new InvalidOperationException("La vue unique est disponible uniquement dans une conversation privée.");
            }
            string[] recipients = Recipients(conv);
            EnsureOutboxRoom(recipients, 1);
            Packet p = new()
            {
                Kind = PacketKind.Text,
                Id = NewId(),
                ConversationId = conv.Info.Id,
                SentAtUnixMs = Now().ToUnixTimeMilliseconds(),
                Text = text,
                ReplyToId = replyToId,
                IsViewOnce = isViewOnce,
            };
            msg = new ChatMessage
            {
                Id = p.Id,
                ConversationId = conv.Info.Id,
                SenderId = _self,
                Text = text,
                SentAtUnixMs = p.SentAtUnixMs,
                IsOutgoing = true,
                ReplyToId = replyToId,
                IsViewOnce = isViewOnce,
                IsReadByMe = true,
                Receipts = new ReadOnlyDictionary<string, MessageStatus>(recipients.ToDictionary(r => r, _ => MessageStatus.Pending, StringComparer.Ordinal)),
            };
            conv.Messages.Add(msg);
            events.Add(Raise(ChatChangeKind.MessageAdded, conv.Info.Id, msg.Id));
            foreach (string r in recipients)
            {
                Enqueue(r, p, events);
            }

            msg = conv.Messages[^1];
        }

        Fire(events);
        return msg;
    }

    /// <summary>Crée ou retourne une conversation privée sans envoyer de message artificiel.</summary>
    /// <param name="peerId">PeerId authentifié du contact.</param>
    /// <returns>La conversation privée existante ou nouvellement créée.</returns>
    public ConversationInfo EnsureDirectConversation(string peerId)
    {
        if (!InputValidator.IsValidPeerId(peerId) || peerId == _self)
        {
            throw new ArgumentException("Identité du contact invalide.", nameof(peerId));
        }

        List<Action> events = new();
        ConversationInfo info;
        lock (_lock)
        {
            Conv conversation = GetOrCreateOutgoingConv(peerId, events);
            info = conversation.Info;
        }

        if (events.Count != 0)
        {
            Fire(events);
        }

        return info;
    }

    /// <summary>Crée ou retourne la conversation locale de notes personnelles, sans destinataire réseau.</summary>
    public ConversationInfo EnsureSelfConversation()
    {
        List<Action> events = new();
        ConversationInfo info;
        lock (_lock)
        {
            if (!_convs.TryGetValue(_self, out Conv? conversation))
            {
                conversation = new Conv(new ConversationInfo { Id = _self, IsSelfConversation = true });
                _convs.Add(_self, conversation);
                events.Add(Raise(ChatChangeKind.ConversationAdded, _self));
            }
            info = conversation.Info;
        }

        if (events.Count != 0)
        {
            Fire(events);
        }

        return info;
    }

    /// <summary>Supprime une conversation de cet appareil sans notifier les autres membres.</summary>
    /// <remarks>Les paquets en attente liés à cette conversation sont aussi retirés des files locales.</remarks>
    public bool RemoveConversation(string conversationId)
    {
        if (string.IsNullOrWhiteSpace(conversationId) || conversationId.Length > 64)
        {
            throw new ArgumentException("Identifiant de conversation invalide.", nameof(conversationId));
        }

        lock (_lock)
        {
            if (!_convs.Remove(conversationId))
            {
                return false;
            }

            foreach (string peerId in _outbox.Keys.ToArray())
            {
                Queue<Packet> filtered = new(_outbox[peerId].Where(packet =>
                    !string.Equals(packet.ConversationId, conversationId, StringComparison.Ordinal)));
                if (filtered.Count == 0)
                {
                    _outbox.Remove(peerId);
                }
                else
                {
                    _outbox[peerId] = filtered;
                }
            }
        }

        Fire(new List<Action> { Raise(ChatChangeKind.ConversationRemoved, conversationId) });
        return true;
    }

    /// <summary>Crée un groupe (nous + 1 à 255 autres) et le diffuse à chaque membre.</summary>
    public ConversationInfo CreateGroup(string name, IEnumerable<string> otherMembers)
    {
        ArgumentNullException.ThrowIfNull(otherMembers);
        if (!InputValidator.TryNormalizeName(name, ProtocolLimits.MaxGroupNameChars, out string title))
        {
            throw new ArgumentException("Nom de groupe invalide.", nameof(name));
        }

        string[] others = otherMembers.Distinct(StringComparer.Ordinal).Where(m => m != _self).ToArray();
        if (others.Length < 1 || others.Length + 1 > ProtocolLimits.MaxGroupMembers || !others.All(InputValidator.IsValidPeerId))
        {
            throw new ArgumentException("Membres invalides (1 à 255 autres PeerId valides).", nameof(otherMembers));
        }

        string[] members = others.Prepend(_self).ToArray();
        List<Action> events = new();
        ConversationInfo info;
        lock (_lock)
        {
            EnsureOutboxRoom(others, 1);
            info = new ConversationInfo { Id = NewId(), IsGroup = true, Title = title, Members = Array.AsReadOnly(members), CreatorId = _self };
            _convs[info.Id] = new Conv(info);
            events.Add(Raise(ChatChangeKind.ConversationAdded, info.Id));
            Packet p = new()
            {
                Kind = PacketKind.GroupCreate,
                Id = NewId(),
                ConversationId = info.Id,
                SentAtUnixMs = Now().ToUnixTimeMilliseconds(),
                GroupName = title,
                Members = members,
            };
            foreach (string o in others)
            {
                Enqueue(o, p, events);
            }
        }

        Fire(events);
        return info;
    }

    /// <summary>Réagit à un message (une réaction par personne : la nouvelle remplace l'ancienne).</summary>
    public void React(string conversationId, string messageId, string emoji)
    {
        if (!InputValidator.IsValidReaction(emoji))
        {
            throw new ArgumentException("Réaction invalide.", nameof(emoji));
        }

        List<Action> events = new();
        lock (_lock)
        {
            Conv conv = RequireConv(conversationId);
            int i = IndexOf(conv, messageId);
            if (i < 0 || conv.Messages[i].IsDeleted)
            {
                throw new InvalidOperationException("Message introuvable.");
            }

            conv.Messages[i] = SetReaction(conv.Messages[i], _self, emoji);
            events.Add(Raise(ChatChangeKind.MessageUpdated, conv.Info.Id, messageId));
            BroadcastControl(conv, new Packet { Kind = PacketKind.Reaction, TargetMessageId = messageId, Emoji = emoji }, events);
        }

        Fire(events);
    }

    /// <summary>Modifie le texte d'un de NOS messages.</summary>
    public void EditMessage(string conversationId, string messageId, string newText)
    {
        if (!InputValidator.IsValidMessageText(newText))
        {
            throw new ArgumentException("Texte invalide.", nameof(newText));
        }

        List<Action> events = new();
        lock (_lock)
        {
            Conv conv = RequireConv(conversationId);
            int i = IndexOf(conv, messageId);
            if (i < 0 || !conv.Messages[i].IsOutgoing || conv.Messages[i].IsDeleted)
            {
                throw new InvalidOperationException("Seul un de vos messages non supprimés peut être modifié.");
            }

            conv.Messages[i] = conv.Messages[i] with { Text = newText, IsEdited = true };
            events.Add(Raise(ChatChangeKind.MessageUpdated, conv.Info.Id, messageId));
            BroadcastControl(conv, new Packet { Kind = PacketKind.Edit, TargetMessageId = messageId, Text = newText }, events);
        }

        Fire(events);
    }

    /// <summary>Supprime pour tous un de NOS messages (le texte est effacé localement et chez les autres).</summary>
    public void DeleteMessage(string conversationId, string messageId)
    {
        List<Action> events = new();
        lock (_lock)
        {
            Conv conv = RequireConv(conversationId);
            int i = IndexOf(conv, messageId);
            if (i < 0 || !conv.Messages[i].IsOutgoing)
            {
                throw new InvalidOperationException("Seul un de vos messages peut être supprimé.");
            }

            conv.Messages[i] = Tombstone(conv.Messages[i]);
            events.Add(Raise(ChatChangeKind.MessageUpdated, conv.Info.Id, messageId));
            BroadcastControl(conv, new Packet { Kind = PacketKind.Delete, TargetMessageId = messageId }, events);
        }

        Fire(events);
    }

    /// <summary>Efface un message reçu à vue unique après consultation et notifie son auteur.</summary>
    public void ConsumeViewOnceMessage(string conversationId, string messageId)
    {
        List<Action> events = new();
        lock (_lock)
        {
            Conv conv = RequireConv(conversationId);
            int i = IndexOf(conv, messageId);
            if (conv.Info.IsGroup || i < 0 || conv.Messages[i].IsOutgoing || !conv.Messages[i].IsViewOnce || conv.Messages[i].IsDeleted)
            {
                throw new InvalidOperationException("Ce message à vue unique n'est plus disponible.");
            }

            ChatMessage message = conv.Messages[i];
            conv.Messages[i] = Tombstone(message);
            events.Add(Raise(ChatChangeKind.MessageUpdated, conv.Info.Id, messageId));
            Packet opened = new()
            {
                Kind = PacketKind.ViewOnceOpened,
                Id = NewId(),
                ConversationId = message.SenderId,
                SentAtUnixMs = Now().ToUnixTimeMilliseconds(),
                TargetMessageId = messageId,
            };
            Enqueue(message.SenderId, opened, events, bestEffortWhenFull: true);
        }

        Fire(events);
    }

    /// <summary>Marque la conversation comme lue : envoie un accusé « lu » pour chaque message entrant pas encore lu.</summary>
    public void MarkRead(string conversationId)
    {
        List<Action> events = new();
        lock (_lock)
        {
            if (!_convs.TryGetValue(conversationId, out Conv? conv))
            {
                return;
            }

            for (int i = 0; i < conv.Messages.Count; i++)
            {
                ChatMessage m = conv.Messages[i];
                if (m.IsOutgoing || m.IsReadByMe)
                {
                    continue;
                }

                conv.Messages[i] = m with { IsReadByMe = true };
                events.Add(Raise(ChatChangeKind.MessageUpdated, conv.Info.Id, m.Id));
                if (!m.IsDeleted)
                {
                    SendReceipt(m.SenderId, conv.Info.Id, m.Id, ReceiptState.Read, events);
                }
            }
        }

        Fire(events);
    }

    /// <summary>Signale « en train d'écrire » (jamais mis en file d'attente : sans intérêt une fois périmé).</summary>
    public void SendTyping(string conversationId)
    {
        lock (_lock)
        {
            if (!_convs.TryGetValue(conversationId, out Conv? conv))
            {
                return;
            }

            foreach (string r in Recipients(conv))
            {
                _ = _transport.TrySend(r, Control(conv.Info, r, new Packet { Kind = PacketKind.Typing }));
            }
        }
    }

    /// <summary>À appeler quand une connexion chiffrée vers <paramref name="peerId"/> vient de s'ouvrir : vide la file d'attente, dans l'ordre.</summary>
    public void OnPeerConnected(string peerId)
    {
        List<Action> events = new();
        lock (_lock)
        {
            RequeueUnconfirmed(peerId, events);
            Flush(peerId, events);
        }

        Fire(events);
    }

    /// <summary>
    /// Un message « transmis » (Sent) mais jamais accusé peut avoir été perdu avec la connexion précédente (paquet encore dans la file
    /// d'écriture du transport). À la reconnexion on le renvoie : le destinataire dédoublonne par Id et ré-accuse « remis ».
    /// </summary>
    private void RequeueUnconfirmed(string peer, List<Action> events)
    {
        foreach (Conv conv in _convs.Values)
        {
            if (!Recipients(conv).Contains(peer, StringComparer.Ordinal))
            {
                continue;
            }

            foreach (ChatMessage m in conv.Messages.ToArray())
            {
                if (!m.IsOutgoing || m.IsDeleted || !m.Receipts.TryGetValue(peer, out MessageStatus s) || s != MessageStatus.Sent)
                {
                    continue;
                }

                if (_outbox.TryGetValue(peer, out Queue<Packet>? q) && (q.Count >= MaxOutboxPerPeer || q.Any(x => x.Id == m.Id)))
                {
                    continue;
                }

                Enqueue(peer, new Packet
                {
                    Kind = PacketKind.Text,
                    Id = m.Id,
                    ConversationId = conv.Info.IsGroup ? conv.Info.Id : peer,
                    SentAtUnixMs = m.SentAtUnixMs,
                    Text = m.Text,
                    ReplyToId = m.ReplyToId,
                    IsViewOnce = m.IsViewOnce,
                }, events);
            }
        }
    }

    // ───────────────────────────── Réception ─────────────────────────────

    /// <summary>
    /// Traite un paquet reçu d'un pair AUTHENTIFIÉ. Revalide le paquet (défense en profondeur) puis l'autorise.
    /// Lève <see cref="ProtocolViolationException"/> si le pair viole le protocole.
    /// </summary>
    public void Receive(string senderPeerId, Packet packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (!InputValidator.IsValidPeerId(senderPeerId) || senderPeerId == _self)
        {
            throw new ProtocolViolationException("Expéditeur invalide.");
        }

        PacketSerializer.Validate(packet, Now());
        if (packet.Kind is PacketKind.ProfileUpdate or PacketKind.AttachmentChunk)
        {
            throw new ProtocolViolationException("Ce paquet de contrôle ne peut pas être traité comme un message de conversation.");
        }

        List<Action> events = new();
        lock (_lock)
        {
            if (packet.Kind == PacketKind.GroupCreate)
            {
                ReceiveGroupCreate(senderPeerId, packet, events);
            }
            else
            {
                Conv? conv = ResolveIncoming(senderPeerId, packet, createDirect: packet.Kind == PacketKind.Text, events);
                if (conv is not null)
                {
                    Dispatch(senderPeerId, conv, packet, events);
                }
            }
        }

        Fire(events);
    }

    private void Dispatch(string sender, Conv conv, Packet p, List<Action> events)
    {
        switch (p.Kind)
        {
            case PacketKind.Text:
                if (IndexOf(conv, p.Id) >= 0)
                {
                    // Doublon (retransmission) : idempotent, mais on ré-accuse pour que l'expéditeur voie « remis ».
                    SendReceipt(sender, conv.Info.Id, p.Id, ReceiptState.Delivered, events);
                    return;
                }

                conv.Messages.Add(new ChatMessage
                {
                    Id = p.Id,
                    ConversationId = conv.Info.Id,
                    SenderId = sender,
                    Text = p.Text!,
                    SentAtUnixMs = p.SentAtUnixMs,
                    IsOutgoing = false,
                    ReplyToId = p.ReplyToId,
                    IsViewOnce = p.IsViewOnce,
                });
                events.Add(Raise(ChatChangeKind.MessageAdded, conv.Info.Id, p.Id));
                SendReceipt(sender, conv.Info.Id, p.Id, ReceiptState.Delivered, events);
                break;

            case PacketKind.Receipt:
                ApplyReceipt(sender, conv, p, events);
                break;

            case PacketKind.Typing:
                events.Add(Raise(ChatChangeKind.Typing, conv.Info.Id, null, sender));
                break;

            case PacketKind.Reaction:
                Update(conv, p.TargetMessageId!, m => m.IsDeleted ? m : SetReaction(m, sender, p.Emoji!), events);
                break;

            case PacketKind.Delete:
                RequireAuthor(conv, p.TargetMessageId!, sender);
                Update(conv, p.TargetMessageId!, Tombstone, events);
                break;

            case PacketKind.Edit:
                RequireAuthor(conv, p.TargetMessageId!, sender);
                Update(conv, p.TargetMessageId!, m => m.IsDeleted ? m : m with { Text = p.Text!, IsEdited = true }, events);
                break;

            case PacketKind.ViewOnceOpened:
            {
                int index = IndexOf(conv, p.TargetMessageId!);
                if (conv.Info.IsGroup || index < 0)
                {
                    return;
                }

                ChatMessage message = conv.Messages[index];
                if (!message.IsOutgoing || !message.IsViewOnce || message.SenderId != _self)
                {
                    throw new ProtocolViolationException("Confirmation de vue unique pour un message non admissible.");
                }

                if (!message.IsViewOnceOpened)
                {
                    conv.Messages[index] = Tombstone(message) with { IsViewOnceOpened = true };
                    events.Add(Raise(ChatChangeKind.MessageUpdated, conv.Info.Id, message.Id));
                }
                break;
            }

            default:
                throw new ProtocolViolationException("Type de paquet inattendu.");
        }
    }

    private void ApplyReceipt(string sender, Conv conv, Packet p, List<Action> events)
    {
        int i = IndexOf(conv, p.TargetMessageId!);
        if (i < 0)
        {
            return; // message inconnu (course, ou déjà purgé) : on ignore, ce n'est pas une faute.
        }

        ChatMessage m = conv.Messages[i];
        if (!m.IsOutgoing || !m.Receipts.TryGetValue(sender, out MessageStatus current))
        {
            throw new ProtocolViolationException("Accusé de réception pour un message dont vous n'êtes pas destinataire.");
        }

        MessageStatus wanted = p.ReceiptState == ReceiptState.Read ? MessageStatus.Read : MessageStatus.Delivered;
        if (wanted <= current)
        {
            return; // jamais de régression (un « remis » tardif ne doit pas écraser « lu »).
        }

        conv.Messages[i] = SetReceipt(m, sender, wanted);
        events.Add(Raise(ChatChangeKind.MessageUpdated, conv.Info.Id, m.Id));
    }

    private void ReceiveGroupCreate(string sender, Packet p, List<Action> events)
    {
        string[] members = p.Members!;
        if (!members.Contains(sender, StringComparer.Ordinal) || !members.Contains(_self, StringComparer.Ordinal))
        {
            throw new ProtocolViolationException("Création de groupe incohérente (créateur ou destinataire absent de la liste).");
        }

        if (_convs.TryGetValue(p.ConversationId, out Conv? existing))
        {
            if (existing.Info.CreatorId != sender)
            {
                throw new ProtocolViolationException("Ce groupe existe déjà : tentative de détournement.");
            }

            return; // même créateur : retransmission, on ignore.
        }

        ConversationInfo info = new() { Id = p.ConversationId, IsGroup = true, Title = p.GroupName, Members = Array.AsReadOnly(members), CreatorId = sender };
        _convs[info.Id] = new Conv(info);
        events.Add(Raise(ChatChangeKind.ConversationAdded, info.Id));
    }

    private Conv? ResolveIncoming(string sender, Packet p, bool createDirect, List<Action> events)
    {
        if (InputValidator.IsValidPeerId(p.ConversationId))
        {
            if (p.ConversationId != _self)
            {
                throw new ProtocolViolationException("Paquet adressé à un autre pair.");
            }

            if (_convs.TryGetValue(sender, out Conv? direct))
            {
                return direct;
            }

            if (!createDirect)
            {
                return null; // contrôle (accusé, réaction…) sans conversation : rien à faire.
            }

            direct = new Conv(new ConversationInfo { Id = sender });
            _convs[sender] = direct;
            events.Add(Raise(ChatChangeKind.ConversationAdded, sender));
            return direct;
        }

        if (!_convs.TryGetValue(p.ConversationId, out Conv? group) || !group.Info.IsGroup)
        {
            throw new ProtocolViolationException("Groupe inconnu.");
        }

        if (!group.Info.Members.Contains(sender, StringComparer.Ordinal))
        {
            throw new ProtocolViolationException("L'expéditeur n'est pas membre du groupe.");
        }

        return group;
    }

    // ───────────────────────────── Aides internes (appelées SOUS le verrou) ─────────────────────────────

    private DateTimeOffset Now() => _clock.GetUtcNow();

    private static string NewId() => Guid.NewGuid().ToString("D");

    private Conv GetOrCreateOutgoingConv(string conversationId, List<Action> events)
    {
        if (_convs.TryGetValue(conversationId, out Conv? c))
        {
            return c;
        }

        if (!InputValidator.IsValidPeerId(conversationId) || conversationId == _self)
        {
            throw new ArgumentException("Conversation inconnue ou PeerId invalide.", nameof(conversationId));
        }

        c = new Conv(new ConversationInfo { Id = conversationId });
        _convs[conversationId] = c;
        events.Add(Raise(ChatChangeKind.ConversationAdded, conversationId));
        return c;
    }

    private Conv RequireConv(string conversationId) =>
        _convs.TryGetValue(conversationId, out Conv? c) ? c : throw new InvalidOperationException("Conversation inconnue.");

    private string[] Recipients(Conv conv) =>
        conv.Info.IsSelfConversation
            ? Array.Empty<string>()
            : conv.Info.IsGroup
            ? conv.Info.Members.Where(m => m != _self).ToArray()
            : new[] { conv.Info.Id };

    private static int IndexOf(Conv conv, string messageId) => conv.Messages.FindIndex(m => m.Id == messageId);

    /// <summary>Complète un paquet de contrôle : nouvel Id, date, et ConversationId tel que le DESTINATAIRE doit le lire.</summary>
    private Packet Control(ConversationInfo info, string recipient, Packet template) =>
        template with
        {
            Id = NewId(),
            SentAtUnixMs = Now().ToUnixTimeMilliseconds(),
            ConversationId = info.IsGroup ? info.Id : recipient,
        };

    private void BroadcastControl(Conv conv, Packet template, List<Action> events)
    {
        foreach (string r in Recipients(conv))
        {
            Enqueue(r, Control(conv.Info, r, template), events);
        }
    }

    private void SendReceipt(string to, string conversationId, string messageId, ReceiptState state, List<Action> events)
    {
        bool group = _convs.TryGetValue(conversationId, out Conv? c) && c.Info.IsGroup;
        Packet p = new()
        {
            Kind = PacketKind.Receipt,
            Id = NewId(),
            ConversationId = group ? conversationId : to,
            SentAtUnixMs = Now().ToUnixTimeMilliseconds(),
            TargetMessageId = messageId,
            ReceiptState = state,
        };
        Enqueue(to, p, events, bestEffortWhenFull: true);
    }

    private void EnsureOutboxRoom(string[] recipients, int needed)
    {
        foreach (string r in recipients)
        {
            if (_outbox.TryGetValue(r, out Queue<Packet>? q) && q.Count + needed > MaxOutboxPerPeer)
            {
                throw new InvalidOperationException("File d'attente pleine pour ce destinataire hors ligne.");
            }
        }
    }

    private void Enqueue(string peer, Packet p, List<Action> events, bool bestEffortWhenFull = false)
    {
        if (!_outbox.TryGetValue(peer, out Queue<Packet>? q))
        {
            q = new Queue<Packet>();
            _outbox[peer] = q;
        }

        if (q.Count >= MaxOutboxPerPeer)
        {
            if (bestEffortWhenFull)
            {
                return; // un accusé perdu est sans gravité ; on ne bloque pas la réception pour ça.
            }

            throw new InvalidOperationException("File d'attente pleine pour ce destinataire hors ligne.");
        }

        q.Enqueue(p);
        Flush(peer, events);
    }

    private void Flush(string peer, List<Action> events)
    {
        if (!_outbox.TryGetValue(peer, out Queue<Packet>? q))
        {
            return;
        }

        while (q.Count > 0)
        {
            Packet head = q.Peek();
            if (!_transport.TrySend(peer, head))
            {
                return; // toujours hors ligne : on garde l'ordre, on réessaiera.
            }

            _ = q.Dequeue();
            if (head.Kind == PacketKind.Text && _convs.TryGetValue(ConvKeyForOutgoing(head, peer), out Conv? conv))
            {
                int i = IndexOf(conv, head.Id);
                if (i >= 0 && conv.Messages[i].Receipts.TryGetValue(peer, out MessageStatus s) && s < MessageStatus.Sent)
                {
                    conv.Messages[i] = SetReceipt(conv.Messages[i], peer, MessageStatus.Sent);
                    events.Add(Raise(ChatChangeKind.MessageUpdated, conv.Info.Id, head.Id));
                }
            }
        }
    }

    // Pour un paquet sortant : en 1-à-1 la clé locale est le PeerId du destinataire ; en groupe, le GUID porté par le paquet.
    private static string ConvKeyForOutgoing(Packet p, string peer) => InputValidator.IsValidPeerId(p.ConversationId) ? peer : p.ConversationId;

    private void Update(Conv conv, string messageId, Func<ChatMessage, ChatMessage> change, List<Action> events)
    {
        int i = IndexOf(conv, messageId);
        if (i < 0)
        {
            return; // message inconnu : ignoré.
        }

        conv.Messages[i] = change(conv.Messages[i]);
        events.Add(Raise(ChatChangeKind.MessageUpdated, conv.Info.Id, messageId));
    }

    private static void RequireAuthor(Conv conv, string messageId, string sender)
    {
        int i = IndexOf(conv, messageId);
        if (i >= 0 && conv.Messages[i].SenderId != sender)
        {
            throw new ProtocolViolationException("Modification/suppression d'un message dont vous n'êtes pas l'auteur.");
        }
    }

    private static ChatMessage Tombstone(ChatMessage m) => m with { Text = string.Empty, IsDeleted = true, IsEdited = false, ReplyToId = null, Reactions = ReadOnlyDictionary<string, string>.Empty };

    private static ChatMessage SetReaction(ChatMessage m, string who, string emoji)
    {
        Dictionary<string, string> d = new(m.Reactions, StringComparer.Ordinal) { [who] = emoji };
        return m with { Reactions = new ReadOnlyDictionary<string, string>(d) };
    }

    private static ChatMessage SetReceipt(ChatMessage m, string who, MessageStatus status)
    {
        Dictionary<string, MessageStatus> d = new(m.Receipts, StringComparer.Ordinal) { [who] = status };
        return m with { Receipts = new ReadOnlyDictionary<string, MessageStatus>(d) };
    }

    private Action Raise(ChatChangeKind kind, string conversationId, string? messageId = null, string? peerId = null)
    {
        ChatChangedEventArgs args = new(kind, conversationId, messageId, peerId);
        return () => Changed?.Invoke(this, args);
    }

    private void Fire(List<Action> events)
    {
        try
        {
            foreach (Action a in events)
            {
                a();
            }
        }
        finally
        {
            // Même si un abonné de « Changed » lève une exception, la sauvegarde de l'état doit quand même être déclenchée :
            // l'état en mémoire a DÉJÀ changé, ne pas l'enregistrer perdrait des données.
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
