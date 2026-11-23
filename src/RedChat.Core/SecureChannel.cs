using System.Buffers.Binary;
using System.Security.Cryptography;

namespace RedChat.Core;

/// <summary>
/// Canal chiffré et authentifié (AES-256-GCM) entre deux pairs, un compteur par sens de circulation.
/// Format scellé : compteur (8 octets, big-endian) || chiffré || tag (16 octets).
/// Le compteur sert de nonce (jamais réutilisé) ET d'anti-rejeu : TCP livre dans l'ordre, donc on exige
/// exactement le compteur attendu ; un message rejoué, supprimé ou réordonné est rejeté.
/// </summary>
public sealed class SecureChannel : IDisposable
{
    private readonly AesGcm _sendKey;
    private readonly AesGcm _recvKey;
    private readonly Lock _gate = new();
    private ulong _sendCounter;
    private ulong _recvCounter;
    private bool _disposed;

    internal SecureChannel(byte[] sendKey, byte[] recvKey)
    {
        _sendKey = new AesGcm(sendKey, ProtocolLimits.AesTagBytes);
        _recvKey = new AesGcm(recvKey, ProtocolLimits.AesTagBytes);
    }

    /// <summary>Nombre maximal d'octets de clair par message scellé (pour tenir dans une trame).</summary>
    public static int MaxPlaintextBytes => ProtocolLimits.MaxFrameBytes - ProtocolLimits.SealOverheadBytes;

    /// <summary>Chiffre et authentifie un message. Thread-safe.</summary>
    public byte[] Seal(ReadOnlySpan<byte> plaintext)
    {
        if (plaintext.Length > MaxPlaintextBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(plaintext), "Message trop grand pour une trame.");
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_sendCounter == ulong.MaxValue)
            {
                // Ne jamais réutiliser un nonce : au bout de 2^64-1 messages (théorique), il faut refaire un handshake.
                throw new InvalidOperationException("Compteur d'envoi épuisé : rétablir la connexion.");
            }

            byte[] output = new byte[ProtocolLimits.CounterBytes + plaintext.Length + ProtocolLimits.AesTagBytes];
            Span<byte> header = output.AsSpan(0, ProtocolLimits.CounterBytes);
            BinaryPrimitives.WriteUInt64BigEndian(header, _sendCounter);

            Span<byte> nonce = stackalloc byte[ProtocolLimits.AesNonceBytes];
            BuildNonce(_sendCounter, nonce);

            _sendKey.Encrypt(
                nonce,
                plaintext,
                output.AsSpan(ProtocolLimits.CounterBytes, plaintext.Length),
                output.AsSpan(ProtocolLimits.CounterBytes + plaintext.Length, ProtocolLimits.AesTagBytes),
                header);   // le compteur est authentifié comme donnée associée

            _sendCounter++;
            return output;
        }
    }

    /// <summary>Vérifie et déchiffre un message reçu. Lève <see cref="ProtocolViolationException"/> si falsifié, rejoué ou mal ordonné.</summary>
    public byte[] Open(ReadOnlySpan<byte> sealedMessage)
    {
        if (sealedMessage.Length < ProtocolLimits.SealOverheadBytes || sealedMessage.Length > ProtocolLimits.MaxFrameBytes)
        {
            throw new ProtocolViolationException("Message scellé de taille invalide.");
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ReadOnlySpan<byte> header = sealedMessage[..ProtocolLimits.CounterBytes];
            ulong counter = BinaryPrimitives.ReadUInt64BigEndian(header);
            if (counter != _recvCounter)
            {
                throw new ProtocolViolationException("Compteur inattendu (rejeu, perte ou réordonnancement).");
            }

            int cipherLength = sealedMessage.Length - ProtocolLimits.SealOverheadBytes;
            ReadOnlySpan<byte> cipher = sealedMessage.Slice(ProtocolLimits.CounterBytes, cipherLength);
            ReadOnlySpan<byte> tag = sealedMessage[^ProtocolLimits.AesTagBytes..];

            Span<byte> nonce = stackalloc byte[ProtocolLimits.AesNonceBytes];
            BuildNonce(counter, nonce);

            byte[] plaintext = new byte[cipherLength];
            try
            {
                _recvKey.Decrypt(nonce, cipher, tag, plaintext, header);
            }
            catch (CryptographicException ex)
            {
                throw new ProtocolViolationException("Authentification du message échouée.", ex);
            }

            // Le compteur n'avance QU'APRÈS une authentification réussie : un paquet forgé ne désynchronise pas le canal.
            _recvCounter++;
            return plaintext;
        }
    }

    private static void BuildNonce(ulong counter, Span<byte> nonce)
    {
        nonce.Clear();
        BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], counter);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _sendKey.Dispose();
            _recvKey.Dispose();
        }
    }
}
