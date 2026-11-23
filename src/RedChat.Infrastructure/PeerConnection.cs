using System.Net;
using System.Net.Sockets;
using RedChat.Core;
using ProtocolViolationException = RedChat.Core.ProtocolViolationException;

namespace RedChat.Infrastructure;

/// <summary>
/// Une connexion TCP déjà authentifiée et chiffrée avec un pair. On n'en obtient une qu'en réussissant le handshake
/// (<see cref="ConnectAsync"/> ou <see cref="AcceptAsync"/>) : il n'existe aucun moyen d'envoyer ou de lire un paquet en clair.
/// </summary>
internal sealed class PeerConnection : IDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly SecureChannel _channel;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    private PeerConnection(TcpClient client, SecureChannel channel, string remotePeerId, bool weInitiated)
    {
        _client = client;
        _stream = client.GetStream();
        _channel = channel;
        RemotePeerId = remotePeerId;
        WeInitiated = weInitiated;
    }

    /// <summary>Identité du pair, prouvée par signature pendant le handshake.</summary>
    public string RemotePeerId { get; }

    /// <summary>Vrai si c'est nous qui avons ouvert la connexion.</summary>
    public bool WeInitiated { get; }

    /// <summary>Ouvre une connexion vers <paramref name="endpoint"/>. Si <paramref name="expectedPeerId"/> est fourni, tout autre pair est refusé.</summary>
    public static async Task<PeerConnection> ConnectAsync(IPEndPoint endpoint, LocalIdentity identity, string? expectedPeerId, CancellationToken ct)
    {
        TcpClient client = new() { NoDelay = true };
        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ProtocolLimits.HandshakeTimeout);
            await client.ConnectAsync(endpoint, timeout.Token).ConfigureAwait(false);
            NetworkStream stream = client.GetStream();
            using HandshakeInitiator hs = new(identity, expectedPeerId);
            await FrameCodec.WriteFrameAsync(stream, hs.Start(), timeout.Token).ConfigureAwait(false);
            byte[] hello2 = await ReadHandshakeFrameAsync(stream, timeout.Token).ConfigureAwait(false);
            byte[] hello3 = hs.ProcessHello2(hello2);
            await FrameCodec.WriteFrameAsync(stream, hello3, timeout.Token).ConfigureAwait(false);
            return new PeerConnection(client, hs.Channel!, hs.RemotePeerId!, weInitiated: true);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            client.Dispose();
            throw new TimeoutException("Délai du handshake dépassé.");
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>Joue le rôle de répondeur sur une connexion entrante.</summary>
    public static async Task<PeerConnection> AcceptAsync(TcpClient client, LocalIdentity identity, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        try
        {
            client.NoDelay = true;
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ProtocolLimits.HandshakeTimeout);
            NetworkStream stream = client.GetStream();
            using HandshakeResponder hs = new(identity);
            byte[] hello1 = await ReadHandshakeFrameAsync(stream, timeout.Token).ConfigureAwait(false);
            await FrameCodec.WriteFrameAsync(stream, hs.ProcessHello1(hello1), timeout.Token).ConfigureAwait(false);
            byte[] hello3 = await ReadHandshakeFrameAsync(stream, timeout.Token).ConfigureAwait(false);
            hs.ProcessHello3(hello3);
            return new PeerConnection(client, hs.Channel!, hs.RemotePeerId!, weInitiated: false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            client.Dispose();
            throw new TimeoutException("Délai du handshake dépassé.");
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>Envoie un paquet (sérialisé, scellé, encadré). Les écritures sont sérialisées : ordre du compteur = ordre sur le fil.</summary>
    public async Task SendAsync(Packet packet, TimeProvider clock, CancellationToken ct)
    {
        byte[] sealedBytes;
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            sealedBytes = _channel.Seal(PacketSerializer.Serialize(packet, clock.GetUtcNow()));
            await FrameCodec.WriteFrameAsync(_stream, sealedBytes, ct).ConfigureAwait(false);
        }
        finally
        {
            _ = _writeGate.Release();
        }
    }

    /// <summary>Lit le prochain paquet. Retourne null si le pair a fermé proprement. Lève <see cref="ProtocolViolationException"/> sur toute anomalie.</summary>
    public async Task<Packet?> ReceiveAsync(TimeProvider clock, CancellationToken ct)
    {
        byte[]? frame = await FrameCodec.ReadFrameAsync(_stream, ct).ConfigureAwait(false);
        if (frame is null)
        {
            return null;
        }

        byte[] plain = _channel.Open(frame);
        return PacketSerializer.Deserialize(plain, clock.GetUtcNow());
    }

    /// <summary>Adresse distante (pour les journaux), si disponible.</summary>
    public EndPoint? RemoteEndPoint => _client.Client?.RemoteEndPoint;

    public void Dispose()
    {
        _channel.Dispose();
        _client.Dispose();
        _writeGate.Dispose();
    }

    private static async Task<byte[]> ReadHandshakeFrameAsync(Stream stream, CancellationToken ct) =>
        await FrameCodec.ReadFrameAsync(stream, ct).ConfigureAwait(false)
        ?? throw new ProtocolViolationException("Connexion fermée pendant le handshake.");
}
