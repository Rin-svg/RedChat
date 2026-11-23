namespace RedChat.Core;

/// <summary>Une conversation et ses messages, tels qu'on les écrit sur disque.</summary>
public sealed record ConversationSnapshot
{
    /// <summary>Description de la conversation (1-à-1 ou groupe).</summary>
    public ConversationInfo Info { get; init; } = new();

    /// <summary>Messages, dans l'ordre d'arrivée.</summary>
    public IReadOnlyList<ChatMessage> Messages { get; init; } = Array.Empty<ChatMessage>();
}

/// <summary>Paquets encore à remettre à un pair hors ligne (dans l'ordre d'envoi).</summary>
public sealed record OutboxSnapshot
{
    /// <summary>Destinataire.</summary>
    public string PeerId { get; init; } = string.Empty;

    /// <summary>Paquets en attente (messages, accusés, réactions…).</summary>
    public IReadOnlyList<Packet> Packets { get; init; } = Array.Empty<Packet>();
}

/// <summary>
/// Photographie complète de l'état de <see cref="ChatService"/> : historique + file d'attente hors ligne.
/// Les deux sont enregistrés ENSEMBLE dans le même instantané, pour qu'un message « en attente » ne puisse jamais
/// exister sans son paquet (ni l'inverse) après un redémarrage.
/// </summary>
public sealed record ChatSnapshot
{
    /// <summary>Version du format de stockage (permet de migrer plus tard).</summary>
    public const int CurrentVersion = 1;

    /// <summary>Version du format de ce document.</summary>
    public int Version { get; init; } = CurrentVersion;

    /// <summary>Conversations et historiques.</summary>
    public IReadOnlyList<ConversationSnapshot> Conversations { get; init; } = Array.Empty<ConversationSnapshot>();

    /// <summary>Files d'attente hors ligne, une entrée par pair.</summary>
    public IReadOnlyList<OutboxSnapshot> Outbox { get; init; } = Array.Empty<OutboxSnapshot>();
}

/// <summary>
/// Stockage durable de l'historique. Implémentée par l'Infrastructure (fichier protégé, écriture atomique).
/// Le Core ne sait pas où ni comment les données sont écrites : il est donc testable sans disque.
/// </summary>
public interface IMessageStore
{
    /// <summary>Charge l'instantané ; <c>null</c> si rien n'a encore été enregistré (ou si le contenu était illisible et a été mis de côté).</summary>
    ChatSnapshot? Load();

    /// <summary>Enregistre l'instantané de façon atomique : en cas d'échec, l'ancienne version reste intacte.</summary>
    void Save(ChatSnapshot snapshot);
}
