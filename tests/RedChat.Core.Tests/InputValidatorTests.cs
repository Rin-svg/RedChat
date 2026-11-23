using Xunit;

namespace RedChat.Core.Tests;

public class InputValidatorTests
{
    [Theory]
    [InlineData("alice")]
    [InlineData("Bob_2-test")]
    [InlineData("a")]
    public void IsValidProfileName_Valid_ReturnsTrue(string name) => Assert.True(InputValidator.IsValidProfileName(name));

    [Theory]
    [InlineData("")]
    [InlineData("abc\n")]              // « $ » laisserait passer ce retour ligne final
    [InlineData("../evil")]
    [InlineData("a b")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("é")]
    [InlineData("CON:")]
    public void IsValidProfileName_Hostile_ReturnsFalse(string name) => Assert.False(InputValidator.IsValidProfileName(name));

    [Fact]
    public void IsValidProfileName_NullOrTooLong_ReturnsFalse()
    {
        Assert.False(InputValidator.IsValidProfileName(null));
        Assert.True(InputValidator.IsValidProfileName(new string('a', ProtocolLimits.MaxProfileNameChars)));
        Assert.False(InputValidator.IsValidProfileName(new string('a', ProtocolLimits.MaxProfileNameChars + 1)));
    }

    [Fact]
    public void IsValidPeerId_Boundaries()
    {
        Assert.True(InputValidator.IsValidPeerId(new string('a', 64)));
        Assert.False(InputValidator.IsValidPeerId(new string('a', 63)));
        Assert.False(InputValidator.IsValidPeerId(new string('a', 65)));
        Assert.False(InputValidator.IsValidPeerId(new string('A', 64)));   // majuscules refusées : une seule forme canonique
        Assert.False(InputValidator.IsValidPeerId(new string('g', 64)));
        Assert.False(InputValidator.IsValidPeerId(new string('a', 64) + "\n"));
        Assert.False(InputValidator.IsValidPeerId(null));
    }

    [Fact]
    public void IsValidGuid_AcceptsOnlyDashedForm()
    {
        Assert.True(InputValidator.IsValidGuid(Guid.NewGuid().ToString("D")));
        Assert.False(InputValidator.IsValidGuid(Guid.NewGuid().ToString("N")));
        Assert.False(InputValidator.IsValidGuid(Guid.NewGuid().ToString("B")));
        Assert.False(InputValidator.IsValidGuid("not-a-guid"));
        Assert.False(InputValidator.IsValidGuid(null));
    }

    [Theory]
    [InlineData("  Alice  ", true, "Alice")]
    [InlineData("Jean-François", true, "Jean-François")]
    [InlineData("", false, "")]
    [InlineData("   ", false, "")]
    [InlineData("Eve\u202Etxt", false, "")]       // inversion bidi : usurpation visuelle
    [InlineData("Ev\u200Be", false, "")]           // espace de largeur nulle
    [InlineData("A\u0000B", false, "")]
    [InlineData("A\nB", false, "")]
    public void TryNormalizeName_Cases(string input, bool expected, string expectedName)
    {
        bool ok = InputValidator.TryNormalizeName(input, ProtocolLimits.MaxDisplayNameChars, out string name);
        Assert.Equal(expected, ok);
        Assert.Equal(expectedName, name);
    }

    [Fact]
    public void TryNormalizeName_LengthBoundaries()
    {
        Assert.True(InputValidator.TryNormalizeName(new string('a', 40), 40, out _));
        Assert.False(InputValidator.TryNormalizeName(new string('a', 41), 40, out _));
        Assert.False(InputValidator.TryNormalizeName(null, 40, out _));
    }

    [Fact]
    public void IsValidMessageText_Rules()
    {
        Assert.True(InputValidator.IsValidMessageText("Bonjour\nça va ?\t😀"));
        Assert.True(InputValidator.IsValidMessageText(new string('x', ProtocolLimits.MaxTextChars)));
        Assert.False(InputValidator.IsValidMessageText(new string('x', ProtocolLimits.MaxTextChars + 1)));
        Assert.False(InputValidator.IsValidMessageText(""));
        Assert.False(InputValidator.IsValidMessageText("   \n  "));
        Assert.False(InputValidator.IsValidMessageText(null));
        Assert.False(InputValidator.IsValidMessageText("a\0b"));
        Assert.False(InputValidator.IsValidMessageText("a\u001B[31mb"));      // séquence d'échappement
    }

    [Fact]
    public void IsValidReaction_Rules()
    {
        Assert.True(InputValidator.IsValidReaction("👍"));
        Assert.False(InputValidator.IsValidReaction(""));
        Assert.False(InputValidator.IsValidReaction(null));
        Assert.False(InputValidator.IsValidReaction("a b"));
        Assert.False(InputValidator.IsValidReaction("x\u202Ey"));   // inversion bidi : usurpation visuelle
        Assert.False(InputValidator.IsValidReaction("\u200B"));      // espace de largeur nulle : réaction « invisible »
        Assert.True(InputValidator.IsValidReaction("👨\u200D👩\u200D👧"));  // emoji composé (ZWJ) : doit rester valide
        Assert.True(InputValidator.IsValidReaction("❤\uFE0F"));
        Assert.False(InputValidator.IsValidReaction(new string('x', ProtocolLimits.MaxReactionChars + 1)));
    }

    [Theory]
    [InlineData("192.168.1.20:45000", true)]
    [InlineData("10.0.0.5:5000", true)]
    [InlineData("172.16.0.1:5000", true)]
    [InlineData("172.31.255.254:5000", true)]
    [InlineData("127.0.0.1:50000", true)]
    [InlineData("169.254.1.1:5000", true)]
    [InlineData("[::1]:5000", true)]
    [InlineData("[fe80::1]:5000", true)]
    [InlineData("[fd00::1]:5000", true)]
    [InlineData("172.32.0.1:5000", false)]        // juste hors de 172.16/12
    [InlineData("172.15.0.1:5000", false)]
    [InlineData("8.8.8.8:5000", false)]            // adresse publique
    [InlineData("0.0.0.0:5000", false)]
    [InlineData("255.255.255.255:5000", false)]
    [InlineData("192.168.1.20:1023", false)]      // port système
    [InlineData("192.168.1.20:1024", true)]
    [InlineData("192.168.1.20:65535", true)]
    [InlineData("192.168.1.20:65536", false)]
    [InlineData("192.168.1.20", false)]            // port manquant
    [InlineData("example.com:5000", false)]        // nom d'hôte : pas de résolution DNS
    [InlineData("", false)]
    public void TryParseLanEndpoint_Cases(string input, bool expected) =>
        Assert.Equal(expected, InputValidator.TryParseLanEndpoint(input, out _));

    [Fact]
    public void TryParseLanEndpoint_NullOrHuge_ReturnsFalse()
    {
        Assert.False(InputValidator.TryParseLanEndpoint(null, out _));
        Assert.False(InputValidator.TryParseLanEndpoint("192.168.1.1:5000" + new string(' ', 100) + "x", out _));
    }
}
