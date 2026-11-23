using System.Security.Cryptography;
using System.Text;
using RedChat.Core;
using Xunit;

namespace RedChat.Infrastructure.Tests;

public sealed class PersistenceTests : IDisposable
{
    private readonly TempRoot _tmp = new();

    public void Dispose() => _tmp.Dispose();

    /// <summary>Une « session » de l'application : profil + service + contacts + persistance, avec horloge et minuteur manuels.</summary>
    private sealed class Session : IDisposable
    {
        public Session(string root, TestProtector? protector = null, TimeSpan? debounce = null)
        {
            Protector = protector ?? new TestProtector();
            Clock = new ManualTimeProvider();
            Store = new ProfileStore(root, "alice", Protector, Clock);
            Chat = new ChatService(Fixtures.Self, new OfflineTransport(), Clock);
            Contacts = new ContactBook(Fixtures.Self, Clock);
            Favorites = new FavoriteConversations();
            Persistence = new ChatPersistence(Chat, Contacts, Store, Clock, debounce ?? TimeSpan.FromMilliseconds(100), Favorites);
        }

        public TestProtector Protector { get; }

        public ManualTimeProvider Clock { get; }

        public ProfileStore Store { get; }

        public ChatService Chat { get; }

        public ContactBook Contacts { get; }

        public FavoriteConversations Favorites { get; }

        public ChatPersistence Persistence { get; }

        public ManualTimeProvider.ManualTimer Timer => Clock.Timer!;

        public void Dispose() => Persistence.Dispose();
    }

    private static string[] Files(ProfileStore s) => Directory.GetFiles(s.DirectoryPath).Select(Path.GetFileName).Select(n => n!).ToArray();

    private static bool HasQuarantined(ProfileStore s, string prefix) => Files(s).Any(n => n.StartsWith(prefix + ".corrupt-", StringComparison.Ordinal));

    // ───────────── Démarrage ─────────────

    [Fact]
    public void Start_OnNewProfile_NoNotice_NothingToSave_NoTimerArmed()
    {
        using Session s = new(_tmp.Path);
        s.Persistence.Start();
        Assert.Null(s.Persistence.StartupNotice);
        Assert.False(s.Persistence.HasUnsavedChanges);
        Assert.False(s.Timer.Armed);
        Assert.Empty(s.Chat.GetConversations());
    }

    [Fact]
    public void Start_Twice_Throws()
    {
        using Session s = new(_tmp.Path);
        s.Persistence.Start();
        Assert.ThrowsAny<InvalidOperationException>(() => s.Persistence.Start());
    }

    [Fact]
    public void Constructor_RejectsAbsurdDebounce()
    {
        using Session ok = new(_tmp.Path);
        Assert.ThrowsAny<ArgumentOutOfRangeException>(() => _ = new ChatPersistence(ok.Chat, null, ok.Store, ok.Clock, TimeSpan.FromMinutes(5)));
        Assert.ThrowsAny<ArgumentOutOfRangeException>(() => _ = new ChatPersistence(ok.Chat, null, ok.Store, ok.Clock, TimeSpan.FromSeconds(-1)));
        Assert.ThrowsAny<ArgumentNullException>(() => _ = new ChatPersistence(null!, null, ok.Store));
        Assert.ThrowsAny<ArgumentNullException>(() => _ = new ChatPersistence(ok.Chat, null, null!));
    }

    [Fact]
    public void Favorites_AreSavedAndRestoredAcrossSessions()
    {
        string groupId = Guid.NewGuid().ToString("D");
        using (Session first = new(_tmp.Path))
        {
            first.Persistence.Start();
            first.Favorites.Add(Fixtures.B);
            first.Favorites.Add(groupId);
            Assert.True(first.Persistence.HasUnsavedChanges);
            first.Persistence.Flush();
            Assert.False(first.Persistence.HasUnsavedChanges);
        }

        using Session second = new(_tmp.Path);
        second.Persistence.Start();
        Assert.True(second.Favorites.Contains(Fixtures.B));
        Assert.True(second.Favorites.Contains(groupId));
        Assert.Equal(2, second.Favorites.GetAll().Count);
    }

    // ───────────── Anti-rafale ─────────────

    [Fact]
    public void BurstOfChanges_ProducesASingleWrite()
    {
        using Session s = new(_tmp.Path);
        s.Persistence.Start();
        int before = s.Protector.ProtectCalls;

        for (int i = 0; i < 50; i++)
        {
            _ = s.Chat.SendText(Fixtures.B, "m" + i);
        }

        Assert.True(s.Persistence.HasUnsavedChanges);
        Assert.Equal(1, s.Timer.ChangeCalls);                         // un seul déclenchement armé pour 50 changements
        Assert.Equal(TimeSpan.FromMilliseconds(100), s.Timer.LastDue);
        Assert.Equal(before, s.Protector.ProtectCalls);               // rien n'est encore écrit

        s.Timer.Fire();
        Assert.Equal(before + 1, s.Protector.ProtectCalls);           // une seule écriture
        Assert.False(s.Persistence.HasUnsavedChanges);

        _ = s.Chat.SendText(Fixtures.B, "après");                     // nouvelle rafale : nouveau déclenchement
        Assert.Equal(2, s.Timer.ChangeCalls);
        Assert.True(s.Timer.Armed);
    }

    [Fact]
    public void Flush_WithNothingToSave_WritesNothing()
    {
        using Session s = new(_tmp.Path);
        s.Persistence.Start();
        int before = s.Protector.ProtectCalls;
        s.Persistence.Flush();
        s.Persistence.Flush();
        Assert.Equal(before, s.Protector.ProtectCalls);
        Assert.Empty(Files(s.Store));
    }

    [Fact]
    public void Dispose_FlushesLastChanges_WithoutWaitingForTheTimer()
    {
        string expected;
        using (Session s = new(_tmp.Path))
        {
            s.Persistence.Start();
            _ = s.Chat.SendText(Fixtures.B, "dernier message avant fermeture");
            expected = Fixtures.Describe(s.Chat);
            Assert.True(s.Persistence.HasUnsavedChanges);
        } // Dispose

        using Session again = new(_tmp.Path);
        again.Persistence.Start();
        Assert.Equal(expected, Fixtures.Describe(again.Chat));
    }

    [Fact]
    public void Dispose_Twice_IsHarmless_AndStopsListening()
    {
        Session s = new(_tmp.Path);
        s.Persistence.Start();
        ManualTimeProvider.ManualTimer timer = s.Timer;
        s.Persistence.Dispose();
        s.Persistence.Dispose();
        int calls = timer.ChangeCalls;
        _ = s.Chat.SendText(Fixtures.B, "après la fermeture");
        s.Contacts.EnsureKnown(Fixtures.B, out _);
        Assert.Equal(calls, timer.ChangeCalls);
        Assert.True(timer.Disposed);
    }

    [Fact]
    public void Dispose_WithoutStart_DoesNotWriteAnything()
    {
        Session s = new(_tmp.Path);
        s.Persistence.Dispose();
        Assert.Empty(Files(s.Store));
    }

    // ───────────── Aller-retour entre deux sessions ─────────────

    [Fact]
    public void NewSession_RestoresChatAndContacts_FromTheLastOne()
    {
        string chatBefore;
        using (Session s1 = new(_tmp.Path))
        {
            s1.Persistence.Start();
            _ = s1.Chat.SendText(Fixtures.B, "bonjour");
            _ = s1.Chat.CreateGroup("Les amis", new[] { Fixtures.B, Fixtures.C });
            _ = s1.Contacts.EnsureKnown(Fixtures.B, out _);
            s1.Contacts.Rename(Fixtures.B, "Léa");
            s1.Contacts.SetVerified(Fixtures.B, true);
            s1.Timer.Fire();
            chatBefore = Fixtures.Describe(s1.Chat);
            Assert.False(s1.Persistence.HasUnsavedChanges);
        }

        using Session s2 = new(_tmp.Path);
        s2.Persistence.Start();
        Assert.Null(s2.Persistence.StartupNotice);
        Assert.Equal(chatBefore, Fixtures.Describe(s2.Chat));
        Assert.Equal("Léa", s2.Contacts.Find(Fixtures.B)!.DisplayName);
        Assert.True(s2.Contacts.Find(Fixtures.B)!.IsVerified);
        Assert.False(s2.Persistence.HasUnsavedChanges); // la restauration elle-même ne déclenche pas d'écriture
    }

    // ───────────── Pannes d'écriture ─────────────

    [Theory]
    [InlineData("io")]
    [InlineData("access")]
    [InlineData("crypto")]
    public void SaveFailure_NeverEscapesTheTimer_WarnsTheUser_RetriesWithBackoff_AndRecovers(string kind)
    {
        TestProtector p = new();
        using Session s = new(_tmp.Path, p);
        s.Persistence.Start();

        int failedEvents = 0, recoveredEvents = 0;
        string? lastMessage = null;
        s.Persistence.SaveFailed += (_, e) => { failedEvents++; lastMessage = e.Message; };
        s.Persistence.SaveRecovered += (_, _) => recoveredEvents++;

        p.FailProtect = _ => kind switch
        {
            "io" => new IOException("disque plein (secret interne /tmp/x)"),
            "access" => new UnauthorizedAccessException("refusé"),
            _ => new CryptographicException("panne"),
        };
        _ = s.Chat.SendText(Fixtures.B, "important");

        s.Timer.Fire();                                   // l'appel du minuteur ne doit JAMAIS lever d'exception
        Assert.Equal(1, failedEvents);
        Assert.True(s.Persistence.HasUnsavedChanges);     // rien n'est perdu : toujours « à enregistrer »
        Assert.True(s.Timer.Armed);
        Assert.Equal(TimeSpan.FromMilliseconds(200), s.Timer.LastDue); // délai doublé
        Assert.False(string.IsNullOrWhiteSpace(lastMessage));
        Assert.DoesNotContain("/tmp", lastMessage);        // pas de détail interne dans le message utilisateur
        Assert.DoesNotContain("Exception", lastMessage);

        s.Timer.Fire();
        Assert.Equal(2, failedEvents);
        Assert.Equal(TimeSpan.FromMilliseconds(400), s.Timer.LastDue);
        Assert.Equal(0, recoveredEvents);

        p.FailProtect = null;                             // la panne disparaît
        s.Timer.Fire();
        Assert.Equal(1, recoveredEvents);
        Assert.Equal(2, failedEvents);
        Assert.False(s.Persistence.HasUnsavedChanges);

        s.Chat.MarkRead(Fixtures.B);
        s.Persistence.Flush();                            // un enregistrement normal ensuite ne ré-annonce pas « récupéré »
        Assert.Equal(1, recoveredEvents);

        using Session again = new(_tmp.Path);
        again.Persistence.Start();
        Assert.Single(again.Chat.GetMessages(Fixtures.B));
    }

    [Fact]
    public void RetryDelay_DoublesButIsCapped()
    {
        TestProtector p = new() { FailProtect = _ => new IOException("x") };
        using Session s = new(_tmp.Path, p);
        s.Persistence.Start();
        _ = s.Chat.SendText(Fixtures.B, "x");
        for (int i = 0; i < 15; i++)
        {
            s.Timer.Fire();
        }

        Assert.Equal(ChatPersistence.MaxRetryDelay, s.Timer.LastDue);
    }

    [Fact]
    public void WhenHistoryFails_ContactsAreStillSaved_AndHistoryIsRetriedLater()
    {
        TestProtector p = new() { FailProtect = plain => Encoding.UTF8.GetString(plain).Contains("\"conversations\"", StringComparison.Ordinal) ? new IOException("x") : null };
        using Session s = new(_tmp.Path, p);
        s.Persistence.Start();
        _ = s.Chat.SendText(Fixtures.B, "historique");
        _ = s.Contacts.EnsureKnown(Fixtures.B, out _);
        s.Contacts.Rename(Fixtures.B, "Léa");

        s.Timer.Fire();
        Assert.True(File.Exists(Path.Combine(s.Store.DirectoryPath, ProfileStore.ContactsFileName))); // les contacts n'ont PAS été sautés
        Assert.False(File.Exists(Path.Combine(s.Store.DirectoryPath, ProfileStore.HistoryFileName)));
        Assert.True(s.Persistence.HasUnsavedChanges);

        p.FailProtect = null;
        s.Timer.Fire();
        Assert.False(s.Persistence.HasUnsavedChanges);
        Assert.True(File.Exists(Path.Combine(s.Store.DirectoryPath, ProfileStore.HistoryFileName)));
    }

    [Fact]
    public void WhenContactsFail_HistoryIsStillSaved_AndContactsAreRetriedLater()
    {
        TestProtector p = new() { FailProtect = plain => Encoding.UTF8.GetString(plain).Contains("\"contacts\"", StringComparison.Ordinal) ? new IOException("x") : null };
        using Session s = new(_tmp.Path, p);
        s.Persistence.Start();
        _ = s.Chat.SendText(Fixtures.B, "historique");
        _ = s.Contacts.EnsureKnown(Fixtures.B, out _);

        s.Timer.Fire();
        Assert.True(File.Exists(Path.Combine(s.Store.DirectoryPath, ProfileStore.HistoryFileName)));
        Assert.False(File.Exists(Path.Combine(s.Store.DirectoryPath, ProfileStore.ContactsFileName)));
        Assert.True(s.Persistence.HasUnsavedChanges); // les contacts restent à enregistrer

        p.FailProtect = null;
        s.Timer.Fire();
        Assert.False(s.Persistence.HasUnsavedChanges);
        Assert.True(File.Exists(Path.Combine(s.Store.DirectoryPath, ProfileStore.ContactsFileName)));
    }

    [Fact]
    public void FailureOnDispose_RaisesSaveFailed_ButDoesNotThrow()
    {
        TestProtector p = new();
        Session s = new(_tmp.Path, p);
        s.Persistence.Start();
        int failed = 0;
        s.Persistence.SaveFailed += (_, _) => failed++;
        _ = s.Chat.SendText(Fixtures.B, "x");
        p.FailProtect = _ => new IOException("x");
        s.Persistence.Dispose(); // ne lève rien : l'interface est prévenue, l'application peut se fermer
        Assert.Equal(1, failed);
    }

    // ───────────── Démarrage sur fichiers abîmés ─────────────

    [Fact]
    public void Start_WithCorruptHistory_WarnsStartsEmpty_KeepsTheBadFile_AndSavesNormallyAfterwards()
    {
        string dir;
        using (Session first = new(_tmp.Path))
        {
            first.Persistence.Start();
            _ = first.Chat.SendText(Fixtures.B, "ancien");
            first.Persistence.Flush();
            dir = first.Store.DirectoryPath;
        }

        File.WriteAllBytes(Path.Combine(dir, ProfileStore.HistoryFileName), RandomNumberGenerator.GetBytes(400));
        File.Delete(Path.Combine(dir, ProfileStore.HistoryFileName + AtomicFile.BackupSuffix));

        using Session s = new(_tmp.Path);
        s.Persistence.Start();
        Assert.NotNull(s.Persistence.StartupNotice);
        Assert.Empty(s.Chat.GetConversations());
        Assert.True(HasQuarantined(s.Store, ProfileStore.HistoryFileName));

        _ = s.Chat.SendText(Fixtures.B, "nouveau");
        s.Timer.Fire();
        Assert.True(File.Exists(Path.Combine(dir, ProfileStore.HistoryFileName)));
        Assert.True(HasQuarantined(s.Store, ProfileStore.HistoryFileName)); // l'ancien fichier abîmé est toujours là
    }

    [Fact]
    public void Start_WithSemanticallyHostileHistory_IsSetAside_AndNothingIsPartiallyImported()
    {
        ProfileStore store = new(_tmp.Path, "alice", new TestProtector(), new ManualTimeProvider());
        // JSON parfaitement valide, mais incohérent : un message « sortant » signé par un autre pair, après une conversation saine.
        ChatMessage forged = new() { Id = Guid.NewGuid().ToString("D"), ConversationId = Fixtures.B, SenderId = Fixtures.C, Text = "falsifié", SentAtUnixMs = ManualTimeProvider.Start.ToUnixTimeMilliseconds(), IsOutgoing = true, Receipts = new Dictionary<string, MessageStatus> { [Fixtures.B] = MessageStatus.Pending } };
        ChatMessage fine = new() { Id = Guid.NewGuid().ToString("D"), ConversationId = Fixtures.C, SenderId = Fixtures.C, Text = "sain", SentAtUnixMs = ManualTimeProvider.Start.ToUnixTimeMilliseconds() };
        store.Save(new ChatSnapshot
        {
            Conversations = new[]
            {
                new ConversationSnapshot { Info = new ConversationInfo { Id = Fixtures.C }, Messages = new[] { fine } },
                new ConversationSnapshot { Info = new ConversationInfo { Id = Fixtures.B }, Messages = new[] { forged } },
            },
        });

        using Session s = new(_tmp.Path);
        s.Persistence.Start();
        Assert.NotNull(s.Persistence.StartupNotice);
        Assert.Empty(s.Chat.GetConversations()); // même la conversation saine n'a pas été importée : tout ou rien
        Assert.True(HasQuarantined(s.Store, ProfileStore.HistoryFileName));
    }

    [Fact]
    public void Start_WithSemanticallyHostileContacts_IsSetAside_HistoryStillRestored()
    {
        ProfileStore store = new(_tmp.Path, "alice", new TestProtector(), new ManualTimeProvider());
        ChatService svc = Fixtures.NewService();
        _ = svc.SendText(Fixtures.B, "garde-moi");
        store.Save(svc.ExportState());
        long ms = ManualTimeProvider.Start.ToUnixTimeMilliseconds();
        store.SaveContacts(new ContactsSnapshot
        {
            Contacts = new[]
            {
                new Contact { PeerId = Fixtures.B, DisplayName = "Léa", FirstSeenUnixMs = ms },
                new Contact { PeerId = Fixtures.C, DisplayName = "LÉA", FirstSeenUnixMs = ms }, // même nom : refusé
            },
        });

        using Session s = new(_tmp.Path);
        s.Persistence.Start();
        Assert.NotNull(s.Persistence.StartupNotice);
        Assert.Empty(s.Contacts.GetAll());
        Assert.Single(s.Chat.GetMessages(Fixtures.B));
        Assert.True(HasQuarantined(s.Store, ProfileStore.ContactsFileName));
        Assert.False(HasQuarantined(s.Store, ProfileStore.HistoryFileName));
    }

    [Fact]
    public void Start_WhenHistoryFileIsLocked_Throws_AndKeepsTheFileUntouched()
    {
        string path;
        using (Session first = new(_tmp.Path))
        {
            first.Persistence.Start();
            _ = first.Chat.SendText(Fixtures.B, "à ne pas perdre");
            first.Persistence.Flush();
            path = Path.Combine(first.Store.DirectoryPath, ProfileStore.HistoryFileName);
        }

        byte[] before = File.ReadAllBytes(path);
        using (FileStream locker = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.True(locker.Length > 0);
            using Session s = new(_tmp.Path);
            Assert.ThrowsAny<IOException>(() => s.Persistence.Start()); // on NE démarre PAS à vide : on écraserait un historique sain
        }

        Assert.True(File.ReadAllBytes(path).AsSpan().SequenceEqual(before));
        Assert.False(HasQuarantined(new ProfileStore(_tmp.Path, "alice", new TestProtector()), ProfileStore.HistoryFileName));
    }
}

/// <summary>Scénarios de bout en bout avec de vrais nœuds réseau (boucle locale).</summary>
public sealed class RestartScenarioTests : IDisposable
{
    private readonly TempRoot _tmp = new();

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void ClosingAndReopeningTheApp_KeepsIdentity_History_AndPendingMessageIsDeliveredLater()
    {
        using NetHelpers.Rig rig = new();
        LanNode bob = rig.Add();
        TestProtector protector = new();

        // ── Session 1 : Alice écrit à Bob alors qu'ils ne sont pas connectés, puis ferme l'application.
        ProfileStore store1 = new(_tmp.Path, "alice", protector);
        using LocalIdentity id1 = store1.LoadOrCreateIdentity();
        string alice = id1.PeerId;
        LanNode a1 = rig.Add(id1);
        using (ChatPersistence p1 = new(a1.Chat, a1.Contacts, store1))
        {
            p1.Start();
            ChatMessage m = a1.Chat.SendText(bob.PeerId, "écrit pendant la panne");
            Assert.Equal(MessageStatus.Pending, m.Status);
        } // fermeture : dernière sauvegarde
        NetHelpers.Await(a1.DisposeAsync().AsTask());

        // ── Session 2 : même profil, même identité.
        ProfileStore store2 = new(_tmp.Path, "alice", protector);
        using LocalIdentity id2 = store2.LoadOrCreateIdentity();
        Assert.Equal(alice, id2.PeerId);
        LanNode a2 = rig.Add(id2);
        using ChatPersistence p2 = new(a2.Chat, a2.Contacts, store2);
        p2.Start();
        Assert.Null(p2.StartupNotice);
        Assert.Equal("écrit pendant la panne", a2.Chat.GetMessages(bob.PeerId)[0].Text);
        Assert.Equal(MessageStatus.Pending, a2.Chat.GetMessages(bob.PeerId)[0].Status);
        Assert.Equal(1, a2.Chat.PendingCount(bob.PeerId));

        // Reconnexion : le message restauré part, Bob le reçoit, l'accusé revient.
        NetHelpers.Rig.Link(a2, bob);
        Assert.True(NetHelpers.WaitUntil(() => bob.Chat.GetMessages(alice).Any(m => m.Text == "écrit pendant la panne")));
        Assert.True(NetHelpers.WaitUntil(() => a2.Chat.GetMessages(bob.PeerId)[0].Status == MessageStatus.Delivered));

        // TOFU : chacun a enregistré l'autre à sa première connexion, non vérifié.
        Contact seenByAlice = a2.Contacts.Find(bob.PeerId)!;
        Assert.NotNull(seenByAlice);
        Assert.False(seenByAlice.IsVerified);
        Assert.Null(seenByAlice.DisplayName);
        Assert.NotNull(bob.Contacts.Find(alice));

        // Et l'état « livré » est bien enregistré : file d'attente vide, statut à jour.
        Assert.True(NetHelpers.WaitUntil(() => a2.Chat.PendingCount(bob.PeerId) == 0));
        p2.Flush();
        ChatSnapshot saved = new ProfileStore(_tmp.Path, "alice", protector).Load()!;
        Assert.Equal(0, saved.Outbox.Sum(o => o.Packets.Count));
        Assert.Equal(MessageStatus.Delivered, saved.Conversations.Single(c => c.Info.Id == bob.PeerId).Messages[0].Status);
    }

    [Fact]
    public void ContactNames_AreLocalOnly_ANewPeerCannotImpersonateAnExistingContact()
    {
        using NetHelpers.Rig rig = new();
        LanNode alice = rig.Add();
        LanNode bob = rig.Add();
        LanNode mallory = rig.Add();
        NetHelpers.Rig.Link(alice, bob);
        alice.Contacts.Rename(bob.PeerId, "Bob");
        NetHelpers.Rig.Link(alice, mallory);

        Contact m = alice.Contacts.Find(mallory.PeerId)!;
        Assert.Null(m.DisplayName);                       // un nouveau pair n'a pas de nom : il ne peut pas se faire appeler « Bob »
        Assert.False(m.IsVerified);
        Assert.ThrowsAny<InvalidOperationException>(() => alice.Contacts.Rename(mallory.PeerId, "bob")); // et l'utilisateur ne peut pas non plus les confondre
        Assert.Equal("Bob", alice.Contacts.DisplayLabel(bob.PeerId));
        Assert.Equal(mallory.PeerId[..8], alice.Contacts.DisplayLabel(mallory.PeerId)); // identifiant court, jamais un nom fourni par le pair
    }
}
