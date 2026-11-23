namespace RedChat.Core;

/// <summary>Audience autorisée pour une publication éphémère.</summary>
public enum StatusAudienceMode
{
    /// <summary>Tous les contacts connus de l'auteur.</summary>
    AllContacts = 1,
    /// <summary>Tous les contacts sauf ceux explicitement exclus.</summary>
    AllExcept = 2,
    /// <summary>Uniquement les contacts explicitement sélectionnés.</summary>
    OnlySelected = 3,
}

/// <summary>Nature du contenu d'un statut.</summary>
public enum StatusContentKind
{
    /// <summary>Publication textuelle.</summary>
    Text = 1,
    /// <summary>Photo PNG ou JPEG.</summary>
    Photo = 2,
    /// <summary>Vidéo MP4 ou WebM.</summary>
    Video = 3,
}

/// <summary>Règle de confidentialité appliquée à une publication de statut.</summary>
public sealed record StatusAudience
{
    /// <summary>Mode de partage.</summary>
    public StatusAudienceMode Mode { get; init; } = StatusAudienceMode.AllContacts;
    /// <summary>Pairs concernés par la liste d'inclusion ou d'exclusion.</summary>
    public IReadOnlySet<string> PeerIds { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Vérifie la règle et retourne une copie indépendante.</summary>
    public StatusAudience Validate(string ownerPeerId)
    {
        if (!InputValidator.IsValidPeerId(ownerPeerId) || !Enum.IsDefined(Mode) || PeerIds is null || PeerIds.Count > 256
            || PeerIds.Any(id => !InputValidator.IsValidPeerId(id) || id == ownerPeerId))
        {
            throw new InvalidDataException("Audience du statut invalide.");
        }

        if (Mode == StatusAudienceMode.AllContacts && PeerIds.Count != 0)
        {
            throw new InvalidDataException("Le mode « tous les contacts » ne doit pas contenir de liste.");
        }

        if (Mode != StatusAudienceMode.AllContacts && PeerIds.Count == 0)
        {
            throw new InvalidDataException("Sélectionnez au moins un contact pour cette audience.");
        }

        return this with { PeerIds = new HashSet<string>(PeerIds, StringComparer.Ordinal) };
    }

    /// <summary>Détermine si un contact est autorisé à recevoir la publication.</summary>
    public bool Allows(string peerId) => InputValidator.IsValidPeerId(peerId) && (Mode switch
    {
        StatusAudienceMode.AllContacts => true,
        StatusAudienceMode.AllExcept => !PeerIds.Contains(peerId),
        StatusAudienceMode.OnlySelected => PeerIds.Contains(peerId),
        _ => false,
    });
}

/// <summary>Publication éphémère créée par un utilisateur RedChat.</summary>
public sealed record EphemeralStatus
{
    /// <summary>Version du format de statut.</summary>
    public int SchemaVersion { get; init; } = 1;
    /// <summary>Identifiant unique de la publication.</summary>
    public string Id { get; init; } = string.Empty;
    /// <summary>Identité cryptographique de l'auteur.</summary>
    public string OwnerPeerId { get; init; } = string.Empty;
    /// <summary>Type de contenu.</summary>
    public StatusContentKind ContentKind { get; init; }
    /// <summary>Texte du statut (texte seul ou légende).</summary>
    public string Text { get; init; } = string.Empty;
    /// <summary>Contenu binaire photo/vidéo ; absent pour un statut texte.</summary>
    public byte[]? MediaBytes { get; init; }
    /// <summary>Instant de création UTC.</summary>
    public DateTimeOffset CreatedAt { get; init; }
    /// <summary>Expiration UTC, fixée à 24 heures après création.</summary>
    public DateTimeOffset ExpiresAt { get; init; }
    /// <summary>Audience appliquée à cette publication.</summary>
    public StatusAudience Audience { get; init; } = new();
    /// <summary>Contacts ayant consulté cette publication (côté auteur uniquement).</summary>
    public IReadOnlySet<string> ViewedByPeerIds { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Durée de vie maximale des statuts.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);
    /// <summary>Taille maximale d'un média de statut.</summary>
    public const int MaxMediaBytes = 20 * 1024 * 1024;

    /// <summary>Valide une publication, son format média, son audience et son horodatage.</summary>
    public EphemeralStatus Validate(DateTimeOffset now, bool isLocalOwner = false)
    {
        if (SchemaVersion != 1 || !InputValidator.IsValidGuid(Id) || !InputValidator.IsValidPeerId(OwnerPeerId)
            || !Enum.IsDefined(ContentKind) || CreatedAt.Offset != TimeSpan.Zero || ExpiresAt.Offset != TimeSpan.Zero
            || ExpiresAt != CreatedAt + Lifetime || CreatedAt > now + ProtocolLimits.MaxFutureSkew
            || now > CreatedAt + Lifetime + ProtocolLimits.MaxFutureSkew)
        {
            throw new InvalidDataException("Métadonnées du statut invalides ou hors période de validité.");
        }

        if (Text is null || Text.Length > 700 || Text.Any(c => char.IsControl(c) && c is not '\n' and not '\r' and not '\t'))
        {
            throw new InvalidDataException("Texte du statut invalide.");
        }

        if (ContentKind == StatusContentKind.Text)
        {
            if (string.IsNullOrWhiteSpace(Text) || MediaBytes is not null)
            {
                throw new InvalidDataException("Un statut texte doit contenir du texte et aucun média.");
            }
        }
        else
        {
            if (MediaBytes is null || MediaBytes.Length is 0 or > MaxMediaBytes || !HasExpectedSignature(ContentKind, MediaBytes))
            {
                throw new InvalidDataException("Le média du statut est vide, trop volumineux ou dans un format non pris en charge.");
            }
        }

        StatusAudience audience = (Audience ?? throw new InvalidDataException("Audience du statut absente.")).Validate(OwnerPeerId);
        HashSet<string> views = new(ViewedByPeerIds ?? throw new InvalidDataException("Liste de vues absente."), StringComparer.Ordinal);
        if (views.Count > 256 || views.Any(id => !InputValidator.IsValidPeerId(id) || id == OwnerPeerId)
            || (!isLocalOwner && views.Count != 0))
        {
            throw new InvalidDataException("Liste des vues du statut invalide.");
        }

        return this with { Audience = audience, MediaBytes = MediaBytes?.ToArray(), ViewedByPeerIds = views };
    }

    /// <summary>Indique si la publication est encore visible à l'instant fourni.</summary>
    public bool IsActive(DateTimeOffset now) => now >= CreatedAt && now < ExpiresAt;

    private static bool HasExpectedSignature(StatusContentKind kind, ReadOnlySpan<byte> bytes) => kind switch
    {
        StatusContentKind.Photo => IsPng(bytes) || IsJpeg(bytes),
        StatusContentKind.Video => IsMp4(bytes) || IsWebM(bytes),
        _ => false,
    };

    private static bool IsPng(ReadOnlySpan<byte> b) => b.Length >= 8 && b[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
    private static bool IsJpeg(ReadOnlySpan<byte> b) => b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF;
    private static bool IsMp4(ReadOnlySpan<byte> b) => b.Length >= 12 && b[4] == (byte)'f' && b[5] == (byte)'t' && b[6] == (byte)'y' && b[7] == (byte)'p';
    private static bool IsWebM(ReadOnlySpan<byte> b) => b.Length >= 4 && b[0] == 0x1A && b[1] == 0x45 && b[2] == 0xDF && b[3] == 0xA3;
}
