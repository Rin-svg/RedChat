using System.Globalization;
using System.Text;
using Xunit;

namespace RedChat.Core.Tests;

public class ChatStateTests
{
    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => TestHelpers.Now;
    }

    /// <summary>Transport contrôlable : hors ligne (TrySend faux) ou en ligne (les paquets sont consignés dans l'ordre).</summary>
    private sealed class Wire : ITransport
    {
        private readonly object _gate = new();
        public bool Online { get; set; }

        public List<Packet> Sent { get; } = new();

        public bool TrySend(string peerId, Packet packet)
        {
            lock (_gate)
            {
                if (!Online)
                {
                    return false;
                }

                Sent.Add(packet);
                return true;
            }
        }
    }

    private static string Self => new('a', 64);

    private static string B => new('b', 64);

    private static string C => new('c', 64);

    private static ChatService NewService(Wire? w = null) => new(Self, w ?? new Wire(), new FixedClock());

    private static long Ms => TestHelpers.Now.ToUnixTimeMilliseconds();

    private static string G(int n) => new Guid(n, 0, 0, new byte[8]).ToString("D");

    private static Packet TextFromB(string text, string? id = null) => new()
    {
        Kind = PacketKind.Text,
        Id = id ?? TestHelpers.NewGuid(),
        ConversationId = Self,
        SentAtUnixMs = Ms,
        Text = text,
    };

    /// <summary>Une conversation riche : messages, réponse, réaction, édition, suppression, groupe, message entrant, lecture.</summary>
    private static ChatService Rich(Wire? w = null)
    {
        ChatService s = NewService(w);
        ChatMessage m1 = s.SendText(B, "salut");
        _ = s.SendText(B, "deuxième", m1.Id);
        s.React(B, m1.Id, "👍");
        s.EditMessage(B, m1.Id, "salut (modifié)");
        ChatMessage m3 = s.SendText(B, "à supprimer");
        s.DeleteMessage(B, m3.Id);
        ConversationInfo g = s.CreateGroup("Les amis", new[] { B, C });
        _ = s.SendText(g.Id, "bonjour le groupe");
        s.Receive(B, TextFromB("coucou"));
        s.MarkRead(B);
        return s;
    }

    /// <summary>Description textuelle complète et déterministe de l'état : sert à comparer « avant » et « après » restauration.</summary>
    private static string Describe(ChatService s)
    {
        StringBuilder sb = new();
        foreach (ConversationInfo c in s.GetConversations().OrderBy(x => x.Id, StringComparer.Ordinal))
        {
            _ = sb.Append(CultureInfo.InvariantCulture, $"C {c.Id} group={c.IsGroup} title={c.Title} members={string.Join(',', c.Members)} creator={c.CreatorId}\n");
            foreach (ChatMessage m in s.GetMessages(c.Id))
            {
                string rx = string.Join(',', m.Reactions.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => k.Key + ":" + k.Value));
                string rc = string.Join(',', m.Receipts.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => k.Key + ":" + k.Value));
                _ = sb.Append(CultureInfo.InvariantCulture, $"  M {m.Id} from={m.SenderId} text={m.Text} at={m.SentAtUnixMs} out={m.IsOutgoing} reply={m.ReplyToId} edited={m.IsEdited} deleted={m.IsDeleted} readByMe={m.IsReadByMe} rx=[{rx}] rc=[{rc}] status={m.Status}\n");
            }
        }

        foreach (OutboxSnapshot o in s.ExportState().Outbox.OrderBy(x => x.PeerId, StringComparer.Ordinal))
        {
            _ = sb.Append(CultureInfo.InvariantCulture, $"O {o.PeerId}: {string.Join(',', o.Packets.Select(p => p.Kind + "/" + p.Id))}\n");
        }

        return sb.ToString();
    }

    // ───────────── Aller-retour ─────────────

    [Fact]
    public void ExportThenImport_RestoresEverything_Identically()
    {
        ChatService original = Rich();
        string before = Describe(original);
        Assert.True(before.Length > 200); // garde-fou : on compare bien quelque chose de riche

        ChatService restored = NewService();
        restored.ImportState(original.ExportState());
        Assert.Equal(before, Describe(restored));
        Assert.Equal(original.PendingCount(B), restored.PendingCount(B));
        Assert.True(restored.PendingCount(B) > 0);
    }

    [Fact]
    public void Export_OfEmptyService_ImportsIntoEmptyService()
    {
        ChatService restored = NewService();
        restored.ImportState(NewService().ExportState());
        Assert.Empty(restored.GetConversations());
    }

    [Fact]
    public void Restored_Service_KeepsWorking_AndOrderOfPendingMessagesIsPreserved()
    {
        Wire w1 = new();
        ChatService s1 = NewService(w1);
        _ = s1.SendText(B, "1");
        _ = s1.SendText(B, "2");
        _ = s1.SendText(B, "3");

        Wire w2 = new();
        ChatService s2 = NewService(w2);
        s2.ImportState(s1.ExportState());
        Assert.True(s2.GetMessages(B).All(m => m.Status == MessageStatus.Pending));

        w2.Online = true;
        s2.OnPeerConnected(B);
        Assert.Equal("1,2,3", string.Join(',', w2.Sent.Where(p => p.Kind == PacketKind.Text).Select(p => p.Text!)));
        Assert.True(s2.GetMessages(B).All(m => m.Status == MessageStatus.Sent));
    }

    [Fact]
    public void Restored_SentButUnconfirmedMessage_IsResentOnReconnect()
    {
        Wire w1 = new() { Online = true };
        ChatService s1 = NewService(w1);
        _ = s1.SendText(B, "perdu avec la connexion"); // « Sent », jamais accusé : l'appli s'est fermée entre-temps
        Assert.Equal(MessageStatus.Sent, s1.GetMessages(B)[0].Status);

        Wire w2 = new() { Online = true };
        ChatService s2 = NewService(w2);
        s2.ImportState(s1.ExportState());
        s2.OnPeerConnected(B);
        Assert.Equal(1, w2.Sent.Count(p => p.Kind == PacketKind.Text && p.Text == "perdu avec la connexion"));
    }

    [Fact]
    public void Restored_Service_AcceptsAcknowledgements_ForRestoredMessages()
    {
        ChatService s1 = NewService();
        ChatMessage m = s1.SendText(B, "salut");
        ChatService s2 = NewService();
        s2.ImportState(s1.ExportState());

        s2.Receive(B, new Packet { Kind = PacketKind.Receipt, Id = TestHelpers.NewGuid(), ConversationId = Self, SentAtUnixMs = Ms, TargetMessageId = m.Id, ReceiptState = ReceiptState.Read });
        Assert.Equal(MessageStatus.Read, s2.GetMessages(B)[0].Status);
    }

    [Fact]
    public void Restored_DuplicateOfAlreadyKnownMessage_IsStillIgnored()
    {
        ChatService s1 = NewService();
        string id = TestHelpers.NewGuid();
        s1.Receive(B, TextFromB("une seule fois", id));
        ChatService s2 = NewService();
        s2.ImportState(s1.ExportState());
        s2.Receive(B, TextFromB("une seule fois", id)); // retransmission après redémarrage
        Assert.Single(s2.GetMessages(B));
    }

    // ───────────── Import : règles de base ─────────────

    [Fact]
    public void Import_IntoNonEmptyService_Throws_AndLeavesServiceUntouched()
    {
        ChatService s = NewService();
        _ = s.SendText(B, "déjà là");
        string before = Describe(s);
        Assert.ThrowsAny<InvalidOperationException>(() => s.ImportState(Rich().ExportState()));
        Assert.Equal(before, Describe(s));
    }

    [Fact]
    public void Import_Twice_SecondIsRefused()
    {
        ChatService s = NewService();
        s.ImportState(Valid());
        Assert.ThrowsAny<InvalidOperationException>(() => s.ImportState(Valid()));
    }

    [Fact]
    public void Import_NullSnapshot_Throws()
    {
        Assert.ThrowsAny<ArgumentNullException>(() => NewService().ImportState(null!));
    }

    [Fact]
    public void Import_AcceptsMessagesAndPacketsDatedAfterTheClockWentBackwards()
    {
        // Scénario réel : l'horloge du PC a été déréglée (pile CMOS, réglage manuel) puis remise en arrière. Les messages déjà
        // enregistrés semblent alors « du futur ». Ils sont légitimes : l'historique ne doit PAS être refusé pour autant.
        long later = TestHelpers.Now.AddDays(400).ToUnixTimeMilliseconds();
        ChatSnapshot v = Valid();
        ConversationSnapshot direct = v.Conversations[0];
        ChatSnapshot snap = v with
        {
            Conversations = new[] { direct with { Messages = direct.Messages.Select(m => m with { SentAtUnixMs = later }).ToArray() }, v.Conversations[1] },
            Outbox = new[] { new OutboxSnapshot { PeerId = B, Packets = v.Outbox[0].Packets.Select(p => p with { SentAtUnixMs = later }).ToArray() } },
        };

        ChatService s = NewService();
        s.ImportState(snap);
        Assert.Equal(2, s.GetMessages(B).Count);
        Assert.Equal(1, s.PendingCount(B));
    }

    [Fact]
    public void Import_CopiesCollections_SoLaterChangesToTheSnapshotDoNotLeakIn()
    {
        Dictionary<string, string> reactions = new() { [B] = "👍" };
        Dictionary<string, MessageStatus> receipts = new() { [B] = MessageStatus.Pending };
        ChatSnapshot v = Valid();
        ConversationSnapshot direct = v.Conversations[0];
        ChatMessage outg = direct.Messages[0] with { Receipts = receipts };
        ChatMessage inc = direct.Messages[1] with { Reactions = reactions };
        ChatSnapshot snap = v with { Conversations = new[] { direct with { Messages = new[] { outg, inc } }, v.Conversations[1] } };

        ChatService s = NewService();
        s.ImportState(snap);
        reactions[B] = "💥";
        reactions[C] = "💥";
        receipts[B] = MessageStatus.Read;

        Assert.Equal("👍", s.GetMessages(B)[1].Reactions[B]);
        Assert.Single(s.GetMessages(B)[1].Reactions);
        Assert.Equal(MessageStatus.Pending, s.GetMessages(B)[0].Status);
    }

    [Fact]
    public void Export_ReturnsCopies_SoTamperingWithTheResultDoesNotAffectTheService()
    {
        ChatService s = Rich();
        string before = Describe(s);
        ChatSnapshot snap = s.ExportState();
        ((ChatMessage[])snap.Conversations[0].Messages)[0] = null!;
        ((ConversationSnapshot[])snap.Conversations)[0] = null!;
        ((OutboxSnapshot[])snap.Outbox)[0] = null!;
        Assert.Equal(before, Describe(s));
    }

    // ───────────── Fichier hostile : chaque cas doit être refusé, sans rien importer ─────────────

    private static ChatSnapshot Valid()
    {
        ChatMessage outgoing = new()
        {
            Id = G(1),
            ConversationId = B,
            SenderId = Self,
            Text = "hello",
            SentAtUnixMs = Ms,
            IsOutgoing = true,
            IsReadByMe = true,
            Receipts = new Dictionary<string, MessageStatus> { [B] = MessageStatus.Pending },
        };
        ChatMessage incoming = new() { Id = G(2), ConversationId = B, SenderId = B, Text = "hi", SentAtUnixMs = Ms };
        ConversationSnapshot direct = new() { Info = new ConversationInfo { Id = B }, Messages = new[] { outgoing, incoming } };
        ChatMessage groupMsg = new()
        {
            Id = G(3),
            ConversationId = G(100),
            SenderId = Self,
            Text = "gm",
            SentAtUnixMs = Ms,
            IsOutgoing = true,
            IsReadByMe = true,
            Receipts = new Dictionary<string, MessageStatus> { [B] = MessageStatus.Sent, [C] = MessageStatus.Pending },
        };
        ConversationSnapshot group = new()
        {
            Info = new ConversationInfo { Id = G(100), IsGroup = true, Title = "Amis", Members = new[] { Self, B, C }, CreatorId = Self },
            Messages = new[] { groupMsg },
        };
        Packet pkt = new() { Kind = PacketKind.Text, Id = G(1), ConversationId = B, SentAtUnixMs = Ms, Text = "hello" };
        return new ChatSnapshot
        {
            Conversations = new[] { direct, group },
            Outbox = new[] { new OutboxSnapshot { PeerId = B, Packets = new[] { pkt } } },
        };
    }

    [Fact]
    public void ValidSnapshot_Imports_SoHostileCasesBelowFailForTheRightReason()
    {
        ChatService s = NewService();
        s.ImportState(Valid());
        Assert.Equal(2, s.GetConversations().Count);
        Assert.Equal(2, s.GetMessages(B).Count);
        Assert.Equal(1, s.PendingCount(B));
    }

    private static Dictionary<string, MessageStatus> Rc(params (string Peer, MessageStatus St)[] items) =>
        items.ToDictionary(i => i.Peer, i => i.St);

    private static ChatSnapshot Mutate(string kind)
    {
        ChatSnapshot v = Valid();
        ConversationSnapshot direct = v.Conversations[0];
        ConversationSnapshot group = v.Conversations[1];
        ChatMessage outg = direct.Messages[0];
        ChatMessage inc = direct.Messages[1];
        ChatMessage gm = group.Messages[0];
        long future = ProtocolLimits.StoredTimestampCeiling.ToUnixTimeMilliseconds() + 1; // au-delà du plafond absolu

        ChatSnapshot WithDirect(ConversationSnapshot d) => v with { Conversations = new[] { d, group } };
        ChatSnapshot WithOut(ChatMessage m) => WithDirect(direct with { Messages = new[] { m, inc } });
        ChatSnapshot WithIn(ChatMessage m) => WithDirect(direct with { Messages = new[] { outg, m } });
        ChatSnapshot WithGroupInfo(ConversationInfo i) => v with { Conversations = new[] { direct, group with { Info = i } } };
        ChatSnapshot WithOutbox(params OutboxSnapshot[] o) => v with { Outbox = o };
        Packet goodPacket = v.Outbox[0].Packets[0];

        return kind switch
        {
            // --- document ---
            "version2" => v with { Version = 2 },
            "version0" => v with { Version = 0 },
            "conversationsNull" => v with { Conversations = null! },
            "outboxNull" => v with { Outbox = null! },
            "duplicateConversation" => v with { Conversations = new[] { direct, direct } },
            "tooManyConversations" => v with { Conversations = Enumerable.Range(0, ChatService.MaxStoredConversations + 1).Select(i => new ConversationSnapshot { Info = new ConversationInfo { Id = (i + 100).ToString("x64", CultureInfo.InvariantCulture) } }).ToArray() },
            "tooManyMessages" => WithDirect(direct with { Messages = Enumerable.Range(0, ChatService.MaxStoredMessagesPerConversation + 1).Select(_ => inc).ToArray() }),

            // --- conversation 1-à-1 ---
            "infoNull" => WithDirect(direct with { Info = null! }),
            "messagesNull" => WithDirect(direct with { Messages = null! }),
            "nullMessage" => WithDirect(direct with { Messages = new ChatMessage[] { null! } }),
            "idNull" => WithDirect(direct with { Info = new ConversationInfo { Id = null! } }),
            "membersNull" => WithDirect(direct with { Info = new ConversationInfo { Id = B, Members = null! } }),
            "directWithSelf" => WithDirect(new ConversationSnapshot { Info = new ConversationInfo { Id = Self } }),
            "directBadId" => WithDirect(new ConversationSnapshot { Info = new ConversationInfo { Id = "nope" } }),
            "directUppercaseId" => WithDirect(new ConversationSnapshot { Info = new ConversationInfo { Id = new string('B', 64) } }),
            "directWithTitle" => WithDirect(direct with { Info = direct.Info with { Title = "x" } }),
            "directWithCreator" => WithDirect(direct with { Info = direct.Info with { CreatorId = B } }),
            "directWithMembers" => WithDirect(direct with { Info = direct.Info with { Members = new[] { Self, B } } }),

            // --- messages ---
            "messageIdNotGuid" => WithOut(outg with { Id = "1" }),
            "messageIdNull" => WithOut(outg with { Id = null! }),
            "duplicateMessageId" => WithIn(inc with { Id = outg.Id }),
            "wrongConversation" => WithOut(outg with { ConversationId = C }),
            "outgoingByOther" => WithOut(outg with { SenderId = B }),
            "senderInvalid" => WithIn(inc with { SenderId = "x" }),
            "senderNull" => WithIn(inc with { SenderId = null! }),
            "incomingFromSelf" => WithIn(inc with { SenderId = Self }),
            "incomingFromStranger" => WithIn(inc with { SenderId = C }),
            "emptyText" => WithOut(outg with { Text = string.Empty }),
            "nullText" => WithOut(outg with { Text = null! }),
            "controlCharText" => WithOut(outg with { Text = "a\u0000b" }),
            "hugeText" => WithOut(outg with { Text = new string('x', ProtocolLimits.MaxTextChars + 1) }),
            "deletedWithText" => WithOut(outg with { IsDeleted = true }),
            "deletedEdited" => WithOut(outg with { IsDeleted = true, Text = string.Empty, IsEdited = true }),
            "deletedWithReply" => WithOut(outg with { IsDeleted = true, Text = string.Empty, ReplyToId = G(9) }),
            "deletedWithReactions" => WithOut(outg with { IsDeleted = true, Text = string.Empty, Reactions = new Dictionary<string, string> { [B] = "👍" } }),
            "dateZero" => WithOut(outg with { SentAtUnixMs = 0 }),
            "dateNegative" => WithOut(outg with { SentAtUnixMs = -5 }),
            "dateBeyondCeiling" => WithOut(outg with { SentAtUnixMs = future }),
            "replyNotGuid" => WithOut(outg with { ReplyToId = "x" }),
            "reactionsNull" => WithOut(outg with { Reactions = null! }),
            "receiptsNull" => WithOut(outg with { Receipts = null! }),

            // --- accusés ---
            "receiptsMissingRecipient" => WithOut(outg with { Receipts = new Dictionary<string, MessageStatus>() }),
            "receiptsExtraStranger" => WithOut(outg with { Receipts = Rc((B, MessageStatus.Pending), (C, MessageStatus.Pending)) }),
            "receiptsOnlyStranger" => WithOut(outg with { Receipts = Rc((C, MessageStatus.Pending)) }),
            "receiptsUndefinedState" => WithOut(outg with { Receipts = Rc((B, (MessageStatus)99)) }),
            "receiptsNegativeState" => WithOut(outg with { Receipts = Rc((B, (MessageStatus)(-1))) }),
            "receiptsOnIncoming" => WithIn(inc with { Receipts = Rc((B, MessageStatus.Read)) }),
            "groupReceiptsMissing" => v with { Conversations = new[] { direct, group with { Messages = new[] { gm with { Receipts = Rc((B, MessageStatus.Sent)) } } } } },
            "groupReceiptsForSelf" => v with { Conversations = new[] { direct, group with { Messages = new[] { gm with { Receipts = Rc((B, MessageStatus.Sent), (C, MessageStatus.Sent), (Self, MessageStatus.Sent)) } } } } },

            // --- réactions ---
            "reactionByStranger" => WithIn(inc with { Reactions = new Dictionary<string, string> { [C] = "👍" } }),
            "reactionByInvalidId" => WithIn(inc with { Reactions = new Dictionary<string, string> { ["zz"] = "👍" } }),
            "reactionBidi" => WithIn(inc with { Reactions = new Dictionary<string, string> { [B] = "👍\u202E" } }),
            "reactionZeroWidth" => WithIn(inc with { Reactions = new Dictionary<string, string> { [B] = "\u200B" } }),
            "reactionTooLong" => WithIn(inc with { Reactions = new Dictionary<string, string> { [B] = new string('x', ProtocolLimits.MaxReactionChars + 1) } }),
            "reactionEmpty" => WithIn(inc with { Reactions = new Dictionary<string, string> { [B] = string.Empty } }),
            "reactionNullValue" => WithIn(inc with { Reactions = new Dictionary<string, string> { [B] = null! } }),

            // --- groupes ---
            "groupBadGuid" => WithGroupInfo(group.Info with { Id = "pas-un-guid" }),
            "groupTitleNull" => WithGroupInfo(group.Info with { Title = null }),
            "groupTitleBidi" => WithGroupInfo(group.Info with { Title = "Am\u202Eis" }),
            "groupTitleUntrimmed" => WithGroupInfo(group.Info with { Title = " Amis" }),
            "groupTitleTooLong" => WithGroupInfo(group.Info with { Title = new string('x', ProtocolLimits.MaxGroupNameChars + 1) }),
            "groupSelfMissing" => WithGroupInfo(group.Info with { Members = new[] { B, C } }),
            "groupOneMember" => WithGroupInfo(group.Info with { Members = new[] { Self } }),
            "groupDuplicateMembers" => WithGroupInfo(group.Info with { Members = new[] { Self, B, B } }),
            "groupMemberInvalid" => WithGroupInfo(group.Info with { Members = new[] { Self, B, "zz" } }),
            "groupTooManyMembers" => WithGroupInfo(group.Info with { Members = new[] { Self }.Concat(Enumerable.Range(0, ProtocolLimits.MaxGroupMembers).Select(i => (i + 100).ToString("x64", CultureInfo.InvariantCulture))).ToArray() }),
            "groupCreatorNull" => WithGroupInfo(group.Info with { CreatorId = null }),
            "groupCreatorNotMember" => WithGroupInfo(group.Info with { CreatorId = new string('d', 64) }),

            // --- file d'attente ---
            "outboxPeerSelf" => WithOutbox(new OutboxSnapshot { PeerId = Self, Packets = new[] { goodPacket } }),
            "outboxPeerInvalid" => WithOutbox(new OutboxSnapshot { PeerId = "zz", Packets = new[] { goodPacket } }),
            "outboxDuplicatePeer" => WithOutbox(new OutboxSnapshot { PeerId = B, Packets = new[] { goodPacket } }, new OutboxSnapshot { PeerId = B, Packets = new[] { goodPacket } }),
            "outboxNullEntry" => WithOutbox(new OutboxSnapshot[] { null! }),
            "outboxPacketsNull" => WithOutbox(new OutboxSnapshot { PeerId = B, Packets = null! }),
            "outboxPacketNull" => WithOutbox(new OutboxSnapshot { PeerId = B, Packets = new Packet[] { null! } }),
            "outboxPacketBadId" => WithOutbox(new OutboxSnapshot { PeerId = B, Packets = new[] { goodPacket with { Id = "x" } } }),
            "outboxPacketBeyondCeiling" => WithOutbox(new OutboxSnapshot { PeerId = B, Packets = new[] { goodPacket with { SentAtUnixMs = future } } }),
            "outboxPacketBadKind" => WithOutbox(new OutboxSnapshot { PeerId = B, Packets = new[] { goodPacket with { Kind = (PacketKind)99 } } }),
            "outboxPacketTextTooLong" => WithOutbox(new OutboxSnapshot { PeerId = B, Packets = new[] { goodPacket with { Text = new string('x', ProtocolLimits.MaxTextChars + 1) } } }),
            "outboxTooLong" => WithOutbox(new OutboxSnapshot { PeerId = B, Packets = Enumerable.Range(0, ChatService.MaxOutboxPerPeer + 1).Select(i => goodPacket with { Id = G(1000 + i) }).ToArray() }),
            "outboxTooManyPeers" => WithOutbox(Enumerable.Range(0, ChatService.MaxStoredConversations + 1).Select(i => new OutboxSnapshot { PeerId = (i + 100).ToString("x64", CultureInfo.InvariantCulture), Packets = new[] { goodPacket } }).ToArray()),
            _ => throw new ArgumentException("cas inconnu : " + kind, nameof(kind)),
        };
    }

    [Theory]
    [InlineData("version2")]
    [InlineData("version0")]
    [InlineData("conversationsNull")]
    [InlineData("outboxNull")]
    [InlineData("duplicateConversation")]
    [InlineData("tooManyConversations")]
    [InlineData("tooManyMessages")]
    [InlineData("infoNull")]
    [InlineData("messagesNull")]
    [InlineData("nullMessage")]
    [InlineData("idNull")]
    [InlineData("membersNull")]
    [InlineData("directWithSelf")]
    [InlineData("directBadId")]
    [InlineData("directUppercaseId")]
    [InlineData("directWithTitle")]
    [InlineData("directWithCreator")]
    [InlineData("directWithMembers")]
    [InlineData("messageIdNotGuid")]
    [InlineData("messageIdNull")]
    [InlineData("duplicateMessageId")]
    [InlineData("wrongConversation")]
    [InlineData("outgoingByOther")]
    [InlineData("senderInvalid")]
    [InlineData("senderNull")]
    [InlineData("incomingFromSelf")]
    [InlineData("incomingFromStranger")]
    [InlineData("emptyText")]
    [InlineData("nullText")]
    [InlineData("controlCharText")]
    [InlineData("hugeText")]
    [InlineData("deletedWithText")]
    [InlineData("deletedEdited")]
    [InlineData("deletedWithReply")]
    [InlineData("deletedWithReactions")]
    [InlineData("dateZero")]
    [InlineData("dateNegative")]
    [InlineData("dateBeyondCeiling")]
    [InlineData("replyNotGuid")]
    [InlineData("reactionsNull")]
    [InlineData("receiptsNull")]
    [InlineData("receiptsMissingRecipient")]
    [InlineData("receiptsExtraStranger")]
    [InlineData("receiptsOnlyStranger")]
    [InlineData("receiptsUndefinedState")]
    [InlineData("receiptsNegativeState")]
    [InlineData("receiptsOnIncoming")]
    [InlineData("groupReceiptsMissing")]
    [InlineData("groupReceiptsForSelf")]
    [InlineData("reactionByStranger")]
    [InlineData("reactionByInvalidId")]
    [InlineData("reactionBidi")]
    [InlineData("reactionZeroWidth")]
    [InlineData("reactionTooLong")]
    [InlineData("reactionEmpty")]
    [InlineData("reactionNullValue")]
    [InlineData("groupBadGuid")]
    [InlineData("groupTitleNull")]
    [InlineData("groupTitleBidi")]
    [InlineData("groupTitleUntrimmed")]
    [InlineData("groupTitleTooLong")]
    [InlineData("groupSelfMissing")]
    [InlineData("groupOneMember")]
    [InlineData("groupDuplicateMembers")]
    [InlineData("groupMemberInvalid")]
    [InlineData("groupTooManyMembers")]
    [InlineData("groupCreatorNull")]
    [InlineData("groupCreatorNotMember")]
    [InlineData("outboxPeerSelf")]
    [InlineData("outboxPeerInvalid")]
    [InlineData("outboxDuplicatePeer")]
    [InlineData("outboxNullEntry")]
    [InlineData("outboxPacketsNull")]
    [InlineData("outboxPacketNull")]
    [InlineData("outboxPacketBadId")]
    [InlineData("outboxPacketBeyondCeiling")]
    [InlineData("outboxPacketBadKind")]
    [InlineData("outboxPacketTextTooLong")]
    [InlineData("outboxTooLong")]
    [InlineData("outboxTooManyPeers")]
    public void Import_HostileSnapshot_IsRefused_AndNothingIsImported(string kind)
    {
        ChatService s = NewService();
        Assert.ThrowsAny<InvalidDataException>(() => s.ImportState(Mutate(kind)));
        Assert.Empty(s.GetConversations());
        Assert.Equal(0, s.PendingCount(B));
        Assert.Empty(s.ExportState().Outbox);

        // et le service reste pleinement utilisable après le refus
        _ = s.SendText(B, "toujours vivant");
        Assert.Single(s.GetMessages(B));
    }

    // ───────────── StateChanged ─────────────

    [Fact]
    public void StateChanged_FiresAfterEveryMutatingOperation()
    {
        ChatService s = NewService();
        int n = 0;
        s.StateChanged += (_, _) => n++;

        ChatMessage m = s.SendText(B, "a");         Assert.Equal(1, n);
        s.React(B, m.Id, "👍");                       Assert.Equal(2, n);
        s.EditMessage(B, m.Id, "b");                 Assert.Equal(3, n);
        s.Receive(B, TextFromB("réponse"));          Assert.Equal(4, n);
        s.MarkRead(B);                               Assert.Equal(5, n);
        _ = s.CreateGroup("G", new[] { B, C });      Assert.Equal(6, n);
        s.DeleteMessage(B, m.Id);                    Assert.Equal(7, n);
        s.OnPeerConnected(B);                        Assert.Equal(8, n);
    }

    [Fact]
    public void StateChanged_FiresEvenWhenOnlyTheOutboxChanged_WhileChangedStaysSilent()
    {
        Wire w = new();
        ChatService s = NewService(w);
        s.Receive(B, TextFromB("coucou")); // hors ligne : l'accusé « remis » reste en file d'attente
        Assert.Equal(1, s.PendingCount(B));

        int changed = 0, state = 0;
        s.Changed += (_, _) => changed++;
        s.StateChanged += (_, _) => state++;
        w.Online = true;
        s.OnPeerConnected(B); // vide la file : changement durable, mais rien à afficher

        Assert.Equal(0, s.PendingCount(B));
        Assert.Equal(0, changed);
        Assert.Equal(1, state);
    }

    [Fact]
    public void StateChanged_StillFires_WhenAChangedSubscriberThrows()
    {
        ChatService s = NewService();
        int state = 0;
        s.Changed += (_, _) => throw new InvalidOperationException("abonné défaillant");
        s.StateChanged += (_, _) => state++;
        Assert.ThrowsAny<InvalidOperationException>(() => s.SendText(B, "x"));
        Assert.Equal(1, state); // l'état a changé : il doit être enregistré malgré tout
    }

    [Fact]
    public void StateChanged_DoesNotFire_WhenOperationIsRejectedBeforeAnyChange()
    {
        ChatService s = NewService();
        int state = 0;
        s.StateChanged += (_, _) => state++;
        Assert.ThrowsAny<ArgumentException>(() => s.SendText(B, string.Empty));
        Assert.ThrowsAny<ArgumentException>(() => s.SendText("zz", "x"));
        Assert.Equal(0, state);
    }

    // ───────────── Concurrence ─────────────

    [Fact]
    public async Task Export_DuringConcurrentSends_IsAlwaysSelfConsistent()
    {
        ChatService s = NewService(); // hors ligne : chaque envoi crée un message « Pending » ET son paquet, atomiquement
        using CancellationTokenSource stop = new();
        using ManualResetEventSlim readerRunning = new(false);
        int checks = 0;

        Task reader = Task.Run(() =>
        {
            do
            {
                ChatSnapshot snap = s.ExportState();
                HashSet<string> queued = snap.Outbox.SelectMany(o => o.Packets).Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
                foreach (ChatMessage m in snap.Conversations.SelectMany(c => c.Messages))
                {
                    Assert.Contains(m.Id, queued); // jamais un message « en attente » sans son paquet
                }

                _ = Interlocked.Increment(ref checks);
                readerRunning.Set();
            }
            while (!stop.IsCancellationRequested);
        });

        readerRunning.Wait(); // les écrivains ne démarrent qu'une fois le lecteur actif : le chevauchement est garanti
        Task[] writers = Enumerable.Range(0, 4).Select(t => Task.Run(() =>
        {
            for (int i = 0; i < 150; i++)
            {
                _ = s.SendText(B, $"t{t}-{i}");
            }
        })).ToArray();

        await Task.WhenAll(writers);
        stop.Cancel();
        await reader;
        Assert.True(Volatile.Read(ref checks) >= 2);
        Assert.Equal(600, s.GetMessages(B).Count);

        ChatService restored = NewService();
        restored.ImportState(s.ExportState());
        Assert.Equal(600, restored.GetMessages(B).Count);
        Assert.Equal(600, restored.PendingCount(B));
    }
}
