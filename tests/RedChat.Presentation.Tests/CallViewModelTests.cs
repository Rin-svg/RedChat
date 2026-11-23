using RedChat.Core;
using RedChat.Infrastructure;
using RedChat.Presentation;
using Xunit;

namespace RedChat.Presentation.Tests;

/// <summary>Contrôleur d'appel factice : enregistre les appels et permet de simuler les événements réseau.</summary>
internal sealed class FakeCallController : ICallController
{
    public CallSessionSnapshot? ActiveSession { get; private set; }

    public event EventHandler<CallSessionChangedEventArgs>? SessionChanged;

    public string? StartedPeerId { get; private set; }

    public CallMediaKind? StartedMedia { get; private set; }

    public int StartCount { get; private set; }

    public int AcceptCount { get; private set; }

    public int RejectCount { get; private set; }

    public int EndCount { get; private set; }

    public Exception? ExceptionToThrow { get; set; }

    public int SubscriberCount => SessionChanged?.GetInvocationList().Length ?? 0;

    public string StartCall(string peerId, CallMediaKind media)
    {
        ThrowIfConfigured();
        StartCount++;
        StartedPeerId = peerId;
        StartedMedia = media;
        Raise(new CallSessionSnapshot("call-1", peerId, media, CallSessionState.OutgoingRinging, true));
        return "call-1";
    }

    public void AcceptIncoming()
    {
        ThrowIfConfigured();
        AcceptCount++;
        Raise(ActiveSession! with { State = CallSessionState.Accepted });
    }

    public void RejectIncoming()
    {
        ThrowIfConfigured();
        RejectCount++;
        Raise(ActiveSession! with { State = CallSessionState.Rejected });
    }

    public void EndCall()
    {
        ThrowIfConfigured();
        EndCount++;
        Raise(ActiveSession! with { State = CallSessionState.Ended });
    }

    /// <summary>Simule un changement de session venu du réseau.</summary>
    public void Raise(CallSessionSnapshot? session)
    {
        ActiveSession = session;
        SessionChanged?.Invoke(this, new CallSessionChangedEventArgs(session));
    }

    private void ThrowIfConfigured()
    {
        if (ExceptionToThrow is { } exception)
        {
            throw exception;
        }
    }
}

/// <summary>Moteur média factice : mémorise l'intention micro/caméra.</summary>
internal sealed class FakeMediaControls : ICallMediaControls
{
    public bool IsMicrophoneMuted { get; private set; }

    public bool IsCameraPaused { get; private set; }

    public Exception? ExceptionToThrow { get; set; }

    public Task SetMicrophoneMutedAsync(bool muted)
    {
        if (ExceptionToThrow is { } exception)
        {
            return Task.FromException(exception);
        }

        IsMicrophoneMuted = muted;
        return Task.CompletedTask;
    }

    public Task SetCameraPausedAsync(bool paused)
    {
        if (ExceptionToThrow is { } exception)
        {
            return Task.FromException(exception);
        }

        IsCameraPaused = paused;
        return Task.CompletedTask;
    }
}

/// <summary>Dispatcher qui met les actions en file : prouve que l'état ne change que sur « le thread de l'interface ».</summary>
internal sealed class QueueDispatcher : IUiDispatcher
{
    private readonly Queue<Action> _queue = new();

    public int Pending => _queue.Count;

    public void Post(Action action) => _queue.Enqueue(action);

    public void RunAll()
    {
        while (_queue.Count > 0)
        {
            _queue.Dequeue()();
        }
    }
}

public sealed class CallViewModelTests
{
    private const string Peer = "peer-a";

    private static CallSessionSnapshot Session(CallSessionState state, CallMediaKind media = CallMediaKind.Audio, bool outgoing = true) =>
        new("call-1", Peer, media, state, outgoing);

    private static CallViewModel Create(FakeCallController controller, string? selectedPeer = Peer) =>
        new(controller, new ImmediateUiDispatcher(), () => selectedPeer);

    [Fact]
    public void StartAudio_UsesSelectedPrivateConversation()
    {
        FakeCallController controller = new();
        CallViewModel vm = Create(controller);

        vm.StartAudioCommand.Execute(null);

        Assert.Equal(1, controller.StartCount);
        Assert.Equal(Peer, controller.StartedPeerId);
        Assert.Equal(CallMediaKind.Audio, controller.StartedMedia!.Value);
        Assert.True(vm.IsCallActive);
        Assert.True(vm.IsBannerVisible);
    }

    [Fact]
    public void StartVideo_RequestsVideoMedia()
    {
        FakeCallController controller = new();
        CallViewModel vm = Create(controller);

        vm.StartVideoCommand.Execute(null);

        Assert.Equal(CallMediaKind.Video, controller.StartedMedia!.Value);
    }

    [Fact]
    public void StartCall_IsDisabledWithoutPrivateConversation()
    {
        FakeCallController controller = new();
        CallViewModel vm = Create(controller, selectedPeer: null);

        Assert.False(vm.CanStartCall);
        Assert.False(vm.StartAudioCommand.CanExecute(null));
        vm.StartAudioCommand.Execute(null);

        Assert.Equal(0, controller.StartCount);
    }

    [Fact]
    public void StartCall_IsDisabledWhileACallIsActive_AndReEnabledAfterwards()
    {
        FakeCallController controller = new();
        CallViewModel vm = Create(controller);
        vm.StartAudioCommand.Execute(null);

        Assert.False(vm.StartAudioCommand.CanExecute(null));
        Assert.False(vm.StartVideoCommand.CanExecute(null));
        // Double-clic : la seconde exécution est ignorée, une seule invitation part.
        vm.StartAudioCommand.Execute(null);
        Assert.Equal(1, controller.StartCount);

        vm.HangupCommand.Execute(null);

        Assert.True(vm.StartAudioCommand.CanExecute(null));
    }

    [Fact]
    public void StartCall_ControllerFailure_ShowsMessageWithoutModalOrCrash()
    {
        FakeCallController controller = new() { ExceptionToThrow = new InvalidOperationException("Le contact doit être connecté avant l’appel.") };
        CallViewModel vm = Create(controller);

        vm.StartAudioCommand.Execute(null);

        Assert.Equal("Le contact doit être connecté avant l’appel.", vm.ErrorText);
        Assert.False(vm.IsCallActive);
    }

    [Fact]
    public void UnexpectedException_DoesNotLeakTechnicalDetails()
    {
        FakeCallController controller = new() { ExceptionToThrow = new FormatException("détail interne sensible C:\\secret") };
        CallViewModel vm = Create(controller);

        vm.StartAudioCommand.Execute(null);

        Assert.Equal("L'opération d'appel a échoué. Réessayez.", vm.ErrorText);
        Assert.DoesNotContain("secret", vm.ErrorText);
    }

    [Fact]
    public void IncomingCall_ShowsBannerWithAcceptAndReject_NoModal()
    {
        FakeCallController controller = new();
        CallViewModel vm = Create(controller);

        controller.Raise(Session(CallSessionState.IncomingRinging, CallMediaKind.Video, outgoing: false));

        Assert.True(vm.IsIncomingRinging);
        Assert.True(vm.IsBannerVisible);
        Assert.Equal("Appel vidéo entrant", vm.StatusText);
        Assert.True(vm.AcceptCommand.CanExecute(null));
        Assert.True(vm.RejectCommand.CanExecute(null));
    }

    [Fact]
    public void Accept_MovesToInCall_AndReject_EndsWithNotice()
    {
        FakeCallController accepting = new();
        CallViewModel vmAccept = Create(accepting);
        accepting.Raise(Session(CallSessionState.IncomingRinging, outgoing: false));
        vmAccept.AcceptCommand.Execute(null);
        Assert.Equal(1, accepting.AcceptCount);
        Assert.True(vmAccept.IsInCall);
        Assert.False(vmAccept.IsIncomingRinging);

        FakeCallController rejecting = new();
        CallViewModel vmReject = Create(rejecting);
        rejecting.Raise(Session(CallSessionState.IncomingRinging, outgoing: false));
        vmReject.RejectCommand.Execute(null);
        Assert.Equal(1, rejecting.RejectCount);
        Assert.False(vmReject.IsCallActive);
        Assert.Equal("Appel refusé.", vmReject.StatusText);
    }

    [Fact]
    public void AcceptAndReject_AreUnavailableOutsideIncomingRinging()
    {
        FakeCallController controller = new();
        CallViewModel vm = Create(controller);

        Assert.False(vm.AcceptCommand.CanExecute(null));
        Assert.False(vm.RejectCommand.CanExecute(null));
        vm.AcceptCommand.Execute(null);
        Assert.Equal(0, controller.AcceptCount);
    }

    [Fact]
    public void Hangup_IsOnlyAvailableDuringACall()
    {
        FakeCallController controller = new();
        CallViewModel vm = Create(controller);
        Assert.False(vm.HangupCommand.CanExecute(null));
        vm.HangupCommand.Execute(null);
        Assert.Equal(0, controller.EndCount);

        controller.Raise(Session(CallSessionState.OutgoingRinging));
        Assert.True(vm.HangupCommand.CanExecute(null));
        vm.HangupCommand.Execute(null);

        Assert.Equal(1, controller.EndCount);
        Assert.False(vm.IsCallActive);
        Assert.Equal("Appel terminé.", vm.StatusText);
    }

    [Fact]
    public void MicrophoneAndCamera_AreHiddenUntilCallAcceptedAndMediaAttached()
    {
        FakeCallController controller = new();
        CallViewModel vm = Create(controller);
        controller.Raise(Session(CallSessionState.OutgoingRinging, CallMediaKind.Video));
        Assert.False(vm.IsInCall);
        Assert.False(vm.IsVideoCall);

        controller.Raise(Session(CallSessionState.Accepted, CallMediaKind.Video));
        Assert.True(vm.IsInCall);
        Assert.True(vm.IsVideoCall);
        // Accepté mais moteur média pas encore créé : le bouton reste inactif.
        Assert.False(vm.ToggleMicrophoneCommand.CanExecute(null));

        vm.AttachMedia(new FakeMediaControls());
        Assert.True(vm.ToggleMicrophoneCommand.CanExecute(null));
        Assert.True(vm.ToggleCameraCommand.CanExecute(null));
    }

    [Fact]
    public void Camera_IsUnavailableDuringAudioCall()
    {
        FakeCallController controller = new();
        CallViewModel vm = Create(controller);
        controller.Raise(Session(CallSessionState.Accepted, CallMediaKind.Audio));
        vm.AttachMedia(new FakeMediaControls());

        Assert.True(vm.ToggleMicrophoneCommand.CanExecute(null));
        Assert.False(vm.IsVideoCall);
        Assert.False(vm.ToggleCameraCommand.CanExecute(null));
    }

    [Fact]
    public void ToggleMicrophone_UpdatesStateAndTooltip_AndToggleBack()
    {
        FakeCallController controller = new();
        CallViewModel vm = Create(controller);
        FakeMediaControls media = new();
        controller.Raise(Session(CallSessionState.Accepted));
        vm.AttachMedia(media);

        vm.ToggleMicrophoneCommand.Execute(null);
        Assert.True(media.IsMicrophoneMuted);
        Assert.True(vm.IsMicrophoneMuted);
        Assert.Equal("Réactiver le microphone", vm.MicrophoneToolTip);

        vm.ToggleMicrophoneCommand.Execute(null);
        Assert.False(media.IsMicrophoneMuted);
        Assert.False(vm.IsMicrophoneMuted);
        Assert.Equal("Couper le microphone", vm.MicrophoneToolTip);
    }

    [Fact]
    public void ToggleCamera_UpdatesStateAndTooltip()
    {
        FakeCallController controller = new();
        CallViewModel vm = Create(controller);
        FakeMediaControls media = new();
        controller.Raise(Session(CallSessionState.Accepted, CallMediaKind.Video));
        vm.AttachMedia(media);

        vm.ToggleCameraCommand.Execute(null);

        Assert.True(media.IsCameraPaused);
        Assert.True(vm.IsCameraPaused);
        Assert.Equal("Reprendre la caméra", vm.CameraToolTip);
    }

    [Fact]
    public void ToggleMicrophone_MediaFailure_KeepsStateAndShowsError()
    {
        FakeCallController controller = new();
        CallViewModel vm = Create(controller);
        FakeMediaControls media = new() { ExceptionToThrow = new InvalidOperationException("Périphérique audio indisponible.") };
        controller.Raise(Session(CallSessionState.Accepted));
        vm.AttachMedia(media);

        vm.ToggleMicrophoneCommand.Execute(null);

        Assert.False(vm.IsMicrophoneMuted);
        Assert.Equal("Périphérique audio indisponible.", vm.ErrorText);
    }

    [Fact]
    public void AttachMedia_SyncsDisplayWithEngineIntent()
    {
        FakeCallController controller = new();
        CallViewModel vm = Create(controller);
        FakeMediaControls media = new();
        controller.Raise(Session(CallSessionState.Accepted, CallMediaKind.Video));
        vm.AttachMedia(media);
        vm.ToggleMicrophoneCommand.Execute(null);

        vm.AttachMedia(null);

        Assert.False(vm.IsMicrophoneMuted);
        Assert.False(vm.ToggleMicrophoneCommand.CanExecute(null));
    }

    [Fact]
    public void EndOfCall_ResetsMediaFlagsAndShowsNotice_ThenDismissClearsIt()
    {
        FakeCallController controller = new();
        CallViewModel vm = Create(controller);
        FakeMediaControls media = new();
        controller.Raise(Session(CallSessionState.Accepted));
        vm.AttachMedia(media);
        vm.ToggleMicrophoneCommand.Execute(null);

        controller.Raise(Session(CallSessionState.Ended));

        Assert.False(vm.IsMicrophoneMuted);
        Assert.False(vm.IsInCall);
        Assert.True(vm.IsBannerVisible);
        Assert.Equal("Appel terminé.", vm.StatusText);

        vm.DismissNoticeCommand.Execute(null);
        Assert.Null(vm.StatusText);
        Assert.False(vm.IsBannerVisible);
    }

    [Fact]
    public void BusyPeer_ShowsNotice()
    {
        FakeCallController controller = new();
        CallViewModel vm = Create(controller);
        controller.Raise(Session(CallSessionState.OutgoingRinging));

        controller.Raise(Session(CallSessionState.Busy));

        Assert.Equal("Le correspondant est occupé.", vm.StatusText);
        Assert.False(vm.IsCallActive);
    }

    [Fact]
    public void NewCall_ClearsPreviousError()
    {
        FakeCallController controller = new() { ExceptionToThrow = new IOException("Impossible d’envoyer.") };
        CallViewModel vm = Create(controller);
        vm.StartAudioCommand.Execute(null);
        Assert.NotNull(vm.ErrorText);

        controller.ExceptionToThrow = null;
        vm.StartAudioCommand.Execute(null);

        Assert.Null(vm.ErrorText);
        Assert.True(vm.IsCallActive);
    }

    [Fact]
    public void MediaStatus_IsShownDuringCall_IgnoredOtherwise_AndTruncated()
    {
        FakeCallController controller = new();
        CallViewModel vm = Create(controller);

        vm.ReportMediaStatus("État WebRTC : connecting.");
        Assert.Null(vm.StatusText);

        controller.Raise(Session(CallSessionState.Accepted));
        vm.ReportMediaStatus("Appel WebRTC connecté.");
        Assert.Equal("Appel WebRTC connecté.", vm.StatusText);

        vm.ReportMediaStatus(new string('x', 5000));
        Assert.Equal(300, vm.StatusText!.Length);
    }

    [Fact]
    public void LateMediaStatusAfterEnd_DoesNotOverwriteEndNotice()
    {
        FakeCallController controller = new();
        CallViewModel vm = Create(controller);
        controller.Raise(Session(CallSessionState.Accepted));
        controller.Raise(Session(CallSessionState.Ended));

        vm.ReportMediaStatus("Appel terminé.");
        vm.ReportMediaStatus("État WebRTC : closed.");

        Assert.Equal("Appel terminé.", vm.StatusText);
    }

    [Fact]
    public void MediaError_IsShownWithoutModal()
    {
        FakeCallController controller = new();
        CallViewModel vm = Create(controller);

        vm.ReportMediaError("Caméra introuvable.");

        Assert.Equal("Caméra introuvable.", vm.ErrorText);
        Assert.True(vm.IsBannerVisible);
    }

    [Fact]
    public void NetworkThreadEvents_ChangeStateOnlyWhenDispatcherRuns()
    {
        FakeCallController controller = new();
        QueueDispatcher dispatcher = new();
        CallViewModel vm = new(controller, dispatcher, () => Peer);

        controller.Raise(Session(CallSessionState.IncomingRinging, outgoing: false));
        Assert.False(vm.IsIncomingRinging);
        Assert.Equal(1, dispatcher.Pending);

        dispatcher.RunAll();
        Assert.True(vm.IsIncomingRinging);
    }

    [Fact]
    public void PropertyChanged_IsRaisedForCallState()
    {
        FakeCallController controller = new();
        CallViewModel vm = Create(controller);
        List<string?> changed = [];
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        controller.Raise(Session(CallSessionState.OutgoingRinging));

        Assert.Contains(nameof(CallViewModel.IsCallActive), changed);
        Assert.Contains(nameof(CallViewModel.CanStartCall), changed);
        Assert.Contains(nameof(CallViewModel.IsBannerVisible), changed);
    }

    [Fact]
    public void RefreshAvailability_FollowsSelectionChanges()
    {
        FakeCallController controller = new();
        string? selected = null;
        CallViewModel vm = new(controller, new ImmediateUiDispatcher(), () => selected);
        Assert.False(vm.StartAudioCommand.CanExecute(null));

        selected = Peer;
        vm.RefreshAvailability();

        Assert.True(vm.CanStartCall);
        Assert.True(vm.StartAudioCommand.CanExecute(null));
    }

    [Fact]
    public void Dispose_UnsubscribesFromController_AndIgnoresLaterEvents()
    {
        FakeCallController controller = new();
        CallViewModel vm = Create(controller);
        Assert.Equal(1, controller.SubscriberCount);

        vm.Dispose();
        vm.Dispose();
        controller.Raise(Session(CallSessionState.IncomingRinging, outgoing: false));
        vm.ReportMediaError("tardif");

        Assert.Equal(0, controller.SubscriberCount);
        Assert.False(vm.IsIncomingRinging);
        Assert.Null(vm.ErrorText);
    }

    [Fact]
    public void ExistingActiveSession_IsPickedUpAtCreation()
    {
        FakeCallController controller = new();
        controller.Raise(Session(CallSessionState.Accepted));

        CallViewModel vm = Create(controller);

        Assert.True(vm.IsInCall);
    }
}
