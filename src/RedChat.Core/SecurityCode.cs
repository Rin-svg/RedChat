using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace RedChat.Core;

/// <summary>
/// « Code de sécurité » de deux interlocuteurs : 12 groupes de 5 chiffres que les deux personnes comparent à voix haute ou en personne.
/// <para>POURQUOI ÇA PROTÈGE : le PeerId est le SHA-256 de la clé publique ; le code est dérivé des DEUX PeerId. Si les deux
/// affichent le même code, chacun a bien la clé de l'autre, donc personne ne s'est glissé au milieu. Le code est identique des
/// deux côtés (l'ordre des identifiants ne compte pas) et change dès qu'un des deux identifiants change.</para>
/// </summary>
public static class SecurityCode
{
    /// <summary>Nombre de groupes de chiffres.</summary>
    public const int Groups = 12;

    /// <summary>Chiffres par groupe.</summary>
    public const int DigitsPerGroup = 5;

    // Séparation de domaine : ce hachage ne peut pas être confondu avec un autre usage de SHA-512 dans l'application.
    private const string DomainTag = "REDCHAT-SECURITY-CODE-V1";

    // 5 octets (40 bits) par groupe → réduits modulo 10^5. Le biais est de l'ordre de 10^-7 : négligeable pour un affichage.
    private const int BytesPerGroup = 5;

    /// <summary>Calcule le code pour la paire (symétrique : <c>Compute(a, b) == Compute(b, a)</c>).</summary>
    /// <exception cref="ArgumentException">PeerId invalide, ou identiques.</exception>
    public static string Compute(string peerIdA, string peerIdB)
    {
        if (!InputValidator.IsValidPeerId(peerIdA) || !InputValidator.IsValidPeerId(peerIdB) || peerIdA == peerIdB)
        {
            throw new ArgumentException("Deux PeerId valides et distincts sont nécessaires.");
        }

        // Tri ordinal : le résultat ne dépend pas de qui est « moi » et de qui est « l'autre ».
        (string low, string high) = string.CompareOrdinal(peerIdA, peerIdB) < 0 ? (peerIdA, peerIdB) : (peerIdB, peerIdA);
        byte[] hash = SHA512.HashData(Encoding.UTF8.GetBytes(string.Concat(DomainTag, "\n", low, "\n", high)));

        StringBuilder sb = new(Groups * (DigitsPerGroup + 1));
        for (int g = 0; g < Groups; g++)
        {
            ulong value = 0;
            for (int i = 0; i < BytesPerGroup; i++)
            {
                value = (value << 8) | hash[(g * BytesPerGroup) + i];
            }

            if (g > 0)
            {
                _ = sb.Append(' ');
            }

            _ = sb.Append((value % 100_000UL).ToString("D5", CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }
}
