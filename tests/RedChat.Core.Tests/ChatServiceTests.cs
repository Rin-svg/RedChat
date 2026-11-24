using Xunit;

namespace RedChat.Core.Tests;

public class ChatServiceTests
{
    /// <summary>Horloge fixe (déterministe).</summary>
    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => TestHelpers.Now;
    }

    /// <summary>
    /// Réseau simulé : chaque paquet est sérialisé/désérialisé comme sur le fil, mis en file, puis livré par <see cref="Pump"/>
    /// (pas de livraison réentrante : plus proche du vrai réseau, et déterministe).
    /// </summary>
    private sealed class Net
    {
        private readonly Dictionary<string, ChatService> _svc = new();
        private readonly HashSet<string> _online = new();
        private readonly Queue<(string From, string To, byte[] Data)> _wire = new();
        private readonly object _gate = new();

        public string NewPeer(char c) => new(c, 64);

        public ChatService Add(string id)
        {
            ChatService s = new(id, new Link(this, id), new FixedClock());
            _svc[id] = s;
            _online.Add(id);
            return s;
        }

        public void SetOnline(string id, bool online)
        {
            lock (_gate)
            {
                if (online) { _online.Add(id); } else { _online.Remove(id); }
            }
        }

        public bool Deliver(string from, string to, Packet p)
        {
            lock (_gate)
            {
                if (!_online.Contains(to))
                {
                    return false;
                }

                _wire.Enqueue((from, to, PacketSerializer.Serialize(p, TestHelpers.Now)));
                return true;
            }
        }

        /// <summary>Simule une coupure : les paquets en transit sont perdus.</summary>
        public void Drop()
        {
            lock (_gate)
            {
                _wire.Clear();
            }
        }

        public void Pump()
        {
            while (true)
            {
                (string From, string To, byte[] Data) item;
                lock (_gate)
                {
                    if (_wire.Count == 0) { return; }
                    item = _wire.Dequeue();
                }

                _svc[item.To].Receive(item.From, PacketSerializer.Deserialize(item.Data, TestHelpers.Now));
            }
        }

        private sealed class Link : ITransport
        {
            private readonly Net _net;
            private readonly string _me;
            public Link(Net net, string me) { _net = net; _me = me; }
            public bool TrySend(string peerId, Packet packet) => _net.Deliver(_me, peerId, packet);
        }
    }

    private static string A => new('a', 64);
    private static string B => new('b', 64);
    private static string C => new('c', 64);
    private static string D => new('d', 64);
    private static readonly string[] Others = { B, C };
    private static readonly string[] NotAPeer = { "nope" };

    private static (Net Net, ChatService A, ChatService B) Pair()
    {
        Net n = new();
        return (n, n.Add(A), n.Add(B));
    }

    private static Packet Pkt(PacketKind kind, string conv, string? target = null, string? text = null) => new()
    {
        Kind = kind,
        Id = TestHelpers.NewGuid(),
        ConversationId = conv,
        SentAtUnixMs = TestHelpers.Now.ToUnixTimeMilliseconds(),
        TargetMessageId = target,
        Text = text,
    };

    // ───────── Envoi / accusés ─────────

    [Fact]
    public void EnsureDirectConversation_CreatesEmptyConversationAndIsIdempotent()
    {
        var (_, chat, _) = Pair();
        int changed = 0;
        int stateChanged = 0;
        chat.StateChanged += (_, _) => stateChanged++;
        chat.Changed += (_, e) =>
        {
            if (e.Kind == ChatChangeKind.ConversationAdded)
            {
                changed++;
            }
        };

        ConversationInfo first = chat.EnsureDirectConversation(B);
        ConversationInfo second = chat.EnsureDirectConversation(B);

        Assert.Equal(B, first.Id);
        Assert.Equal(first, second);
        Assert.Empty(chat.GetMessages(B));
        Assert.Single(chat.GetConversations());
        Assert.Equal(1, changed);
        Assert.Equal(1, stateChanged);
    }

    [Fact]
    public void RemoveConversation_RemovesLocalHistoryAndPendingPacketsOnly()
    {
        var (net, chat, peer) = Pair();
        net.SetOnline(B, false);
        _ = chat.SendText(B, "Message en attente");
        Assert.Single(chat.GetConversations());
        Assert.Equal(1, chat.PendingCount(B));

        int removedEvents = 0;
        chat.Changed += (_, args) =>
        {
            if (args.Kind == ChatChangeKind.ConversationRemoved && args.ConversationId == B)
            {
                removedEvents++;
            }
        };

        Assert.True(chat.RemoveConversation(B));
        Assert.Empty(chat.GetConversations());
        Assert.Equal(0, chat.PendingCount(B));
        Assert.Equal(1, removedEvents);
        Assert.False(chat.RemoveConversation(B));
        Assert.Empty(peer.GetConversations());
    }

    [Fact]
    public void EnsureDirectConversation_RejectsInvalidOrSelfPeer()
    {
        var (_, chat, _) = Pair();

        Assert.Throws<ArgumentException>(() => chat.EnsureDirectConversation("invalid"));
        Assert.Throws<ArgumentException>(() => chat.EnsureDirectConversation(A));
        Assert.Empty(chat.GetConversations());
    }

    [Fact]
    public void SelfConversation_StoresMessagesLocallyWithoutNetworkDelivery()
    {
        ChatService chat = NewService(A);

        ConversationInfo first = chat.EnsureSelfConversation();
        ConversationInfo second = chat.EnsureSelfConversation();
        ChatMessage message = chat.SendText(A, "Note personnelle");

        Assert.Equal(A, first.Id);
        Assert.True(first.IsSelfConversation);
        Assert.Equal(first, second);
        Assert.Equal(A, message.ConversationId);
        Assert.True(message.IsOutgoing);
        Assert.Empty(message.Receipts);
        Assert.Empty(chat.GetMessages(B));
        Assert.Single(chat.GetMessages(A));
        Assert.Equal(0, chat.PendingCount(A));

        ChatService restored = NewService(A);
        restored.ImportState(chat.ExportState());
        Assert.Equal("Note personnelle", Assert.Single(restored.GetMessages(A)).Text);
    }

    [Fact]
    public void SendText_IsReceived_AndAcknowledgedAsDelivered()
    {
        var (net, a, b) = Pair();
        ChatMessage sent = a.SendText(B, "Salut Bob");
        Assert.Equal(MessageStatus.Sent, sent.Status);
        net.Pump();

        Assert.Single(b.GetMessages(A));
        Assert.Equal("Salut Bob", b.GetMessages(A)[0].Text);
        Assert.False(b.GetMessages(A)[0].IsOutgoing);
        Assert.Equal(MessageStatus.Delivered, a.GetMessages(B)[0].Status);
    }

    [Fact]
    public void ViewOnceText_IsRemovedOnOpen_AndSenderIsNotified()
    {
        var (net, a, b) = Pair();
        ChatMessage sent = a.SendText(B, "secret", isViewOnce: true);

        net.Pump();

        ChatMessage received = Assert.Single(b.GetMessages(A));
        Assert.True(received.IsViewOnce);
        Assert.Equal("secret", received.Text);

        b.ConsumeViewOnceMessage(A, received.Id);
        Assert.True(b.GetMessages(A)[0].IsDeleted);
        Assert.Equal(string.Empty, b.GetMessages(A)[0].Text);

        net.Pump();

        ChatMessage opened = Assert.Single(a.GetMessages(B));
        Assert.True(opened.IsViewOnceOpened);
        Assert.True(opened.IsDeleted);
        Assert.Equal(string.Empty, opened.Text);
    }

    [Fact]
    public void MarkRead_SendsReadReceipt()
    {
        var (net, a, b) = Pair();
        a.SendText(B, "lis-moi");
        net.Pump();
        b.MarkRead(A);
        net.Pump();
        Assert.Equal(MessageStatus.Read, a.GetMessages(B)[0].Status);
        Assert.True(b.GetMessages(A)[0].IsReadByMe);
    }

    [Fact]
    public void OfflinePeer_MessagesStayPending_ThenFlushInOrder()
    {
        var (net, a, b) = Pair();
        net.SetOnline(B, false);
        a.SendText(B, "un");
        a.SendText(B, "deux");
        a.SendText(B, "trois");
        Assert.Equal(3, a.PendingCount(B));
        Assert.Equal(MessageStatus.Pending, a.GetMessages(B)[0].Status);

        net.SetOnline(B, true);
        a.OnPeerConnected(B);
        net.Pump();

        Assert.Equal(0, a.PendingCount(B));
        ChatMessage[] got = b.GetMessages(A).ToArray();
        Assert.Equal(3, got.Length);
        Assert.Equal("un", got[0].Text);
        Assert.Equal("deux", got[1].Text);
        Assert.Equal("trois", got[2].Text);
        Assert.Equal(MessageStatus.Delivered, a.GetMessages(B)[2].Status);
    }

    [Fact]
    public void LostInTransit_IsResentOnReconnect_AndDeduplicated()
    {
        var (net, a, b) = Pair();
        a.SendText(B, "perdu en route");
        Assert.Equal(MessageStatus.Sent, a.GetMessages(B)[0].Status);
        net.Drop();                       // la connexion tombe avec le paquet dans la file du transport
        a.OnPeerConnected(B);
        net.Pump();
        Assert.Single(b.GetMessages(A));
        Assert.Equal(MessageStatus.Delivered, a.GetMessages(B)[0].Status);

        a.OnPeerConnected(B);             // déjà remis : rien à renvoyer
        net.Pump();
        Assert.Single(b.GetMessages(A));
        Assert.Equal(0, a.PendingCount(B));
    }

    [Fact]
    public void Reconnect_DoesNotResendDeletedMessages()
    {
        var (net, a, b) = Pair();
        ChatMessage m = a.SendText(B, "à effacer");
        net.Drop();
        a.DeleteMessage(B, m.Id);
        net.Drop();
        a.OnPeerConnected(B);
        net.Pump();
        Assert.Empty(b.GetMessages(A));
    }

    [Fact]
    public void DuplicateText_IsStoredOnce()
    {
        var (_, _, b) = Pair();
        Packet p = Pkt(PacketKind.Text, B, text: "coucou");
        b.Receive(A, p);
        b.Receive(A, p);
        Assert.Single(b.GetMessages(A));
    }

    [Fact]
    public void LateDeliveredReceipt_DoesNotDowngradeRead()
    {
        var (net, a, b) = Pair();
        ChatMessage m = a.SendText(B, "x");
        net.Pump();
        a.Receive(B, new Packet { Kind = PacketKind.Receipt, Id = TestHelpers.NewGuid(), ConversationId = A, SentAtUnixMs = TestHelpers.Now.ToUnixTimeMilliseconds(), TargetMessageId = m.Id, ReceiptState = ReceiptState.Read });
        a.Receive(B, new Packet { Kind = PacketKind.Receipt, Id = TestHelpers.NewGuid(), ConversationId = A, SentAtUnixMs = TestHelpers.Now.ToUnixTimeMilliseconds(), TargetMessageId = m.Id, ReceiptState = ReceiptState.Delivered });
        Assert.Equal(MessageStatus.Read, a.GetMessages(B)[0].Status);
        Assert.Single(b.GetMessages(A));
    }

    [Fact]
    public void Receipt_ForMessageNotSentByUs_IsViolation()
    {
        var (net, a, b) = Pair();
        ChatMessage fromB = b.SendText(A, "message de Bob");
        net.Pump();
        // Bob prétend avoir « lu » son propre message : ce n'est pas un message sortant de A, donc aucun accusé n'est attendu.
        Packet fake = new() { Kind = PacketKind.Receipt, Id = TestHelpers.NewGuid(), ConversationId = A, SentAtUnixMs = TestHelpers.Now.ToUnixTimeMilliseconds(), TargetMessageId = fromB.Id, ReceiptState = ReceiptState.Read };
        Assert.Throws<ProtocolViolationException>(() => a.Receive(B, fake));
    }

    [Fact]
    public void Receipt_ForUnknownMessage_IsIgnored()
    {
        var (net, a, _) = Pair();
        a.SendText(B, "x");
        net.Pump();
        Packet p = new() { Kind = PacketKind.Receipt, Id = TestHelpers.NewGuid(), ConversationId = A, SentAtUnixMs = TestHelpers.Now.ToUnixTimeMilliseconds(), TargetMessageId = TestHelpers.NewGuid(), ReceiptState = ReceiptState.Read };
        a.Receive(B, p);
        Assert.Equal(MessageStatus.Delivered, a.GetMessages(B)[0].Status);
    }

    // ───────── Pairs hostiles : validation de l'expéditeur et de l'adressage ─────────

    [Fact]
    public void Receive_FromSelfOrInvalidSender_IsViolation()
    {
        var (_, _, b) = Pair();
        Packet p = Pkt(PacketKind.Text, B, text: "x");
        Assert.Throws<ProtocolViolationException>(() => b.Receive(B, p));
        Assert.Throws<ProtocolViolationException>(() => b.Receive("pas-un-peerid", p));
    }

    [Fact]
    public void Receive_PacketAddressedToSomeoneElse_IsViolation()
    {
        var (_, _, b) = Pair();
        Assert.Throws<ProtocolViolationException>(() => b.Receive(A, Pkt(PacketKind.Text, C, text: "x")));
    }

    [Fact]
    public void Receive_FutureDatedPacket_IsViolation()
    {
        var (_, _, b) = Pair();
        Packet p = Pkt(PacketKind.Text, B, text: "x") with { SentAtUnixMs = TestHelpers.Now.AddDays(3).ToUnixTimeMilliseconds() };
        Assert.Throws<ProtocolViolationException>(() => b.Receive(A, p));
    }

    [Fact]
    public void Receive_InvalidTextAndReaction_AreViolations()
    {
        var (_, _, b) = Pair();
        Assert.Throws<ProtocolViolationException>(() => b.Receive(A, Pkt(PacketKind.Text, B, text: "")));
        Assert.Throws<ProtocolViolationException>(() => b.Receive(A, Pkt(PacketKind.Text, B, text: new string('x', ProtocolLimits.MaxTextChars + 1))));
        Packet bad = Pkt(PacketKind.Reaction, B, target: TestHelpers.NewGuid()) with { Emoji = "x\u202Ey" };
        Assert.Throws<ProtocolViolationException>(() => b.Receive(A, bad));
    }

    [Fact]
    public void Receive_ControlPacketWithoutConversation_IsIgnored()
    {
        var (_, _, b) = Pair();
        b.Receive(A, Pkt(PacketKind.Delete, B, target: TestHelpers.NewGuid()));
        Assert.Empty(b.GetConversations());
    }

    // ───────── Édition / suppression / réactions ─────────

    [Fact]
    public void Edit_ByAuthor_UpdatesBothSides()
    {
        var (net, a, b) = Pair();
        ChatMessage m = a.SendText(B, "faute");
        net.Pump();
        a.EditMessage(B, m.Id, "corrigé");
        net.Pump();
        Assert.Equal("corrigé", a.GetMessages(B)[0].Text);
        Assert.Equal("corrigé", b.GetMessages(A)[0].Text);
        Assert.True(b.GetMessages(A)[0].IsEdited);
    }

    [Fact]
    public void Edit_OfSomeoneElsesMessage_IsViolation_AndLocalEditRefused()
    {
        var (net, a, b) = Pair();
        ChatMessage m = a.SendText(B, "mien");
        net.Pump();
        // Côté A : B tente de modifier le message de A.
        Assert.Throws<ProtocolViolationException>(() => a.Receive(B, Pkt(PacketKind.Edit, A, m.Id, "piraté")));
        Assert.Equal("mien", a.GetMessages(B)[0].Text);
        Assert.Throws<InvalidOperationException>(() => b.EditMessage(A, m.Id, "non"));
    }

    [Fact]
    public void Delete_ByAuthor_ErasesTextEverywhere_AndBlocksLaterEdit()
    {
        var (net, a, b) = Pair();
        ChatMessage m = a.SendText(B, "secret");
        net.Pump();
        a.DeleteMessage(B, m.Id);
        net.Pump();
        Assert.True(b.GetMessages(A)[0].IsDeleted);
        Assert.Equal(string.Empty, b.GetMessages(A)[0].Text);
        Packet late = Pkt(PacketKind.Edit, B, m.Id, "ressuscité");
        b.Receive(A, late);
        Assert.Equal(string.Empty, b.GetMessages(A)[0].Text);
        Assert.Throws<InvalidOperationException>(() => a.EditMessage(B, m.Id, "x"));
    }

    [Fact]
    public void Delete_ByNonAuthor_IsViolation()
    {
        var (net, a, _) = Pair();
        ChatMessage m = a.SendText(B, "x");
        net.Pump();
        Assert.Throws<ProtocolViolationException>(() => a.Receive(B, Pkt(PacketKind.Delete, A, m.Id)));
        Assert.False(a.GetMessages(B)[0].IsDeleted);
    }

    [Fact]
    public void Reaction_ReplacesPreviousOnePerPerson_AndIsClearedByDelete()
    {
        var (net, a, b) = Pair();
        ChatMessage m = a.SendText(B, "x");
        net.Pump();
        b.React(A, m.Id, "👍");
        b.React(A, m.Id, "❤️");
        net.Pump();
        Assert.Single(a.GetMessages(B)[0].Reactions);
        Assert.Equal("❤️", a.GetMessages(B)[0].Reactions[B]);
        a.DeleteMessage(B, m.Id);
        Assert.Empty(a.GetMessages(B)[0].Reactions);
    }

    [Fact]
    public void Reaction_ToUnknownMessage_IsIgnored_LocalRefused()
    {
        var (net, a, b) = Pair();
        a.SendText(B, "x");
        net.Pump();
        b.Receive(A, Pkt(PacketKind.Reaction, B, TestHelpers.NewGuid()) with { Emoji = "👍" });
        Assert.Throws<InvalidOperationException>(() => b.React(A, TestHelpers.NewGuid(), "👍"));
    }

    [Fact]
    public void EarlierSnapshot_IsNotAffectedByLaterChanges()
    {
        var (net, a, _) = Pair();
        ChatMessage m = a.SendText(B, "v1");
        net.Pump();
        ChatMessage before = a.GetMessages(B)[0];
        a.EditMessage(B, m.Id, "v2");
        Assert.Equal("v1", before.Text);
        Assert.Equal("v2", a.GetMessages(B)[0].Text);
    }

    // ───────── Saisies locales invalides ─────────

    [Fact]
    public void SendText_Invalid_Throws()
    {
        var (_, a, _) = Pair();
        Assert.ThrowsAny<ArgumentException>(() => a.SendText(B, ""));
        Assert.ThrowsAny<ArgumentException>(() => a.SendText(B, new string('x', ProtocolLimits.MaxTextChars + 1)));
        Assert.ThrowsAny<ArgumentException>(() => a.SendText("pas-un-peerid", "x"));
        Assert.ThrowsAny<ArgumentException>(() => a.SendText(A, "à moi-même"));
        Assert.ThrowsAny<ArgumentException>(() => a.SendText(B, "x", "pas-un-guid"));
    }

    [Fact]
    public void Constructor_InvalidSelfId_Throws()
    {
        Assert.ThrowsAny<ArgumentException>(() => NewService("x"));
        Assert.ThrowsAny<ArgumentException>(() => NewService(new string('A', 64)));
        Assert.NotNull(NewService(A));
    }

    private static ChatService NewService(string id) => new(id, new NullTransport());

    private sealed class NullTransport : ITransport
    {
        public bool TrySend(string peerId, Packet packet) => false;
    }

    [Fact]
    public void OutboxFull_RefusesNewMessages_WithoutLosingOldOnes()
    {
        Net net = new();
        ChatService a = net.Add(A);
        net.Add(B);
        net.SetOnline(B, false);
        for (int i = 0; i < ChatService.MaxOutboxPerPeer; i++)
        {
            a.SendText(B, "m" + i);
        }

        Assert.Throws<InvalidOperationException>(() => a.SendText(B, "de trop"));
        Assert.Equal(ChatService.MaxOutboxPerPeer, a.PendingCount(B));
        Assert.Equal(ChatService.MaxOutboxPerPeer, a.GetMessages(B).Count);
    }

    // ───────── Typing ─────────

    [Fact]
    public void Typing_IsSentWhenOnline_AndNeverQueued()
    {
        var (net, a, b) = Pair();
        a.SendText(B, "x");
        net.Pump();
        List<ChatChangeKind> seen = new();
        b.Changed += (_, e) => seen.Add(e.Kind);
        a.SendTyping(B);
        net.Pump();
        Assert.Contains(ChatChangeKind.Typing.ToString(), string.Join(",", seen));

        net.SetOnline(B, false);
        a.SendTyping(B);
        Assert.Equal(0, a.PendingCount(B));
    }

    // ───────── Groupes ─────────

    private static (Net Net, ChatService A, ChatService B, ChatService C, string Group) Trio()
    {
        Net n = new();
        ChatService a = n.Add(A);
        ChatService b = n.Add(B);
        ChatService c = n.Add(C);
        string g = a.CreateGroup("  Les amis ", Others).Id;
        n.Pump();
        return (n, a, b, c, g);
    }

    [Fact]
    public void Group_IsCreatedOnEveryMember_WithNormalizedName()
    {
        var (_, a, b, c, g) = Trio();
        Assert.Equal("Les amis", a.GetConversations().First(x => x.Id == g).Title);
        Assert.Equal("Les amis", b.GetConversations().First(x => x.Id == g).Title);
        Assert.Equal(A, c.GetConversations().First(x => x.Id == g).CreatorId);
        Assert.Equal(3, b.GetConversations().First(x => x.Id == g).Members.Count);
    }

    [Fact]
    public void Group_MessageReachesAllMembers_StatusIsLowestAcrossRecipients()
    {
        var (net, a, b, c, g) = Trio();
        net.SetOnline(C, false);
        a.SendText(g, "à tous");
        net.Pump();
        Assert.Single(b.GetMessages(g));
        Assert.Equal(MessageStatus.Pending, a.GetMessages(g)[0].Status);   // C n'a rien reçu
        net.SetOnline(C, true);
        a.OnPeerConnected(C);
        net.Pump();
        Assert.Single(c.GetMessages(g));
        Assert.Equal(MessageStatus.Delivered, a.GetMessages(g)[0].Status);
        b.MarkRead(g);
        net.Pump();
        Assert.Equal(MessageStatus.Delivered, a.GetMessages(g)[0].Status); // C n'a pas encore lu
        c.MarkRead(g);
        net.Pump();
        Assert.Equal(MessageStatus.Read, a.GetMessages(g)[0].Status);
    }

    [Fact]
    public void Group_MemberReplies_OthersSeeIt()
    {
        var (net, a, b, c, g) = Trio();
        b.SendText(g, "salut");
        net.Pump();
        Assert.Equal(B, a.GetMessages(g)[0].SenderId);
        Assert.Equal(B, c.GetMessages(g)[0].SenderId);
    }

    [Fact]
    public void Group_TextFromNonMember_IsViolation()
    {
        var (_, a, _, _, g) = Trio();
        Assert.Throws<ProtocolViolationException>(() => a.Receive(D, Pkt(PacketKind.Text, g, text: "intrus")));
    }

    [Fact]
    public void Group_UnknownGroupText_IsViolation()
    {
        var (_, _, b) = Pair();
        Assert.Throws<ProtocolViolationException>(() => b.Receive(A, Pkt(PacketKind.Text, TestHelpers.NewGuid(), text: "x")));
    }

    [Fact]
    public void GroupCreate_Inconsistent_IsViolation()
    {
        var (_, _, b) = Pair();
        Packet notMe = Pkt(PacketKind.GroupCreate, TestHelpers.NewGuid()) with { GroupName = "G", Members = new[] { A, C } };
        Packet noCreator = Pkt(PacketKind.GroupCreate, TestHelpers.NewGuid()) with { GroupName = "G", Members = new[] { B, C } };
        Assert.Throws<ProtocolViolationException>(() => b.Receive(A, notMe));
        Assert.Throws<ProtocolViolationException>(() => b.Receive(A, noCreator));
        Assert.Empty(b.GetConversations());
    }

    [Fact]
    public void GroupCreate_HijackByAnotherCreator_IsViolation_ReplayBySameCreatorIgnored()
    {
        var (_, _, b, _, g) = Trio();
        Packet hijack = Pkt(PacketKind.GroupCreate, g) with { GroupName = "Pirate", Members = new[] { C, B } };
        Assert.Throws<ProtocolViolationException>(() => b.Receive(C, hijack));
        Packet replay = Pkt(PacketKind.GroupCreate, g) with { GroupName = "Les amis", Members = new[] { A, B, C } };
        b.Receive(A, replay);
        Assert.Equal("Les amis", b.GetConversations().First(x => x.Id == g).Title);
    }

    [Fact]
    public void CreateGroup_InvalidInputs_Throw()
    {
        var (_, a, _) = Pair();
        Assert.ThrowsAny<ArgumentException>(() => a.CreateGroup("", new[] { B }));
        Assert.ThrowsAny<ArgumentException>(() => a.CreateGroup("G", Array.Empty<string>()));
        Assert.ThrowsAny<ArgumentException>(() => a.CreateGroup("G", new[] { A }));
        Assert.ThrowsAny<ArgumentException>(() => a.CreateGroup("G", NotAPeer));
        string[] tooMany = Enumerable.Range(0, ProtocolLimits.MaxGroupMembers).Select(i => i.ToString("x64", System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        Assert.ThrowsAny<ArgumentException>(() => a.CreateGroup("G", tooMany));
    }

    // ───────── Événements et concurrence ─────────

    [Fact]
    public void Events_AreRaised_AndHandlerMayCallBackIntoService()
    {
        var (net, a, b) = Pair();
        int added = 0;
        b.Changed += (_, e) =>
        {
            if (e.Kind == ChatChangeKind.MessageAdded)
            {
                added += b.GetMessages(e.ConversationId).Count;
            }
        };
        a.SendText(B, "x");
        net.Pump();
        Assert.Equal(1, added);
    }

    [Fact]
    public void ParallelSends_AreAllRecorded()
    {
        var (net, a, b) = Pair();
        List<Thread> threads = new();
        for (int t = 0; t < 8; t++)
        {
            int id = t;
            Thread th = new(() =>
            {
                for (int i = 0; i < 50; i++)
                {
                    a.SendText(B, $"t{id}-{i}");
                }
            });
            threads.Add(th);
            th.Start();
        }

        foreach (Thread th in threads)
        {
            th.Join();
        }

        net.Pump();
        Assert.Equal(400, a.GetMessages(B).Count);
        Assert.Equal(400, b.GetMessages(A).Count);
        Assert.True(a.GetMessages(B).All(m => m.Status == MessageStatus.Delivered));
    }
}
