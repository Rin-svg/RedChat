namespace RedChat.Core;

/// <summary>
/// Toutes les limites du protocole au même endroit. Ce sont ces valeurs qu'un pair hostile cherche à dépasser :
/// les nommer permet de les tester (valeur max, max+1) et de les ajuster sans chasser des nombres magiques.
/// </summary>
public static class ProtocolLimits
{
    /// <summary>Version du protocole annoncée dans le handshake.</summary>
    public const byte ProtocolVersion = 6;

    /// <summary>Taille maximale d'une trame sur le fil (768 Kio), avatar public encodé en base64 inclus. Au-delà : connexion coupée, sans allouer.</summary>
    public const int MaxFrameBytes = 768 * 1024;

    /// <summary>Taille maximale d'un champ binaire du handshake (clés publiques, signatures).</summary>
    public const int MaxHandshakeFieldBytes = 512;

    /// <summary>Taille de l'aléa (nonce) de handshake.</summary>
    public const int HandshakeNonceBytes = 32;

    /// <summary>Longueur d'une signature ECDSA P-256 au format fixe IEEE P1363 (r||s).</summary>
    public const int SignatureBytes = 64;

    /// <summary>Taille d'une clé AES-256.</summary>
    public const int AesKeyBytes = 32;

    /// <summary>Taille du nonce AES-GCM (96 bits).</summary>
    public const int AesNonceBytes = 12;

    /// <summary>Taille du tag d'authentification AES-GCM (128 bits).</summary>
    public const int AesTagBytes = 16;

    /// <summary>Taille de l'en-tête d'un message scellé : le compteur (8 octets).</summary>
    public const int CounterBytes = 8;

    /// <summary>Surcoût d'un message scellé : compteur + tag.</summary>
    public const int SealOverheadBytes = CounterBytes + AesTagBytes;

    /// <summary>Taille maximale d’un fichier transféré.</summary>
    public const int MaxAttachmentBytes = 25 * 1024 * 1024;

    /// <summary>Taille maximale d’un bloc de fichier (la marge permet l’encodage JSON/base64 dans une trame).</summary>
    public const int AttachmentChunkBytes = 384 * 1024;

    /// <summary>Taille maximale d’un bloc de média de statut, encodé en base64 dans une trame JSON.</summary>
    public const int StatusMediaChunkBytes = 384 * 1024;

    /// <summary>Longueur maximale du nom de média d’un statut.</summary>
    public const int MaxStatusMediaNameChars = 180;

    /// <summary>Longueur maximale du nom de fichier transmis.</summary>
    public const int MaxAttachmentFileNameChars = 180;

    /// <summary>Longueur maximale d'une description SDP d'appel (en caractères).</summary>
    public const int MaxCallSdpChars = 256 * 1024;

    /// <summary>Longueur maximale d'un candidat ICE (en caractères).</summary>
    public const int MaxIceCandidateChars = 4096;

    /// <summary>Longueur maximale de l'identifiant MID ICE (en caractères).</summary>
    public const int MaxIceMidChars = 256;

    /// <summary>Longueur maximale d'un message texte (en caractères UTF-16).</summary>
    public const int MaxTextChars = 8192;

    /// <summary>Longueur maximale d'un nom d'affichage.</summary>
    public const int MaxDisplayNameChars = 40;

    /// <summary>Longueur maximale d'un nom de groupe.</summary>
    public const int MaxGroupNameChars = 60;

    /// <summary>Nombre maximal de membres d'un groupe (créateur inclus).</summary>
    public const int MaxGroupMembers = 256;

    /// <summary>Longueur maximale d'une réaction (emoji, éventuellement composé).</summary>
    public const int MaxReactionChars = 16;

    /// <summary>Longueur maximale d'un nom de profil (argument --profile).</summary>
    public const int MaxProfileNameChars = 30;

    /// <summary>Profondeur JSON maximale acceptée à la réception.</summary>
    public const int MaxJsonDepth = 8;

    /// <summary>Port minimal accepté pour une connexion manuelle (on exclut les ports système).</summary>
    public const int MinPort = 1024;

    /// <summary>Durée maximale d'un handshake avant abandon.</summary>
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Décalage d'horloge maximal toléré pour la date d'envoi d'un paquet (dans le futur).</summary>
    public static readonly TimeSpan MaxFutureSkew = TimeSpan.FromDays(1);

    /// <summary>
    /// Plafond absolu des dates dans un fichier enregistré (année 9000). Contrairement à un paquet venant du réseau, un fichier écrit par
    /// l'application elle-même peut légitimement contenir des dates « dans le futur » : si l'horloge du PC est déréglée puis remise à l'heure
    /// (pile CMOS, réglage manuel), tout l'historique serait sinon refusé. On borne seulement l'absurde.
    /// </summary>
    public static readonly DateTimeOffset StoredTimestampCeiling = new(9000, 1, 1, 0, 0, 0, TimeSpan.Zero);
}
