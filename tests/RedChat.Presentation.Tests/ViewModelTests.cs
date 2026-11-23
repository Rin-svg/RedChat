using RedChat.Core;
using RedChat.Presentation;
using Xunit;

namespace RedChat.Presentation.Tests;

public sealed class ViewModelTests
{
    private const string SelfPeer = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OtherPeer = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public void ConversationViewModel_RejectsEmptyAndOversizedDrafts()
    {
        ChatService chat = new(SelfPeer, new OfflineTransport());
        ContactBook contacts = new(SelfPeer);
        ConversationViewModel vm = new(new ConversationInfo { Id = OtherPeer }, chat, contacts, new ImmediateUiDispatcher());
        try
        {
            vm.Draft = "   ";
            Assert.False(vm.CanSend);
            vm.Draft = new string('x', ProtocolLimits.MaxTextChars + 1);
            Assert.False(vm.CanSend);
            vm.Draft = new string('x', ProtocolLimits.MaxTextChars);
            Assert.True(vm.CanSend);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public void ContactViewModel_RejectsInvalidNameAndShowsSecurityCode()
    {
        ContactBook contacts = new(SelfPeer);
        Contact contact = contacts.EnsureKnown(OtherPeer, out _);
        ContactViewModel vm = new(contact, SelfPeer, contacts);
        vm.DisplayName = "nom\u202Eusurpé";
        vm.RenameCommand.Execute(null);
        Assert.NotNull(vm.Error);
        Assert.Equal(12, vm.SecurityCode.Split(' ').Length);
        Assert.False(vm.IsVerified);
        vm.ToggleVerifiedCommand.Execute(null);
        Assert.True(vm.IsVerified);
    }


    [Fact]
    public void CreateGroupViewModel_EnablesOnlyAfterValidNameAndMember()
    {
        ChatService chat = new(SelfPeer, new OfflineTransport());
        ContactBook contacts = new(SelfPeer);
        _ = contacts.EnsureKnown(OtherPeer, out _);
        CreateGroupViewModel vm = new(chat, contacts);
        Assert.False(vm.CanCreate);
        vm.Name = "Équipe";
        Assert.False(vm.CanCreate);
        vm.SetSelected(OtherPeer, true);
        Assert.True(vm.CanCreate);
        vm.CreateCommand.Execute(null);
        Assert.NotNull(vm.CreatedGroup);
        Assert.Equal("Équipe", vm.CreatedGroup!.Title);
    }

    [Fact]
    public void ConversationViewModel_UnsubscribesWhenDisposed()
    {
        ChatService chat = new(SelfPeer, new OfflineTransport());
        ContactBook contacts = new(SelfPeer);
        ConversationViewModel vm = new(new ConversationInfo { Id = OtherPeer }, chat, contacts, new ImmediateUiDispatcher());
        vm.Dispose();
        contacts.EnsureKnown(OtherPeer, out _);
        chat.SendText(OtherPeer, "message après fermeture");
        Assert.Empty(vm.Messages);
    }


    [Fact]
    public void ConversationViewModel_MarshalsConcurrentEventsThroughDispatcher()
    {
        ChatService chat = new(SelfPeer, new OfflineTransport());
        ContactBook contacts = new(SelfPeer);
        RecordingDispatcher dispatcher = new();
        using ConversationViewModel vm = new(new ConversationInfo { Id = OtherPeer }, chat, contacts, dispatcher);
        Parallel.For(0, 32, i => chat.SendText(OtherPeer, "message " + i));
        dispatcher.Drain();
        Assert.Equal(32, vm.Messages.Count);
    }

    [Fact]
    public void NotificationPolicy_NotifiesForIncomingMessageInAnotherConversation()
    {
        ChatMessage message = new() { ConversationId = OtherPeer, IsOutgoing = false };

        Assert.True(IncomingMessageNotificationPolicy.ShouldNotify(
            ChatChangeKind.MessageAdded, message, OtherPeer, SelfPeer));
    }

    [Fact]
    public void NotificationPolicy_DoesNotNotifyForOutgoingMessage()
    {
        ChatMessage message = new() { ConversationId = OtherPeer, IsOutgoing = true };

        Assert.False(IncomingMessageNotificationPolicy.ShouldNotify(
            ChatChangeKind.MessageAdded, message, OtherPeer, SelfPeer));
    }

    [Fact]
    public void NotificationPolicy_DoesNotNotifyForConversationAlreadyDisplayed()
    {
        ChatMessage message = new() { ConversationId = OtherPeer, IsOutgoing = false };

        Assert.False(IncomingMessageNotificationPolicy.ShouldNotify(
            ChatChangeKind.MessageAdded, message, OtherPeer, OtherPeer));
    }

    [Fact]
    public void NotificationPolicy_DoesNotNotifyForMessageUpdatesOrMissingMessages()
    {
        ChatMessage message = new() { ConversationId = OtherPeer, IsOutgoing = false };

        Assert.False(IncomingMessageNotificationPolicy.ShouldNotify(
            ChatChangeKind.MessageUpdated, message, OtherPeer, SelfPeer));
        Assert.False(IncomingMessageNotificationPolicy.ShouldNotify(
            ChatChangeKind.MessageAdded, null, OtherPeer, SelfPeer));
    }

    private static readonly byte[] PngBytes = [137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3, 4];

    private static PersonalProfile ProfileWith(byte[]? avatar) => new()
    {
        PeerId = OtherPeer,
        DisplayName = "Bob",
        Status = string.Empty,
        About = string.Empty,
        AvatarBytes = avatar,
    };

    [Fact]
    public void ConversationViewModel_ExposesContactPublishedAvatar_AndRaisesChangeOnUpdate()
    {
        ChatService chat = new(SelfPeer, new OfflineTransport());
        ContactBook contacts = new(SelfPeer);
        _ = contacts.EnsureKnown(OtherPeer, out _);
        ConversationViewModel vm = new(new ConversationInfo { Id = OtherPeer }, chat, contacts, new ImmediateUiDispatcher());
        try
        {
            Assert.Null(vm.RemoteAvatarBytes);
            List<string?> changed = [];
            vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

            contacts.UpdatePublicProfile(OtherPeer, ProfileWith(PngBytes));

            Assert.Equal(PngBytes, vm.RemoteAvatarBytes);
            Assert.Contains(nameof(ConversationViewModel.RemoteAvatarBytes), changed);

            // Le contact retire sa photo : l'interface doit revenir aux initiales.
            contacts.UpdatePublicProfile(OtherPeer, ProfileWith(null));
            Assert.Null(vm.RemoteAvatarBytes);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public void ConversationViewModel_RemoteAvatarBytes_IsACopy()
    {
        ChatService chat = new(SelfPeer, new OfflineTransport());
        ContactBook contacts = new(SelfPeer);
        _ = contacts.EnsureKnown(OtherPeer, out _);
        contacts.UpdatePublicProfile(OtherPeer, ProfileWith(PngBytes));
        ConversationViewModel vm = new(new ConversationInfo { Id = OtherPeer }, chat, contacts, new ImmediateUiDispatcher());
        try
        {
            byte[] first = vm.RemoteAvatarBytes ?? throw new InvalidOperationException("Photo attendue.");
            first[0] = 0;
            Assert.Equal(PngBytes, vm.RemoteAvatarBytes);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public void ConversationViewModel_Group_HasNoRemoteAvatar()
    {
        ChatService chat = new(SelfPeer, new OfflineTransport());
        ContactBook contacts = new(SelfPeer);
        _ = contacts.EnsureKnown(OtherPeer, out _);
        contacts.UpdatePublicProfile(OtherPeer, ProfileWith(PngBytes));
        ConversationViewModel vm = new(
            new ConversationInfo { Id = Guid.NewGuid().ToString("D"), IsGroup = true, Title = "Équipe" },
            chat, contacts, new ImmediateUiDispatcher());
        try
        {
            Assert.Null(vm.RemoteAvatarBytes);
        }
        finally
        {
            vm.Dispose();
        }
    }

    private sealed class RecordingDispatcher : IUiDispatcher
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _actions = new();

        public void Post(Action action) => _actions.Enqueue(action);

        public void Drain()
        {
            while (_actions.TryDequeue(out Action? action))
            {
                action();
            }
        }
    }

    private sealed class OfflineTransport : ITransport
    {
        public bool TrySend(string peerId, Packet packet) => false;
    }
}
