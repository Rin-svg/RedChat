using RedChat.Core;
using Xunit;

namespace RedChat.Core.Tests;

public sealed class FavoriteConversationsTests
{
    private const string Peer = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Group = "f5c5e6e2-6d5c-4b7e-9d25-6b0df3b52a12";

    [Fact]
    public void Toggle_AddsAndRemovesFavorite_AndRaisesChange()
    {
        FavoriteConversations favorites = new();
        int changes = 0;
        favorites.Changed += (_, _) => changes++;

        Assert.True(favorites.Toggle(Peer));
        Assert.True(favorites.Contains(Peer));
        Assert.False(favorites.Toggle(Peer));
        Assert.False(favorites.Contains(Peer));
        Assert.Equal(2, changes);
    }

    [Fact]
    public void ExportImport_RestoresPrivateAndGroupFavorites()
    {
        FavoriteConversations original = new();
        original.Add(Peer);
        original.Add(Group);

        FavoriteConversations restored = new();
        restored.Import(original.Export());

        Assert.Equal(new[] { Peer, Group }, restored.GetAll());
    }

    [Theory]
    [InlineData("")]
    [InlineData("../../favorites")]
    [InlineData("not-a-peer-or-group")]
    [InlineData("F5C5E6E2-6D5C-4B7E-9D25-6B0DF3B52A12-extra")]
    public void Add_RejectsInvalidConversationId(string id)
    {
        FavoriteConversations favorites = new();
        Assert.ThrowsAny<ArgumentException>(() => favorites.Add(id));
        Assert.Empty(favorites.GetAll());
    }

    [Fact]
    public void Import_RejectsDuplicatesAndInvalidVersion()
    {
        FavoriteConversations duplicateTarget = new();
        Assert.Throws<InvalidDataException>(() => duplicateTarget.Import(new FavoriteConversationsSnapshot
        {
            ConversationIds = new[] { Peer, Peer }
        }));

        FavoriteConversations versionTarget = new();
        Assert.Throws<InvalidDataException>(() => versionTarget.Import(new FavoriteConversationsSnapshot { Version = 99 }));
    }
}
