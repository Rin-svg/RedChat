using System.Security.Cryptography;
using RedChat.Core;
using ProtocolViolationException = RedChat.Core.ProtocolViolationException;

namespace RedChat.Infrastructure;

/// <summary>Publication de statut reçue et entièrement vérifiée depuis un pair authentifié.</summary>
public sealed class StatusViewedEventArgs : EventArgs
{
    /// <summary>Crée les informations d'une consultation authentifiée.</summary>
    public StatusViewedEventArgs(string viewerPeerId, string statusId)
    {
        ViewerPeerId = viewerPeerId;
        StatusId = statusId;
    }

    /// <summary>Identité du contact ayant consulté le statut.</summary>
    public string ViewerPeerId { get; }
    /// <summary>Identifiant du statut consulté.</summary>
    public string StatusId { get; }
}

/// <summary>Informations d’un retrait émis par l’auteur authentifié d’un statut.</summary>
public sealed class StatusDeletedEventArgs : EventArgs
{
    /// <summary>Crée les informations d'un retrait authentifié.</summary>
    public StatusDeletedEventArgs(string ownerPeerId, string statusId)
    {
        OwnerPeerId = ownerPeerId;
        StatusId = statusId;
    }
    /// <summary>Identité de l’auteur issue du handshake.</summary>
    public string OwnerPeerId { get; }
    /// <summary>Identifiant de la publication retirée.</summary>
    public string StatusId { get; }
}

/// <summary>Informations d'une publication reçue et vérifiée.</summary>
public sealed class StatusReceivedEventArgs : EventArgs
{
    /// <summary>Crée les informations de réception.</summary>
    public StatusReceivedEventArgs(string senderPeerId, EphemeralStatus status)
    {
        SenderPeerId = senderPeerId;
        Status = status;
    }

    /// <summary>Identité authentifiée de l'expéditeur.</summary>
    public string SenderPeerId { get; }
    /// <summary>Publication vérifiée.</summary>
    public EphemeralStatus Status { get; }
}

/// <summary>
/// Réception en mémoire des statuts privés. L'auteur est toujours l'identité issue du handshake,
/// jamais une identité déclarée dans le paquet. Les règles d'audience ne sont jamais reçues.
/// </summary>
public sealed class StatusTransferService : IDisposable
{
    private const int MaxConcurrentTransfers = 2;
    private const long MaxBufferedBytes = 40L * 1024 * 1024;
    private const int MaxRememberedStatuses = 512;
    private static readonly TimeSpan TransferTimeout = TimeSpan.FromMinutes(10);
    private readonly string _selfPeerId;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private readonly Dictionary<string, IncomingStatus> _incoming = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _completed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _receivedReceipts = new(StringComparer.Ordinal);
    private readonly Timer _cleanupTimer;
    private long _bufferedBytes;
    private bool _disposed;

    private sealed class IncomingStatus
    {
        public required string SenderPeerId { get; init; }
        public required string StatusId { get; init; }
        public required StatusContentKind ContentKind { get; init; }
        public required string Text { get; init; }
        public required string MediaName { get; init; }
        public required int MediaSize { get; init; }
        public required string Sha256 { get; init; }
        public required DateTimeOffset CreatedAt { get; init; }
        public required DateTimeOffset ExpiresAt { get; init; }
        public required byte[] Buffer { get; init; }
        public required bool[] Received { get; init; }
        public required int ChunkCount { get; init; }
        public int ReceivedCount { get; set; }
        public DateTimeOffset LastUpdated { get; set; }
    }

    /// <summary>Crée le service de réception des statuts pour une identité locale.</summary>
    /// <param name="selfPeerId">Identifiant cryptographique local.</param>
    /// <param name="clock">Horloge injectable pour les tests.</param>
    public StatusTransferService(string selfPeerId, TimeProvider? clock = null)
    {
        if (!InputValidator.IsValidPeerId(selfPeerId))
        {
            throw new ArgumentException("PeerId local invalide.", nameof(selfPeerId));
        }
        _selfPeerId = selfPeerId;
        _clock = clock ?? TimeProvider.System;
        _cleanupTimer = new Timer(_ => Cleanup(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>Déclenché uniquement après réception complète et vérification du contenu.</summary>
    public event EventHandler<StatusReceivedEventArgs>? StatusReceived;

    /// <summary>Déclenché après validation d’un reçu de consultation envoyé par un pair authentifié.</summary>
    public event EventHandler<StatusViewedEventArgs>? StatusViewed;

    /// <summary>Déclenché lorsqu’un auteur authentifié retire une publication reçue.</summary>
    public event EventHandler<StatusDeletedEventArgs>? StatusDeleted;

    /// <summary>Traite une publication ou un bloc provenant du pair authentifié par le transport.</summary>
    public void Receive(string senderPeerId, Packet packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!InputValidator.IsValidPeerId(senderPeerId) || senderPeerId == _selfPeerId)
        {
            throw new ProtocolViolationException("Auteur du statut invalide.");
        }
        if (packet.ConversationId != _selfPeerId)
        {
            throw new ProtocolViolationException("Le statut n'est pas adressé à cet appareil.");
        }

        DateTimeOffset now = _clock.GetUtcNow();
        PacketSerializer.Validate(packet, now);
        if (packet.Kind == PacketKind.StatusPublish)
        {
            ReceivePublish(senderPeerId, packet, now);
            return;
        }
        if (packet.Kind == PacketKind.StatusMediaChunk)
        {
            ReceiveChunk(senderPeerId, packet, now);
            return;
        }
        if (packet.Kind == PacketKind.StatusViewReceipt)
        {
            ReceiveViewReceipt(senderPeerId, packet, now);
            return;
        }
        if (packet.Kind == PacketKind.StatusDelete)
        {
            ReceiveDelete(senderPeerId, packet, now);
            return;
        }
        throw new ArgumentException("Paquet de statut non pris en charge.", nameof(packet));
    }

    /// <summary>Libère le minuteur et efface les médias incomplets conservés en mémoire.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _cleanupTimer.Dispose();
            foreach (IncomingStatus item in _incoming.Values)
            {
                CryptographicOperations.ZeroMemory(item.Buffer);
            }
            _incoming.Clear();
            _completed.Clear();
            _receivedReceipts.Clear();
            _bufferedBytes = 0;
        }
        GC.SuppressFinalize(this);
    }


    private void ReceiveViewReceipt(string viewerPeerId, Packet packet, DateTimeOffset now)
    {
        string replayKey = viewerPeerId + ":" + packet.Id;
        lock (_gate)
        {
            CleanupExpiredLocked(now);
            if (_receivedReceipts.ContainsKey(replayKey))
            {
                return;
            }
            _receivedReceipts[replayKey] = now.Add(EphemeralStatus.Lifetime);
            while (_receivedReceipts.Count > 2048)
            {
                _receivedReceipts.Remove(_receivedReceipts.Keys.First());
            }
        }

        StatusViewed?.Invoke(this, new StatusViewedEventArgs(viewerPeerId, packet.StatusId!));
    }


    private void ReceiveDelete(string ownerPeerId, Packet packet, DateTimeOffset now)
    {
        string replayKey = ownerPeerId + ":delete:" + packet.Id;
        lock (_gate)
        {
            CleanupExpiredLocked(now);
            if (_receivedReceipts.ContainsKey(replayKey))
            {
                return;
            }
            _receivedReceipts[replayKey] = now.Add(EphemeralStatus.Lifetime);
            while (_receivedReceipts.Count > 2048)
            {
                _receivedReceipts.Remove(_receivedReceipts.Keys.First());
            }
        }

        // L'identité de l'auteur vient exclusivement du pair authentifié par le transport.
        StatusDeleted?.Invoke(this, new StatusDeletedEventArgs(ownerPeerId, packet.StatusId!));
    }

    private void ReceivePublish(string senderPeerId, Packet packet, DateTimeOffset now)
    {
        DateTimeOffset created = DateTimeOffset.FromUnixTimeMilliseconds(packet.StatusCreatedAtUnixMs!.Value);
        DateTimeOffset expires = DateTimeOffset.FromUnixTimeMilliseconds(packet.StatusExpiresAtUnixMs!.Value);
        if (created > now || expires <= now)
        {
            throw new ProtocolViolationException("Ce statut n'est pas actuellement valable.");
        }

        if (packet.StatusContentKind == StatusContentKind.Text)
        {
            EphemeralStatus status = new()
            {
                Id = packet.StatusId!, OwnerPeerId = senderPeerId, ContentKind = StatusContentKind.Text,
                Text = packet.StatusText!, CreatedAt = created, ExpiresAt = expires,
            };
            status = status.Validate(now);
            lock (_gate)
            {
                EnsureNew(senderPeerId, packet.StatusId!);
                RememberCompleted(senderPeerId, packet.StatusId!, expires);
            }
            StatusReceived?.Invoke(this, new StatusReceivedEventArgs(senderPeerId, status));
            return;
        }

        int size = checked((int)packet.StatusMediaSize!.Value);
        int count = checked((size + ProtocolLimits.StatusMediaChunkBytes - 1) / ProtocolLimits.StatusMediaChunkBytes);
        string key = MakeKey(senderPeerId, packet.StatusId!);
        lock (_gate)
        {
            CleanupExpiredLocked(now);
            EnsureNew(senderPeerId, packet.StatusId!);
            if (_incoming.Count >= MaxConcurrentTransfers || _bufferedBytes + size > MaxBufferedBytes)
            {
                throw new ProtocolViolationException("Quota de réception des statuts atteint.");
            }
            IncomingStatus incoming = new()
            {
                SenderPeerId = senderPeerId, StatusId = packet.StatusId!, ContentKind = packet.StatusContentKind!.Value,
                Text = packet.StatusText!, MediaName = packet.StatusMediaName!, MediaSize = size,
                Sha256 = packet.StatusMediaSha256!, CreatedAt = created, ExpiresAt = expires,
                Buffer = new byte[size], Received = new bool[count], ChunkCount = count, LastUpdated = now,
            };
            _incoming.Add(key, incoming);
            _bufferedBytes += size;
        }
    }

    private void ReceiveChunk(string senderPeerId, Packet packet, DateTimeOffset now)
    {
        StatusReceivedEventArgs? completed = null;
        string key = MakeKey(senderPeerId, packet.StatusId!);
        lock (_gate)
        {
            CleanupExpiredLocked(now);
            if (!_incoming.TryGetValue(key, out IncomingStatus? incoming))
            {
                throw new ProtocolViolationException("Bloc reçu sans métadonnées de statut correspondantes.");
            }
            if (incoming.MediaSize != packet.StatusMediaSize || incoming.Sha256 != packet.StatusMediaSha256
                || incoming.ChunkCount != packet.StatusChunkCount)
            {
                throw new ProtocolViolationException("Les métadonnées des blocs du statut sont incohérentes.");
            }

            int index = packet.StatusChunkIndex!.Value;
            if (incoming.Received[index])
            {
                throw new ProtocolViolationException("Bloc de statut reçu en double.");
            }
            int offset = checked(index * ProtocolLimits.StatusMediaChunkBytes);
            packet.StatusChunkData!.CopyTo(incoming.Buffer, offset);
            incoming.Received[index] = true;
            incoming.ReceivedCount++;
            incoming.LastUpdated = now;

            if (incoming.ReceivedCount == incoming.ChunkCount)
            {
                byte[] actualHash = SHA256.HashData(incoming.Buffer);
                byte[] expectedHash = Convert.FromHexString(incoming.Sha256);
                if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
                {
                    RemoveIncoming(key, incoming);
                    throw new ProtocolViolationException("Empreinte SHA-256 du média de statut incorrecte.");
                }

                EphemeralStatus status = new()
                {
                    Id = incoming.StatusId, OwnerPeerId = senderPeerId, ContentKind = incoming.ContentKind,
                    Text = incoming.Text, MediaBytes = incoming.Buffer.ToArray(),
                    CreatedAt = incoming.CreatedAt, ExpiresAt = incoming.ExpiresAt,
                };
                try { status = status.Validate(now); }
                catch (InvalidDataException ex)
                {
                    RemoveIncoming(key, incoming);
                    throw new ProtocolViolationException("Le média du statut a été rejeté.", ex);
                }
                RemoveIncoming(key, incoming);
                RememberCompleted(senderPeerId, incoming.StatusId, incoming.ExpiresAt);
                completed = new StatusReceivedEventArgs(senderPeerId, status);
            }
        }
        if (completed is not null)
        {
            StatusReceived?.Invoke(this, completed);
        }
    }

    private void EnsureNew(string sender, string statusId)
    {
        string key = MakeKey(sender, statusId);
        if (_completed.TryGetValue(key, out DateTimeOffset prior) && prior > _clock.GetUtcNow())
        {
            throw new ProtocolViolationException("Publication de statut déjà reçue.");
        }
        if (_incoming.ContainsKey(key))
        {
            throw new ProtocolViolationException("Identifiant de statut déjà en cours de réception.");
        }
    }

    private void RememberCompleted(string sender, string statusId, DateTimeOffset expires)
    {
        _completed[MakeKey(sender, statusId)] = expires;
        while (_completed.Count > MaxRememberedStatuses)
        {
            _completed.Remove(_completed.Keys.First());
        }
    }

    private void Cleanup()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                CleanupExpiredLocked(_clock.GetUtcNow());
            }
        }
    }

    private void CleanupExpiredLocked(DateTimeOffset now)
    {
        foreach ((string key, IncomingStatus value) in _incoming.ToArray())
        {
            if (now - value.LastUpdated > TransferTimeout || value.ExpiresAt <= now)
            {
                RemoveIncoming(key, value);
            }
        }
        foreach (string key in _completed.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToArray())
        {
            _completed.Remove(key);
        }
        foreach (string key in _receivedReceipts.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToArray())
        {
            _receivedReceipts.Remove(key);
        }
    }

    private void RemoveIncoming(string key, IncomingStatus value)
    {
        _incoming.Remove(key);
        _bufferedBytes -= value.Buffer.Length;
        CryptographicOperations.ZeroMemory(value.Buffer);
    }

    private static string MakeKey(string sender, string statusId) => sender + ":" + statusId;
}
