using System.Text.Json.Serialization;

namespace RedChat.Core;

/// <summary>Type de paquet applicatif échangé dans le canal chiffré.</summary>
public enum PacketKind
{
    /// <summary>Message texte (éventuellement en réponse à un autre).</summary>
    Text = 1,

    /// <summary>Accusé : message remis ou lu.</summary>
    Receipt = 2,

    /// <summary>Indicateur « est en train d'écrire ».</summary>
    Typing = 3,

    /// <summary>Création d'un groupe, diffusée par le créateur à chaque membre.</summary>
    GroupCreate = 4,

    /// <summary>Réaction (emoji) à un message.</summary>
    Reaction = 5,

    /// <summary>Suppression d'un message pour tous (par son auteur).</summary>
    Delete = 6,

    /// <summary>Modification du texte d'un message (par son auteur).</summary>
    Edit = 7,

    /// <summary>Publication du profil personnel par le pair authentifié.</summary>
    ProfileUpdate = 8,

    /// <summary>Bloc d’un transfert de pièce jointe, sur le canal chiffré authentifié.</summary>
    AttachmentChunk = 9,

    /// <summary>Signalisation d’appel privé (invitation, acceptation, refus, fin).</summary>
    CallControl = 10,

    /// <summary>Métadonnées d’une publication de statut éphémère.</summary>
    StatusPublish = 11,

    /// <summary>Bloc binaire d’un média de statut.</summary>
    StatusMediaChunk = 12,

    /// <summary>Confirmation de consultation d’un statut.</summary>
    StatusViewReceipt = 13,

    /// <summary>Retrait d’un statut par son auteur.</summary>
    StatusDelete = 14,

    /// <summary>Confirmation d’ouverture d’un message en vue unique.</summary>
    ViewOnceOpened = 15,

    /// <summary>Confirmation d’ouverture d’une pièce jointe en vue unique.</summary>
    AttachmentViewOnceOpened = 16,
}

/// <summary>Action de signalisation utilisée pour gérer le cycle de vie d’un appel.</summary>
public enum CallControlAction
{
    /// <summary>Demande de démarrage d’un appel.</summary>
    Invite = 1,
    /// <summary>Invitation acceptée.</summary>
    Accept = 2,
    /// <summary>Invitation refusée.</summary>
    Reject = 3,
    /// <summary>Appel terminé ou annulé.</summary>
    End = 4,
    /// <summary>Le destinataire est déjà occupé.</summary>
    Busy = 5,
    /// <summary>Description de session SDP proposée par l’initiateur.</summary>
    Offer = 6,
    /// <summary>Description de session SDP de réponse.</summary>
    Answer = 7,
    /// <summary>Candidat ICE découvert pendant la négociation.</summary>
    IceCandidate = 8,
}

/// <summary>Type d’appel demandé.</summary>
public enum CallMediaKind
{
    /// <summary>Appel audio.</summary>
    Audio = 1,
    /// <summary>Appel vidéo.</summary>
    Video = 2,
}

/// <summary>État de l’accusé de réception d’un message.</summary>
public enum ReceiptState
{
    /// <summary>Le message est arrivé sur l'appareil du destinataire.</summary>
    Delivered = 1,

    /// <summary>Le destinataire a ouvert la conversation et lu le message.</summary>
    Read = 2,
}

/// <summary>
/// Paquet applicatif. Un seul type « à plat » (champs facultatifs selon <see cref="Kind"/>) : simple à sérialiser et à valider
/// champ par champ. Les règles par type sont dans <see cref="PacketSerializer.Validate"/>.
/// </summary>
public sealed record Packet
{
    /// <summary>Type du paquet.</summary>
    public PacketKind Kind { get; init; }

    /// <summary>Identifiant unique du paquet/message (GUID « D »).</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Conversation concernée : PeerId de l'interlocuteur (1-à-1) ou GUID du groupe.</summary>
    public string ConversationId { get; init; } = string.Empty;

    /// <summary>Date d'envoi, en millisecondes Unix UTC (celle de l'expéditeur).</summary>
    public long SentAtUnixMs { get; init; }

    /// <summary>Texte (Text, Edit).</summary>
    public string? Text { get; init; }

    /// <summary>Vrai pour un message texte à consulter une seule fois.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsViewOnce { get; init; }

    /// <summary>Message auquel on répond (Text).</summary>
    public string? ReplyToId { get; init; }

    /// <summary>Message visé (Receipt, Reaction, Delete, Edit).</summary>
    public string? TargetMessageId { get; init; }

    /// <summary>État de l'accusé (Receipt).</summary>
    public ReceiptState? ReceiptState { get; init; }

    /// <summary>Nom du groupe (GroupCreate).</summary>
    public string? GroupName { get; init; }

    /// <summary>PeerId des membres du groupe, créateur inclus (GroupCreate).</summary>
    public string[]? Members { get; init; }

    /// <summary>Emoji de la réaction (Reaction).</summary>
    public string? Emoji { get; init; }

    /// <summary>Identité annoncée par un paquet ProfileUpdate (doit être l'expéditeur authentifié).</summary>
    public string? ProfilePeerId { get; init; }

    /// <summary>Nom public annoncé.</summary>
    public string? ProfileDisplayName { get; init; }

    /// <summary>Statut public annoncé.</summary>
    public string? ProfileStatus { get; init; }

    /// <summary>Description publique annoncée.</summary>
    public string? ProfileAbout { get; init; }

    /// <summary>Avatar PNG/JPEG annoncé (512 Kio maximum).</summary>
    public byte[]? ProfileAvatarBytes { get; init; }

    /// <summary>Identifiant du transfert de fichier (AttachmentChunk).</summary>
    public string? AttachmentTransferId { get; init; }

    /// <summary>Vrai si la pièce jointe doit être ouverte puis supprimée après une seule consultation.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool AttachmentIsViewOnce { get; init; }

    /// <summary>Nom de fichier sans chemin (AttachmentChunk).</summary>
    public string? AttachmentFileName { get; init; }

    /// <summary>Taille totale du fichier en octets (AttachmentChunk).</summary>
    public long? AttachmentFileSize { get; init; }

    /// <summary>Empreinte SHA-256 hexadécimale du fichier complet (AttachmentChunk).</summary>
    public string? AttachmentSha256 { get; init; }

    /// <summary>Index du bloc, à partir de zéro (AttachmentChunk).</summary>
    public int? AttachmentChunkIndex { get; init; }

    /// <summary>Nombre total de blocs (AttachmentChunk).</summary>
    public int? AttachmentChunkCount { get; init; }

    /// <summary>Contenu binaire du bloc (AttachmentChunk).</summary>
    public byte[]? AttachmentChunkData { get; init; }

    /// <summary>Identifiant de session d’appel (CallControl).</summary>
    public string? CallId { get; init; }

    /// <summary>Action de signalisation (CallControl).</summary>
    public CallControlAction? CallAction { get; init; }

    /// <summary>Mode audio ou vidéo (CallControl).</summary>
    public CallMediaKind? CallMedia { get; init; }

    /// <summary>Contenu SDP ou candidat ICE (CallControl), limité par le protocole.</summary>
    public string? CallSignalData { get; init; }

    /// <summary>Identifiant de flux média associé à un candidat ICE.</summary>
    public string? CallSignalMid { get; init; }

    /// <summary>Index de ligne média associé à un candidat ICE.</summary>
    public int? CallSignalMLineIndex { get; init; }

    /// <summary>Identifiant de la publication de statut (StatusPublish/StatusMediaChunk).</summary>
    public string? StatusId { get; init; }

    /// <summary>Type du contenu du statut (StatusPublish).</summary>
    public StatusContentKind? StatusContentKind { get; init; }

    /// <summary>Texte ou légende du statut (StatusPublish).</summary>
    public string? StatusText { get; init; }

    /// <summary>Nom du média, sans chemin (StatusPublish).</summary>
    public string? StatusMediaName { get; init; }

    /// <summary>Taille totale du média en octets (StatusPublish/StatusMediaChunk).</summary>
    public long? StatusMediaSize { get; init; }

    /// <summary>Empreinte SHA-256 hexadécimale du média complet.</summary>
    public string? StatusMediaSha256 { get; init; }

    /// <summary>Date de création UTC en millisecondes Unix (StatusPublish).</summary>
    public long? StatusCreatedAtUnixMs { get; init; }

    /// <summary>Date d’expiration UTC en millisecondes Unix (StatusPublish).</summary>
    public long? StatusExpiresAtUnixMs { get; init; }

    /// <summary>Champ réservé : doit rester absent du paquet réseau afin de protéger la confidentialité de l'audience.</summary>
    public StatusAudienceMode? StatusAudienceMode { get; init; }

    /// <summary>Champ réservé : la liste privée des contacts ne doit jamais être transmise aux destinataires.</summary>
    public string[]? StatusAudiencePeerIds { get; init; }

    /// <summary>Index du bloc média de statut (à partir de zéro).</summary>
    public int? StatusChunkIndex { get; init; }

    /// <summary>Nombre total de blocs du média de statut.</summary>
    public int? StatusChunkCount { get; init; }

    /// <summary>Octets du bloc média de statut.</summary>
    public byte[]? StatusChunkData { get; init; }
}
