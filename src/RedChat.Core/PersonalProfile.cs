namespace RedChat.Core;

/// <summary>Informations publiques modifiables de l'utilisateur, indépendantes de son identité cryptographique.</summary>
public sealed record PersonalProfile
{
    /// <summary>Version du format de profil.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>PeerId auquel ce profil personnel est rattaché.</summary>
    public string PeerId { get; init; } = string.Empty;

    /// <summary>Nom public affiché aux contacts.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Statut court de l'utilisateur.</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>Description « À propos ».</summary>
    public string About { get; init; } = string.Empty;

    /// <summary>Photo d'avatar au format PNG ou JPEG, limitée à 512 Kio.</summary>
    public byte[]? AvatarBytes { get; init; }

    /// <summary>Vérifie les champs publics et retourne une copie normalisée.</summary>
    public PersonalProfile Validate(string expectedPeerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedPeerId);
        if (SchemaVersion != 1 || !string.Equals(PeerId, expectedPeerId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Le profil ne correspond pas à l'identité locale.");
        }

        if (Status is null || About is null)
        {
            throw new InvalidDataException("Le statut ou la description publique est absent.");
        }

        if (!InputValidator.TryNormalizeName(DisplayName, 40, out string name))
        {
            throw new InvalidDataException("Le nom public doit contenir entre 1 et 40 caractères et ne pas contenir de caractères invisibles ou de contrôle.");
        }

        string status = Status.Trim();
        if (status.Length > 0)
        {
            if (!InputValidator.TryNormalizeName(status, 80, out string normalizedStatus))
            {
                throw new InvalidDataException("Le statut dépasse 80 caractères ou contient des caractères interdits.");
            }

            status = normalizedStatus;
        }

        string about = About.Trim();
        if (about.Length > 280 || about.Any(c =>
            (char.IsControl(c) && c is not '\n' and not '\r' and not '\t') || IsBidiOrInvisible(c)))
        {
            throw new InvalidDataException("La description dépasse 280 caractères ou contient des caractères interdits.");
        }

        if (AvatarBytes is { Length: > 512 * 1024 } || AvatarBytes is { Length: 0 })
        {
            throw new InvalidDataException("La photo de profil doit peser au maximum 512 Kio.");
        }

        if (AvatarBytes is { } avatar && !IsPng(avatar) && !IsJpeg(avatar))
        {
            throw new InvalidDataException("La photo doit être une image PNG ou JPEG valide.");
        }

        return this with { DisplayName = name, Status = status, About = about, AvatarBytes = AvatarBytes?.ToArray() };
    }

    private static bool IsBidiOrInvisible(char c) =>
        c == '\u200B' || c == '\u200E' || c == '\u200F'
        || (c >= '\u2028' && c <= '\u202E')
        || (c >= '\u2066' && c <= '\u2069')
        || c == '\uFEFF';

    private static bool IsPng(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 8 && bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });

    private static bool IsJpeg(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF;

    /// <summary>Crée le profil public initial associé à l'identité existante.</summary>
    public static PersonalProfile CreateDefault(string peerId) => new()
    {
        PeerId = peerId,
        DisplayName = "Utilisateur RedChat",
    };
}
