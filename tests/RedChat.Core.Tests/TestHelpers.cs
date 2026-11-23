using RedChat.Core;

namespace RedChat.Core.Tests;

/// <summary>Utilitaires partagés : handshake complet en mémoire, paquets valides.</summary>
internal static class TestHelpers
{
    public static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    public sealed record Connected(
        LocalIdentity Alice, LocalIdentity Bob,
        SecureChannel AliceChannel, SecureChannel BobChannel,
        string AliceSeesPeer, string BobSeesPeer);

    /// <summary>Joue les 3 messages du handshake en mémoire : Alice initie, Bob répond.</summary>
    public static Connected Connect()
    {
        LocalIdentity alice = LocalIdentity.Create();
        LocalIdentity bob = LocalIdentity.Create();
        using HandshakeInitiator init = new(alice, bob.PeerId);
        using HandshakeResponder resp = new(bob);
        byte[] h2 = resp.ProcessHello1(init.Start());
        byte[] h3 = init.ProcessHello2(h2);
        resp.ProcessHello3(h3);
        return new Connected(alice, bob, init.Channel!, resp.Channel!, init.RemotePeerId!, resp.RemotePeerId!);
    }

    public static string NewGuid() => Guid.NewGuid().ToString("D");

    public static string FakePeerId(char c) => new(c, 64);

    public static Packet ValidText(string text = "Salut !") => new()
    {
        Kind = PacketKind.Text,
        Id = NewGuid(),
        ConversationId = FakePeerId('a'),
        SentAtUnixMs = Now.ToUnixTimeMilliseconds(),
        Text = text,
    };
}
