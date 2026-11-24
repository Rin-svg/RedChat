using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace RedChat.Core;

/// <summary>Avancement d'un message sortant pour UN destinataire (l'ordre des valeurs compte : on ne fait que monter).</summary>
public enum MessageStatus
{
    /// <summary>Pas encore remis à la connexion réseau (destinataire hors ligne : message dans la file d'attente).</summary>
    Pending = 0,

    /// <summary>Transmis au canal chiffré du destinataire.</summary>
    Sent = 1,

    /// <summary>Arrivé sur l'appareil du destinataire.</summary>
    Delivered = 2,

    /// <summary>Lu par le destinataire.</summary>
    Read = 3,
}

/// <summary>Nature du changement signalé à l'interface.</summary>
public enum ChatChangeKind
{
    /// <summary>Nouvelle conversation (ex. groupe créé par un autre).</summary>
    ConversationAdded,

    /// <summary>Nouveau message (envoyé ou reçu).</summary>
    MessageAdded,

    /// <summary>Message modifié : accusé, réaction, édition, suppression, lecture.</summary>
    MessageUpdated,

    /// <summary>Un interlocuteur est en train d'écrire.</summary>
    Typing,

    /// <summary>Une conversation a été supprimée de cet appareil.</summary>
    ConversationRemoved,
}

/// <summary>Données de l'événement <see cref="ChatService.Changed"/>.</summary>
public sealed class ChatChangedEventArgs : EventArgs
{
    /// <summary>Crée les données d'événement.</summary>
    public ChatChangedEventArgs(ChatChangeKind kind, string conversationId, string? messageId = null, string? peerId = null)
    {
        Kind = kind;
        ConversationId = conversationId;
        MessageId = messageId;
        PeerId = peerId;
    }

    /// <summary>Nature du changement.</summary>
    public ChatChangeKind Kind { get; }

    /// <summary>Conversation concernée.</summary>
    public string ConversationId { get; }

    /// <summary>Message concerné, le cas échéant.</summary>
    public string? MessageId { get; }

    /// <summary>Pair à l'origine du changement (indicateur « écrit… »).</summary>
    public string? PeerId { get; }
}

/// <summary>
/// Message de conversation. Enregistrement IMMUABLE : chaque changement (accusé, édition…) remplace l'instance dans le service,
/// donc l'interface peut lire une instance obtenue plus tôt sans risque de lecture à moitié modifiée entre deux threads.
/// </summary>
public sealed record ChatMessage
{
    /// <summary>Identifiant du message (GUID).</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Clé de la conversation (PeerId de l'interlocuteur en 1-à-1, GUID du groupe sinon).</summary>
    public string ConversationId { get; init; } = string.Empty;

    /// <summary>PeerId de l'auteur.</summary>
    public string SenderId { get; init; } = string.Empty;

    /// <summary>Texte (vide si supprimé).</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Date d'envoi (ms Unix UTC, horloge de l'expéditeur).</summary>
    public long SentAtUnixMs { get; init; }

    /// <summary>Vrai si le message vient de nous.</summary>
    public bool IsOutgoing { get; init; }

    /// <summary>Message auquel celui-ci répond.</summary>
    public string? ReplyToId { get; init; }

    /// <summary>Vrai si le texte a été modifié par son auteur.</summary>
    public bool IsEdited { get; init; }

    /// <summary>Vrai si l'auteur l'a supprimé pour tous.</summary>
    public bool IsDeleted { get; init; }

    /// <summary>Message entrant : vrai une fois lu localement (accusé « lu » envoyé).</summary>
    public bool IsReadByMe { get; init; }

    /// <summary>Réactions : PeerId → emoji (une réaction par personne).</summary>
    public IReadOnlyDictionary<string, string> Reactions { get; init; } = ReadOnlyDictionary<string, string>.Empty;

    /// <summary>Message sortant : avancement par destinataire.</summary>
    public IReadOnlyDictionary<string, MessageStatus> Receipts { get; init; } = ReadOnlyDictionary<string, MessageStatus>.Empty;

    /// <summary>Avancement global d'un message sortant : le plus bas de tous les destinataires (groupe : tous doivent l'avoir).</summary>
    [JsonIgnore] // propriété calculée à partir de Receipts : jamais écrite, et ignorée si un fichier en contient une (elle ne peut donc pas être falsifiée).
    public MessageStatus Status => Receipts.Count == 0 ? MessageStatus.Pending : Receipts.Values.Min();
}

/// <summary>Description d'une conversation (1-à-1 ou groupe).</summary>
public sealed record ConversationInfo
{
    /// <summary>PeerId de l'interlocuteur (1-à-1) ou GUID du groupe.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Conversation personnelle locale, sans destinataire réseau.</summary>
    public bool IsSelfConversation { get; init; }

    /// <summary>Vrai pour un groupe.</summary>
    public bool IsGroup { get; init; }

    /// <summary>Nom du groupe (null en 1-à-1 : l'interface affiche le nom du contact).</summary>
    public string? Title { get; init; }

    /// <summary>Membres, nous compris (groupe) ou vide (1-à-1).</summary>
    public IReadOnlyList<string> Members { get; init; } = Array.Empty<string>();

    /// <summary>Créateur du groupe (null en 1-à-1).</summary>
    public string? CreatorId { get; init; }
}

/// <summary>Sortie réseau vers un pair. Implémentée par l'Infrastructure (TCP + canal chiffré).</summary>
public interface ITransport
{
    /// <summary>
    /// Tente de transmettre le paquet au pair via une connexion ouverte. Renvoie faux s'il n'y a pas de connexion
    /// (le service mettra alors le paquet en file d'attente). NE DOIT PAS BLOQUER : l'appel se fait sous le verrou du service.
    /// </summary>
    bool TrySend(string peerId, Packet packet);
}
