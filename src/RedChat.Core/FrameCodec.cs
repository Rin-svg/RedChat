using System.Buffers.Binary;

namespace RedChat.Core;

/// <summary>
/// Découpage du flux TCP en trames : 4 octets de longueur (big-endian) puis la charge utile.
/// La longueur annoncée est vérifiée AVANT toute allocation : un pair ne peut pas nous faire réserver 4 Gio.
/// </summary>
public static class FrameCodec
{
    private const int HeaderBytes = 4;

    /// <summary>Écrit une trame (en-tête + charge utile) sur le flux.</summary>
    public static async Task WriteFrameAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (payload.Length == 0 || payload.Length > ProtocolLimits.MaxFrameBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), "Taille de trame invalide.");
        }

        byte[] header = new byte[HeaderBytes];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)payload.Length);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Lit une trame complète. Retourne <c>null</c> si le flux se ferme proprement AVANT le premier octet d'une trame ;
    /// lève <see cref="ProtocolViolationException"/> si la trame est tronquée ou de taille interdite.
    /// </summary>
    public static async Task<byte[]?> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] header = new byte[HeaderBytes];
        int first = await ReadUpToAsync(stream, header, ct).ConfigureAwait(false);
        if (first == 0)
        {
            return null;
        }

        if (first != HeaderBytes)
        {
            throw new ProtocolViolationException("En-tête de trame tronqué.");
        }

        uint length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length == 0 || length > ProtocolLimits.MaxFrameBytes)
        {
            throw new ProtocolViolationException($"Taille de trame interdite : {length}.");
        }

        byte[] payload = new byte[length];
        int read = await ReadUpToAsync(stream, payload, ct).ConfigureAwait(false);
        if (read != payload.Length)
        {
            throw new ProtocolViolationException("Trame tronquée.");
        }

        return payload;
    }

    private static async Task<int> ReadUpToAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }

            total += n;
        }

        return total;
    }
}
