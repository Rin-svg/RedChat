using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using RedChat.Core;

namespace RedChat.Infrastructure;

/// <summary>Un pair a annoncé sa présence sur le réseau local.</summary>
public sealed class PeerDiscoveredEventArgs : EventArgs
{
    /// <summary>Crée les données d'événement.</summary>
    public PeerDiscoveredEventArgs(string peerId, IPEndPoint endpoint)
    {
        PeerId = peerId;
        Endpoint = endpoint;
    }

    /// <summary>PeerId ANNONCÉ (non authentifié : le handshake TCP le vérifiera).</summary>
    public string PeerId { get; }

    /// <summary>Adresse IP source du datagramme + port TCP annoncé.</summary>
    public IPEndPoint Endpoint { get; }
}

/// <summary>
/// Découverte des pairs par diffusion UDP. Chaque nœud annonce « REDCHAT1 &lt;PeerId&gt; &lt;portTCP&gt;» toutes les quelques secondes.
/// <para>SÉCURITÉ : une annonce n'est qu'un INDICE, jamais une preuve d'identité (n'importe qui peut forger un datagramme). La connexion
/// suivante passe le handshake en exigeant le PeerId annoncé : un usurpateur échoue à signer. On n'accepte que des adresses source de réseau
/// local, un format strict et court, et on limite la cadence par pair.</para>
/// </summary>
public sealed class UdpDiscovery : IDisposable
{
    /// <summary>Port UDP par défaut.</summary>
    public const int DefaultPort = 45821;

    /// <summary>Taille maximale acceptée pour un datagramme d'annonce.</summary>
    public const int MaxDatagramBytes = 128;

    private const string Prefix = "REDCHAT1";
    private const int MaxTrackedPeers = 1024;
    private static readonly long MinRepeatMs = 1000;

    private readonly string _selfPeerId;
    private readonly int _tcpPort;
    private readonly int _udpPort;
    private readonly IPAddress _sendTo;
    private readonly IPAddress _bind;
    private readonly TimeSpan _interval;
    private readonly Dictionary<string, long> _lastSeen = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();
    private UdpClient? _udp;
    private Task? _receive;
    private Task? _announce;
    private bool _disposed;

    /// <summary>Crée la découverte. Les paramètres facultatifs servent aux tests (boucle locale) ; par défaut : diffusion générale.</summary>
    public UdpDiscovery(string selfPeerId, int tcpPort, int udpPort = DefaultPort, IPAddress? sendTo = null, IPAddress? bindAddress = null, TimeSpan? interval = null)
    {
        if (!InputValidator.IsValidPeerId(selfPeerId))
        {
            throw new ArgumentException("PeerId invalide.", nameof(selfPeerId));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(tcpPort, ProtocolLimits.MinPort);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(tcpPort, 65535);
        _selfPeerId = selfPeerId;
        _tcpPort = tcpPort;
        _udpPort = udpPort;
        _sendTo = sendTo ?? IPAddress.Broadcast;
        _bind = bindAddress ?? IPAddress.Any;
        _interval = interval ?? TimeSpan.FromSeconds(3);
    }

    /// <summary>Un pair (autre que nous) a été entendu.</summary>
    public event EventHandler<PeerDiscoveredEventArgs>? PeerDiscovered;

    /// <summary>Construit le datagramme d'annonce.</summary>
    public static byte[] BuildAnnouncement(string peerId, int tcpPort) =>
        Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{Prefix} {peerId} {tcpPort}"));

    /// <summary>Analyse stricte d'un datagramme. Faux pour tout ce qui n'est pas exactement une annonce valide d'une adresse locale.</summary>
    public static bool TryParseAnnouncement(ReadOnlySpan<byte> data, IPAddress from, out string peerId, out int port)
    {
        peerId = string.Empty;
        port = 0;
        ArgumentNullException.ThrowIfNull(from);
        if (data.Length == 0 || data.Length > MaxDatagramBytes || !InputValidator.IsLanAddress(from))
        {
            return false;
        }

        foreach (byte b in data)
        {
            if (b < 0x20 || b > 0x7E)
            {
                return false; // ASCII imprimable uniquement
            }
        }

        string[] parts = Encoding.ASCII.GetString(data).Split(' ');
        if (parts.Length != 3 || parts[0] != Prefix || !InputValidator.IsValidPeerId(parts[1]))
        {
            return false;
        }

        string digits = parts[2];
        if (digits.Length is 0 or > 5 || !digits.All(char.IsAsciiDigit)
            || !int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int p)
            || p < ProtocolLimits.MinPort || p > 65535)
        {
            return false;
        }

        peerId = parts[1];
        port = p;
        return true;
    }

    /// <summary>Démarre l'écoute et l'annonce périodique.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_udp is not null)
        {
            throw new InvalidOperationException("Déjà démarré.");
        }

        UdpClient udp = new() { EnableBroadcast = true, ExclusiveAddressUse = false };
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(_bind, _udpPort));
        _udp = udp;
        _receive = Task.Run(() => ReceiveLoopAsync(udp, _cts.Token));
        _announce = Task.Run(() => AnnounceLoopAsync(udp, _cts.Token));
    }

    /// <summary>Arrête la découverte.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();
        _udp?.Dispose();
        try
        {
            Task.WaitAll(new[] { _receive, _announce }.Where(t => t is not null).Cast<Task>().ToArray(), TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
            // les tâches se terminent par annulation / socket fermé : sans conséquence.
        }

        _cts.Dispose();
    }

    private async Task AnnounceLoopAsync(UdpClient udp, CancellationToken ct)
    {
        byte[] datagram = BuildAnnouncement(_selfPeerId, _tcpPort);
        IPEndPoint target = new(_sendTo, _udpPort);
        using PeriodicTimer timer = new(_interval);
        try
        {
            do
            {
                try
                {
                    _ = await udp.SendAsync(datagram, target, ct).ConfigureAwait(false);
                }
                catch (SocketException)
                {
                    // réseau momentanément indisponible : on réessaiera au prochain tour.
                }
            }
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // arrêt demandé.
        }
    }

    private async Task ReceiveLoopAsync(UdpClient udp, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try
            {
                r = await udp.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                continue; // ex. ICMP « port injoignable » reçu en retour : on ignore.
            }

            if (!TryParseAnnouncement(r.Buffer, r.RemoteEndPoint.Address, out string peerId, out int port) || peerId == _selfPeerId)
            {
                continue;
            }

            if (!ShouldReport(peerId))
            {
                continue;
            }

            PeerDiscovered?.Invoke(this, new PeerDiscoveredEventArgs(peerId, new IPEndPoint(r.RemoteEndPoint.Address, port)));
        }
    }

    /// <summary>Limite la cadence par pair (1/s) et la taille de la table (un attaquant ne peut pas la faire grossir sans fin).</summary>
    private bool ShouldReport(string peerId)
    {
        long now = Environment.TickCount64;
        if (_lastSeen.Count >= MaxTrackedPeers)
        {
            _lastSeen.Clear();
        }

        if (_lastSeen.TryGetValue(peerId, out long last) && now - last < MinRepeatMs)
        {
            return false;
        }

        _lastSeen[peerId] = now;
        return true;
    }
}
