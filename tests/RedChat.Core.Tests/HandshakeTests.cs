using Xunit;

namespace RedChat.Core.Tests;

public class HandshakeTests
{
    [Fact]
    public void Handshake_Valid_BothSidesAuthenticateEachOther()
    {
        TestHelpers.Connected c = TestHelpers.Connect();
        Assert.Equal(c.Bob.PeerId, c.AliceSeesPeer);
        Assert.Equal(c.Alice.PeerId, c.BobSeesPeer);
    }

    [Fact]
    public void Handshake_Valid_ChannelsInteroperateInBothDirections()
    {
        TestHelpers.Connected c = TestHelpers.Connect();
        byte[] hello = { 1, 2, 3, 4 };
        Assert.Equal(hello.Length, c.BobChannel.Open(c.AliceChannel.Seal(hello)).Length);
        byte[] back = c.AliceChannel.Open(c.BobChannel.Seal(hello));
        Assert.Equal(4, back.Length);
        Assert.Equal((byte)3, back[2]);
    }

    [Fact]
    public void PeerId_IsStableAndDerivedFromPublicKey()
    {
        using LocalIdentity a = LocalIdentity.Create();
        Assert.Equal(PeerIdentity.ComputePeerId(a.PublicKeySpki), a.PeerId);
        Assert.True(InputValidator.IsValidPeerId(a.PeerId));
    }

    [Fact]
    public void LocalIdentity_ExportImport_KeepsSamePeerId()
    {
        using LocalIdentity a = LocalIdentity.Create();
        using LocalIdentity b = LocalIdentity.FromPrivateKey(a.ExportPrivateKey());
        Assert.Equal(a.PeerId, b.PeerId);
    }

    [Fact]
    public void LocalIdentity_FromGarbage_Throws() =>
        Assert.ThrowsAny<Exception>(() => LocalIdentity.FromPrivateKey(new byte[] { 1, 2, 3 }));

    [Fact]
    public void Initiator_WrongExpectedPeer_IsRejected()
    {
        using LocalIdentity alice = LocalIdentity.Create();
        using LocalIdentity bob = LocalIdentity.Create();
        using LocalIdentity eve = LocalIdentity.Create();
        // Alice croit parler à Eve (d'après l'annonce de découverte) mais c'est Bob qui répond.
        using HandshakeInitiator init = new(alice, eve.PeerId);
        using HandshakeResponder resp = new(bob);
        byte[] h2 = resp.ProcessHello1(init.Start());
        Assert.Throws<ProtocolViolationException>(() => init.ProcessHello2(h2));
        Assert.Null(init.Channel);
    }

    [Fact]
    public void Initiator_ConnectingToSelf_IsRejected()
    {
        using LocalIdentity alice = LocalIdentity.Create();
        using HandshakeInitiator init = new(alice);
        using HandshakeResponder resp = new(alice);
        byte[] h2 = resp.ProcessHello1(init.Start());
        Assert.Throws<ProtocolViolationException>(() => init.ProcessHello2(h2));
    }

    [Fact]
    public void Initiator_InvalidExpectedPeerId_ThrowsArgumentException()
    {
        using LocalIdentity alice = LocalIdentity.Create();
        Assert.Throws<ArgumentException>(() => { using HandshakeInitiator _ = new(alice, "pas-un-peer-id"); });
    }

    [Fact]
    public void Hello2_AnyBitFlip_IsRejected()
    {
        using LocalIdentity alice = LocalIdentity.Create();
        using LocalIdentity bob = LocalIdentity.Create();
        using HandshakeResponder probe = new(bob);
        using HandshakeInitiator init0 = new(alice);
        byte[] hello2 = probe.ProcessHello1(init0.Start());

        // On retourne chaque octet du Hello2 (un à la fois) : AUCUNE altération ne doit être acceptée.
        for (int i = 0; i < hello2.Length; i++)
        {
            using HandshakeInitiator init = new(alice);
            using HandshakeResponder resp = new(bob);
            byte[] h2 = resp.ProcessHello1(init.Start());
            byte[] tampered = (byte[])h2.Clone();
            tampered[i] ^= 0x01;
            bool rejected = false;
            try { init.ProcessHello2(tampered); }
            catch (ProtocolViolationException) { rejected = true; }
            Assert.True(rejected);
            Assert.Null(init.Channel);
        }
    }

    [Fact]
    public void Hello3_SignatureFromOtherIdentity_IsRejected()
    {
        // Mallory rejoue le Hello3 d'une AUTRE session : la signature couvre la transcription, donc elle doit échouer.
        using LocalIdentity alice = LocalIdentity.Create();
        using LocalIdentity bob = LocalIdentity.Create();

        using HandshakeInitiator init1 = new(alice);
        using HandshakeResponder resp1 = new(bob);
        byte[] h3Session1 = init1.ProcessHello2(resp1.ProcessHello1(init1.Start()));

        using HandshakeInitiator init2 = new(alice);
        using HandshakeResponder resp2 = new(bob);
        init2.ProcessHello2(resp2.ProcessHello1(init2.Start()));

        Assert.Throws<ProtocolViolationException>(() => resp2.ProcessHello3(h3Session1));
        Assert.Null(resp2.Channel);
    }

    [Fact]
    public void Hello3_Truncated_AtEveryLength_IsRejected()
    {
        using LocalIdentity alice = LocalIdentity.Create();
        using LocalIdentity bob = LocalIdentity.Create();
        using HandshakeInitiator init = new(alice);
        using HandshakeResponder resp = new(bob);
        byte[] h3 = init.ProcessHello2(resp.ProcessHello1(init.Start()));

        for (int len = 0; len < h3.Length; len++)
        {
            using HandshakeInitiator i2 = new(alice);
            using HandshakeResponder r2 = new(bob);
            i2.ProcessHello2(r2.ProcessHello1(i2.Start()));
            byte[] cut = h3[..len];
            Assert.Throws<ProtocolViolationException>(() => r2.ProcessHello3(cut));
        }
    }

    [Fact]
    public void Hello3_TrailingBytes_AreRejected()
    {
        using LocalIdentity alice = LocalIdentity.Create();
        using LocalIdentity bob = LocalIdentity.Create();
        using HandshakeInitiator init = new(alice);
        using HandshakeResponder resp = new(bob);
        byte[] h3 = init.ProcessHello2(resp.ProcessHello1(init.Start()));
        byte[] padded = h3.Concat(new byte[] { 0 }).ToArray();
        Assert.Throws<ProtocolViolationException>(() => resp.ProcessHello3(padded));
    }

    [Fact]
    public void Responder_RandomGarbage_OnlyThrowsProtocolViolation()
    {
        using LocalIdentity bob = LocalIdentity.Create();
        Random rng = new(12345);   // graine fixe : test déterministe
        for (int i = 0; i < 300; i++)
        {
            byte[] junk = new byte[rng.Next(0, 200)];
            rng.NextBytes(junk);
            if (junk.Length > 0 && i % 2 == 0) { junk[0] = 0x01; }   // type plausible, reste aléatoire
            using HandshakeResponder resp = new(bob);
            Assert.Throws<ProtocolViolationException>(() => resp.ProcessHello1(junk));
        }
    }

    [Fact]
    public void Responder_Hello1_WithOtherCurveKey_IsRejected()
    {
        using LocalIdentity bob = LocalIdentity.Create();
        using System.Security.Cryptography.ECDiffieHellman p384 =
            System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP384);
        byte[] eph = p384.ExportSubjectPublicKeyInfo();
        byte[] hello1 = new byte[] { 0x01, ProtocolLimits.ProtocolVersion, (byte)(eph.Length >> 8), (byte)(eph.Length & 0xFF) }
            .Concat(eph).Concat(new byte[ProtocolLimits.HandshakeNonceBytes]).ToArray();
        using HandshakeResponder resp = new(bob);
        Assert.Throws<ProtocolViolationException>(() => resp.ProcessHello1(hello1));
    }

    [Fact]
    public void Responder_Hello1_WrongVersion_IsRejected()
    {
        using LocalIdentity alice = LocalIdentity.Create();
        using LocalIdentity bob = LocalIdentity.Create();
        using HandshakeInitiator init = new(alice);
        byte[] h1 = init.Start();
        h1[1] = 99;
        using HandshakeResponder resp = new(bob);
        Assert.Throws<ProtocolViolationException>(() => resp.ProcessHello1(h1));
    }

    [Fact]
    public void Hello1_OversizedField_IsRejectedWithoutAllocation()
    {
        using LocalIdentity bob = LocalIdentity.Create();
        byte[] h1 = { 0x01, ProtocolLimits.ProtocolVersion, 0xFF, 0xFF };   // champ annoncé : 65535 octets
        using HandshakeResponder resp = new(bob);
        Assert.Throws<ProtocolViolationException>(() => resp.ProcessHello1(h1));
    }

    [Fact]
    public void Handshake_OutOfOrderCalls_ThrowInvalidOperation()
    {
        using LocalIdentity alice = LocalIdentity.Create();
        using HandshakeInitiator init = new(alice);
        Assert.Throws<InvalidOperationException>(() => init.ProcessHello2(new byte[] { 2 }));
        using HandshakeResponder resp = new(alice);
        Assert.Throws<InvalidOperationException>(() => resp.ProcessHello3(new byte[] { 3 }));
    }

    [Fact]
    public void TwoSessions_HaveDifferentKeys()
    {
        // Secret avant : le chiffré d'un même clair doit différer d'une session à l'autre.
        TestHelpers.Connected a = TestHelpers.Connect();
        TestHelpers.Connected b = TestHelpers.Connect();
        byte[] same = { 9, 9, 9, 9 };
        byte[] c1 = a.AliceChannel.Seal(same);
        byte[] c2 = b.AliceChannel.Seal(same);
        Assert.False(c1.AsSpan(8, 4).SequenceEqual(c2.AsSpan(8, 4)));
    }
}

public class IdentityCanonicalFormTests
{
    [Fact]
    public void Hello2_NonCanonicalIdentityKey_IsRejected_SoOneKeyHasOnePeerId()
    {
        // Régression : l'octet « bits inutilisés » du BIT STRING (0x00 → 0x01), à l'offset 25 du SubjectPublicKeyInfo P-256,
        // était accepté, ce qui donnait à une même clé un second PeerId. Le parseur système n'accepte cette variante que pour
        // ~50 % des clés (dernier octet pair) : on teste donc 32 identités, sinon le test ne détecterait le défaut qu'une fois sur deux.
        for (int i = 0; i < 32; i++)
        {
            using LocalIdentity alice = LocalIdentity.Create();
            using LocalIdentity bob = LocalIdentity.Create();
            using HandshakeInitiator init = new(alice);
            using HandshakeResponder resp = new(bob);
            byte[] hello2 = resp.ProcessHello1(init.Start());

            int keyStart = hello2.AsSpan().IndexOf(bob.PublicKeySpki.AsSpan());
            Assert.True(keyStart > 0);
            byte[] tampered = (byte[])hello2.Clone();
            tampered[keyStart + 25] = 0x01;
            Assert.Throws<ProtocolViolationException>(() => init.ProcessHello2(tampered));
        }
    }
}
