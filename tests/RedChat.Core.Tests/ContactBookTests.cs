using Xunit;

namespace RedChat.Core.Tests;

public class ContactBookTests
{
    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => TestHelpers.Now;
    }

    private static readonly string Me = new('1', 64);
    private static string P(char c) => new(c, 64);

    private static ContactBook NewBook() => new(Me, new FixedClock());

    // ───────────── TOFU ─────────────

    [Fact]
    public void EnsureKnown_FirstContact_IsNewUnverifiedAndUnnamed()
    {
        ContactBook book = NewBook();
        Contact c = book.EnsureKnown(P('a'), out bool isNew);
        Assert.True(isNew);
        Assert.Equal(P('a'), c.PeerId);
        Assert.Null(c.DisplayName);
        Assert.False(c.IsVerified);
        Assert.Equal(TestHelpers.Now.ToUnixTimeMilliseconds(), c.FirstSeenUnixMs);
    }

    [Fact]
    public void EnsureKnown_SecondTime_IsNotNew_AndKeepsFirstSeenAndChoices()
    {
        ContactBook book = NewBook();
        _ = book.EnsureKnown(P('a'), out _);
        book.Rename(P('a'), "Léa");
        book.SetVerified(P('a'), true);

        Contact again = book.EnsureKnown(P('a'), out bool isNew);
        Assert.False(isNew);
        Assert.Equal("Léa", again.DisplayName);
        Assert.True(again.IsVerified);
    }

    [Fact]
    public void EnsureKnown_Rejects_InvalidIds_AndSelf()
    {
        ContactBook book = NewBook();
        Assert.ThrowsAny<ArgumentException>(() => book.EnsureKnown("", out _));
        Assert.ThrowsAny<ArgumentException>(() => book.EnsureKnown("xyz", out _));
        Assert.ThrowsAny<ArgumentException>(() => book.EnsureKnown(new string('A', 64), out _)); // majuscules : non canonique
        Assert.ThrowsAny<ArgumentException>(() => book.EnsureKnown(Me, out _));
        Assert.Empty(book.GetAll());
    }

    [Fact]
    public void EnsureKnown_BookIsBounded()
    {
        ContactBook book = NewBook();
        for (int i = 0; i < ContactBook.MaxContacts; i++)
        {
            _ = book.EnsureKnown((i + 2).ToString("x64", System.Globalization.CultureInfo.InvariantCulture), out _);
        }

        Assert.Equal(ContactBook.MaxContacts, book.GetAll().Count);
        Assert.ThrowsAny<InvalidOperationException>(() => book.EnsureKnown(P('f'), out _));
        // un contact déjà connu reste accessible même carnet plein
        _ = book.EnsureKnown((2).ToString("x64", System.Globalization.CultureInfo.InvariantCulture), out bool isNew);
        Assert.False(isNew);
    }

    [Fact]
    public void UpdatePublicProfile_StoresRemoteProfileWithoutChangingLocalName()
    {
        ContactBook book = NewBook();
        string peerId = P('a');
        _ = book.EnsureKnown(peerId, out _);
        book.Rename(peerId, "Mon surnom");
        PersonalProfile profile = new()
        {
            PeerId = peerId,
            DisplayName = "Nom public",
            Status = "Disponible",
            About = "À propos",
        };

        book.UpdatePublicProfile(peerId, profile);

        Contact contact = book.Find(peerId)!;
        Assert.Equal("Mon surnom", contact.DisplayName);
        Assert.Equal("Nom public", contact.PublicProfile!.DisplayName);
        Assert.Equal("Disponible", contact.PublicProfile.Status);
    }

    [Fact]
    public void ExportImport_PreservesPublicProfileAndLocalNickname()
    {
        ContactBook source = NewBook();
        string peerId = P('a');
        _ = source.EnsureKnown(peerId, out _);
        source.Rename(peerId, "Surnom local");
        source.UpdatePublicProfile(peerId, new PersonalProfile
        {
            PeerId = peerId,
            DisplayName = "Nom public",
            Status = "Disponible",
            About = "Description",
        });

        ContactBook restored = NewBook();
        restored.Import(source.Export());

        Assert.Equal("Surnom local", restored.DisplayLabel(peerId));
        Assert.Equal("Nom public", restored.Find(peerId)!.PublicProfile!.DisplayName);
    }

    // ───────────── Noms ─────────────

    [Fact]
    public void Rename_TrimsAndStores()
    {
        ContactBook book = NewBook();
        _ = book.EnsureKnown(P('a'), out _);
        book.Rename(P('a'), "  Maman  ");
        Assert.Equal("Maman", book.Find(P('a'))!.DisplayName);
        Assert.Equal("Maman", book.DisplayLabel(P('a')));
    }

    [Fact]
    public void Rename_EmptyOrNull_ClearsName()
    {
        ContactBook book = NewBook();
        _ = book.EnsureKnown(P('a'), out _);
        book.Rename(P('a'), "Maman");
        book.Rename(P('a'), "   ");
        Assert.Null(book.Find(P('a'))!.DisplayName);
        book.Rename(P('a'), "Papa");
        book.Rename(P('a'), null);
        Assert.Null(book.Find(P('a'))!.DisplayName);
    }

    [Theory]
    [InlineData("Maman")]
    [InlineData("maman")]
    [InlineData("MAMAN ")]
    [InlineData("ＭＡＭＡＮ")] // pleine largeur : visuellement identique après normalisation
    public void Rename_DuplicateOfAnotherContact_IsRefused(string second)
    {
        ContactBook book = NewBook();
        _ = book.EnsureKnown(P('a'), out _);
        _ = book.EnsureKnown(P('b'), out _);
        book.Rename(P('a'), "Maman");
        Assert.ThrowsAny<InvalidOperationException>(() => book.Rename(P('b'), second));
        Assert.Null(book.Find(P('b'))!.DisplayName);
    }

    [Fact]
    public void Rename_ComposedVsDecomposedAccent_AreTheSameName()
    {
        ContactBook book = NewBook();
        _ = book.EnsureKnown(P('a'), out _);
        _ = book.EnsureKnown(P('b'), out _);
        book.Rename(P('a'), "L\u00E9a");          // é composé
        Assert.ThrowsAny<InvalidOperationException>(() => book.Rename(P('b'), "Le\u0301a")); // e + accent combinant
    }

    [Fact]
    public void Rename_SameContactCanChangeCaseOfItsOwnName()
    {
        ContactBook book = NewBook();
        _ = book.EnsureKnown(P('a'), out _);
        book.Rename(P('a'), "maman");
        book.Rename(P('a'), "Maman");
        Assert.Equal("Maman", book.Find(P('a'))!.DisplayName);
    }

    [Theory]
    [InlineData("Li\u202Eea")]      // inversion de texte (usurpation visuelle)
    [InlineData("a\u200Bb")]        // zéro-largeur
    [InlineData("a\nb")]            // saut de ligne
    [InlineData("a\0b")]            // nul
    public void Rename_RejectsHostileCharacters(string name)
    {
        ContactBook book = NewBook();
        _ = book.EnsureKnown(P('a'), out _);
        Assert.ThrowsAny<ArgumentException>(() => book.Rename(P('a'), name));
        Assert.Null(book.Find(P('a'))!.DisplayName);
    }

    [Fact]
    public void Rename_LengthLimit_40Ok_41Refused()
    {
        ContactBook book = NewBook();
        _ = book.EnsureKnown(P('a'), out _);
        book.Rename(P('a'), new string('x', ProtocolLimits.MaxDisplayNameChars));
        Assert.ThrowsAny<ArgumentException>(() => book.Rename(P('a'), new string('x', ProtocolLimits.MaxDisplayNameChars + 1)));
    }

    [Fact]
    public void Rename_And_SetVerified_OnUnknownContact_Throw()
    {
        ContactBook book = NewBook();
        Assert.ThrowsAny<InvalidOperationException>(() => book.Rename(P('a'), "X"));
        Assert.ThrowsAny<InvalidOperationException>(() => book.SetVerified(P('a'), true));
    }

    [Fact]
    public void DisplayLabel_FallsBackToShortId_AndNeverThrows()
    {
        ContactBook book = NewBook();
        Assert.Equal("aaaaaaaa", book.DisplayLabel(P('a')));
        Assert.Equal("?", book.DisplayLabel("n'importe quoi"));
        Assert.Equal("?", book.DisplayLabel(""));
    }

    // ───────────── Événements ─────────────

    [Fact]
    public void Changed_FiresOnRealChangesOnly()
    {
        ContactBook book = NewBook();
        int n = 0;
        book.Changed += (_, _) => n++;
        _ = book.EnsureKnown(P('a'), out _);   // +1
        _ = book.EnsureKnown(P('a'), out _);   // connu : pas de changement
        book.Rename(P('a'), "Léa");            // +1
        book.SetVerified(P('a'), true);        // +1
        book.Remove(P('b'));                   // inconnu : rien
        book.Remove(P('a'));                   // +1
        Assert.Equal(4, n);
        Assert.Empty(book.GetAll());
    }

    // ───────────── Sauvegarde / restauration ─────────────

    [Fact]
    public void ExportImport_RoundTrip()
    {
        ContactBook a = NewBook();
        _ = a.EnsureKnown(P('a'), out _);
        _ = a.EnsureKnown(P('b'), out _);
        a.Rename(P('a'), "Léa");
        a.SetVerified(P('a'), true);

        ContactBook b = NewBook();
        b.Import(a.Export());
        Assert.Equal(2, b.GetAll().Count);
        Assert.Equal("Léa", b.Find(P('a'))!.DisplayName);
        Assert.True(b.Find(P('a'))!.IsVerified);
        Assert.Null(b.Find(P('b'))!.DisplayName);
    }

    private static Contact Good(char c, string? name = null) =>
        new() { PeerId = P(c), DisplayName = name, FirstSeenUnixMs = TestHelpers.Now.ToUnixTimeMilliseconds() };

    [Theory]
    [InlineData("version")]
    [InlineData("self")]
    [InlineData("invalidId")]
    [InlineData("duplicateId")]
    [InlineData("duplicateName")]
    [InlineData("duplicateNameOtherCase")]
    [InlineData("bidiName")]
    [InlineData("untrimmedName")]
    [InlineData("longName")]
    [InlineData("dateZero")]
    [InlineData("dateBeyondCeiling")]
    [InlineData("nullContact")]
    [InlineData("nullList")]
    [InlineData("tooMany")]
    public void Import_HostileFile_IsRefused_AllOrNothing(string kind)
    {
        ContactsSnapshot snap = kind switch
        {
            "version" => new ContactsSnapshot { Version = 2, Contacts = new[] { Good('a') } },
            "self" => new ContactsSnapshot { Contacts = new[] { Good('a'), new Contact { PeerId = Me, FirstSeenUnixMs = 1 } } },
            "invalidId" => new ContactsSnapshot { Contacts = new[] { Good('a'), new Contact { PeerId = "zz", FirstSeenUnixMs = 1 } } },
            "duplicateId" => new ContactsSnapshot { Contacts = new[] { Good('a'), Good('a') } },
            "duplicateName" => new ContactsSnapshot { Contacts = new[] { Good('a', "Léa"), Good('b', "Léa") } },
            "duplicateNameOtherCase" => new ContactsSnapshot { Contacts = new[] { Good('a', "Léa"), Good('b', "LÉA") } },
            "bidiName" => new ContactsSnapshot { Contacts = new[] { Good('a', "Le\u202Ea") } },
            "untrimmedName" => new ContactsSnapshot { Contacts = new[] { Good('a', " Léa") } },
            "longName" => new ContactsSnapshot { Contacts = new[] { Good('a', new string('x', 41)) } },
            "dateZero" => new ContactsSnapshot { Contacts = new[] { new Contact { PeerId = P('a'), FirstSeenUnixMs = 0 } } },
            "dateBeyondCeiling" => new ContactsSnapshot { Contacts = new[] { new Contact { PeerId = P('a'), FirstSeenUnixMs = ProtocolLimits.StoredTimestampCeiling.ToUnixTimeMilliseconds() + 1 } } },
            "nullContact" => new ContactsSnapshot { Contacts = new Contact[] { null! } },
            "nullList" => new ContactsSnapshot { Contacts = null! },
            _ => new ContactsSnapshot { Contacts = Enumerable.Range(0, ContactBook.MaxContacts + 1).Select(i => new Contact { PeerId = (i + 2).ToString("x64", System.Globalization.CultureInfo.InvariantCulture), FirstSeenUnixMs = 1 }).ToArray() },
        };

        ContactBook book = NewBook();
        Assert.ThrowsAny<InvalidDataException>(() => book.Import(snap));
        Assert.Empty(book.GetAll()); // aucun contact « à moitié » importé
    }

    [Fact]
    public void Import_AcceptsFirstSeenDatesAfterTheClockWentBackwards()
    {
        ContactBook book = NewBook();
        book.Import(new ContactsSnapshot { Contacts = new[] { new Contact { PeerId = P('a'), FirstSeenUnixMs = TestHelpers.Now.AddDays(400).ToUnixTimeMilliseconds() } } });
        Assert.Single(book.GetAll());
    }

    [Fact]
    public void Import_OnNonEmptyBook_Throws_AndKeepsContacts()
    {
        ContactBook book = NewBook();
        _ = book.EnsureKnown(P('a'), out _);
        Assert.ThrowsAny<InvalidOperationException>(() => book.Import(new ContactsSnapshot { Contacts = new[] { Good('b') } }));
        Assert.Single(book.GetAll());
    }

    [Fact]
    public async Task ConcurrentEnsureKnown_NoLossNoCorruption()
    {
        ContactBook book = NewBook();
        Task[] tasks = Enumerable.Range(0, 8).Select(t => Task.Run(() =>
        {
            for (int i = 0; i < 200; i++)
            {
                _ = book.EnsureKnown(((t * 1000) + i + 2).ToString("x64", System.Globalization.CultureInfo.InvariantCulture), out _);
            }
        })).ToArray();
        await Task.WhenAll(tasks);
        Assert.Equal(1600, book.GetAll().Count);
    }
}

public class SecurityCodeTests
{
    private static string P(char c) => new(c, 64);

    [Fact]
    public void KnownVector_MatchesIndependentComputation()
    {
        // Valeur calculée séparément (Python) : détecte toute modification accidentelle de l'algorithme ou du format.
        Assert.Equal("93732 77576 31917 51676 85835 99414 53701 05385 07126 93213 17348 11923", SecurityCode.Compute(P('a'), P('b')));
        Assert.Equal("86729 82606 15564 27494 53975 45673 04592 35011 36042 79946 89728 49200", SecurityCode.Compute(new string('a', 63) + "c", P('b')));
    }

    [Fact]
    public void Code_IsSymmetric()
    {
        Assert.Equal(SecurityCode.Compute(P('a'), P('b')), SecurityCode.Compute(P('b'), P('a')));
    }

    [Fact]
    public void Code_HasExpectedShape()
    {
        string[] groups = SecurityCode.Compute(P('c'), P('d')).Split(' ');
        Assert.Equal(SecurityCode.Groups, groups.Length);
        foreach (string g in groups)
        {
            Assert.Equal(SecurityCode.DigitsPerGroup, g.Length);
            Assert.True(g.All(char.IsAsciiDigit));
        }
    }

    [Fact]
    public void Code_ChangesWhenEitherIdentityChanges()
    {
        string baseCode = SecurityCode.Compute(P('a'), P('b'));
        string[] g0 = baseCode.Split(' ');
        string[] g1 = SecurityCode.Compute(new string('a', 63) + "c", P('b')).Split(' ');
        string[] g2 = SecurityCode.Compute(P('a'), new string('b', 63) + "c").Split(' ');
        Assert.True(Enumerable.Range(0, 12).Count(i => g0[i] != g1[i]) >= 10); // effet d'avalanche : presque tous les groupes changent
        Assert.True(Enumerable.Range(0, 12).Count(i => g0[i] != g2[i]) >= 10);
    }

    [Fact]
    public void Code_ForDifferentPairs_Differs()
    {
        Assert.True(SecurityCode.Compute(P('a'), P('b')) != SecurityCode.Compute(P('a'), P('c')));
        Assert.True(SecurityCode.Compute(P('a'), P('b')) != SecurityCode.Compute(P('b'), P('c')));
    }

    [Fact]
    public void Code_Rejects_BadInput()
    {
        Assert.ThrowsAny<ArgumentException>(() => SecurityCode.Compute(P('a'), P('a')));
        Assert.ThrowsAny<ArgumentException>(() => SecurityCode.Compute(P('a'), "abc"));
        Assert.ThrowsAny<ArgumentException>(() => SecurityCode.Compute("", P('a')));
        Assert.ThrowsAny<ArgumentException>(() => SecurityCode.Compute(new string('A', 64), P('a')));
    }
}
