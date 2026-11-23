using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace RedChat.Core;

/// <summary>
/// Validation par liste blanche de toutes les entrées externes (saisie, ligne de commande, réseau).
/// La validation d'interface n'est qu'un confort : ce sont ces méthodes qui font barrière.
/// </summary>
public static partial class InputValidator
{
    // \A et \z (et non ^ et $) : « $ » accepte un « \n » final, ce qui laisserait passer "abc\n".
    [GeneratedRegex(@"\A[A-Za-z0-9_-]{1,30}\z")]
    private static partial Regex ProfileNameRegex();

    [GeneratedRegex(@"\A[0-9a-f]{64}\z")]
    private static partial Regex PeerIdRegex();

    /// <summary>Nom de profil valide pour --profile (sert à fabriquer un nom de dossier : donc ultra-restrictif).</summary>
    public static bool IsValidProfileName(string? value) =>
        value is not null && ProfileNameRegex().IsMatch(value);

    /// <summary>Identifiant de pair : 64 caractères hexadécimaux minuscules (SHA-256 de la clé publique).</summary>
    public static bool IsValidPeerId(string? value) =>
        value is not null && PeerIdRegex().IsMatch(value);

    /// <summary>Identifiant de message/groupe : GUID au format « D » (36 caractères).</summary>
    public static bool IsValidGuid(string? value) =>
        value is not null && value.Length == 36 && Guid.TryParseExact(value, "D", out _);

    /// <summary>
    /// Nettoie et valide un nom d'affichage ou de groupe : rogne les espaces, refuse vide, trop long,
    /// caractères de contrôle et caractères d'inversion/invisibles (usurpation visuelle).
    /// </summary>
    public static bool TryNormalizeName(string? input, int maxChars, out string name)
    {
        name = string.Empty;
        if (input is null)
        {
            return false;
        }

        string trimmed = input.Trim();
        if (trimmed.Length == 0 || trimmed.Length > maxChars)
        {
            return false;
        }

        foreach (char c in trimmed)
        {
            if (IsDisallowedInName(c))
            {
                return false;
            }
        }

        name = trimmed;
        return true;
    }

    /// <summary>
    /// Valide un texte de message : non vide (hors espaces), borné, sans caractère de contrôle autre que retour ligne/tabulation
    /// (le NUL et les séquences d'échappement n'ont rien à faire dans un message).
    /// </summary>
    public static bool IsValidMessageText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > ProtocolLimits.MaxTextChars)
        {
            return false;
        }

        foreach (char c in text)
        {
            if (char.IsControl(c) && c != '\n' && c != '\r' && c != '\t')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Valide une réaction : courte, sans caractère de contrôle, non vide.</summary>
    public static bool IsValidReaction(string? emoji)
    {
        if (string.IsNullOrEmpty(emoji) || emoji.Length > ProtocolLimits.MaxReactionChars)
        {
            return false;
        }

        foreach (char c in emoji)
        {
            if (char.IsControl(c) || char.IsWhiteSpace(c) || IsBidiOrInvisible(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Inversions bidi, marques directionnelles, séparateurs de ligne et espaces de largeur nulle : invisibles ou trompeurs à l'écran.
    /// On laisse volontairement passer U+200C/U+200D (liaison des emojis composés, ex. 👨‍👩‍👧) et les sélecteurs de variation.
    /// </summary>
    private static bool IsBidiOrInvisible(char c) =>
        c == '\u200B' || c == '\u200E' || c == '\u200F'
        || (c >= '\u2028' && c <= '\u202E')
        || (c >= '\u2066' && c <= '\u2069')
        || c == '\uFEFF';

    /// <summary>
    /// Analyse « IP:port » saisi à la main. N'accepte que des adresses de réseau local (privées, boucle, lien-local) :
    /// l'application ne doit pas pouvoir être détournée pour joindre un hôte public.
    /// </summary>
    public static bool TryParseLanEndpoint(string? input, out IPEndPoint? endpoint)
    {
        endpoint = null;
        if (string.IsNullOrWhiteSpace(input) || input.Length > 64)
        {
            return false;
        }

        if (!IPEndPoint.TryParse(input.Trim(), out IPEndPoint? parsed))
        {
            return false;
        }

        if (parsed.Port < ProtocolLimits.MinPort || parsed.Port > IPEndPoint.MaxPort || !IsLanAddress(parsed.Address))
        {
            return false;
        }

        endpoint = parsed;
        return true;
    }

    /// <summary>Vrai si l'adresse est privée, de boucle ou lien-local (IPv4 ou IPv6) ; faux pour toute adresse publique ou spéciale.</summary>
    public static bool IsLanAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] b = address.GetAddressBytes();
            return b[0] == 10
                || b[0] == 127
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254);
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal;
        }

        return false;
    }

    private static bool IsDisallowedInName(char c) =>
        char.IsControl(c)
        || (c >= '\u200B' && c <= '\u200F')   // espaces de largeur nulle, marques directionnelles
        || (c >= '\u2028' && c <= '\u202E')   // séparateurs de ligne/paragraphe, inversions bidi
        || (c >= '\u2066' && c <= '\u2069')   // isolats bidi
        || c == '\uFEFF';                     // BOM / espace insécable de largeur nulle
}
