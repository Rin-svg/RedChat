using System.Security.Cryptography;
using System.Text;

namespace RedChat.Core;

/// <summary>
/// Briques communes du handshake. Protocole (3 messages, signatures sur la transcription complète) :
/// <code>
///  I → R : Hello1 = ephI, nonceI
///  R → I : Hello2 = ephR, nonceR, idR, Sig_R( "responder" || T )
///  I → R : Hello3 = idI, Sig_I( "initiator" || T || SHA256(idR) )
///  T = SHA256( ephI, nonceI, ephR, nonceR )   ;  clés = HKDF-SHA256( ECDH(ephI,ephR), sel = T )
/// </code>
/// Propriétés : secret avant (clés éphémères), authentification mutuelle (les signatures couvrent les deux clés éphémères,
/// donc pas d'homme du milieu sans clé d'identité), liaison de rôle (un message signé « responder » ne peut pas être rejoué comme « initiator »).
/// </summary>
internal static class HandshakeCrypto
{
    private const byte TypeHello1 = 0x01;
    private const byte TypeHello2 = 0x02;
    private const byte TypeHello3 = 0x03;
    private static readonly byte[] ResponderLabel = Encoding.ASCII.GetBytes("RedChat/v1/responder");
    private static readonly byte[] InitiatorLabel = Encoding.ASCII.GetBytes("RedChat/v1/initiator");
    private static readonly byte[] KeyInfo = Encoding.ASCII.GetBytes("RedChat/v1/keys");

    public static byte[] RandomNonce() => RandomNumberGenerator.GetBytes(ProtocolLimits.HandshakeNonceBytes);

    public static byte[] ExportEphemeral(ECDiffieHellman key) => key.ExportSubjectPublicKeyInfo();

    public static ECDiffieHellman NewEphemeral() => ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

    /// <summary>Importe une clé éphémère reçue ; refuse toute courbe autre que P-256.</summary>
    public static ECDiffieHellman ImportEphemeral(byte[] spki)
    {
        ECDiffieHellman key = ECDiffieHellman.Create();
        try
        {
            key.ImportSubjectPublicKeyInfo(spki, out int read);
            if (read != spki.Length || key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7")
            {
                throw new ProtocolViolationException("Clé éphémère : courbe ou format non accepté.");
            }

            // Même exigence de forme canonique que pour la clé d'identité (la transcription signée porte sur ces octets).
            if (!key.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(spki))
            {
                throw new ProtocolViolationException("Clé éphémère : encodage non canonique.");
            }

            return key;
        }
        catch (Exception ex) when (ex is CryptographicException or PlatformNotSupportedException or ArgumentException or System.Formats.Asn1.AsnContentException)
        {
            // Une clé hostile peut provoquer plusieurs types d'exceptions selon la plateforme (courbe inconnue, DER invalide…) :
            // toutes sont ramenées à UNE seule exception de protocole, seule connue de l'appelant.
            key.Dispose();
            throw new ProtocolViolationException("Clé éphémère illisible.", ex);
        }
        catch (ProtocolViolationException)
        {
            key.Dispose();
            throw;
        }
    }

    public static byte[] Transcript(byte[] ephI, byte[] nonceI, byte[] ephR, byte[] nonceR)
    {
        ByteWriter w = new();
        w.WriteField(ephI);
        w.WriteFixed(nonceI);
        w.WriteField(ephR);
        w.WriteFixed(nonceR);
        return SHA256.HashData(w.ToArray());
    }

    public static byte[] ResponderSignedData(byte[] transcript) => Concat(ResponderLabel, transcript);

    public static byte[] InitiatorSignedData(byte[] transcript, byte[] responderPublicKey) =>
        Concat(InitiatorLabel, transcript, SHA256.HashData(responderPublicKey));

    /// <summary>Dérive les deux clés directionnelles : (initiateur→répondeur, répondeur→initiateur).</summary>
    public static (byte[] InitiatorToResponder, byte[] ResponderToInitiator) DeriveKeys(
        ECDiffieHellman local, ECDiffieHellman remote, byte[] transcript)
    {
        byte[] shared = local.DeriveRawSecretAgreement(remote.PublicKey);
        try
        {
            byte[] okm = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 2 * ProtocolLimits.AesKeyBytes, transcript, KeyInfo);
            return (okm[..ProtocolLimits.AesKeyBytes], okm[ProtocolLimits.AesKeyBytes..]);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(shared);
        }
    }

    public static byte[] BuildHello1(byte[] eph, byte[] nonce)
    {
        ByteWriter w = new();
        w.WriteByte(TypeHello1);
        w.WriteByte(ProtocolLimits.ProtocolVersion);
        w.WriteField(eph);
        w.WriteFixed(nonce);
        return w.ToArray();
    }

    public static (byte[] Eph, byte[] Nonce) ParseHello1(byte[] message)
    {
        ByteReader r = new(message);
        ExpectType(r, TypeHello1);
        byte version = r.ReadByte();
        if (version != ProtocolLimits.ProtocolVersion)
        {
            throw new ProtocolViolationException($"Version de protocole non supportée : {version}.");
        }

        byte[] eph = r.ReadField();
        byte[] nonce = r.ReadFixed(ProtocolLimits.HandshakeNonceBytes);
        r.EnsureEnd();
        return (eph, nonce);
    }

    public static byte[] BuildHello2(byte[] eph, byte[] nonce, byte[] identityPublicKey, byte[] signature)
    {
        ByteWriter w = new();
        w.WriteByte(TypeHello2);
        w.WriteField(eph);
        w.WriteFixed(nonce);
        w.WriteField(identityPublicKey);
        w.WriteField(signature);
        return w.ToArray();
    }

    public static (byte[] Eph, byte[] Nonce, byte[] IdentityKey, byte[] Signature) ParseHello2(byte[] message)
    {
        ByteReader r = new(message);
        ExpectType(r, TypeHello2);
        byte[] eph = r.ReadField();
        byte[] nonce = r.ReadFixed(ProtocolLimits.HandshakeNonceBytes);
        byte[] id = r.ReadField();
        byte[] sig = r.ReadField();
        r.EnsureEnd();
        return (eph, nonce, id, sig);
    }

    public static byte[] BuildHello3(byte[] identityPublicKey, byte[] signature)
    {
        ByteWriter w = new();
        w.WriteByte(TypeHello3);
        w.WriteField(identityPublicKey);
        w.WriteField(signature);
        return w.ToArray();
    }

    public static (byte[] IdentityKey, byte[] Signature) ParseHello3(byte[] message)
    {
        ByteReader r = new(message);
        ExpectType(r, TypeHello3);
        byte[] id = r.ReadField();
        byte[] sig = r.ReadField();
        r.EnsureEnd();
        return (id, sig);
    }

    private static void ExpectType(ByteReader r, byte expected)
    {
        if (r.ReadByte() != expected)
        {
            throw new ProtocolViolationException("Type de message de handshake inattendu.");
        }
    }

    private static byte[] Concat(params byte[][] parts)
    {
        byte[] result = new byte[parts.Sum(p => p.Length)];
        int offset = 0;
        foreach (byte[] p in parts)
        {
            p.CopyTo(result, offset);
            offset += p.Length;
        }

        return result;
    }
}

/// <summary>Côté qui ouvre la connexion. Usage : <c>Start()</c> → envoyer ; recevoir Hello2 → <c>ProcessHello2()</c> → envoyer Hello3 ; puis <see cref="Channel"/>.</summary>
public sealed class HandshakeInitiator : IDisposable
{
    private readonly LocalIdentity _local;
    private readonly string? _expectedRemotePeerId;
    private ECDiffieHellman? _ephemeral;
    private byte[]? _ephPublic;
    private byte[]? _nonce;

    /// <summary>Crée un initiateur. Si <paramref name="expectedRemotePeerId"/> est fourni (annonce de découverte), un autre pair est refusé.</summary>
    public HandshakeInitiator(LocalIdentity local, string? expectedRemotePeerId = null)
    {
        ArgumentNullException.ThrowIfNull(local);
        if (expectedRemotePeerId is not null && !InputValidator.IsValidPeerId(expectedRemotePeerId))
        {
            throw new ArgumentException("PeerId attendu invalide.", nameof(expectedRemotePeerId));
        }

        _local = local;
        _expectedRemotePeerId = expectedRemotePeerId;
    }

    /// <summary>Canal sécurisé, disponible une fois <see cref="ProcessHello2"/> réussi.</summary>
    public SecureChannel? Channel { get; private set; }

    /// <summary>PeerId authentifié du pair distant (non nul seulement après succès).</summary>
    public string? RemotePeerId { get; private set; }

    /// <summary>Produit le premier message.</summary>
    public byte[] Start()
    {
        if (_ephemeral is not null)
        {
            throw new InvalidOperationException("Handshake déjà démarré.");
        }

        _ephemeral = HandshakeCrypto.NewEphemeral();
        _ephPublic = HandshakeCrypto.ExportEphemeral(_ephemeral);
        _nonce = HandshakeCrypto.RandomNonce();
        return HandshakeCrypto.BuildHello1(_ephPublic, _nonce);
    }

    /// <summary>Traite la réponse du pair, authentifie-le, et produit le troisième message.</summary>
    public byte[] ProcessHello2(byte[] hello2)
    {
        ArgumentNullException.ThrowIfNull(hello2);
        if (_ephemeral is null || _ephPublic is null || _nonce is null || Channel is not null)
        {
            throw new InvalidOperationException("Ordre des messages de handshake invalide.");
        }

        (byte[] ephR, byte[] nonceR, byte[] idKeyR, byte[] sigR) = HandshakeCrypto.ParseHello2(hello2);
        string remoteId = PeerIdentity.ComputePeerId(idKeyR);
        if (remoteId == _local.PeerId)
        {
            throw new ProtocolViolationException("Connexion à soi-même refusée.");
        }

        if (_expectedRemotePeerId is not null && remoteId != _expectedRemotePeerId)
        {
            throw new ProtocolViolationException("Le pair ne correspond pas à l'identité attendue.");
        }

        byte[] transcript = HandshakeCrypto.Transcript(_ephPublic, _nonce, ephR, nonceR);
        using (ECDsa idR = PeerIdentity.ImportPublicKey(idKeyR))
        {
            if (!PeerIdentity.VerifySignature(idR, HandshakeCrypto.ResponderSignedData(transcript), sigR))
            {
                throw new ProtocolViolationException("Signature du répondeur invalide.");
            }
        }

        using ECDiffieHellman remoteEph = HandshakeCrypto.ImportEphemeral(ephR);
        (byte[] i2r, byte[] r2i) = HandshakeCrypto.DeriveKeys(_ephemeral, remoteEph, transcript);
        byte[] sigI = _local.Sign(HandshakeCrypto.InitiatorSignedData(transcript, idKeyR));

        Channel = new SecureChannel(sendKey: i2r, recvKey: r2i);
        RemotePeerId = remoteId;
        CryptographicOperations.ZeroMemory(i2r);
        CryptographicOperations.ZeroMemory(r2i);
        return HandshakeCrypto.BuildHello3(_local.PublicKeySpki, sigI);
    }

    /// <inheritdoc />
    public void Dispose() => _ephemeral?.Dispose();
}

/// <summary>Côté qui accepte la connexion. Usage : recevoir Hello1 → <c>ProcessHello1()</c> → envoyer Hello2 ; recevoir Hello3 → <c>ProcessHello3()</c> ; puis <see cref="Channel"/>.</summary>
public sealed class HandshakeResponder : IDisposable
{
    private readonly LocalIdentity _local;
    private ECDiffieHellman? _ephemeral;
    private byte[]? _transcript;
    private byte[]? _i2r;
    private byte[]? _r2i;

    /// <summary>Crée un répondeur pour l'identité locale donnée.</summary>
    public HandshakeResponder(LocalIdentity local)
    {
        ArgumentNullException.ThrowIfNull(local);
        _local = local;
    }

    /// <summary>Canal sécurisé, disponible une fois <see cref="ProcessHello3"/> réussi.</summary>
    public SecureChannel? Channel { get; private set; }

    /// <summary>PeerId authentifié du pair distant (non nul seulement après succès).</summary>
    public string? RemotePeerId { get; private set; }

    /// <summary>Traite Hello1 et produit Hello2 (signé).</summary>
    public byte[] ProcessHello1(byte[] hello1)
    {
        ArgumentNullException.ThrowIfNull(hello1);
        if (_ephemeral is not null)
        {
            throw new InvalidOperationException("Ordre des messages de handshake invalide.");
        }

        (byte[] ephI, byte[] nonceI) = HandshakeCrypto.ParseHello1(hello1);
        using ECDiffieHellman remoteEph = HandshakeCrypto.ImportEphemeral(ephI);

        _ephemeral = HandshakeCrypto.NewEphemeral();
        byte[] ephR = HandshakeCrypto.ExportEphemeral(_ephemeral);
        byte[] nonceR = HandshakeCrypto.RandomNonce();
        _transcript = HandshakeCrypto.Transcript(ephI, nonceI, ephR, nonceR);
        (_i2r, _r2i) = HandshakeCrypto.DeriveKeys(_ephemeral, remoteEph, _transcript);

        byte[] sigR = _local.Sign(HandshakeCrypto.ResponderSignedData(_transcript));
        return HandshakeCrypto.BuildHello2(ephR, nonceR, _local.PublicKeySpki, sigR);
    }

    /// <summary>Traite Hello3 : authentifie l'initiateur. Le canal n'existe qu'après ce succès.</summary>
    public void ProcessHello3(byte[] hello3)
    {
        ArgumentNullException.ThrowIfNull(hello3);
        if (_transcript is null || _i2r is null || _r2i is null || Channel is not null)
        {
            throw new InvalidOperationException("Ordre des messages de handshake invalide.");
        }

        (byte[] idKeyI, byte[] sigI) = HandshakeCrypto.ParseHello3(hello3);
        string remoteId = PeerIdentity.ComputePeerId(idKeyI);
        if (remoteId == _local.PeerId)
        {
            throw new ProtocolViolationException("Connexion à soi-même refusée.");
        }

        using (ECDsa idI = PeerIdentity.ImportPublicKey(idKeyI))
        {
            if (!PeerIdentity.VerifySignature(idI, HandshakeCrypto.InitiatorSignedData(_transcript, _local.PublicKeySpki), sigI))
            {
                throw new ProtocolViolationException("Signature de l'initiateur invalide.");
            }
        }

        Channel = new SecureChannel(sendKey: _r2i, recvKey: _i2r);
        RemotePeerId = remoteId;
        CryptographicOperations.ZeroMemory(_i2r);
        CryptographicOperations.ZeroMemory(_r2i);
    }

    /// <inheritdoc />
    public void Dispose() => _ephemeral?.Dispose();
}
