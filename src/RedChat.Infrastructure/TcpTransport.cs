using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using RedChat.Core;
using ProtocolViolationException = RedChat.Core.ProtocolViolationException;

namespace RedChat.Infrastructure;

/// <summary>Informe l'application d'une connexion ouverte ou fermée.</summary>
public sealed class PeerConnectionEventArgs : EventArgs
{
    /// <summary>Crée les données d'événement.</summary>
    public PeerConnectionEventArgs(string peerId) => PeerId = peerId;

    /// <summary>Pair concerné (identité authentifiée).</summary>
    public string PeerId { get; }
}

/// <summary>Message de diagnostic non fatal (échec de connexion, pair coupé pour violation du protocole…).</summary>
public sealed class PeerPacketEventArgs : EventArgs
{
    /// <summary>Crée les données d'un paquet reçu sur un canal authentifié.</summary>
    public PeerPacketEventArgs(string peerId, Packet packet)
    {
        PeerId = peerId;
        Packet = packet;
    }

    /// <summary>Identité authentifiée par le handshake.</summary>
    public string PeerId { get; }

    /// <summary>Paquet applicatif validé.</summary>
    public Packet Packet { get; }
}

/// <summary>Message de diagnostic non fatal (échec de connexion, pair coupé pour violation du protocole…).</summary>
public sealed class TransportDiagnosticEventArgs : EventArgs
{
    /// <summary>Crée les données d'événement.</summary>
    public TransportDiagnosticEventArgs(string message) => Message = message;

    /// <summary>Texte lisible, sans donnée secrète.</summary>
    public string Message { get; }
}

/// <summary>
/// Écoute les connexions entrantes, ouvre les sortantes, authentifie chaque pair par handshake puis relaie les paquets.
/// <para><see cref="TrySend"/> ne bloque jamais : le paquet entre dans une file bornée par pair, vidée par une tâche d'écriture dédiée.
/// Un pair lent ne peut donc pas figer le <see cref="ChatService"/> (qui appelle <see cref="TrySend"/> sous son verrou).</para>
/// </summary>
public sealed class TcpTransport : ITransport, IAsyncDisposable
{
    /// <summary>Connexions simultanées maximales (établies + handshakes en cours).</summary>
    public const int MaxConnections = 64;

    /// <summary>Handshakes simultanés maximaux depuis une même adresse IP.</summary>
    public const int MaxPendingPerAddress = 4;

    /// <summary>Taille de la file d'écriture par pair.</summary>
    public const int OutQueueSize = 1024;

    private sealed class Session
    {
        public Session(PeerConnection conn)
        {
            Conn = conn;
            Out = Channel.CreateBounded<Packet>(new BoundedChannelOptions(OutQueueSize) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        }

        public PeerConnection Conn { get; }

        public Channel<Packet> Out { get; }

        public CancellationTokenSource Cts { get; } = new();

        public Task[] Loops { get; set; } = Array.Empty<Task>();
    }

    private readonly LocalIdentity _identity;
    private readonly IPAddress _bind;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<IPAddress, int> _pendingByAddress = new();
    private readonly CancellationTokenSource _cts = new();
    private int _active;      // sessions + handshakes en cours
    private TcpListener? _listener;
    private Task? _acceptLoop;
    private ChatService? _chat;
    private bool _disposed;

    /// <summary>Crée le transport pour l'identité locale ; <paramref name="bindAddress"/> = interface d'écoute (défaut : toutes).</summary>
    public TcpTransport(LocalIdentity identity, IPAddress? bindAddress = null, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        _identity = identity;
        _bind = bindAddress ?? IPAddress.Any;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Port TCP d'écoute (éphémère, choisi par le système) une fois <see cref="Start"/> appelé.</summary>
    public int Port { get; private set; }

    /// <summary>Créneaux de connexion occupés (sessions établies + handshakes en cours). Sert aux diagnostics et aux tests de fuite.</summary>
    public int ActiveSlots
    {
        get
        {
            lock (_gate)
            {
                return _active;
            }
        }
    }

    /// <summary>Un pair vient de s'authentifier et sa connexion est prête à servir.</summary>
    public event EventHandler<PeerConnectionEventArgs>? PeerConnected;

    /// <summary>La connexion d'un pair s'est fermée (et n'a pas été remplacée par une autre).</summary>
    public event EventHandler<PeerConnectionEventArgs>? PeerDisconnected;

    /// <summary>Événement d'information/diagnostic.</summary>
    public event EventHandler<TransportDiagnosticEventArgs>? Diagnostic;

    /// <summary>Paquet reçu après déchiffrement et validation, avec l'identité authentifiée du pair.</summary>
    public event EventHandler<PeerPacketEventArgs>? PacketReceived;

    /// <summary>Instantané des PeerId actuellement connectés.</summary>
    public IReadOnlyList<string> GetConnectedPeerIds()
    {
        lock (_gate)
        {
            return _sessions.Keys.ToArray();
        }
    }

    /// <summary>Relie le transport au service de conversation : paquets reçus → <see cref="ChatService.Receive"/>, connexions → <see cref="ChatService.OnPeerConnected"/>.</summary>
    public void Attach(ChatService chat)
    {
        ArgumentNullException.ThrowIfNull(chat);
        _chat = chat;
    }

    /// <summary>Démarre l'écoute sur un port éphémère.</summary>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_listener is not null)
            {
                throw new InvalidOperationException("Déjà démarré.");
            }

            _listener = new TcpListener(_bind, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _acceptLoop = Task.Run(() => AcceptLoopAsync(_listener, _cts.Token));
        }
    }

    /// <summary>Vrai si une connexion authentifiée est ouverte avec ce pair.</summary>
    public bool IsConnected(string peerId)
    {
        lock (_gate)
        {
            return _sessions.ContainsKey(peerId);
        }
    }

    /// <inheritdoc />
    public bool TrySend(string peerId, Packet packet)
    {
        Session? s;
        lock (_gate)
        {
            _ = _sessions.TryGetValue(peerId, out s);
        }

        return s is not null && s.Out.Writer.TryWrite(packet);
    }

    /// <summary>Ouvre une connexion vers un pair ; retourne son PeerId authentifié. Lève une exception si la connexion ou le handshake échoue.</summary>
    public async Task<string> ConnectAsync(IPEndPoint endpoint, string? expectedPeerId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!TryReserveSlot(null))
        {
            throw new InvalidOperationException("Trop de connexions.");
        }

        bool released = false;
        try
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
            PeerConnection conn = await PeerConnection.ConnectAsync(endpoint, _identity, expectedPeerId, linked.Token).ConfigureAwait(false);
            released = true; // le créneau est désormais « porté » par la session (ou rendu par Register si elle est rejetée)
            Register(conn);
            return conn.RemotePeerId;
        }
        finally
        {
            if (!released)
            {
                ReleaseSlot(null);
            }
        }
    }

    /// <summary>Ferme tout : écoute, connexions, tâches.</summary>
    public async ValueTask DisposeAsync()
    {
        Session[] sessions;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            sessions = _sessions.Values.ToArray();
            _sessions.Clear();
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        _listener?.Stop();
        foreach (Session s in sessions)
        {
            Close(s);
        }

        List<Task> waits = new();
        if (_acceptLoop is not null)
        {
            waits.Add(_acceptLoop);
        }

        waits.AddRange(sessions.SelectMany(s => s.Loops));
        try
        {
            await Task.WhenAll(waits).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException or SocketException or ObjectDisposedException)
        {
            // arrêt « au mieux » : les tâches restantes se terminent seules (les sockets sont fermés).
        }

        _cts.Dispose();
    }

    // ───────────────────────── Entrant ─────────────────────────

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                if (ct.IsCancellationRequested || ex is ObjectDisposedException)
                {
                    return;
                }

                continue;
            }

            IPAddress? remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
            if (remote is null || !TryReserveSlot(remote))
            {
                client.Dispose();
                Report("Connexion entrante refusée (limite atteinte).");
                continue;
            }

            _ = Task.Run(() => HandleInboundAsync(client, remote, ct), CancellationToken.None);
        }
    }

    private async Task HandleInboundAsync(TcpClient client, IPAddress remote, CancellationToken ct)
    {
        PeerConnection? conn = null;
        try
        {
            conn = await PeerConnection.AcceptAsync(client, _identity, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ProtocolViolationException or IOException or SocketException or TimeoutException or OperationCanceledException or ObjectDisposedException)
        {
            Report($"Handshake entrant échoué : {ex.GetType().Name}.");
        }
        finally
        {
            ReleaseSlot(remote); // fin du handshake : on libère le compteur « par adresse » (le créneau global passe à la session)
        }

        if (conn is null)
        {
            ReleaseSlot(null);
            return;
        }

        Register(conn);
    }

    private bool TryReserveSlot(IPAddress? remote)
    {
        lock (_gate)
        {
            if (_disposed || _active >= MaxConnections)
            {
                return false;
            }

            if (remote is not null)
            {
                _ = _pendingByAddress.TryGetValue(remote, out int n);
                if (n >= MaxPendingPerAddress)
                {
                    return false;
                }

                _pendingByAddress[remote] = n + 1;
            }

            _active++;
            return true;
        }
    }

    private void ReleaseSlot(IPAddress? remote)
    {
        lock (_gate)
        {
            if (remote is null)
            {
                _active--;
                return;
            }

            if (_pendingByAddress.TryGetValue(remote, out int n))
            {
                if (n <= 1)
                {
                    _ = _pendingByAddress.Remove(remote);
                }
                else
                {
                    _pendingByAddress[remote] = n - 1;
                }
            }
        }
    }

    // ───────────────────────── Sessions ─────────────────────────

    private void Register(PeerConnection conn)
    {
        Session session = new(conn);
        Session? replaced = null;
        bool keepNew = true;
        lock (_gate)
        {
            if (_disposed)
            {
                keepNew = false;
            }
            else if (_sessions.TryGetValue(conn.RemotePeerId, out Session? existing))
            {
                keepNew = KeepNew(existing.Conn, conn);
                if (keepNew)
                {
                    replaced = existing;
                }
            }

            if (keepNew)
            {
                _sessions[conn.RemotePeerId] = session;
            }
        }

        if (!keepNew)
        {
            conn.Dispose();
            session.Cts.Dispose();
            ReleaseSlot(null);
            return;
        }

        if (replaced is not null)
        {
            Close(replaced);
            ReleaseSlot(null);
        }

        session.Loops = new[]
        {
            Task.Run(() => ReadLoopAsync(session)),
            Task.Run(() => WriteLoopAsync(session)),
        };
        PeerConnected?.Invoke(this, new PeerConnectionEventArgs(conn.RemotePeerId));
        _chat?.OnPeerConnected(conn.RemotePeerId);
    }

    /// <summary>
    /// Deux pairs qui se connectent l'un à l'autre en même temps créent deux connexions : sans règle commune chacun pourrait fermer
    /// une connexion différente et les perdre toutes les deux. Règle : on garde celle ouverte par le PeerId le plus petit ;
    /// si les deux ont le même initiateur (reconnexion), la nouvelle remplace l'ancienne (probablement morte).
    /// </summary>
    private bool KeepNew(PeerConnection existing, PeerConnection fresh)
    {
        if (existing.WeInitiated == fresh.WeInitiated)
        {
            return true;
        }

        string initiatorOfNew = fresh.WeInitiated ? _identity.PeerId : fresh.RemotePeerId;
        string preferred = string.CompareOrdinal(_identity.PeerId, fresh.RemotePeerId) < 0 ? _identity.PeerId : fresh.RemotePeerId;
        return initiatorOfNew == preferred;
    }

    private async Task ReadLoopAsync(Session s)
    {
        string peer = s.Conn.RemotePeerId;
        try
        {
            while (!s.Cts.IsCancellationRequested)
            {
                Packet? p = await s.Conn.ReceiveAsync(_clock, s.Cts.Token).ConfigureAwait(false);
                if (p is null)
                {
                    break;
                }

                if (p.Kind is PacketKind.ProfileUpdate or PacketKind.AttachmentChunk or PacketKind.CallControl)
                {
                    PacketReceived?.Invoke(this, new PeerPacketEventArgs(peer, p));
                }
                else
                {
                    _chat?.Receive(peer, p);
                }
            }
        }
        catch (ProtocolViolationException ex)
        {
            Report($"Pair {Short(peer)} déconnecté (violation du protocole : {ex.Message}).");
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // fermeture normale ou coupure réseau.
        }

        Drop(s);
    }

    private async Task WriteLoopAsync(Session s)
    {
        try
        {
            await foreach (Packet p in s.Out.Reader.ReadAllAsync(s.Cts.Token).ConfigureAwait(false))
            {
                await s.Conn.SendAsync(p, _clock, s.Cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or ProtocolViolationException)
        {
            // coupure : la session est fermée ci-dessous.
        }

        Drop(s);
    }

    /// <summary>Retire la session du registre (si c'est encore la courante), la ferme, et prévient l'application.</summary>
    private void Drop(Session s)
    {
        bool wasCurrent;
        lock (_gate)
        {
            wasCurrent = _sessions.TryGetValue(s.Conn.RemotePeerId, out Session? cur) && ReferenceEquals(cur, s);
            if (wasCurrent)
            {
                _ = _sessions.Remove(s.Conn.RemotePeerId);
            }
        }

        if (!Close(s))
        {
            return;
        }

        if (wasCurrent)
        {
            ReleaseSlot(null);
            PeerDisconnected?.Invoke(this, new PeerConnectionEventArgs(s.Conn.RemotePeerId));
        }
    }

    private static readonly object CloseGate = new();

    private static bool Close(Session s)
    {
        lock (CloseGate)
        {
            if (s.Cts.IsCancellationRequested)
            {
                return false;
            }

            s.Cts.Cancel();
        }

        _ = s.Out.Writer.TryComplete();
        s.Conn.Dispose();
        return true;
    }

    private void Report(string message) => Diagnostic?.Invoke(this, new TransportDiagnosticEventArgs(message));

    private static string Short(string peerId) => peerId.Length > 8 ? peerId[..8] : peerId;
}
