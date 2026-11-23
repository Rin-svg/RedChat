namespace RedChat.Core;

/// <summary>Écrivain binaire minimal pour les messages de handshake (champs à longueur préfixée sur 2 octets).</summary>
internal sealed class ByteWriter
{
    private readonly List<byte> _buffer = new();

    public void WriteByte(byte value) => _buffer.Add(value);

    /// <summary>Écrit un champ de taille fixe connue des deux côtés (pas de préfixe de longueur).</summary>
    public void WriteFixed(ReadOnlySpan<byte> data) => _buffer.AddRange(data);

    /// <summary>Écrit un champ variable précédé de sa longueur (u16, big-endian).</summary>
    public void WriteField(ReadOnlySpan<byte> data)
    {
        if (data.Length > ProtocolLimits.MaxHandshakeFieldBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(data), "Champ de handshake trop grand.");
        }

        _buffer.Add((byte)(data.Length >> 8));
        _buffer.Add((byte)(data.Length & 0xFF));
        _buffer.AddRange(data);
    }

    public byte[] ToArray() => _buffer.ToArray();
}

/// <summary>Lecteur binaire à bornes vérifiées : toute lecture hors limites lève <see cref="ProtocolViolationException"/>.</summary>
internal sealed class ByteReader
{
    private readonly byte[] _data;
    private int _pos;

    public ByteReader(byte[] data) => _data = data;

    public byte ReadByte()
    {
        if (_pos >= _data.Length)
        {
            throw new ProtocolViolationException("Message tronqué.");
        }

        return _data[_pos++];
    }

    public byte[] ReadFixed(int length)
    {
        if (length < 0 || length > _data.Length - _pos)
        {
            throw new ProtocolViolationException("Message tronqué.");
        }

        byte[] result = _data.AsSpan(_pos, length).ToArray();
        _pos += length;
        return result;
    }

    public byte[] ReadField()
    {
        int length = (ReadByte() << 8) | ReadByte();
        if (length > ProtocolLimits.MaxHandshakeFieldBytes)
        {
            throw new ProtocolViolationException("Champ de handshake trop grand.");
        }

        return ReadFixed(length);
    }

    /// <summary>Refuse les octets en trop : un message bien formé est consommé exactement.</summary>
    public void EnsureEnd()
    {
        if (_pos != _data.Length)
        {
            throw new ProtocolViolationException("Octets inattendus en fin de message.");
        }
    }
}
