using Xunit;

namespace RedChat.Core.Tests;

public sealed class MessageForwardTests
{
    private static string A => new('a', 64);
    private static string B => new('b', 64);
    private static string C => new('c', 64);

    private sealed class OfflineTransport : ITransport
    {
        public bool TrySend(string peerId, Packet packet) => false;
    }

    private static ChatService NewService() => new(A, new OfflineTransport());

    private static ChatMessage Message(string conversationId = "", string text = "Bonjour", bool deleted = false) => new()
    {
        Id = TestHelpers.NewGuid(),
        ConversationId = conversationId.Length == 0 ? B : conversationId,
        SenderId = B,
        Text = text,
        SentAtUnixMs = TestHelpers.Now.ToUnixTimeMilliseconds(),
        IsDeleted = deleted,
    };

    // ───────── Règle métier ─────────

    [Fact]
    public void Validate_AllowsTextMessageToAnotherConversation()
    {
        Assert.Null(MessageForwardPolicy.Validate(Message(), C));
    }

    [Fact]
    public void Validate_RejectsMissingMessage()
    {
        Assert.NotNull(MessageForwardPolicy.Validate(null, C));
    }

    [Fact]
    public void Validate_RejectsDeletedMessage()
    {
        Assert.NotNull(MessageForwardPolicy.Validate(Message(text: string.Empty, deleted: true), C));
    }

    [Fact]
    public void Validate_RejectsSameConversationAsOrigin()
    {
        Assert.NotNull(MessageForwardPolicy.Validate(Message(), B));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_RejectsMissingTarget(string? target)
    {
        Assert.NotNull(MessageForwardPolicy.Validate(Message(), target));
    }

    [Fact]
    public void Validate_RejectsOversizedTargetIdentifier()
    {
        Assert.NotNull(MessageForwardPolicy.Validate(Message(), new string('c', 65)));
    }

    [Fact]
    public void Validate_RejectsEmptyOrOversizedText()
    {
        Assert.NotNull(MessageForwardPolicy.Validate(Message(text: "   "), C));
        Assert.NotNull(MessageForwardPolicy.Validate(Message(text: new string('x', ProtocolLimits.MaxTextChars + 1)), C));
    }

    // ───────── Service ─────────

    [Fact]
    public void ForwardMessage_SendsOrdinaryTextToTargetConversation()
    {
        ChatService chat = NewService();
        ChatMessage original = chat.SendText(B, "Rendez-vous à 18 h");
        _ = chat.EnsureDirectConversation(C);

        ChatMessage forwarded = chat.ForwardMessage(B, original.Id, C);

        Assert.NotEqual(original.Id, forwarded.Id);
        Assert.Equal(C, forwarded.ConversationId);
        Assert.Equal("Rendez-vous à 18 h", forwarded.Text);
        Assert.True(forwarded.IsOutgoing);
        Assert.Null(forwarded.ReplyToId);
        Assert.Equal(forwarded.Text, Assert.Single(chat.GetMessages(C)).Text);
        Assert.Equal(1, chat.PendingCount(C));
        Assert.Equal("Rendez-vous à 18 h", Assert.Single(chat.GetMessages(B)).Text);
    }

    [Fact]
    public void ForwardMessage_CanForwardAReceivedMessage()
    {
        ChatService chat = NewService();
        chat.Receive(B, new Packet
        {
            Kind = PacketKind.Text,
            Id = TestHelpers.NewGuid(),
            ConversationId = A,
            SentAtUnixMs = TestHelpers.Now.ToUnixTimeMilliseconds(),
            Text = "Message reçu de Bob",
        });
        _ = chat.EnsureDirectConversation(C);
        ChatMessage received = Assert.Single(chat.GetMessages(B));

        ChatMessage forwarded = chat.ForwardMessage(B, received.Id, C);

        Assert.Equal("Message reçu de Bob", forwarded.Text);
        Assert.Equal(A, forwarded.SenderId);
        Assert.True(forwarded.IsOutgoing);
    }

    [Fact]
    public void ForwardMessage_CanTargetSelfConversation()
    {
        ChatService chat = NewService();
        ChatMessage original = chat.SendText(B, "À garder");
        _ = chat.EnsureSelfConversation();

        ChatMessage forwarded = chat.ForwardMessage(B, original.Id, A);

        Assert.Equal(A, forwarded.ConversationId);
        Assert.Empty(forwarded.Receipts);
        Assert.Equal(0, chat.PendingCount(A));
    }

    [Fact]
    public void ForwardMessage_RejectsDeletedMessage()
    {
        ChatService chat = NewService();
        ChatMessage original = chat.SendText(B, "À supprimer");
        _ = chat.EnsureDirectConversation(C);
        chat.DeleteMessage(B, original.Id);

        _ = Assert.Throws<InvalidOperationException>(() => chat.ForwardMessage(B, original.Id, C));
        Assert.Empty(chat.GetMessages(C));
    }

    [Fact]
    public void ForwardMessage_RejectsSameConversationAndUnknownTarget()
    {
        ChatService chat = NewService();
        ChatMessage original = chat.SendText(B, "Texte");

        _ = Assert.Throws<InvalidOperationException>(() => chat.ForwardMessage(B, original.Id, B));
        _ = Assert.Throws<InvalidOperationException>(() => chat.ForwardMessage(B, original.Id, C));
        Assert.Single(chat.GetConversations());
    }

    [Fact]
    public void ForwardMessage_RejectsUnknownMessageAndInvalidIdentifier()
    {
        ChatService chat = NewService();
        _ = chat.SendText(B, "Texte");
        _ = chat.EnsureDirectConversation(C);

        _ = Assert.Throws<InvalidOperationException>(() => chat.ForwardMessage(B, TestHelpers.NewGuid(), C));
        _ = Assert.Throws<ArgumentException>(() => chat.ForwardMessage(B, "pas-un-guid", C));
        _ = Assert.Throws<InvalidOperationException>(() => chat.ForwardMessage(new string('d', 64), TestHelpers.NewGuid(), C));
        Assert.Empty(chat.GetMessages(C));
    }
}