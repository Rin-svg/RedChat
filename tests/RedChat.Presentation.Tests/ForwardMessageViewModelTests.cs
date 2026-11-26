using RedChat.Core;
using RedChat.Presentation;
using Xunit;

namespace RedChat.Presentation.Tests;

public sealed class ForwardMessageViewModelTests
{
    private const string SelfPeer = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OtherPeer = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string ThirdPeer = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

    private sealed class OfflineTransport : ITransport
    {
        public bool TrySend(string peerId, Packet packet) => false;
    }

    /// <summary>Un message dans une conversation privée, deux groupes comme destinations possibles.</summary>
    private sealed class Scenario : IDisposable
    {
        private readonly List<ConversationViewModel> _all = new();

        public Scenario()
        {
            Chat = new ChatService(SelfPeer, new OfflineTransport());
            Contacts = new ContactBook(SelfPeer);
            Message = Chat.SendText(OtherPeer, "Rendez-vous à 18 h");
            Origin = Add(Chat.GetConversations().Single(c => c.Id == OtherPeer));
            Team = Add(Chat.CreateGroup("Équipe", new[] { ThirdPeer }));
            Family = Add(Chat.CreateGroup("Famille", new[] { ThirdPeer }));
        }

        public ChatService Chat { get; }

        public ContactBook Contacts { get; }

        public ChatMessage Message { get; }

        public ConversationViewModel Origin { get; }

        public ConversationViewModel Team { get; }

        public ConversationViewModel Family { get; }

        public IReadOnlyList<ConversationViewModel> Conversations => _all;

        public ForwardMessageViewModel CreateViewModel() => new(Chat, Message, Conversations);

        public ConversationViewModel Add(ConversationInfo info)
        {
            ConversationViewModel vm = new(info, Chat, Contacts, new ImmediateUiDispatcher());
            _all.Add(vm);
            return vm;
        }

        public void Dispose()
        {
            foreach (ConversationViewModel vm in _all)
            {
                vm.Dispose();
            }
        }
    }

    [Fact]
    public void Targets_ExcludeTheOriginConversation()
    {
        using Scenario s = new();
        ForwardMessageViewModel vm = s.CreateViewModel();

        Assert.Equal(2, vm.Targets.Count);
        Assert.DoesNotContain(s.Origin, vm.Targets);
        Assert.Contains(s.Team, vm.Targets);
        Assert.Contains(s.Family, vm.Targets);
    }

    [Fact]
    public void CanForward_RequiresASelectedTarget()
    {
        using Scenario s = new();
        ForwardMessageViewModel vm = s.CreateViewModel();

        Assert.False(vm.CanForward);
        Assert.False(vm.ForwardCommand.CanExecute(null));

        vm.SelectedTarget = s.Team;

        Assert.True(vm.CanForward);
        Assert.True(vm.ForwardCommand.CanExecute(null));
    }

    [Fact]
    public void ForwardCommand_SendsTheTextToTheSelectedConversation()
    {
        using Scenario s = new();
        ForwardMessageViewModel vm = s.CreateViewModel();
        vm.SelectedTarget = s.Team;

        vm.ForwardCommand.Execute(null);

        Assert.Null(vm.Error);
        Assert.NotNull(vm.ForwardedMessage);
        Assert.Equal(s.Team.Info.Id, vm.ForwardedMessage!.ConversationId);
        Assert.Equal("Rendez-vous à 18 h", vm.ForwardedMessage.Text);
        Assert.Equal("Rendez-vous à 18 h", Assert.Single(s.Chat.GetMessages(s.Team.Info.Id)).Text);
        Assert.Empty(s.Chat.GetMessages(s.Family.Info.Id));
    }

    [Fact]
    public void Search_FiltersByNameAndClearsASelectionThatIsNoLongerListed()
    {
        using Scenario s = new();
        ForwardMessageViewModel vm = s.CreateViewModel();
        vm.SelectedTarget = s.Team;

        vm.Search = "fam";

        Assert.Same(s.Family, Assert.Single(vm.Targets));
        Assert.Null(vm.SelectedTarget);
        Assert.False(vm.CanForward);

        vm.Search = string.Empty;

        Assert.Equal(2, vm.Targets.Count);
    }

    [Fact]
    public void ForwardCommand_ReportsAnErrorWhenTheTargetIsUnknownToTheService()
    {
        using Scenario s = new();
        ConversationViewModel ghost = s.Add(new ConversationInfo { Id = ThirdPeer });
        ForwardMessageViewModel vm = s.CreateViewModel();
        vm.SelectedTarget = ghost;

        vm.ForwardCommand.Execute(null);

        Assert.NotNull(vm.Error);
        Assert.Null(vm.ForwardedMessage);
        Assert.Empty(s.Chat.GetMessages(ThirdPeer));
    }

    [Fact]
    public void PreviewText_IsTruncatedForLongMessages()
    {
        using Scenario s = new();
        ChatMessage longMessage = s.Message with { Text = new string('x', 500) };
        ForwardMessageViewModel vm = new(s.Chat, longMessage, s.Conversations);

        Assert.Equal(120, vm.PreviewText.Length);
        Assert.EndsWith("…", vm.PreviewText, StringComparison.Ordinal);
    }
}