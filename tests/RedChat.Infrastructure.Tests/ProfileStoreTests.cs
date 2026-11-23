using System.Security.Cryptography;
using System.Text;
using RedChat.Core;
using RedChat.Infrastructure.Security;
using Xunit;

namespace RedChat.Infrastructure.Tests;

public sealed class ProfileStoreTests : IDisposable
{
    private readonly TempRoot _tmp = new();

    public void Dispose() => _tmp.Dispose();

    private ProfileStore Open(string name = "alice", ISecretProtector? protector = null) =>
        new(_tmp.Path, name, protector ?? new TestProtector(), new ManualTimeProvider());

    private static string File_(ProfileStore s, string name) => Path.Combine(s.DirectoryPath, name);

    private static string[] Files(ProfileStore s) => Directory.GetFiles(s.DirectoryPath).Select(Path.GetFileName).Select(n => n!).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    private static void WriteProtected(ProfileStore s, string fileName, byte[] plaintext, ISecretProtector? p = null) =>
        File.WriteAllBytes(File_(s, fileName), (p ?? new TestProtector()).Protect(plaintext));

    private static void FlipByte(string path, int index)
    {
        byte[] b = File.ReadAllBytes(path);
        b[index] ^= 0xFF;
        File.WriteAllBytes(path, b);
    }

    // ───────────── Nom de profil et dossier ─────────────

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../evil")]
    [InlineData("..\\evil")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a b")]
    [InlineData("a:b")]
    [InlineData("é")]
    [InlineData("x\n")]
    [InlineData("a\0b")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // 31 caractères : une de trop
    public void Constructor_RejectsInvalidProfileNames_AndCreatesNothing(string name)
    {
        Assert.ThrowsAny<ArgumentException>(() => Open(name));
        Assert.False(Directory.Exists(_tmp.Path) && Directory.GetFileSystemEntries(_tmp.Path).Length > 0);
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("nul")]
    [InlineData("COM1")]
    [InlineData("lpt9")]
    [InlineData("AUX")]
    public void Constructor_RejectsReservedDeviceNames(string name)
    {
        Assert.ThrowsAny<UnsafePathException>(() => Open(name));
    }

    [Fact]
    public void Constructor_CreatesFolder_StrictlyUnderRoot_AndAcceptsMaxLengthName()
    {
        ProfileStore s = Open(new string('p', 30));
        Assert.True(Directory.Exists(s.DirectoryPath));
        Assert.StartsWith(Path.GetFullPath(_tmp.Path), s.DirectoryPath);
        Assert.Equal(Path.GetFullPath(_tmp.Path), Path.GetDirectoryName(s.DirectoryPath));
    }

    [Fact]
    public void TwoProfiles_AreIsolated()
    {
        ProfileStore a = Open("alice");
        ProfileStore b = Open("bob");
        Assert.True(a.DirectoryPath != b.DirectoryPath);
        using LocalIdentity ia = a.LoadOrCreateIdentity();
        using LocalIdentity ib = b.LoadOrCreateIdentity();
        Assert.True(ia.PeerId != ib.PeerId);
        a.Save(Fixtures.Rich().ExportState());
        Assert.Null(b.Load());
        Assert.False(File.Exists(File_(b, ProfileStore.HistoryFileName)));
    }

    // ───────────── Profil personnel ─────────────

    [Fact]
    public void PersonalProfile_RoundTrips_WithoutChangingIdentity()
    {
        ProfileStore store = Open();
        using LocalIdentity identity = store.LoadOrCreateIdentity();
        string peerId = identity.PeerId;
        PersonalProfile profile = new()
        {
            PeerId = peerId,
            DisplayName = "  Alice  ",
            Status = " Disponible ",
            About = "À propos de moi",
            AvatarBytes = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 },
        };

        store.SavePersonalProfile(profile, peerId);
        PersonalProfile loaded = Assert.IsType<PersonalProfile>(store.LoadPersonalProfile(peerId));

        Assert.Equal("Alice", loaded.DisplayName);
        Assert.Equal("Disponible", loaded.Status);
        Assert.Equal("À propos de moi", loaded.About);
        Assert.Equal(profile.AvatarBytes, loaded.AvatarBytes);
        using LocalIdentity reloadedIdentity = Open().LoadOrCreateIdentity();
        Assert.Equal(peerId, reloadedIdentity.PeerId);
        Assert.True(File.Exists(File_(store, ProfileStore.PersonalProfileFileName)));
    }

    [Fact]
    public void PersonalProfile_RejectsMismatchedIdentityAndInvalidAvatar()
    {
        ProfileStore store = Open();
        using LocalIdentity identity = store.LoadOrCreateIdentity();
        PersonalProfile wrongIdentity = PersonalProfile.CreateDefault(new string('b', 64));
        Assert.Throws<InvalidDataException>(() => store.SavePersonalProfile(wrongIdentity, identity.PeerId));
        PersonalProfile badAvatar = PersonalProfile.CreateDefault(identity.PeerId) with { AvatarBytes = new byte[] { 1, 2, 3 } };
        Assert.Throws<InvalidDataException>(() => store.SavePersonalProfile(badAvatar, identity.PeerId));
        Assert.False(File.Exists(File_(store, ProfileStore.PersonalProfileFileName)));
    }

    // ───────────── Identité ─────────────

    [Fact]
    public void Identity_IsCreatedOnce_ThenReloadedIdentically()
    {
        using LocalIdentity first = Open().LoadOrCreateIdentity();
        using LocalIdentity second = Open().LoadOrCreateIdentity();
        Assert.Equal(first.PeerId, second.PeerId);
        Assert.True(first.PublicKeySpki.AsSpan().SequenceEqual(second.PublicKeySpki));
    }

    [Fact]
    public void Identity_PrivateKey_IsNeverWrittenInClear()
    {
        TestProtector p = new();
        ProfileStore s = Open(protector: p);
        using LocalIdentity id = s.LoadOrCreateIdentity();
        byte[] pkcs8 = id.ExportPrivateKey();
        byte[] file = File.ReadAllBytes(File_(s, ProfileStore.IdentityFileName));
        Assert.Equal(-1, file.AsSpan().IndexOf(pkcs8));
        Assert.Equal(-1, file.AsSpan().IndexOf(pkcs8.AsSpan(0, 24)));
        Assert.Equal(1, p.ProtectCalls);
    }

    [Fact]
    public void Identity_RoundTripsThroughTheStoredFile_SoItCanStillSign()
    {
        using LocalIdentity a = Open().LoadOrCreateIdentity();
        using LocalIdentity b = Open().LoadOrCreateIdentity();
        using LocalIdentity peer = LocalIdentity.Create();
        // Même identité = même PeerId ET même clé privée : un handshake réussit avec la copie rechargée.
        using HandshakeInitiator init = new(b, peer.PeerId);
        using HandshakeResponder resp = new(peer);
        byte[] h2 = resp.ProcessHello1(init.Start());
        resp.ProcessHello3(init.ProcessHello2(h2));
        Assert.Equal(a.PeerId, resp.RemotePeerId);
    }

    [Theory]
    [InlineData("flipFirst")]
    [InlineData("flipMiddle")]
    [InlineData("flipLast")]
    [InlineData("truncate")]
    [InlineData("empty")]
    [InlineData("garbage")]
    [InlineData("oversized")]
    public void Identity_UnusableFile_Throws_AndIsNeverOverwritten_NorReplacedByANewIdentity(string kind)
    {
        ProfileStore s = Open();
        using (LocalIdentity created = s.LoadOrCreateIdentity())
        {
            Assert.True(created.PeerId.Length == 64);
        }

        string path = File_(s, ProfileStore.IdentityFileName);
        byte[] good = File.ReadAllBytes(path);
        byte[] bad = kind switch
        {
            "flipFirst" => Flip(good, 0),
            "flipMiddle" => Flip(good, good.Length / 2),
            "flipLast" => Flip(good, good.Length - 1),
            "truncate" => good[..(good.Length / 2)],
            "empty" => Array.Empty<byte>(),
            "garbage" => RandomNumberGenerator.GetBytes(300),
            _ => new byte[(int)ProfileStore.MaxIdentityFileBytes + 1],
        };
        File.WriteAllBytes(path, bad);

        for (int i = 0; i < 2; i++) // à chaque démarrage, même réponse : jamais de « nouvelle identité » silencieuse
        {
            ProfileStoreException ex = Assert.ThrowsAny<ProfileStoreException>(() => Open().LoadOrCreateIdentity());
            Assert.DoesNotContain(_tmp.Path, ex.Message); // pas de chemin interne dans le message utilisateur
        }

        Assert.True(File.ReadAllBytes(path).AsSpan().SequenceEqual(bad)); // fichier intact
        Assert.Single(Files(s));                                 // rien d'autre n'a été créé (ni .bak, ni .tmp)

        static byte[] Flip(byte[] src, int index)
        {
            byte[] copy = (byte[])src.Clone();
            copy[index] ^= 0xFF;
            return copy;
        }
    }

    [Fact]
    public void Identity_ProtectedByAnotherAccount_IsRefusedClearly()
    {
        ProfileStore s = Open(protector: new TestProtector(key: 0x11));
        using (s.LoadOrCreateIdentity())
        {
        }

        ProfileStoreException ex = Assert.ThrowsAny<ProfileStoreException>(() => Open(protector: new TestProtector(key: 0x22)).LoadOrCreateIdentity());
        Assert.Contains("identité", ex.Message);
    }

    [Fact]
    public void Identity_ValidlyProtected_ButNotAKey_IsRefused()
    {
        ProfileStore s = Open();
        WriteProtected(s, ProfileStore.IdentityFileName, new byte[] { 1, 2, 3, 4 });
        Assert.ThrowsAny<ProfileStoreException>(() => s.LoadOrCreateIdentity());
    }

    [Fact]
    public void Identity_WithAnUnexpectedCurve_IsRefused()
    {
        ProfileStore s = Open();
        using ECDsa p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        WriteProtected(s, ProfileStore.IdentityFileName, p384.ExportPkcs8PrivateKey());
        Assert.ThrowsAny<ProfileStoreException>(() => s.LoadOrCreateIdentity());
    }

    [Fact]
    public void Identity_WhenSavingFails_NothingIsWritten_AndNextStartCanStillCreateIt()
    {
        TestProtector p = new() { FailProtect = _ => new CryptographicException("panne simulée") };
        ProfileStore s = Open(protector: p);
        Assert.ThrowsAny<CryptographicException>(() => s.LoadOrCreateIdentity());
        Assert.Empty(Files(s));

        p.FailProtect = null;
        using LocalIdentity ok = s.LoadOrCreateIdentity();
        Assert.Single(Files(s));
    }

    [Fact]
    public void Identity_SymbolicLink_IsRefused()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // créer un lien symbolique exige des droits particuliers sous Windows
        }

        ProfileStore s = Open();
        string target = Path.Combine(_tmp.Path, "cible.bin");
        File.WriteAllBytes(target, new TestProtector().Protect(new byte[] { 1 }));
        File.CreateSymbolicLink(File_(s, ProfileStore.IdentityFileName), target);
        Assert.ThrowsAny<ProfileStoreException>(() => s.LoadOrCreateIdentity());
    }

    // ───────────── Historique : aller-retour et atomicité ─────────────

    [Fact]
    public void Load_WhenNothingSaved_ReturnsNull_WithoutNotice()
    {
        ProfileStore s = Open();
        Assert.Null(s.Load());
        Assert.Null(s.LoadContacts());
        Assert.Null(s.RecoveryNotice);
    }

    [Fact]
    public void History_RoundTrips_ThroughTheStoredFile()
    {
        ChatService original = Fixtures.Rich();
        Open().Save(original.ExportState());

        ChatService restored = Fixtures.NewService();
        ChatSnapshot? loaded = Open().Load();
        Assert.NotNull(loaded);
        restored.ImportState(loaded!);
        Assert.Equal(Fixtures.Describe(original), Fixtures.Describe(restored));
    }

    [Fact]
    public void History_IsNeverWrittenInClear()
    {
        ProfileStore s = Open();
        ChatService svc = Fixtures.NewService();
        _ = svc.SendText(Fixtures.B, "mot-de-passe-du-wifi-12345");
        s.Save(svc.ExportState());
        byte[] file = File.ReadAllBytes(File_(s, ProfileStore.HistoryFileName));
        Assert.Equal(-1, file.AsSpan().IndexOf("mot-de-passe-du-wifi-12345"u8));
        Assert.Equal(-1, file.AsSpan().IndexOf("conversations"u8));
    }

    [Fact]
    public void Save_LeavesNoTemporaryFile_AndKeepsThePreviousVersionAsBackup()
    {
        ProfileStore s = Open();
        ChatService svc = Fixtures.NewService();
        _ = svc.SendText(Fixtures.B, "un");
        s.Save(svc.ExportState());
        byte[] first = File.ReadAllBytes(File_(s, ProfileStore.HistoryFileName));
        Assert.Single(Files(s));

        _ = svc.SendText(Fixtures.B, "deux");
        s.Save(svc.ExportState());
        Assert.Equal("history.dat,history.dat.bak", string.Join(',', Files(s)));
        Assert.True(File.ReadAllBytes(File_(s, ProfileStore.HistoryFileName + AtomicFile.BackupSuffix)).AsSpan().SequenceEqual(first));
        Assert.True(Files(s).All(n => !n.EndsWith(".tmp", StringComparison.Ordinal)));
    }

    [Fact]
    public void Save_WhenProtectionFails_TheExistingFileIsUntouched()
    {
        TestProtector p = new();
        ProfileStore s = Open(protector: p);
        s.Save(Fixtures.Rich().ExportState());
        byte[] before = File.ReadAllBytes(File_(s, ProfileStore.HistoryFileName));

        p.FailProtect = _ => new CryptographicException("panne simulée");
        Assert.ThrowsAny<CryptographicException>(() => s.Save(Fixtures.NewService().ExportState()));
        Assert.True(File.ReadAllBytes(File_(s, ProfileStore.HistoryFileName)).AsSpan().SequenceEqual(before));
        Assert.Equal("history.dat", string.Join(',', Files(s)));
    }

    [Fact]
    public void Save_TooLarge_IsRefusedWithAClearMessage_AndKeepsTheOldFile()
    {
        TestProtector p = new();
        ProfileStore s = Open(protector: p);
        s.Save(Fixtures.Rich().ExportState());
        byte[] before = File.ReadAllBytes(File_(s, ProfileStore.HistoryFileName));

        p.OversizedOutput = true;
        ProfileStoreException ex = Assert.ThrowsAny<ProfileStoreException>(() => s.Save(Fixtures.Rich().ExportState()));
        Assert.Contains("volumineux", ex.Message);
        Assert.True(File.ReadAllBytes(File_(s, ProfileStore.HistoryFileName)).AsSpan().SequenceEqual(before));
    }

    [Fact]
    public void Save_NullSnapshot_Throws()
    {
        Assert.ThrowsAny<ArgumentNullException>(() => Open().Save(null!));
        Assert.ThrowsAny<ArgumentNullException>(() => Open().SaveContacts(null!));
    }

    [Fact]
    public async Task ConcurrentSaves_AreSerialized_AndLeaveAValidFile()
    {
        ProfileStore s = Open();
        ChatService svc = Fixtures.NewService();
        Task[] tasks = Enumerable.Range(0, 4).Select(t => Task.Run(() =>
        {
            for (int i = 0; i < 15; i++)
            {
                _ = svc.SendText(Fixtures.B, $"t{t}-{i}");
                s.Save(svc.ExportState());
            }
        })).ToArray();
        await Task.WhenAll(tasks);

        ChatSnapshot? loaded = Open().Load();
        Assert.NotNull(loaded);
        Fixtures.NewService().ImportState(loaded!);
        Assert.Null(s.RecoveryNotice);
        Assert.True(Files(s).All(n => !n.EndsWith(".tmp", StringComparison.Ordinal)));
    }

    // ───────────── Fichier abîmé : récupération sans jamais rien détruire ─────────────

    private static void Corrupt(string path, string kind)
    {
        byte[] b = File.ReadAllBytes(path);
        switch (kind)
        {
            case "flipByte": FlipByte(path, b.Length / 2); break;
            case "truncateHalf": File.WriteAllBytes(path, b[..(b.Length / 2)]); break;
            case "empty": File.WriteAllBytes(path, Array.Empty<byte>()); break;
            case "garbage": File.WriteAllBytes(path, RandomNumberGenerator.GetBytes(500)); break;
            default: throw new ArgumentException(kind);
        }
    }

    [Theory]
    [InlineData("flipByte")]
    [InlineData("truncateHalf")]
    [InlineData("empty")]
    [InlineData("garbage")]
    public void Load_CorruptMain_WithGoodBackup_RestoresBackup_KeepsTheBadFile_AndWarns(string kind)
    {
        ProfileStore s = Open();
        ChatService svc = Fixtures.NewService();
        _ = svc.SendText(Fixtures.B, "version un");
        s.Save(svc.ExportState());
        _ = svc.SendText(Fixtures.B, "version deux");
        s.Save(svc.ExportState());
        Corrupt(File_(s, ProfileStore.HistoryFileName), kind);

        ProfileStore reopened = Open();
        ChatSnapshot? loaded = reopened.Load();
        Assert.NotNull(loaded);
        Assert.Single(loaded!.Conversations[0].Messages); // la version PRÉCÉDENTE
        Assert.NotNull(reopened.RecoveryNotice);
        Assert.DoesNotContain(_tmp.Path, reopened.RecoveryNotice!);
        Assert.Equal(1, Files(reopened).Count(n => n.Contains(".corrupt-", StringComparison.Ordinal))); // jamais supprimé
    }

    [Theory]
    [InlineData("flipByte")]
    [InlineData("truncateHalf")]
    [InlineData("empty")]
    [InlineData("garbage")]
    public void Load_CorruptMain_WithoutBackup_ReturnsNull_ButKeepsTheBadFile(string kind)
    {
        ProfileStore s = Open();
        s.Save(Fixtures.Rich().ExportState());
        Corrupt(File_(s, ProfileStore.HistoryFileName), kind);
        byte[] bad = File.ReadAllBytes(File_(s, ProfileStore.HistoryFileName));

        ProfileStore reopened = Open();
        Assert.Null(reopened.Load());
        Assert.NotNull(reopened.RecoveryNotice);
        string moved = Files(reopened).Single(n => n.Contains(".corrupt-", StringComparison.Ordinal));
        Assert.True(File.ReadAllBytes(Path.Combine(reopened.DirectoryPath, moved)).AsSpan().SequenceEqual(bad)); // octet pour octet
        Assert.False(File.Exists(File_(reopened, ProfileStore.HistoryFileName)));
    }

    [Fact]
    public void Load_MainMissing_BackupValid_RestoresIt()
    {
        ProfileStore s = Open();
        ChatService svc = Fixtures.NewService();
        _ = svc.SendText(Fixtures.B, "un");
        s.Save(svc.ExportState());
        _ = svc.SendText(Fixtures.B, "deux");
        s.Save(svc.ExportState());
        File.Delete(File_(s, ProfileStore.HistoryFileName)); // p. ex. coupure entre deux étapes d'un autre outil

        ProfileStore reopened = Open();
        Assert.Single(reopened.Load()!.Conversations[0].Messages);
        Assert.NotNull(reopened.RecoveryNotice);
    }

    [Fact]
    public void Load_BothMainAndBackupCorrupt_ReturnsNull_AndOnlyMainIsMovedAside()
    {
        ProfileStore s = Open();
        s.Save(Fixtures.Rich().ExportState());
        s.Save(Fixtures.Rich().ExportState());
        Corrupt(File_(s, ProfileStore.HistoryFileName), "garbage");
        Corrupt(File_(s, ProfileStore.HistoryFileName + AtomicFile.BackupSuffix), "garbage");

        ProfileStore reopened = Open();
        Assert.Null(reopened.Load());
        Assert.NotNull(reopened.RecoveryNotice);
        Assert.Equal(1, Files(reopened).Count(n => n.Contains(".corrupt-", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("unknownField")]
    [InlineData("wrongType")]
    [InlineData("arrayRoot")]
    [InlineData("jsonNull")]
    [InlineData("deepNesting")]
    [InlineData("integerEnum")]
    [InlineData("trailingGarbage")]
    [InlineData("invalidUtf8")]
    [InlineData("emptyObjectVersionString")]
    [InlineData("comments")]
    public void Load_ValidlyProtected_ButHostileJson_IsTreatedAsCorrupt(string kind)
    {
        ProfileStore s = Open();
        byte[] plain = kind switch
        {
            "unknownField" => Encoding.UTF8.GetBytes("{\"version\":1,\"conversations\":[],\"outbox\":[],\"extra\":1}"),
            "wrongType" => Encoding.UTF8.GetBytes("{\"version\":1,\"conversations\":[{\"info\":{\"id\":\"x\",\"isGroup\":\"oui\"}}],\"outbox\":[]}"),
            "arrayRoot" => Encoding.UTF8.GetBytes("[]"),
            "jsonNull" => Encoding.UTF8.GetBytes("null"),
            "deepNesting" => Encoding.UTF8.GetBytes(new string('[', 200) + new string(']', 200)),
            "integerEnum" => Encoding.UTF8.GetBytes("{\"version\":1,\"conversations\":[{\"info\":{\"id\":\"x\"},\"messages\":[{\"receipts\":{\"x\":2}}]}],\"outbox\":[]}"),
            "trailingGarbage" => Encoding.UTF8.GetBytes("{\"version\":1,\"conversations\":[],\"outbox\":[]}xyz"),
            "invalidUtf8" => new byte[] { 0xFF, 0xFE, 0xFD },
            "emptyObjectVersionString" => Encoding.UTF8.GetBytes("{\"version\":\"1\"}"),
            _ => Encoding.UTF8.GetBytes("{\"version\":1, /* piégé */ \"conversations\":[],\"outbox\":[]}"),
        };
        WriteProtected(s, ProfileStore.HistoryFileName, plain);

        ProfileStore reopened = Open();
        Assert.Null(reopened.Load());
        Assert.NotNull(reopened.RecoveryNotice);
        Assert.Equal(1, Files(reopened).Count(n => n.Contains(".corrupt-", StringComparison.Ordinal)));
    }

    [Fact]
    public void Load_AnInjectedStatusField_IsIgnored_StatusRemainsDerivedFromReceipts()
    {
        ProfileStore s = Open();
        string json = "{\"version\":1,\"conversations\":[{\"info\":{\"id\":\"" + Fixtures.B + "\"},\"messages\":[{"
            + "\"id\":\"" + Guid.NewGuid().ToString("D") + "\",\"conversationId\":\"" + Fixtures.B + "\",\"senderId\":\"" + Fixtures.Self + "\","
            + "\"text\":\"x\",\"sentAtUnixMs\":1,\"isOutgoing\":true,\"receipts\":{\"" + Fixtures.B + "\":\"pending\"},\"status\":\"read\"}]}],\"outbox\":[]}";
        WriteProtected(s, ProfileStore.HistoryFileName, Encoding.UTF8.GetBytes(json));

        ChatSnapshot? snap = Open().Load();
        Assert.NotNull(snap);
        Assert.Equal(MessageStatus.Pending, snap!.Conversations[0].Messages[0].Status); // le « read » injecté n'a aucun effet
    }

    [Fact]
    public void Load_ValidMinimalJson_IsAccepted_SoTheHostileCasesAboveFailForTheRightReason()
    {
        ProfileStore s = Open();
        WriteProtected(s, ProfileStore.HistoryFileName, Encoding.UTF8.GetBytes("{\"version\":1,\"conversations\":[],\"outbox\":[]}"));
        ChatSnapshot? snap = Open().Load();
        Assert.NotNull(snap);
        Assert.Empty(snap!.Conversations);
        Assert.Null(s.RecoveryNotice);
    }

    [Fact]
    public void Load_HugeFile_IsRefusedWithoutBeingLoaded_AndSetAside()
    {
        ProfileStore s = Open();
        using (FileStream fs = new(File_(s, ProfileStore.HistoryFileName), FileMode.Create))
        {
            fs.SetLength(ProfileStore.MaxHistoryFileBytes + 1); // fichier « creux » : ne consomme pas réellement 64 Mio
        }

        ProfileStore reopened = Open();
        Assert.Null(reopened.Load());
        Assert.NotNull(reopened.RecoveryNotice);
        Assert.Equal(1, Files(reopened).Count(n => n.Contains(".corrupt-", StringComparison.Ordinal)));
    }

    [Fact]
    public void Load_SymbolicLinkInsteadOfFile_IsRefused_AndSetAside()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        ProfileStore s = Open();
        string target = Path.Combine(_tmp.Path, "ailleurs.bin");
        File.WriteAllBytes(target, new TestProtector().Protect(Encoding.UTF8.GetBytes("{\"version\":1,\"conversations\":[],\"outbox\":[]}")));
        File.CreateSymbolicLink(File_(s, ProfileStore.HistoryFileName), target);

        ProfileStore reopened = Open();
        Assert.Null(reopened.Load());
        Assert.NotNull(reopened.RecoveryNotice);
        Assert.True(File.Exists(target)); // la cible n'a pas été touchée
    }

    [Fact]
    public void Load_FileLockedByAnotherProgram_ThrowsIOException_AndDoesNotTreatItAsCorruption()
    {
        ProfileStore s = Open();
        s.Save(Fixtures.Rich().ExportState());
        string path = File_(s, ProfileStore.HistoryFileName);
        byte[] before = File.ReadAllBytes(path);

        using (FileStream locker = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.True(locker.Length > 0);
            ProfileStore reopened = Open();
            Assert.ThrowsAny<IOException>(() => reopened.Load());
            Assert.Null(reopened.RecoveryNotice); // verrou transitoire ≠ fichier corrompu
        }

        Assert.Equal(0, Files(s).Count(n => n.Contains(".corrupt-", StringComparison.Ordinal)));
        Assert.True(File.ReadAllBytes(path).AsSpan().SequenceEqual(before));
        Assert.NotNull(Open().Load()); // le verrou levé, tout fonctionne
    }

    [Fact]
    public void Quarantine_NeverDeletesAnything_AndIsANoOpWithoutFile()
    {
        ProfileStore s = Open();
        s.QuarantineHistory();
        s.QuarantineContacts();
        Assert.Empty(Files(s));
        Assert.Null(s.RecoveryNotice);

        s.Save(Fixtures.Rich().ExportState());
        byte[] content = File.ReadAllBytes(File_(s, ProfileStore.HistoryFileName));
        s.QuarantineHistory();
        string moved = Files(s).Single();
        Assert.Contains(".corrupt-", moved);
        Assert.True(File.ReadAllBytes(Path.Combine(s.DirectoryPath, moved)).AsSpan().SequenceEqual(content));
        Assert.NotNull(s.RecoveryNotice);
    }

    // ───────────── Contacts ─────────────

    [Fact]
    public void Contacts_RoundTrip_ThroughTheStoredFile()
    {
        ContactBook book = new(Fixtures.Self, new ManualTimeProvider());
        _ = book.EnsureKnown(Fixtures.B, out _);
        book.Rename(Fixtures.B, "Léa");
        book.SetVerified(Fixtures.B, true);
        Open().SaveContacts(book.Export());

        ContactBook restored = new(Fixtures.Self, new ManualTimeProvider());
        restored.Import(Open().LoadContacts()!);
        Assert.Equal("Léa", restored.Find(Fixtures.B)!.DisplayName);
        Assert.True(restored.Find(Fixtures.B)!.IsVerified);
    }

    [Fact]
    public void Contacts_Corrupt_IsSetAside_AndOtherFilesAreUntouched()
    {
        ProfileStore s = Open();
        s.Save(Fixtures.Rich().ExportState());
        ContactBook book = new(Fixtures.Self, new ManualTimeProvider());
        _ = book.EnsureKnown(Fixtures.B, out _);
        s.SaveContacts(book.Export());
        Corrupt(File_(s, ProfileStore.ContactsFileName), "garbage");

        ProfileStore reopened = Open();
        Assert.Null(reopened.LoadContacts());
        Assert.NotNull(reopened.Load()); // l'historique n'est pas affecté
        Assert.Equal(1, Files(reopened).Count(n => n.StartsWith("contacts.dat.corrupt-", StringComparison.Ordinal)));
    }

    // ───────────── AtomicFile ─────────────

    [Fact]
    public void AtomicFile_Write_CreatesThenReplaces_AndLeavesNoTempFile()
    {
        Directory.CreateDirectory(_tmp.Path);
        string path = Path.Combine(_tmp.Path, "f.bin");
        AtomicFile.Write(path, new byte[] { 1, 2, 3 });
        AtomicFile.Write(path, new byte[] { 4, 5 });
        Assert.Equal("4,5", string.Join(',', File.ReadAllBytes(path)));
        Assert.Equal("1,2,3", string.Join(',', File.ReadAllBytes(path + AtomicFile.BackupSuffix)));
        Assert.Equal(2, Directory.GetFiles(_tmp.Path).Length);
    }

    [Fact]
    public void AtomicFile_Write_ToMissingDirectory_ThrowsIOException_AndCreatesNothing()
    {
        string path = Path.Combine(_tmp.Path, "inexistant", "f.bin");
        Assert.ThrowsAny<IOException>(() => AtomicFile.Write(path, new byte[] { 1 }));
        Assert.False(Directory.Exists(_tmp.Path) && Directory.GetFileSystemEntries(_tmp.Path).Length > 0);
    }

    [Fact]
    public void AtomicFile_Write_RejectsBadArguments()
    {
        Assert.ThrowsAny<ArgumentException>(() => AtomicFile.Write(string.Empty, new byte[] { 1 }));
        Assert.ThrowsAny<ArgumentException>(() => AtomicFile.Write("sans-dossier.bin", new byte[] { 1 }));
        Assert.ThrowsAny<ArgumentNullException>(() => AtomicFile.Write(Path.Combine(_tmp.Path, "f"), null!));
    }

    [Fact]
    public void AtomicFile_ReadBounded_MissingFile_ReturnsNull_AndLimitIsExact()
    {
        Directory.CreateDirectory(_tmp.Path);
        string path = Path.Combine(_tmp.Path, "f.bin");
        Assert.Null(AtomicFile.ReadBounded(path, 10));

        File.WriteAllBytes(path, new byte[10]);
        Assert.Equal(10, AtomicFile.ReadBounded(path, 10)!.Length);           // à la limite : accepté
        Assert.ThrowsAny<InvalidDataException>(() => AtomicFile.ReadBounded(path, 9)); // un octet de trop : refusé
    }

    // ───────────── Protecteur de test ─────────────

    [Fact]
    public void InsecureTestProtector_RoundTrips_AndDetectsTampering()
    {
        InsecureTestSecretProtector p = new();
        byte[] blob = p.Protect(new byte[] { 1, 2, 3 });
        Assert.Equal("1,2,3", string.Join(',', p.Unprotect(blob)));

        byte[] tampered = (byte[])blob.Clone();
        tampered[^1] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => p.Unprotect(tampered));
        Assert.ThrowsAny<CryptographicException>(() => p.Unprotect(new byte[] { 1, 2, 3 }));
        Assert.ThrowsAny<CryptographicException>(() => p.Unprotect(Array.Empty<byte>()));
    }
}
