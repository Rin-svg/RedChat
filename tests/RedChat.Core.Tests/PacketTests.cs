using System.Text;
using Xunit;

namespace RedChat.Core.Tests;

public class PacketTests
{
    private static Packet Roundtrip(Packet p) =>
        PacketSerializer.Deserialize(PacketSerializer.Serialize(p, TestHelpers.Now), TestHelpers.Now);

    [Fact]
    public void Text_RoundTrips()
    {
        Packet back = Roundtrip(TestHelpers.ValidText("Salut 😀 ça va ?"));
        Assert.Equal(PacketKind.Text, back.Kind);
        Assert.Equal("Salut 😀 ça va ?", back.Text);
    }

    [Fact]
    public void Text_WithReply_RoundTrips()
    {
        string reply = TestHelpers.NewGuid();
        Packet back = Roundtrip(TestHelpers.ValidText() with { ReplyToId = reply });
        Assert.Equal(reply, back.ReplyToId);
    }

    [Fact]
    public void Receipt_RoundTrips()
    {
        Packet p = new()
        {
            Kind = PacketKind.Receipt, Id = TestHelpers.NewGuid(), ConversationId = TestHelpers.FakePeerId('b'),
            SentAtUnixMs = TestHelpers.Now.ToUnixTimeMilliseconds(), TargetMessageId = TestHelpers.NewGuid(), ReceiptState = ReceiptState.Read,
        };
        Assert.Equal(ReceiptState.Read, Roundtrip(p).ReceiptState);
    }

    [Fact]
    public void AttachmentChunk_RoundTrips_AndRejectsInvalidMetadata()
    {
        byte[] data = new byte[ProtocolLimits.AttachmentChunkBytes];
        Packet packet = new()
        {
            Kind = PacketKind.AttachmentChunk,
            Id = TestHelpers.NewGuid(),
            ConversationId = TestHelpers.FakePeerId('b'),
            SentAtUnixMs = TestHelpers.Now.ToUnixTimeMilliseconds(),
            AttachmentTransferId = TestHelpers.NewGuid(),
            AttachmentFileName = "document.pdf",
            AttachmentFileSize = data.Length + 3,
            AttachmentSha256 = new string('A', 64),
            AttachmentChunkIndex = 0,
            AttachmentChunkCount = 2,
            AttachmentChunkData = data,
        };

        Packet back = Roundtrip(packet);
        Assert.Equal(packet.AttachmentTransferId, back.AttachmentTransferId);
        Assert.Equal(packet.AttachmentFileName, back.AttachmentFileName);
        Assert.Equal(data, back.AttachmentChunkData);
        Assert.Throws<ProtocolViolationException>(() => PacketSerializer.Validate(packet with { AttachmentFileName = "../secret.txt" }, TestHelpers.Now));
        Assert.Throws<ProtocolViolationException>(() => PacketSerializer.Validate(packet with { AttachmentFileName = "CON.txt" }, TestHelpers.Now));
        Assert.Throws<ProtocolViolationException>(() => PacketSerializer.Validate(packet with { AttachmentFileSize = ProtocolLimits.MaxAttachmentBytes + 1L }, TestHelpers.Now));
        Assert.Throws<ProtocolViolationException>(() => PacketSerializer.Validate(packet with { AttachmentChunkCount = 1 }, TestHelpers.Now));
        Assert.Throws<ProtocolViolationException>(() => PacketSerializer.Validate(packet with { AttachmentSha256 = "not-a-hash" }, TestHelpers.Now));
    }

    [Fact]
    public void CallControl_RoundTrips_AndRejectsInvalidFields()
    {
        Packet packet = new()
        {
            Kind = PacketKind.CallControl,
            Id = TestHelpers.NewGuid(),
            ConversationId = TestHelpers.FakePeerId('b'),
            SentAtUnixMs = TestHelpers.Now.ToUnixTimeMilliseconds(),
            CallId = TestHelpers.NewGuid(),
            CallAction = CallControlAction.Invite,
            CallMedia = CallMediaKind.Audio,
        };
        Packet back = Roundtrip(packet);
        Assert.Equal(CallControlAction.Invite, back.CallAction);
        Assert.Equal(CallMediaKind.Audio, back.CallMedia);
        Assert.Throws<ProtocolViolationException>(() => PacketSerializer.Validate(packet with { CallId = "bad" }, TestHelpers.Now));
        Assert.Throws<ProtocolViolationException>(() => PacketSerializer.Validate(packet with { CallAction = null }, TestHelpers.Now));
        Assert.Throws<ProtocolViolationException>(() => PacketSerializer.Validate(packet with { CallMedia = (CallMediaKind)99 }, TestHelpers.Now));
        Assert.Throws<ProtocolViolationException>(() => PacketSerializer.Validate(packet with { ConversationId = TestHelpers.NewGuid() }, TestHelpers.Now));

        Packet offer = packet with
        {
            CallAction = CallControlAction.Offer,
            CallSignalData = "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\n",
        };
        Assert.Equal(offer.CallSignalData, Roundtrip(offer).CallSignalData);
        Assert.Throws<ProtocolViolationException>(() => PacketSerializer.Validate(offer with { CallSignalData = null }, TestHelpers.Now));
        Assert.Throws<ProtocolViolationException>(() => PacketSerializer.Validate(offer with { CallSignalData = new string('x', ProtocolLimits.MaxCallSdpChars + 1) }, TestHelpers.Now));

        Packet ice = packet with
        {
            CallAction = CallControlAction.IceCandidate,
            CallSignalData = "candidate:1 1 UDP 2122260223 192.168.1.10 50000 typ host",
            CallSignalMid = "0",
            CallSignalMLineIndex = 0,
        };
        Assert.Equal(ice.CallSignalData, Roundtrip(ice).CallSignalData);
        Assert.Throws<ProtocolViolationException>(() => PacketSerializer.Validate(ice with { CallSignalMid = null }, TestHelpers.Now));
        Assert.Throws<ProtocolViolationException>(() => PacketSerializer.Validate(ice with { CallSignalMLineIndex = -1 }, TestHelpers.Now));
    }

    [Fact]
    public void ProfileUpdate_RoundTrips_AndValidatesPublicFields()
    {
        string peerId = TestHelpers.FakePeerId('b');
        Packet packet = new()
        {
            Kind = PacketKind.ProfileUpdate,
            Id = TestHelpers.NewGuid(),
            ConversationId = peerId,
            SentAtUnixMs = TestHelpers.Now.ToUnixTimeMilliseconds(),
            ProfilePeerId = peerId,
            ProfileDisplayName = "Léa",
            ProfileStatus = "Disponible",
            ProfileAbout = "Bonjour",
            ProfileAvatarBytes = null,
        };

        Packet received = Roundtrip(packet);
        Assert.Equal(peerId, received.ProfilePeerId);
        Assert.Equal("Léa", received.ProfileDisplayName);
        Assert.Throws<ProtocolViolationException>(() => PacketSerializer.Validate(
            packet with { ProfilePeerId = "invalid" }, TestHelpers.Now));
        Assert.Throws<ProtocolViolationException>(() => PacketSerializer.Validate(
            packet with { ProfileDisplayName = new string('x', 41) }, TestHelpers.Now));
        Assert.Throws<ProtocolViolationException>(() => PacketSerializer.Validate(
            packet with { ProfileDisplayName = "ami\u202Etxt" }, TestHelpers.Now));
    }

    [Fact]
    public void GroupCreate_Valid_RoundTrips()
    {
        Packet p = new()
        {
            Kind = PacketKind.GroupCreate, Id = TestHelpers.NewGuid(), ConversationId = TestHelpers.NewGuid(),
            SentAtUnixMs = TestHelpers.Now.ToUnixTimeMilliseconds(), GroupName = "Famille",
            Members = new[] { TestHelpers.FakePeerId('a'), TestHelpers.FakePeerId('b'), TestHelpers.FakePeerId('c') },
        };
        Assert.Equal(3, Roundtrip(p).Members!.Length);
    }

    [Fact]
    public void GroupCreate_InvalidVariants_Rejected()
    {
        Packet baseP = new()
        {
            Kind = PacketKind.GroupCreate, Id = TestHelpers.NewGuid(), ConversationId = TestHelpers.NewGuid(),
            SentAtUnixMs = TestHelpers.Now.ToUnixTimeMilliseconds(), GroupName = "G",
            Members = new[] { TestHelpers.FakePeerId('a'), TestHelpers.FakePeerId('b') },
        };
        Packet[] bad =
        {
            baseP with { Members = new[] { TestHelpers.FakePeerId('a') } },                                         // 1 seul membre
            baseP with { Members = new[] { TestHelpers.FakePeerId('a'), TestHelpers.FakePeerId('a') } },            // doublon
            baseP with { Members = new[] { TestHelpers.FakePeerId('a'), "pas-un-peer" } },                          // id invalide
            baseP with { Members = Enumerable.Range(0, ProtocolLimits.MaxGroupMembers + 1).Select(i => new string((char)('a' + i % 6), 60) + i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture)).ToArray() },
            baseP with { Members = null },
            baseP with { GroupName = "" },
            baseP with { GroupName = "  espaces  " },                                                              // non normalisé
            baseP with { GroupName = new string('x', ProtocolLimits.MaxGroupNameChars + 1) },
            baseP with { GroupName = "Eve\u202Etxt" },
            baseP with { ConversationId = TestHelpers.FakePeerId('a') },                                            // un groupe a un GUID
        };
        foreach (Packet p in bad)
        {
            Assert.Throws<ProtocolViolationException>(() => PacketSerializer.Validate(p, TestHelpers.Now));
        }
    }

    [Fact]
    public void Validate_CommonFieldViolations_Rejected()
    {
        Packet ok = TestHelpers.ValidText();
        Packet[] bad =
        {
            ok with { Id = "123" },
            ok with { ConversationId = "x" },
            ok with { SentAtUnixMs = 0 },
            ok with { SentAtUnixMs = -5 },
            ok with { SentAtUnixMs = TestHelpers.Now.AddDays(2).ToUnixTimeMilliseconds() },
            ok with { Kind = (PacketKind)99 },
            ok with { Text = null },
            ok with { Text = new string('x', ProtocolLimits.MaxTextChars + 1) },
            ok with { ReplyToId = "pas-un-guid" },
        };
        foreach (Packet p in bad)
        {
            Assert.Throws<ProtocolViolationException>(() => PacketSerializer.Validate(p, TestHelpers.Now));
        }
    }

    [Fact]
    public void Validate_FutureWithinSkew_Accepted_AndBoundaryChecked()
    {
        Packet ok = TestHelpers.ValidText() with { SentAtUnixMs = (TestHelpers.Now + ProtocolLimits.MaxFutureSkew).ToUnixTimeMilliseconds() };
        PacketSerializer.Validate(ok, TestHelpers.Now);
        Assert.Throws<ProtocolViolationException>(() => PacketSerializer.Validate(ok with { SentAtUnixMs = ok.SentAtUnixMs + 1 }, TestHelpers.Now));
    }

    [Fact]
    public void Reaction_Delete_Edit_Rules()
    {
        Packet baseP = new() { Id = TestHelpers.NewGuid(), ConversationId = TestHelpers.FakePeerId('a'), SentAtUnixMs = TestHelpers.Now.ToUnixTimeMilliseconds(), TargetMessageId = TestHelpers.NewGuid() };
        PacketSerializer.Validate(baseP with { Kind = PacketKind.Reaction, Emoji = "❤" }, TestHelpers.Now);
        PacketSerializer.Validate(baseP with { Kind = PacketKind.Delete }, TestHelpers.Now);
        PacketSerializer.Validate(baseP with { Kind = PacketKind.Edit, Text = "corrigé" }, TestHelpers.Now);
        Assert.Throws<ProtocolViolationException>(() => PacketSerializer.Validate(baseP with { Kind = PacketKind.Reaction, Emoji = null }, TestHelpers.Now));
        Assert.Throws<ProtocolViolationException>(() => PacketSerializer.Validate(baseP with { Kind = PacketKind.Delete, TargetMessageId = null }, TestHelpers.Now));
        Assert.Throws<ProtocolViolationException>(() => PacketSerializer.Validate(baseP with { Kind = PacketKind.Edit, Text = "" }, TestHelpers.Now));
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{")]
    [InlineData("not json")]
    [InlineData("{\"kind\":99}")]                                                                // entier refusé pour une énumération
    [InlineData("{\"kind\":\"text\",\"unknownField\":1}")]                                       // champ inconnu refusé
    [InlineData("{\"kind\":\"text\",\"id\":\"x\"}")]
    [InlineData("{\"Kind\":\"text\"}")]                                                          // casse stricte
    public void Deserialize_HostileJson_OnlyThrowsProtocolViolation(string json) =>
        Assert.Throws<ProtocolViolationException>(() => PacketSerializer.Deserialize(Encoding.UTF8.GetBytes(json), TestHelpers.Now));

    [Fact]
    public void Deserialize_DeepNesting_Rejected()
    {
        string deep = new string('[', 5000) + new string(']', 5000);
        Assert.Throws<ProtocolViolationException>(() => PacketSerializer.Deserialize(Encoding.UTF8.GetBytes(deep), TestHelpers.Now));
    }

    [Fact]
    public void Deserialize_Oversized_Rejected() =>
        Assert.Throws<ProtocolViolationException>(() => PacketSerializer.Deserialize(new byte[ProtocolLimits.MaxFrameBytes + 1], TestHelpers.Now));

    [Fact]
    public void Deserialize_InvalidUtf8_OnlyThrowsProtocolViolation()
    {
        Random rng = new(7);
        for (int i = 0; i < 200; i++)
        {
            byte[] junk = new byte[rng.Next(1, 120)];
            rng.NextBytes(junk);
            Assert.Throws<ProtocolViolationException>(() => PacketSerializer.Deserialize(junk, TestHelpers.Now));
        }
    }

    [Fact]
    public void Serialize_InvalidPacket_Refused() =>
        Assert.Throws<ProtocolViolationException>(() => PacketSerializer.Serialize(TestHelpers.ValidText("") , TestHelpers.Now));

    [Fact]
    public void EndToEnd_PacketThroughSecureChannel()
    {
        TestHelpers.Connected c = TestHelpers.Connect();
        Packet sent = TestHelpers.ValidText("Bonjour Bob");
        byte[] wire = c.AliceChannel.Seal(PacketSerializer.Serialize(sent, TestHelpers.Now));
        Packet received = PacketSerializer.Deserialize(c.BobChannel.Open(wire), TestHelpers.Now);
        Assert.Equal("Bonjour Bob", received.Text);
        Assert.Equal(sent.Id, received.Id);
        Assert.DoesNotContain("Bonjour", Encoding.UTF8.GetString(wire));   // le clair ne circule pas sur le fil
    }
}
