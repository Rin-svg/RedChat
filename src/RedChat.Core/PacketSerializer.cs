using System.Text.Json;
using System.Text.Json.Serialization;

namespace RedChat.Core;

/// <summary>
/// Sérialisation JSON et validation des paquets. Tout paquet reçu passe par <see cref="Deserialize"/> :
/// JSON strict (profondeur bornée, champs inconnus refusés, énumérations par nom uniquement) PUIS validation métier.
/// </summary>
public static class PacketSerializer
{
    // Options mises en cache (les recréer à chaque appel est coûteux et signalé par l'analyseur CA1869).
    private static readonly HashSet<string> ReservedAttachmentNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "CLOCK$", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = ProtocolLimits.MaxJsonDepth,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    /// <summary>Sérialise un paquet en UTF-8 après l'avoir validé (on ne produit jamais un paquet que le pair refuserait).</summary>
    public static byte[] Serialize(Packet packet, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(packet);
        Validate(packet, now);
        return JsonSerializer.SerializeToUtf8Bytes(packet, Options);
    }

    /// <summary>Désérialise et valide un paquet reçu. Lève <see cref="ProtocolViolationException"/> pour toute anomalie.</summary>
    public static Packet Deserialize(ReadOnlySpan<byte> utf8Json, DateTimeOffset now)
    {
        if (utf8Json.IsEmpty || utf8Json.Length > ProtocolLimits.MaxFrameBytes)
        {
            throw new ProtocolViolationException("Taille de paquet invalide.");
        }

        Packet? packet;
        try
        {
            packet = JsonSerializer.Deserialize<Packet>(utf8Json, Options);
        }
        catch (JsonException ex)
        {
            throw new ProtocolViolationException("JSON invalide.", ex);
        }

        if (packet is null)
        {
            throw new ProtocolViolationException("Paquet vide.");
        }

        Validate(packet, now);
        return packet;
    }

    /// <summary>Applique les règles par type de paquet (liste blanche). Lève <see cref="ProtocolViolationException"/> si une règle est violée.</summary>
    public static void Validate(Packet p, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(p);
        Require(Enum.IsDefined(p.Kind), "Type de paquet inconnu.");
        Require(InputValidator.IsValidGuid(p.Id), "Identifiant de paquet invalide.");
        Require(p.SentAtUnixMs > 0, "Date d'envoi invalide.");
        Require(p.SentAtUnixMs <= (now + ProtocolLimits.MaxFutureSkew).ToUnixTimeMilliseconds(), "Date d'envoi dans le futur.");
        Require(InputValidator.IsValidPeerId(p.ConversationId) || InputValidator.IsValidGuid(p.ConversationId), "Conversation invalide.");

        switch (p.Kind)
        {
            case PacketKind.Text:
                Require(InputValidator.IsValidMessageText(p.Text), "Texte invalide.");
                Require(p.ReplyToId is null || InputValidator.IsValidGuid(p.ReplyToId), "Réponse invalide.");
                Require(!p.IsViewOnce || InputValidator.IsValidPeerId(p.ConversationId), "La vue unique est réservée aux conversations privées.");
                break;

            case PacketKind.Receipt:
                Require(InputValidator.IsValidGuid(p.TargetMessageId), "Message visé invalide.");
                Require(p.ReceiptState is not null && Enum.IsDefined(p.ReceiptState.Value), "État d'accusé invalide.");
                break;

            case PacketKind.Typing:
                break;

            case PacketKind.GroupCreate:
                Require(InputValidator.IsValidGuid(p.ConversationId), "Identifiant de groupe invalide.");
                Require(p.GroupName is not null && InputValidator.TryNormalizeName(p.GroupName, ProtocolLimits.MaxGroupNameChars, out string normalized) && normalized == p.GroupName,
                    "Nom de groupe invalide.");
                Require(p.Members is { Length: >= 2 and <= ProtocolLimits.MaxGroupMembers }, "Nombre de membres invalide.");
                Require(p.Members!.All(InputValidator.IsValidPeerId), "Membre invalide.");
                Require(p.Members!.Distinct(StringComparer.Ordinal).Count() == p.Members!.Length, "Membres en double.");
                break;

            case PacketKind.Reaction:
                Require(InputValidator.IsValidGuid(p.TargetMessageId), "Message visé invalide.");
                Require(InputValidator.IsValidReaction(p.Emoji), "Réaction invalide.");
                break;

            case PacketKind.Delete:
                Require(InputValidator.IsValidGuid(p.TargetMessageId), "Message visé invalide.");
                break;

            case PacketKind.Edit:
                Require(InputValidator.IsValidGuid(p.TargetMessageId), "Message visé invalide.");
                Require(InputValidator.IsValidMessageText(p.Text), "Texte invalide.");
                break;

            case PacketKind.ViewOnceOpened:
                Require(InputValidator.IsValidPeerId(p.ConversationId), "Une confirmation de vue unique doit viser un pair privé.");
                Require(InputValidator.IsValidGuid(p.TargetMessageId), "Message visé invalide.");
                break;

            case PacketKind.AttachmentViewOnceOpened:
                Require(InputValidator.IsValidPeerId(p.ConversationId), "Une confirmation de pièce jointe doit viser un pair privé.");
                Require(p.AttachmentTransferId is not null && InputValidator.IsValidGuid(p.AttachmentTransferId), "Identifiant de transfert invalide.");
                break;

            case PacketKind.AttachmentChunk:
                ValidateAttachmentChunk(p);
                break;

            case PacketKind.CallControl:
                Require(InputValidator.IsValidPeerId(p.ConversationId), "Un appel doit viser un pair privé.");
                Require(InputValidator.IsValidGuid(p.CallId), "Identifiant d’appel invalide.");
                Require(p.CallAction is not null && Enum.IsDefined(p.CallAction.Value), "Action d’appel invalide.");
                Require(p.CallMedia is not null && Enum.IsDefined(p.CallMedia.Value), "Mode d’appel invalide.");
                switch (p.CallAction)
                {
                    case CallControlAction.Invite:
                    case CallControlAction.Accept:
                    case CallControlAction.Reject:
                    case CallControlAction.End:
                    case CallControlAction.Busy:
                        Require(p.CallSignalData is null && p.CallSignalMid is null && p.CallSignalMLineIndex is null, "Données média inattendues.");
                        break;
                    case CallControlAction.Offer:
                    case CallControlAction.Answer:
                        Require(p.CallSignalData is { Length: > 0 and <= ProtocolLimits.MaxCallSdpChars }, "Description SDP invalide.");
                        Require(p.CallSignalMid is null && p.CallSignalMLineIndex is null, "Métadonnées ICE inattendues.");
                        break;
                    case CallControlAction.IceCandidate:
                        Require(p.CallSignalData is { Length: > 0 and <= ProtocolLimits.MaxIceCandidateChars }, "Candidat ICE invalide.");
                        Require(p.CallSignalMid is { Length: > 0 and <= ProtocolLimits.MaxIceMidChars } && !p.CallSignalMid.Any(char.IsControl), "MID ICE invalide.");
                        Require(p.CallSignalMLineIndex is >= 0 and <= 32, "Index de ligne média ICE invalide.");
                        break;
                    default:
                        throw new ProtocolViolationException("Action d’appel inconnue.");
                }
                break;

            case PacketKind.StatusPublish:
                ValidateStatusPublish(p, now);
                break;

            case PacketKind.StatusMediaChunk:
                ValidateStatusMediaChunk(p);
                break;

            case PacketKind.StatusViewReceipt:
            case PacketKind.StatusDelete:
                Require(InputValidator.IsValidPeerId(p.ConversationId), "Un événement de statut doit viser un pair privé.");
                Require(InputValidator.IsValidGuid(p.StatusId), "Identifiant de statut invalide.");
                break;

            case PacketKind.ProfileUpdate:
                Require(p.ProfilePeerId is not null && InputValidator.IsValidPeerId(p.ProfilePeerId), "Identité du profil invalide.");
                Require(p.ProfileDisplayName is not null, "Nom public absent.");
                Require(p.ProfileStatus is not null, "Statut public absent.");
                Require(p.ProfileAbout is not null, "Description publique absente.");
                Require(string.Equals(p.ConversationId, p.ProfilePeerId, StringComparison.Ordinal), "Conversation du profil invalide.");
                try
                {
                    _ = new PersonalProfile
                    {
                        PeerId = p.ProfilePeerId!,
                        DisplayName = p.ProfileDisplayName!,
                        Status = p.ProfileStatus!,
                        About = p.ProfileAbout!,
                        AvatarBytes = p.ProfileAvatarBytes,
                    }.Validate(p.ProfilePeerId!);
                }
                catch (InvalidDataException ex)
                {
                    throw new ProtocolViolationException("Profil personnel invalide.", ex);
                }
                break;
        }
    }


    private static void ValidateStatusPublish(Packet p, DateTimeOffset now)
    {
        Require(InputValidator.IsValidPeerId(p.ConversationId), "Une publication de statut doit viser un pair privé.");
        Require(InputValidator.IsValidGuid(p.StatusId), "Identifiant de statut invalide.");
        Require(p.StatusContentKind is not null && Enum.IsDefined(p.StatusContentKind.Value), "Type de statut invalide.");
        Require(p.StatusText is { Length: <= 700 } && !p.StatusText.Any(c => char.IsControl(c) && c is not '\n' and not '\r' and not '\t'), "Texte de statut invalide.");
        Require(p.StatusCreatedAtUnixMs is >= -62135596800000 and <= 253402300799999
            && p.StatusExpiresAtUnixMs is >= -62135596800000 and <= 253402300799999, "Dates du statut absentes ou hors limites.");
        DateTimeOffset created = DateTimeOffset.FromUnixTimeMilliseconds(p.StatusCreatedAtUnixMs!.Value);
        DateTimeOffset expires = DateTimeOffset.FromUnixTimeMilliseconds(p.StatusExpiresAtUnixMs!.Value);
        Require(created <= now + ProtocolLimits.MaxFutureSkew && expires == created + EphemeralStatus.Lifetime && now < expires, "Période de validité du statut incorrecte.");
        // Les règles d'audience restent exclusivement sur l'appareil auteur. Les inclure dans le paquet
        // révélerait à chaque destinataire la liste des contacts exclus ou sélectionnés.
        Require(p.StatusAudienceMode is null && p.StatusAudiencePeerIds is null,
            "Les règles privées d'audience ne doivent jamais être transmises au destinataire.");

        if (p.StatusContentKind == RedChat.Core.StatusContentKind.Text)
        {
            Require(!string.IsNullOrWhiteSpace(p.StatusText), "Un statut texte doit contenir du texte.");
            Require(p.StatusMediaName is null && p.StatusMediaSize is null && p.StatusMediaSha256 is null, "Métadonnées média inattendues pour un statut texte.");
        }
        else
        {
            Require(p.StatusMediaName is not null && IsSafeAttachmentName(p.StatusMediaName) && p.StatusMediaName.Length <= ProtocolLimits.MaxStatusMediaNameChars, "Nom du média de statut invalide.");
            Require(p.StatusMediaSize is > 0 and <= EphemeralStatus.MaxMediaBytes, "Taille du média de statut invalide.");
            Require(p.StatusMediaSha256 is { Length: 64 } && p.StatusMediaSha256.All(Uri.IsHexDigit), "Empreinte du média de statut invalide.");
        }
    }

    private static void ValidateStatusMediaChunk(Packet p)
    {
        Require(InputValidator.IsValidPeerId(p.ConversationId), "Un bloc de statut doit viser un pair privé.");
        Require(InputValidator.IsValidGuid(p.StatusId), "Identifiant de statut invalide.");
        Require(p.StatusMediaSize is > 0 and <= EphemeralStatus.MaxMediaBytes, "Taille totale du média invalide.");
        Require(p.StatusMediaSha256 is { Length: 64 } && p.StatusMediaSha256.All(Uri.IsHexDigit), "Empreinte du média invalide.");
        Require(p.StatusChunkCount is > 0 && p.StatusChunkIndex is >= 0 && p.StatusChunkIndex < p.StatusChunkCount, "Index de bloc de statut invalide.");
        long expectedCount = (p.StatusMediaSize!.Value + ProtocolLimits.StatusMediaChunkBytes - 1) / ProtocolLimits.StatusMediaChunkBytes;
        Require(p.StatusChunkCount == expectedCount, "Nombre de blocs de statut incohérent.");
        Require(p.StatusChunkData is { Length: > 0 and <= ProtocolLimits.StatusMediaChunkBytes }, "Données du bloc de statut invalides.");
        int expectedLength = p.StatusChunkIndex == p.StatusChunkCount - 1
            ? checked((int)(p.StatusMediaSize!.Value - (long)p.StatusChunkIndex!.Value * ProtocolLimits.StatusMediaChunkBytes))
            : ProtocolLimits.StatusMediaChunkBytes;
        Require(p.StatusChunkData!.Length == expectedLength, "Longueur du bloc de statut incohérente.");
    }

    private static void ValidateAttachmentChunk(Packet p)
    {
        Require(p.AttachmentTransferId is not null && InputValidator.IsValidGuid(p.AttachmentTransferId), "Identifiant de transfert invalide.");
        Require(p.AttachmentFileName is not null && IsSafeAttachmentName(p.AttachmentFileName), "Nom de fichier invalide.");
        Require(!p.AttachmentIsViewOnce || InputValidator.IsValidPeerId(p.ConversationId)
            && IsViewOnceMediaName(p.AttachmentFileName!), "Média à vue unique invalide.");
        Require(p.AttachmentFileSize is > 0 and <= ProtocolLimits.MaxAttachmentBytes, "Taille de fichier invalide.");
        Require(p.AttachmentSha256 is { Length: 64 } && p.AttachmentSha256.All(Uri.IsHexDigit), "Empreinte de fichier invalide.");
        Require(p.AttachmentChunkCount is > 0 && p.AttachmentChunkIndex is >= 0 && p.AttachmentChunkIndex < p.AttachmentChunkCount, "Index de bloc invalide.");
        long expectedCount = (p.AttachmentFileSize!.Value + ProtocolLimits.AttachmentChunkBytes - 1) / ProtocolLimits.AttachmentChunkBytes;
        Require(p.AttachmentChunkCount == expectedCount, "Nombre de blocs incohérent.");
        Require(p.AttachmentChunkData is { Length: > 0 and <= ProtocolLimits.AttachmentChunkBytes }, "Contenu de bloc invalide.");
        int expectedLength = p.AttachmentChunkIndex == p.AttachmentChunkCount - 1
            ? checked((int)(p.AttachmentFileSize!.Value - (long)p.AttachmentChunkIndex!.Value * ProtocolLimits.AttachmentChunkBytes))
            : ProtocolLimits.AttachmentChunkBytes;
        Require(p.AttachmentChunkData!.Length == expectedLength, "Longueur de bloc incohérente.");
    }

    private static bool IsSafeAttachmentName(string name) =>
        name.Length is > 0 and <= ProtocolLimits.MaxAttachmentFileNameChars
        && name == Path.GetFileName(name)
        && !name.Any(char.IsControl)
        && !name.Any(ch => "<>:\"/\\|?*".Contains(ch))
        && !name.EndsWith('.') && !name.EndsWith(' ')
        && name is not "." and not ".."
        && !ReservedAttachmentNames.Contains(name.Split('.')[0]);

    private static bool IsViewOnceMediaName(string name)
    {
        string extension = Path.GetExtension(name);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".gif", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".m4v", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".mov", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".avi", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".wmv", StringComparison.OrdinalIgnoreCase);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new ProtocolViolationException(message);
        }
    }
}
