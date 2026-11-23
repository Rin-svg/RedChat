using System.Security.Cryptography;

namespace RedChat.Core;

/// <summary>Fonctions liées à l'identité d'un pair : dérivation du PeerId et vérification des signatures.</summary>
public static class PeerIdentity
{
    /// <summary>OID de la courbe NIST P-256 : la seule acceptée (on refuse toute autre courbe annoncée par un pair).</summary>
    private const string P256Oid = "1.2.840.10045.3.1.7";

    /// <summary>
    /// PeerId = SHA-256 de la clé publique (format SubjectPublicKeyInfo), en hexadécimal minuscule.
    /// L'identifiant est donc auto-certifié : on ne peut pas l'usurper sans posséder la clé privée correspondante.
    /// </summary>
    public static string ComputePeerId(ReadOnlySpan<byte> publicKeySpki) =>
        Convert.ToHexStringLower(SHA256.HashData(publicKeySpki));

    /// <summary>Importe une clé publique ECDSA P-256 reçue du réseau ; lève <see cref="ProtocolViolationException"/> si invalide.</summary>
    internal static ECDsa ImportPublicKey(byte[] spki)
    {
        ECDsa key = ECDsa.Create();
        try
        {
            key.ImportSubjectPublicKeyInfo(spki, out int read);
            if (read != spki.Length || key.ExportParameters(false).Curve.Oid.Value != P256Oid)
            {
                throw new ProtocolViolationException("Clé d'identité : courbe ou format non accepté.");
            }

            // FORME CANONIQUE OBLIGATOIRE. Le parseur DER tolère certaines variantes (ex. octet « bits inutilisés » ≠ 0) :
            // la même clé aurait alors plusieurs encodages, donc plusieurs PeerId (SHA-256 des octets). On exige que
            // la ré-exportation redonne exactement les octets reçus, pour qu'une clé = un seul PeerId.
            if (!key.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(spki))
            {
                throw new ProtocolViolationException("Clé d'identité : encodage non canonique.");
            }

            return key;
        }
        catch (Exception ex) when (ex is CryptographicException or PlatformNotSupportedException or ArgumentException or System.Formats.Asn1.AsnContentException)
        {
            // Une clé hostile peut provoquer plusieurs types d'exceptions selon la plateforme (courbe inconnue, DER invalide…) :
            // toutes sont ramenées à UNE seule exception de protocole, seule connue de l'appelant.
            key.Dispose();
            throw new ProtocolViolationException("Clé d'identité illisible.", ex);
        }
        catch (ProtocolViolationException)
        {
            key.Dispose();
            throw;
        }
    }

    /// <summary>Vérifie une signature ECDSA P-256/SHA-256 (format fixe r||s). Faux si la signature est invalide.</summary>
    internal static bool VerifySignature(ECDsa publicKey, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature) =>
        signature.Length == ProtocolLimits.SignatureBytes
        && publicKey.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
}

/// <summary>
/// Identité locale : paire de clés ECDSA P-256. La clé privée ne quitte jamais cet objet en clair,
/// sauf via <see cref="ExportPrivateKey"/> destiné au stockage protégé (DPAPI) par la couche Infrastructure.
/// </summary>
public sealed class LocalIdentity : IDisposable
{
    private readonly ECDsa _key;

    private LocalIdentity(ECDsa key)
    {
        _key = key;
        PublicKeySpki = key.ExportSubjectPublicKeyInfo();
        PeerId = PeerIdentity.ComputePeerId(PublicKeySpki);
    }

    /// <summary>Clé publique au format SubjectPublicKeyInfo (DER).</summary>
    public byte[] PublicKeySpki { get; }

    /// <summary>Identifiant du pair (SHA-256 de la clé publique, hexadécimal minuscule).</summary>
    public string PeerId { get; }

    /// <summary>Génère une nouvelle identité.</summary>
    public static LocalIdentity Create() => new(ECDsa.Create(ECCurve.NamedCurves.nistP256));

    /// <summary>Recharge une identité depuis une clé privée PKCS#8 (déchiffrée par l'appelant).</summary>
    public static LocalIdentity FromPrivateKey(byte[] pkcs8)
    {
        ArgumentNullException.ThrowIfNull(pkcs8);
        ECDsa key = ECDsa.Create();
        try
        {
            key.ImportPkcs8PrivateKey(pkcs8, out _);
            if (key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7")
            {
                throw new CryptographicException("Courbe d'identité inattendue.");
            }

            return new LocalIdentity(key);
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    /// <summary>Exporte la clé privée (PKCS#8). À chiffrer immédiatement avant écriture disque.</summary>
    public byte[] ExportPrivateKey() => _key.ExportPkcs8PrivateKey();

    internal byte[] Sign(ReadOnlySpan<byte> data) =>
        _key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    /// <inheritdoc />
    public void Dispose() => _key.Dispose();
}
