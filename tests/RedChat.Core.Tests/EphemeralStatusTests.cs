using RedChat.Core;
using Xunit;

namespace RedChat.Core.Tests;

public sealed class EphemeralStatusTests
{
    private const string Owner = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Alice = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Bob = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

    [Fact]
    public void DefaultAudienceAllowsEveryContact()
    {
        StatusAudience audience = new StatusAudience().Validate(Owner);
        Assert.True(audience.Allows(Alice));
        Assert.True(audience.Allows(Bob));
    }

    [Fact]
    public void AllExceptBlocksOnlySelectedContacts()
    {
        StatusAudience audience = new StatusAudience
        {
            Mode = StatusAudienceMode.AllExcept,
            PeerIds = new HashSet<string>(StringComparer.Ordinal) { Alice },
        }.Validate(Owner);

        Assert.False(audience.Allows(Alice));
        Assert.True(audience.Allows(Bob));
    }

    [Fact]
    public void OnlySelectedAllowsOnlySelectedContacts()
    {
        StatusAudience audience = new StatusAudience
        {
            Mode = StatusAudienceMode.OnlySelected,
            PeerIds = new HashSet<string>(StringComparer.Ordinal) { Alice },
        }.Validate(Owner);

        Assert.True(audience.Allows(Alice));
        Assert.False(audience.Allows(Bob));
    }

    [Fact]
    public void TextStatusExpiresAfterExactlyTwentyFourHours()
    {
        DateTimeOffset created = DateTimeOffset.UtcNow;
        EphemeralStatus status = new EphemeralStatus
        {
            Id = Guid.NewGuid().ToString("D"),
            OwnerPeerId = Owner,
            ContentKind = StatusContentKind.Text,
            Text = "Bonjour !",
            CreatedAt = created,
            ExpiresAt = created + EphemeralStatus.Lifetime,
        }.Validate(created);

        Assert.True(status.IsActive(created));
        Assert.False(status.IsActive(status.ExpiresAt));
    }

    [Fact]
    public void AudienceCannotIncludeOwnerOrUseEmptySelection()
    {
        Assert.Throws<InvalidDataException>(() => new StatusAudience
        {
            Mode = StatusAudienceMode.OnlySelected,
            PeerIds = new HashSet<string>(StringComparer.Ordinal),
        }.Validate(Owner));

        Assert.Throws<InvalidDataException>(() => new StatusAudience
        {
            Mode = StatusAudienceMode.AllExcept,
            PeerIds = new HashSet<string>(StringComparer.Ordinal) { Owner },
        }.Validate(Owner));
    }
}
