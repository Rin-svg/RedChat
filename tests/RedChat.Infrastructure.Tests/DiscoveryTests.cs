using System.Net;
using System.Net.Sockets;
using Xunit;

namespace RedChat.Infrastructure.Tests;

public class DiscoveryTests
{
    private static readonly string Peer = new('a', 64);
    private static readonly IPAddress Lan = IPAddress.Parse("192.168.1.20");

    [Fact]
    public void Announcement_RoundTrips()
    {
        byte[] data = UdpDiscovery.BuildAnnouncement(Peer, 50123);
        Assert.True(UdpDiscovery.TryParseAnnouncement(data, Lan, out string id, out int port));
        Assert.Equal(Peer, id);
        Assert.Equal(50123, port);
    }

    [Theory]
    [InlineData("REDCHAT2 {P} 50000")]            // mauvais préfixe
    [InlineData("redchat1 {P} 50000")]
    [InlineData("REDCHAT1 {P}")]                  // champ manquant
    [InlineData("REDCHAT1 {P} 50000 x")]          // champ en trop
    [InlineData("REDCHAT1  {P} 50000")]           // double espace
    [InlineData(" REDCHAT1 {P} 50000")]
    [InlineData("REDCHAT1 {P} 50000 ")]
    [InlineData("REDCHAT1 {P} 80")]               // port système
    [InlineData("REDCHAT1 {P} 1023")]
    [InlineData("REDCHAT1 {P} 65536")]
    [InlineData("REDCHAT1 {P} 099999")]           // trop long
    [InlineData("REDCHAT1 {P} +5000")]
    [InlineData("REDCHAT1 {P} -5000")]
    [InlineData("REDCHAT1 {P} 5e4")]
    [InlineData("REDCHAT1 abc 50000")]            // PeerId trop court
    [InlineData("REDCHAT1 {PU} 50000")]           // hexadécimal en majuscules
    [InlineData("REDCHAT1 {P}\n 50000")]          // caractère de contrôle
    [InlineData("")]
    public void Announcement_Malformed_IsRejected(string template)
    {
        string text = template.Replace("{PU}", Peer.ToUpperInvariant().Replace('A', 'A'), StringComparison.Ordinal).Replace("{P}", Peer, StringComparison.Ordinal);
        if (template.Contains("{PU}", StringComparison.Ordinal))
        {
            text = "REDCHAT1 " + new string('A', 64) + " 50000";
        }

        Assert.False(UdpDiscovery.TryParseAnnouncement(System.Text.Encoding.ASCII.GetBytes(text), Lan, out _, out _));
    }

    [Fact]
    public void Announcement_NonAsciiOrOversized_IsRejected()
    {
        byte[] nonAscii = System.Text.Encoding.UTF8.GetBytes("REDCHAT1 " + Peer + " 5０000");
        Assert.False(UdpDiscovery.TryParseAnnouncement(nonAscii, Lan, out _, out _));
        byte[] big = new byte[UdpDiscovery.MaxDatagramBytes + 1];
        Array.Fill(big, (byte)'a');
        Assert.False(UdpDiscovery.TryParseAnnouncement(big, Lan, out _, out _));
    }

    [Fact]
    public void Announcement_FromPublicAddress_IsRejected()
    {
        byte[] data = UdpDiscovery.BuildAnnouncement(Peer, 50123);
        Assert.False(UdpDiscovery.TryParseAnnouncement(data, IPAddress.Parse("8.8.8.8"), out _, out _));
        Assert.False(UdpDiscovery.TryParseAnnouncement(data, IPAddress.Parse("2001:4860:4860::8888"), out _, out _));
        Assert.True(UdpDiscovery.TryParseAnnouncement(data, IPAddress.Parse("10.1.2.3"), out _, out _));
        Assert.True(UdpDiscovery.TryParseAnnouncement(data, IPAddress.Loopback, out _, out _));
    }

    [Fact]
    public void Constructor_RejectsBadArguments()
    {
        Assert.ThrowsAny<ArgumentException>(() => Make("nope", 50000));
        Assert.ThrowsAny<ArgumentException>(() => Make(Peer, 80));
        Assert.ThrowsAny<ArgumentException>(() => Make(Peer, 70000));
    }

    private static UdpDiscovery Make(string id, int port) => new(id, port);

    [Fact]
    public void Listener_ReportsValidAnnouncement_IgnoresGarbageSelfAndRepeats()
    {
        int udpPort = NetHelpers.FreeUdpPort();
        string self = new('b', 64);
        string other = new('c', 64);
        List<PeerDiscoveredEventArgs> seen = new();
        using UdpDiscovery d = new(self, 50000, udpPort, IPAddress.Loopback, IPAddress.Loopback, TimeSpan.FromMilliseconds(200));
        d.PeerDiscovered += (_, e) => { lock (seen) { seen.Add(e); } };
        d.Start();

        using UdpClient raw = new();
        IPEndPoint target = new(IPAddress.Loopback, udpPort);
        byte[] junk = { 1, 2, 3, 255 };
        raw.Send(junk, junk.Length, target);
        byte[] own = UdpDiscovery.BuildAnnouncement(self, 50000);
        raw.Send(own, own.Length, target);                      // sa propre annonce : ignorée
        byte[] ok = UdpDiscovery.BuildAnnouncement(other, 51000);
        raw.Send(ok, ok.Length, target);
        raw.Send(ok, ok.Length, target);                        // répétition immédiate : limitée à 1/s
        raw.Send(ok, ok.Length, target);

        Assert.True(NetHelpers.WaitUntil(() => { lock (seen) { return seen.Count >= 1; } }, 5000));
        Thread.Sleep(300);
        lock (seen)
        {
            Assert.Single(seen);
            Assert.Equal(other, seen[0].PeerId);
            Assert.Equal(51000, seen[0].Endpoint.Port);
            Assert.Equal(IPAddress.Loopback, seen[0].Endpoint.Address);
        }
    }
}
