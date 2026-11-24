using System.Diagnostics.CodeAnalysis;
using System.Collections.ObjectModel;

namespace RedChat.Core;

// Persistance de l'état : photographie (ExportState) et restauration VALIDÉE (ImportState).
// SÉCURITÉ : le fichier d'historique est une entrée NON FIABLE. L'utilisateur peut l'avoir modifié à la main, un disque peut l'avoir
// corrompu, un autre programme peut l'avoir remplacé. Rien n'est donc recopié tel quel dans le service : chaque champ est revalidé
// avec les mêmes règles que pour un paquet reçu du réseau, et l'import est « tout ou rien » (aucun état partiel en cas de refus).
public sealed partial class ChatService
{
    /// <summary>Nombre maximal de conversations acceptées à la restauration (borne mémoire face à un fichier piégé).</summary>
    public const int MaxStoredConversations = 5_000;

    /// <summary>Nombre maximal de messages par conversation acceptés à la restauration.</summary>
    public const int MaxStoredMessagesPerConversation = 100_000;

    /// <summary>Photographie cohérente de l'état durable (conversations, messages, file d'attente). Prise sous le verrou, donc jamais « à moitié ».</summary>
    public ChatSnapshot ExportState()
    {
        lock (_lock)
        {
            return new ChatSnapshot
            {
                Conversations = _convs.Values
                    .Select(c => new ConversationSnapshot { Info = c.Info, Messages = c.Messages.ToArray() })
                    .ToArray(),
                Outbox = _outbox
                    .Where(kv => kv.Value.Count > 0)
                    .Select(kv => new OutboxSnapshot { PeerId = kv.Key, Packets = kv.Value.ToArray() })
                    .ToArray(),
            };
        }
    }

    /// <summary>
    /// Restaure un état enregistré. À appeler UNE fois, sur un service neuf, avant toute connexion.
    /// </summary>
    /// <exception cref="InvalidDataException">L'instantané est incohérent ou hostile : rien n'a été importé.</exception>
    /// <exception cref="InvalidOperationException">Le service contient déjà des données.</exception>
    public void ImportState(ChatSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        // 1) Tout est construit et validé HORS du service : si une seule règle échoue, le service reste vierge.
        Dictionary<string, Conv> convs = new(StringComparer.Ordinal);
        Dictionary<string, Queue<Packet>> outbox = new(StringComparer.Ordinal);
        BuildStoredState(snapshot, convs, outbox);

        // 2) Validation passée : on remplace l'état d'un seul geste, sous le verrou.
        lock (_lock)
        {
            if (_convs.Count != 0 || _outbox.Count != 0)
            {
                throw new InvalidOperationException("La restauration n'est possible que sur un service vide.");
            }

            foreach (KeyValuePair<string, Conv> kv in convs)
            {
                _convs[kv.Key] = kv.Value;
            }

            foreach (KeyValuePair<string, Queue<Packet>> kv in outbox)
            {
                _outbox[kv.Key] = kv.Value;
            }
        }
    }

    private void BuildStoredState(ChatSnapshot snapshot, Dictionary<string, Conv> convs, Dictionary<string, Queue<Packet>> outbox)
    {
        RequireStored(snapshot.Version == ChatSnapshot.CurrentVersion, "Version de fichier non prise en charge.");
        RequireStored(snapshot.Conversations is not null && snapshot.Outbox is not null, "Instantané incomplet.");
        RequireStored(snapshot.Conversations!.Count <= MaxStoredConversations, "Trop de conversations.");
        RequireStored(snapshot.Outbox!.Count <= MaxStoredConversations, "Trop de files d'attente.");

        // Dates : plafond ABSOLU (et non « maintenant + 1 jour ») : voir ProtocolLimits.StoredTimestampCeiling (horloge PC déréglée puis corrigée).
        long newestAllowedMs = ProtocolLimits.StoredTimestampCeiling.ToUnixTimeMilliseconds();
        DateTimeOffset packetClock = ProtocolLimits.StoredTimestampCeiling - ProtocolLimits.MaxFutureSkew;

        foreach (ConversationSnapshot cs in snapshot.Conversations)
        {
            Conv conv = BuildStoredConversation(cs, newestAllowedMs);
            RequireStored(convs.TryAdd(conv.Info.Id, conv), "Conversation en double.");
        }

        foreach (OutboxSnapshot os in snapshot.Outbox)
        {
            RequireStored(os is not null && os.Packets is not null, "File d'attente vide.");
            RequireStored(InputValidator.IsValidPeerId(os!.PeerId) && os.PeerId != _self, "Destinataire de file d'attente invalide.");
            RequireStored(os.Packets.Count <= MaxOutboxPerPeer, "File d'attente trop longue.");
            Queue<Packet> q = new();
            foreach (Packet p in os.Packets)
            {
                RequireStored(p is not null, "Paquet vide.");
                try
                {
                    PacketSerializer.Validate(p!, packetClock);
                }
                catch (ProtocolViolationException ex)
                {
                    throw new InvalidDataException("Paquet en attente invalide : " + ex.Message, ex);
                }

                q.Enqueue(p!);
            }

            RequireStored(outbox.TryAdd(os.PeerId, q), "File d'attente en double.");
        }
    }

    private Conv BuildStoredConversation(ConversationSnapshot? cs, long newestAllowedMs)
    {
        RequireStored(cs is not null && cs.Info is not null && cs.Messages is not null, "Conversation incomplète.");
        ConversationInfo info = cs!.Info;
        RequireStored(info.Id is not null && info.Members is not null, "Description de conversation incomplète.");

        if (info.IsSelfConversation)
        {
            RequireStored(!info.IsGroup && info.Id == _self, "Conversation personnelle invalide.");
            RequireStored(info.Title is null && info.Members.Count == 0 && info.CreatorId is null, "Conversation personnelle avec des champs de groupe.");
        }
        else if (info.IsGroup)
        {
            RequireStored(InputValidator.IsValidGuid(info.Id), "Identifiant de groupe invalide.");
            RequireStored(
                info.Title is not null && InputValidator.TryNormalizeName(info.Title, ProtocolLimits.MaxGroupNameChars, out string normalized) && normalized == info.Title,
                "Nom de groupe invalide.");
            RequireStored(info.Members.Count is >= 2 and <= ProtocolLimits.MaxGroupMembers, "Nombre de membres invalide.");
            RequireStored(info.Members.All(InputValidator.IsValidPeerId), "Membre invalide.");
            RequireStored(info.Members.Distinct(StringComparer.Ordinal).Count() == info.Members.Count, "Membres en double.");
            RequireStored(info.Members.Contains(_self, StringComparer.Ordinal), "Le groupe ne nous contient pas.");
            RequireStored(info.CreatorId is not null && info.Members.Contains(info.CreatorId, StringComparer.Ordinal), "Créateur de groupe invalide.");
        }
        else
        {
            RequireStored(InputValidator.IsValidPeerId(info.Id) && info.Id != _self, "Interlocuteur invalide.");
            RequireStored(info.Title is null && info.Members.Count == 0 && info.CreatorId is null, "Conversation 1-à-1 avec des champs de groupe.");
        }

        // Copie défensive : on ne garde jamais les collections de l'instantané (elles pourraient être modifiées après coup par l'appelant).
        ConversationInfo cleanInfo = info with { Members = Array.AsReadOnly(info.Members.ToArray()) };
        Conv conv = new(cleanInfo);
        string[] recipients = Recipients(conv);
        string[] participants = cleanInfo.IsGroup ? cleanInfo.Members.ToArray() : new[] { _self, cleanInfo.Id };

        RequireStored(cs.Messages.Count <= MaxStoredMessagesPerConversation, "Trop de messages dans une conversation.");
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (ChatMessage? m in cs.Messages)
        {
            RequireStored(m is not null, "Message vide.");
            conv.Messages.Add(BuildStoredMessage(m!, cleanInfo, recipients, participants, seen, newestAllowedMs));
        }

        return conv;
    }

    private ChatMessage BuildStoredMessage(ChatMessage m, ConversationInfo info, string[] recipients, string[] participants, HashSet<string> seen, long newestAllowedMs)
    {
        RequireStored(m.Id is not null && InputValidator.IsValidGuid(m.Id) && seen.Add(m.Id), "Identifiant de message invalide ou en double.");
        RequireStored(m.ConversationId == info.Id, "Message rangé dans la mauvaise conversation.");
        RequireStored(m.SenderId is not null && InputValidator.IsValidPeerId(m.SenderId), "Auteur invalide.");
        RequireStored(m.SentAtUnixMs > 0 && m.SentAtUnixMs <= newestAllowedMs, "Date de message invalide.");
        RequireStored(m.ReplyToId is null || InputValidator.IsValidGuid(m.ReplyToId), "Réponse invalide.");
        RequireStored(m.Text is not null && (m.IsDeleted ? m.Text.Length == 0 : InputValidator.IsValidMessageText(m.Text)), "Texte invalide.");
        RequireStored(!m.IsViewOnce || !info.IsGroup && !info.IsSelfConversation, "Message à vue unique dans une conversation invalide.");
        RequireStored(!m.IsViewOnceOpened || m.IsOutgoing && m.IsViewOnce && m.IsDeleted, "État d'ouverture de vue unique invalide.");
        RequireStored(m.Reactions is not null && m.Receipts is not null, "Message incomplet.");

        if (m.IsOutgoing)
        {
            RequireStored(m.SenderId == _self, "Message sortant d'un autre auteur.");
            RequireStored(
                m.Receipts.Count == recipients.Length && recipients.All(r => m.Receipts.ContainsKey(r)),
                "Accusés incohérents avec les destinataires.");
            RequireStored(m.Receipts.Values.All(v => Enum.IsDefined(v)), "État d'accusé inconnu.");
        }
        else
        {
            RequireStored(m.SenderId != _self, "Message entrant venant de nous-mêmes.");
            RequireStored(info.IsGroup ? info.Members.Contains(m.SenderId, StringComparer.Ordinal) : m.SenderId == info.Id, "Auteur étranger à la conversation.");
            RequireStored(m.Receipts.Count == 0, "Accusés sur un message entrant.");
        }

        if (m.IsDeleted)
        {
            RequireStored(m.Reactions.Count == 0 && m.ReplyToId is null && !m.IsEdited, "Message supprimé avec du contenu résiduel.");
        }

        RequireStored(m.Reactions.Count <= ProtocolLimits.MaxGroupMembers, "Trop de réactions.");
        foreach (KeyValuePair<string, string> r in m.Reactions)
        {
            RequireStored(participants.Contains(r.Key, StringComparer.Ordinal) && InputValidator.IsValidReaction(r.Value), "Réaction invalide.");
        }

        // Dictionnaires reconstruits : instances propres, véritablement non modifiables (celles désérialisées sont des Dictionary<> ordinaires).
        return m with
        {
            Reactions = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(m.Reactions, StringComparer.Ordinal)),
            Receipts = new ReadOnlyDictionary<string, MessageStatus>(new Dictionary<string, MessageStatus>(m.Receipts, StringComparer.Ordinal)),
        };
    }

    private static void RequireStored([DoesNotReturnIf(false)] bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidDataException("Historique enregistré invalide : " + message);
        }
    }
}
