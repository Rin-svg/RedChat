using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using RedChat.Core;
using Xunit;
using ProtocolViolationException = RedChat.Core.ProtocolViolationException;

namespace RedChat.Infrastructure.Tests;

public class TransportTests
{
    private static bool Has(LanNode n, string conv, string text) =>
        n.Chat.GetMessages(conv).Any(m => m.Text == text);

    [Fact]
    public void TwoNodes_ExchangeMessages_WithReceipts()
    {
        using NetHelpers.Rig rig = new();
        LanNode a = rig.Add();
        LanNode b = rig.Add();
        NetHelpers.Rig.Link(a, b);

        string big = new string('é', 3000) + "👨\u200D👩\u200D👧" + new string('x', 4000);
        a.Chat.SendText(b.PeerId, "salut Bob");
        a.Chat.SendText(b.PeerId, big);
        Assert.True(NetHelpers.WaitUntil(() => b.Chat.GetMessages(a.PeerId).Count == 2));
        Assert.Equal("salut Bob", b.Chat.GetMessages(a.PeerId)[0].Text);
        Assert.Equal(big, b.Chat.GetMessages(a.PeerId)[1].Text);
        Assert.True(NetHelpers.WaitUntil(() => a.Chat.GetMessages(b.PeerId).All(m => m.Status == MessageStatus.Delivered)));

        b.Chat.MarkRead(a.PeerId);
        Assert.True(NetHelpers.WaitUntil(() => a.Chat.GetMessages(b.PeerId).All(m => m.Status == MessageStatus.Read)));

        b.Chat.SendText(a.PeerId, "réponse");
        Assert.True(NetHelpers.WaitUntil(() => Has(a, b.PeerId, "réponse")));
    }

    [Fact]
    public async Task TwoNodes_ExchangeAuthenticatedCallInvitationAndAcceptance()
    {
        using NetHelpers.Rig rig = new();
        LanNode caller = rig.Add();
        LanNode receiver = rig.Add();
        NetHelpers.Rig.Link(caller, receiver);
        TaskCompletionSource<CallSessionSnapshot> incoming = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<CallSessionSnapshot> accepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.Calls.SessionChanged += (_, e) =>
        {
            if (e.Session?.State == CallSessionState.IncomingRinging)
            {
                incoming.TrySetResult(e.Session);
            }
        };
        caller.Calls.SessionChanged += (_, e) =>
        {
            if (e.Session?.State == CallSessionState.Accepted)
            {
                accepted.TrySetResult(e.Session);
            }
        };

        string callId = caller.Calls.StartCall(receiver.PeerId, CallMediaKind.Audio);
        CallSessionSnapshot request = await incoming.Task.WaitAsync(NetHelpers.Limit);
        Assert.Equal(callId, request.CallId);
        Assert.Equal(caller.PeerId, request.PeerId);
        receiver.Calls.AcceptIncoming();
        CallSessionSnapshot response = await accepted.Task.WaitAsync(NetHelpers.Limit);
        Assert.Equal(callId, response.CallId);
        Assert.Equal(CallSessionState.Accepted, response.State);
    }

    [Fact]
    public async Task TwoNodes_ExchangeCallRejection()
    {
        using NetHelpers.Rig rig = new();
        LanNode caller = rig.Add();
        LanNode receiver = rig.Add();
        NetHelpers.Rig.Link(caller, receiver);
        TaskCompletionSource<CallSessionSnapshot> rejected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        caller.Calls.SessionChanged += (_, e) =>
        {
            if (e.Session?.State == CallSessionState.Rejected)
            {
                rejected.TrySetResult(e.Session);
            }
        };

        string callId = caller.Calls.StartCall(receiver.PeerId, CallMediaKind.Audio);
        Assert.True(NetHelpers.WaitUntil(() => receiver.Calls.ActiveSession?.State == CallSessionState.IncomingRinging));
        receiver.Calls.RejectIncoming();
        CallSessionSnapshot result = await rejected.Task.WaitAsync(NetHelpers.Limit);
        Assert.Equal(callId, result.CallId);
        Assert.Equal(CallSessionState.Rejected, result.State);
    }

    [Fact]
    public async Task TwoNodes_ExchangeWebRtcOfferAnswerAndIceOverAuthenticatedChannel()
    {
        using NetHelpers.Rig rig = new();
        LanNode caller = rig.Add();
        LanNode receiver = rig.Add();
        NetHelpers.Rig.Link(caller, receiver);

        TaskCompletionSource<CallSessionSnapshot> incoming = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<CallSignalReceivedEventArgs> offerReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<CallSignalReceivedEventArgs> answerReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<CallSignalReceivedEventArgs> iceReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.Calls.SessionChanged += (_, e) =>
        {
            if (e.Session?.State == CallSessionState.IncomingRinging)
            {
                incoming.TrySetResult(e.Session);
            }
        };
        receiver.Calls.SignalReceived += (_, e) =>
        {
            if (e.Action == CallControlAction.Offer)
            {
                offerReceived.TrySetResult(e);
            }
        };
        caller.Calls.SignalReceived += (_, e) =>
        {
            if (e.Action == CallControlAction.Answer)
            {
                answerReceived.TrySetResult(e);
            }
            else if (e.Action == CallControlAction.IceCandidate)
            {
                iceReceived.TrySetResult(e);
            }
        };

        string callId = caller.Calls.StartCall(receiver.PeerId, CallMediaKind.Video);
        await incoming.Task.WaitAsync(NetHelpers.Limit);
        receiver.Calls.AcceptIncoming();
        Assert.True(NetHelpers.WaitUntil(() => caller.Calls.ActiveSession?.State == CallSessionState.Accepted));

        const string offer = "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\n";
        const string answer = "v=0\r\no=- 2 2 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\n";
        const string candidate = "candidate:1 1 UDP 2122260223 192.168.1.10 50000 typ host";
        caller.Calls.SendOffer(offer);
        CallSignalReceivedEventArgs offerEvent = await offerReceived.Task.WaitAsync(NetHelpers.Limit);
        Assert.Equal(callId, offerEvent.Session.CallId);
        Assert.Equal(offer, offerEvent.Data);

        receiver.Calls.SendAnswer(answer);
        CallSignalReceivedEventArgs answerEvent = await answerReceived.Task.WaitAsync(NetHelpers.Limit);
        Assert.Equal(answer, answerEvent.Data);

        receiver.Calls.SendIceCandidate(candidate, "0", 0);
        CallSignalReceivedEventArgs iceEvent = await iceReceived.Task.WaitAsync(NetHelpers.Limit);
        Assert.Equal(candidate, iceEvent.Data);
        Assert.Equal("0", iceEvent.Mid);
        Assert.Equal(0, iceEvent.MLineIndex);
    }

    [Fact]
    public void CallInvitation_RequiresAuthenticatedConnectedPeer()
    {
        using NetHelpers.Rig rig = new();
        LanNode caller = rig.Add();
        LanNode receiver = rig.Add();
        Assert.Throws<InvalidOperationException>(() => caller.Calls.StartCall(receiver.PeerId, CallMediaKind.Audio));
    }

    [Fact]
    public async Task TwoNodes_TransferMultiChunkAttachment_AndVerifySha256()
    {
        using NetHelpers.Rig rig = new();
        using TempRoot sourceRoot = new();
        using TempRoot destinationRoot = new();
        LanNode sender = rig.Add(options: new LanNodeOptions
        {
            BindAddress = IPAddress.Loopback,
            EnableDiscovery = false,
            AttachmentDirectory = Path.Combine(sourceRoot.Path, "sender-incoming"),
        });
        LanNode receiver = rig.Add(options: new LanNodeOptions
        {
            BindAddress = IPAddress.Loopback,
            EnableDiscovery = false,
            AttachmentDirectory = destinationRoot.Path,
        });
        NetHelpers.Rig.Link(sender, receiver);
        Directory.CreateDirectory(sourceRoot.Path);
        string sourcePath = Path.Combine(sourceRoot.Path, "archive.bin");
        byte[] expected = RandomNumberGenerator.GetBytes(900_000);
        await File.WriteAllBytesAsync(sourcePath, expected);

        TaskCompletionSource<AttachmentReceivedEventArgs> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.Attachments.FileReceived += (_, e) => received.TrySetResult(e);

        await sender.Attachments.SendFileAsync(receiver.PeerId, sourcePath);
        AttachmentReceivedEventArgs result = await received.Task.WaitAsync(NetHelpers.Limit);

        Assert.Equal("archive.bin", result.FileName);
        Assert.Equal(expected.LongLength, result.FileSize);
        Assert.Equal(expected, await File.ReadAllBytesAsync(result.FilePath));
        Assert.True(NetHelpers.WaitUntil(() => receiver.Chat.GetMessages(sender.PeerId).Any(m => m.Text.StartsWith("📎 archive.bin", StringComparison.Ordinal))));
    }

    [Fact]
    public async Task ViewOnceImage_IsDeletedAfterOpen_AndAcknowledgedToSender()
    {
        using NetHelpers.Rig rig = new();
        using TempRoot sourceRoot = new();
        using TempRoot destinationRoot = new();
        LanNode sender = rig.Add(options: new LanNodeOptions
        {
            BindAddress = IPAddress.Loopback,
            EnableDiscovery = false,
            AttachmentDirectory = Path.Combine(sourceRoot.Path, "sender-incoming"),
        });
        LanNode receiver = rig.Add(options: new LanNodeOptions
        {
            BindAddress = IPAddress.Loopback,
            EnableDiscovery = false,
            AttachmentDirectory = destinationRoot.Path,
        });
        NetHelpers.Rig.Link(sender, receiver);

        string sourcePath = Path.Combine(sourceRoot.Path, "photo.jpg");
        byte[] expected = RandomNumberGenerator.GetBytes(256);
        await File.WriteAllBytesAsync(sourcePath, expected);

        TaskCompletionSource<AttachmentReceivedEventArgs> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<string> opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.Attachments.FileReceived += (_, e) => received.TrySetResult(e);
        sender.Attachments.ViewOnceAttachmentOpened += transferId => opened.TrySetResult(transferId);

        await sender.Attachments.SendFileAsync(receiver.PeerId, sourcePath, isViewOnce: true);
        AttachmentReceivedEventArgs attachment = await received.Task.WaitAsync(NetHelpers.Limit);

        Assert.True(attachment.IsViewOnce);
        Assert.Contains("ViewOnce", attachment.FilePath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(expected, await File.ReadAllBytesAsync(attachment.FilePath));
        Assert.True(receiver.Attachments.ConsumeViewOnceAttachment(attachment));
        Assert.False(File.Exists(attachment.FilePath));
        Assert.Equal(attachment.TransferId, await opened.Task.WaitAsync(NetHelpers.Limit));
    }

    [Fact]
    public void AttachmentWithIncorrectSha256_IsRejectedAndTemporaryFileRemoved()
    {
        using NetHelpers.Rig rig = new();
        using TempRoot destinationRoot = new();
        LanNode sender = rig.Add();
        LanNode receiver = rig.Add(options: new LanNodeOptions
        {
            BindAddress = IPAddress.Loopback,
            EnableDiscovery = false,
            AttachmentDirectory = destinationRoot.Path,
        });
        byte[] content = { 1, 2, 3, 4 };
        Packet packet = new()
        {
            Kind = PacketKind.AttachmentChunk,
            Id = Guid.NewGuid().ToString("D"),
            ConversationId = receiver.PeerId,
            SentAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            AttachmentTransferId = Guid.NewGuid().ToString("D"),
            AttachmentFileName = "safe.bin",
            AttachmentFileSize = content.Length,
            AttachmentSha256 = Convert.ToHexString(SHA256.HashData(new byte[] { 4, 3, 2, 1 })),
            AttachmentChunkIndex = 0,
            AttachmentChunkCount = 1,
            AttachmentChunkData = content,
        };

        Assert.Throws<InvalidDataException>(() => receiver.Attachments.Receive(sender.PeerId, packet));
        Assert.Empty(Directory.GetFiles(destinationRoot.Path));
    }

    [Fact]
    public void TwoNodes_ExchangeAuthenticatedPublicProfiles_WithoutOverwritingLocalNicknames()
    {
        using NetHelpers.Rig rig = new();
        LanNode a = rig.Add();
        LanNode b = rig.Add();
        a.SetLocalProfile(new PersonalProfile
        {
            PeerId = a.PeerId,
            DisplayName = "Alice publique",
            Status = "Disponible",
            About = "Profil de test Alice",
        });
        b.SetLocalProfile(new PersonalProfile
        {
            PeerId = b.PeerId,
            DisplayName = "Bob public",
            Status = "Occupé",
            About = "Profil de test Bob",
        });

        NetHelpers.Rig.Link(a, b);

        Assert.True(NetHelpers.WaitUntil(() =>
            a.Contacts.Find(b.PeerId)?.PublicProfile?.DisplayName == "Bob public"
            && b.Contacts.Find(a.PeerId)?.PublicProfile?.DisplayName == "Alice publique"));

        a.Contacts.Rename(b.PeerId, "Mon surnom de Bob");
        Assert.Equal("Mon surnom de Bob", a.Contacts.DisplayLabel(b.PeerId));
        Assert.Equal("Bob public", a.Contacts.Find(b.PeerId)!.PublicProfile!.DisplayName);
    }

    [Fact]
    public void ProfileUpdate_ClaimingAnotherPeerIdentity_IsRejected()
    {
        using NetHelpers.Rig rig = new();
        LanNode sender = rig.Add();
        LanNode receiver = rig.Add();
        LanNode impersonated = rig.Add();
        NetHelpers.Rig.Link(sender, receiver);

        Packet forged = new()
        {
            Kind = PacketKind.ProfileUpdate,
            Id = Guid.NewGuid().ToString("D"),
            ConversationId = impersonated.PeerId,
            SentAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ProfilePeerId = impersonated.PeerId,
            ProfileDisplayName = "Faux profil",
            ProfileStatus = string.Empty,
            ProfileAbout = string.Empty,
        };

        Assert.True(sender.Transport.TrySend(receiver.PeerId, forged));
        Assert.True(NetHelpers.WaitUntil(() => !receiver.Transport.IsConnected(sender.PeerId)));
        Assert.Null(receiver.Contacts.Find(impersonated.PeerId));
    }

    [Fact]
    public async Task ConnectWithWrongExpectedPeer_IsRefused_AsImpersonation()
    {
        using NetHelpers.Rig rig = new();
        LanNode a = rig.Add();
        LanNode b = rig.Add();
        string wrong = new('f', 64);
        await Assert.ThrowsAnyAsync<ProtocolViolationException>(() => a.Transport.ConnectAsync(NetHelpers.Rig.EndpointOf(b), wrong, CancellationToken.None));
        Thread.Sleep(200);
        Assert.False(a.Transport.IsConnected(b.PeerId));
        Assert.False(b.Transport.IsConnected(a.PeerId));
    }

    [Fact]
    public async Task ConnectManual_RejectsNonLanOrMalformedTargets()
    {
        using NetHelpers.Rig rig = new();
        LanNode a = rig.Add();
        await Assert.ThrowsAnyAsync<ArgumentException>(() => a.ConnectManualAsync("8.8.8.8:50000"));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => a.ConnectManualAsync("example.com:50000"));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => a.ConnectManualAsync("192.168.1.5:80"));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => a.ConnectManualAsync(""));
    }

    [Fact]
    public void ConnectManual_WorksOnLoopback()
    {
        using NetHelpers.Rig rig = new();
        LanNode a = rig.Add();
        LanNode b = rig.Add();
        string peer = NetHelpers.Await(a.ConnectManualAsync(NetHelpers.Rig.Text(b)));
        Assert.Equal(b.PeerId, peer);
    }

    [Fact]
    public void MessagesSentBeforeConnection_AreDeliveredInOrderOnConnect()
    {
        using NetHelpers.Rig rig = new();
        LanNode a = rig.Add();
        LanNode b = rig.Add();
        a.Chat.SendText(b.PeerId, "1");
        a.Chat.SendText(b.PeerId, "2");
        a.Chat.SendText(b.PeerId, "3");
        Assert.Equal(3, a.Chat.PendingCount(b.PeerId));
        NetHelpers.Rig.Link(a, b);
        Assert.True(NetHelpers.WaitUntil(() => b.Chat.GetMessages(a.PeerId).Count == 3));
        Assert.Equal("123", string.Concat(b.Chat.GetMessages(a.PeerId).Select(m => m.Text)));
        Assert.True(NetHelpers.WaitUntil(() => a.Chat.PendingCount(b.PeerId) == 0));
    }

    [Fact]
    public void Reconnect_AfterPeerRestart_DeliversWhatWasWaiting()
    {
        using NetHelpers.Rig rig = new();
        LocalIdentity idB = rig.NewIdentity();
        LanNode a = rig.Add();
        LanNode b1 = rig.Add(idB);
        NetHelpers.Rig.Link(a, b1);
        a.Chat.SendText(b1.PeerId, "avant");
        Assert.True(NetHelpers.WaitUntil(() => Has(b1, a.PeerId, "avant")));

        NetHelpers.Await(b1.DisposeAsync().AsTask());
        Assert.True(NetHelpers.WaitUntil(() => !a.Transport.IsConnected(idB.PeerId)));
        a.Chat.SendText(idB.PeerId, "pendant");
        Assert.Equal(1, a.Chat.PendingCount(idB.PeerId));

        LanNode b2 = rig.Add(idB);          // même identité, nouveau port : « redémarrage »
        NetHelpers.Rig.Link(a, b2);
        Assert.True(NetHelpers.WaitUntil(() => Has(b2, a.PeerId, "pendant")));
        Assert.True(NetHelpers.WaitUntil(() => a.Chat.GetMessages(idB.PeerId).All(m => m.Status >= MessageStatus.Delivered)));
    }

    [Fact]
    public void SimultaneousConnectionsInBothDirections_LeaveOneWorkingLink()
    {
        for (int round = 0; round < 5; round++)
        {
            using NetHelpers.Rig rig = new();
            LanNode a = rig.Add();
            LanNode b = rig.Add();
            Task<string> t1 = Task.Run(() => a.Transport.ConnectAsync(NetHelpers.Rig.EndpointOf(b), null, CancellationToken.None));
            Task<string> t2 = Task.Run(() => b.Transport.ConnectAsync(NetHelpers.Rig.EndpointOf(a), null, CancellationToken.None));
            _ = NetHelpers.Await(t1);
            _ = NetHelpers.Await(t2);
            Assert.True(NetHelpers.WaitUntil(() => a.Transport.IsConnected(b.PeerId) && b.Transport.IsConnected(a.PeerId)));
            Thread.Sleep(300);              // laisse le temps à la règle de départage de fermer la connexion en trop
            Assert.True(a.Transport.IsConnected(b.PeerId) && b.Transport.IsConnected(a.PeerId));

            a.Chat.SendText(b.PeerId, "a->b");
            b.Chat.SendText(a.PeerId, "b->a");
            Assert.True(NetHelpers.WaitUntil(() => Has(b, a.PeerId, "a->b") && Has(a, b.PeerId, "b->a")));
        }
    }

    [Fact]
    public void Group_WorksAcrossThreeNodes()
    {
        using NetHelpers.Rig rig = new();
        LanNode a = rig.Add();
        LanNode b = rig.Add();
        LanNode c = rig.Add();
        NetHelpers.Rig.Link(a, b);
        NetHelpers.Rig.Link(a, c);
        NetHelpers.Rig.Link(b, c);
        string g = a.Chat.CreateGroup("Équipe", new[] { b.PeerId, c.PeerId }).Id;
        Assert.True(NetHelpers.WaitUntil(() => b.Chat.GetConversations().Any(x => x.Id == g) && c.Chat.GetConversations().Any(x => x.Id == g)));
        b.Chat.SendText(g, "bonjour le groupe");
        Assert.True(NetHelpers.WaitUntil(() => Has(a, g, "bonjour le groupe") && Has(c, g, "bonjour le groupe")));
        Assert.True(NetHelpers.WaitUntil(() => b.Chat.GetMessages(g)[0].Status == MessageStatus.Delivered));
    }

    // ───────── Clients hostiles ─────────

    private static TcpClient RawClient(LanNode target)
    {
        TcpClient c = new();
        c.Connect(IPAddress.Loopback, target.Transport.Port);
        return c;
    }

    /// <summary>Vrai si le serveur a fermé la connexion (lecture : 0 octet ou erreur) dans le délai.</summary>
    private static bool ServerClosed(TcpClient c, int ms = 5000)
    {
        c.ReceiveTimeout = ms;
        byte[] buf = new byte[1];
        try
        {
            return c.GetStream().Read(buf, 0, 1) == 0;
        }
        catch (IOException ex)
        {
            // Délai dépassé = le serveur n'a PAS fermé (test en échec) ; reset/broken pipe = fermé.
            return ex.InnerException is not SocketException { SocketErrorCode: SocketError.TimedOut };
        }
    }

    private static void AssertStillServes(LanNode victim, NetHelpers.Rig rig)
    {
        LanNode friend = rig.Add();
        NetHelpers.Rig.Link(friend, victim);
        friend.Chat.SendText(victim.PeerId, "toujours là ?");
        Assert.True(NetHelpers.WaitUntil(() => Has(victim, friend.PeerId, "toujours là ?")));
    }

    [Fact]
    public void GarbageHandshake_IsDropped_AndNodeKeepsServing()
    {
        using NetHelpers.Rig rig = new();
        LanNode victim = rig.Add();
        string diag = string.Empty;
        victim.Transport.Diagnostic += (_, e) => diag += e.Message;
        using TcpClient raw = RawClient(victim);
        byte[] frame = { 0, 0, 0, 5, 1, 2, 3, 4, 5 };       // trame bien formée, contenu absurde
        raw.GetStream().Write(frame, 0, frame.Length);
        Assert.True(ServerClosed(raw));
        Assert.True(NetHelpers.WaitUntil(() => diag.Contains("Handshake", StringComparison.Ordinal)));
        AssertStillServes(victim, rig);
    }

    [Fact]
    public void FrameBombHeader_IsDropped_WithoutAllocatingOrHanging()
    {
        using NetHelpers.Rig rig = new();
        LanNode victim = rig.Add();
        using TcpClient raw = RawClient(victim);
        byte[] header = { 0xFF, 0xFF, 0xFF, 0xFF };          // 4 Gio annoncés
        raw.GetStream().Write(header, 0, header.Length);
        Assert.True(ServerClosed(raw));
        AssertStillServes(victim, rig);
    }

    [Fact]
    public void ClientThatDisconnectsMidHandshake_DoesNotLeakSlots()
    {
        using NetHelpers.Rig rig = new();
        LanNode victim = rig.Add();
        for (int i = 0; i < TcpTransport.MaxPendingPerAddress * 4; i++)
        {
            using TcpClient raw = RawClient(victim);
            try
            {
                raw.GetStream().Write(new byte[] { 0, 0 }, 0, 2); // moitié d'un en-tête puis fermeture
            }
            catch (IOException)
            {
                // le serveur a déjà refusé cette connexion (limite par adresse) : c'est un comportement légitime.
            }
        }

        Thread.Sleep(800);                                                            // le serveur traite les connexions en file
        Assert.True(NetHelpers.WaitUntil(() => victim.Transport.ActiveSlots == 0));   // aucun créneau ne doit fuir
        AssertStillServes(victim, rig);
        Assert.True(NetHelpers.WaitUntil(() => victim.Transport.ActiveSlots == 1));   // seule la session légitime occupe un créneau
    }

    [Fact]
    public async Task FailedOutgoingConnections_DoNotLeakSlots()
    {
        using NetHelpers.Rig rig = new();
        LanNode a = rig.Add();
        LanNode b = rig.Add();
        string wrong = new('f', 64);
        for (int i = 0; i < 5; i++)
        {
            await Assert.ThrowsAnyAsync<ProtocolViolationException>(() => a.Transport.ConnectAsync(NetHelpers.Rig.EndpointOf(b), wrong, CancellationToken.None));
        }

        Assert.Equal(0, a.Transport.ActiveSlots);
        NetHelpers.Rig.Link(a, b);
        Assert.Equal(1, a.Transport.ActiveSlots);
        NetHelpers.Await(b.DisposeAsync().AsTask());
        Assert.True(NetHelpers.WaitUntil(() => a.Transport.ActiveSlots == 0));
    }

    [Fact]
    public void TooManyStalledHandshakes_FromOneAddress_AreRefused()
    {
        using NetHelpers.Rig rig = new();
        LanNode victim = rig.Add();
        List<TcpClient> stalled = new();
        try
        {
            for (int i = 0; i < TcpTransport.MaxPendingPerAddress; i++)
            {
                stalled.Add(RawClient(victim));               // ouvrent la connexion et se taisent
            }

            Thread.Sleep(300);
            using TcpClient excess = RawClient(victim);
            Assert.True(ServerClosed(excess, 3000));
        }
        finally
        {
            foreach (TcpClient c in stalled)
            {
                c.Dispose();
            }
        }

        Thread.Sleep(500);                                    // les handshakes bloqués échouent et rendent leurs créneaux
        AssertStillServes(victim, rig);
    }
}
