using System.Security.Cryptography;

namespace RedChat.Infrastructure;

/// <summary>
/// Protège des données sensibles AU REPOS (clé privée d'identité, historique, contacts) avant leur écriture sur disque.
/// <para>En production sous Windows : implémentation DPAPI (<c>ProtectedData</c>, portée « utilisateur courant »), fournie par le
/// projet de l'application (elle exige le paquet NuGet System.Security.Cryptography.ProtectedData, absent du Core volontairement).
/// Ici, l'abstraction permet de tester tout le reste sans Windows.</para>
/// </summary>
public interface ISecretProtector
{
    /// <summary>Chiffre les données. Le résultat ne doit être lisible que par l'utilisateur courant de la machine.</summary>
    byte[] Protect(byte[] plaintext);

    /// <summary>Déchiffre. Lève <see cref="CryptographicException"/> si les données sont altérées ou ne viennent pas de ce protecteur.</summary>
    byte[] Unprotect(byte[] protectedData);
}

/// <summary>
/// PROTECTEUR DE TEST — NE PROTÈGE RIEN. Les données restent lisibles en clair dans le fichier ; seule une somme de contrôle détecte
/// les altérations (pour pouvoir tester les cas « fichier corrompu »). Volontairement <c>internal</c> : le code de l'application ne peut
/// pas l'utiliser par inadvertance, seuls les tests y ont accès.
/// </summary>
internal sealed class InsecureTestSecretProtector : ISecretProtector
{
    private static readonly byte[] Magic = "INSECURE-TEST-ONLY:"u8.ToArray();

    public byte[] Protect(byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        byte[] check = SHA256.HashData(plaintext);
        byte[] result = new byte[Magic.Length + plaintext.Length + check.Length];
        Magic.CopyTo(result, 0);
        plaintext.CopyTo(result, Magic.Length);
        check.CopyTo(result, Magic.Length + plaintext.Length);
        return result;
    }

    public byte[] Unprotect(byte[] protectedData)
    {
        ArgumentNullException.ThrowIfNull(protectedData);
        int checkLength = SHA256.HashSizeInBytes;
        if (protectedData.Length < Magic.Length + checkLength || !protectedData.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            throw new CryptographicException("Données non reconnues par ce protecteur.");
        }

        byte[] plain = protectedData.AsSpan(Magic.Length, protectedData.Length - Magic.Length - checkLength).ToArray();
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(plain), protectedData.AsSpan(protectedData.Length - checkLength)))
        {
            throw new CryptographicException("Données altérées.");
        }

        return plain;
    }
}
