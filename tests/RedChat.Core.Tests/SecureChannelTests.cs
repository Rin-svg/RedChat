using Xunit;

namespace RedChat.Core.Tests;

public class SecureChannelTests
{
    [Fact]
    public void Seal_Open_RoundTripsManyMessagesInOrder()
    {
        TestHelpers.Connected c = TestHelpers.Connect();
        for (int i = 0; i < 100; i++)
        {
            byte[] msg = System.Text.Encoding.UTF8.GetBytes("message " + i);
            byte[] back = c.BobChannel.Open(c.AliceChannel.Seal(msg));
            Assert.Equal("message " + i, System.Text.Encoding.UTF8.GetString(back));
        }
    }

    [Fact]
    public void Open_ReplayedMessage_IsRejected()
    {
        TestHelpers.Connected c = TestHelpers.Connect();
        byte[] sealedMsg = c.AliceChannel.Seal(new byte[] { 1, 2, 3 });
        c.BobChannel.Open(sealedMsg);
        Assert.Throws<ProtocolViolationException>(() => c.BobChannel.Open(sealedMsg));
    }

    [Fact]
    public void Open_SkippedMessage_IsRejected()
    {
        TestHelpers.Connected c = TestHelpers.Connect();
        c.AliceChannel.Seal(new byte[] { 1 });                       // message 0 « perdu »
        byte[] second = c.AliceChannel.Seal(new byte[] { 2 });
        Assert.Throws<ProtocolViolationException>(() => c.BobChannel.Open(second));
    }

    [Fact]
    public void Open_EveryByteFlipped_IsRejected()
    {
        TestHelpers.Connected c = TestHelpers.Connect();
        byte[] good = c.AliceChannel.Seal(new byte[] { 10, 20, 30, 40, 50 });
        for (int i = 0; i < good.Length; i++)
        {
            byte[] bad = (byte[])good.Clone();
            bad[i] ^= 0x80;
            Assert.Throws<ProtocolViolationException>(() => c.BobChannel.Open(bad));
        }

        // Un paquet forgé ne doit PAS désynchroniser le canal : le vrai message passe toujours ensuite.
        Assert.Equal(5, c.BobChannel.Open(good).Length);
    }

    [Fact]
    public void Open_TooShort_Rejected()
    {
        TestHelpers.Connected c = TestHelpers.Connect();
        Assert.Throws<ProtocolViolationException>(() => c.BobChannel.Open(new byte[ProtocolLimits.SealOverheadBytes - 1]));
        Assert.Throws<ProtocolViolationException>(() => c.BobChannel.Open(Array.Empty<byte>()));
    }

    [Fact]
    public void Open_OwnMessageReflectedBack_IsRejected()
    {
        // Attaque par réflexion : renvoyer à Alice son propre message. Les clés étant directionnelles, l'authentification échoue.
        TestHelpers.Connected c = TestHelpers.Connect();
        byte[] fromAlice = c.AliceChannel.Seal(new byte[] { 7 });
        Assert.Throws<ProtocolViolationException>(() => c.AliceChannel.Open(fromAlice));
    }

    [Fact]
    public void Open_MessageFromOtherSession_IsRejected()
    {
        TestHelpers.Connected a = TestHelpers.Connect();
        TestHelpers.Connected b = TestHelpers.Connect();
        byte[] msg = a.AliceChannel.Seal(new byte[] { 1 });
        Assert.Throws<ProtocolViolationException>(() => b.BobChannel.Open(msg));
    }

    [Fact]
    public void Seal_MaxPlaintext_Works_AndOneMoreThrows()
    {
        TestHelpers.Connected c = TestHelpers.Connect();
        byte[] max = new byte[SecureChannel.MaxPlaintextBytes];
        byte[] sealedMax = c.AliceChannel.Seal(max);
        Assert.Equal(ProtocolLimits.MaxFrameBytes, sealedMax.Length);
        Assert.Equal(max.Length, c.BobChannel.Open(sealedMax).Length);
        Assert.ThrowsAny<ArgumentOutOfRangeException>(() => c.AliceChannel.Seal(new byte[SecureChannel.MaxPlaintextBytes + 1]));
    }

    [Fact]
    public void Seal_EmptyPlaintext_RoundTrips()
    {
        TestHelpers.Connected c = TestHelpers.Connect();
        Assert.Empty(c.BobChannel.Open(c.AliceChannel.Seal(ReadOnlySpan<byte>.Empty)));
    }

    [Fact]
    public void Seal_AfterDispose_Throws()
    {
        TestHelpers.Connected c = TestHelpers.Connect();
        c.AliceChannel.Dispose();
        Assert.ThrowsAny<ObjectDisposedException>(() => c.AliceChannel.Seal(new byte[] { 1 }));
    }

    [Fact]
    public void Seal_ConcurrentSenders_NeverReuseACounter()
    {
        // Deux threads scellent en parallèle : tous les compteurs doivent être distincts (sinon réutilisation de nonce = faille AES-GCM).
        TestHelpers.Connected c = TestHelpers.Connect();
        var results = new System.Collections.Concurrent.ConcurrentBag<byte[]>();
        Parallel.For(0, 400, _ => results.Add(c.AliceChannel.Seal(new byte[] { 1 })));
        int distinct = results.Select(r => Convert.ToHexString(r.AsSpan(0, 8))).Distinct().Count();
        Assert.Equal(400, distinct);
    }
}
