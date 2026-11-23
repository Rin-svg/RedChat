using RedChat.Core;
using Xunit;

namespace RedChat.Infrastructure.Tests;

/// <summary>
/// Cycle de vie d'un appel quand le pair disparaît (fermeture de fenêtre, plantage, coupure réseau).
/// POURQUOI : le paquet « End » peut se perdre ; la perte de la connexion authentifiée doit alors terminer l'appel.
/// </summary>
public class CallLifecycleTests
{
    private static CallSessionState? StateOf(LanNode node) => node.Calls.ActiveSession?.State;

    [Fact]
    public void PeerDisconnect_DuringAcceptedCall_EndsSessionOnSurvivor()
    {
        using NetHelpers.Rig rig = new();
        LanNode caller = rig.Add();
        LanNode receiver = rig.Add();
        NetHelpers.Rig.Link(caller, receiver);

        _ = caller.Calls.StartCall(receiver.PeerId, CallMediaKind.Audio);
        Assert.True(NetHelpers.WaitUntil(() => StateOf(receiver) == CallSessionState.IncomingRinging));
        receiver.Calls.AcceptIncoming();
        Assert.True(NetHelpers.WaitUntil(() => StateOf(caller) == CallSessionState.Accepted));

        // Le correspondant disparaît sans envoyer « End » (on ferme son nœud directement).
        NetHelpers.Await(caller.DisposeAsync().AsTask());

        Assert.True(NetHelpers.WaitUntil(() => StateOf(receiver) == CallSessionState.Ended));
    }

    [Fact]
    public void PeerDisconnect_WhileRinging_EndsIncomingInvitation()
    {
        using NetHelpers.Rig rig = new();
        LanNode caller = rig.Add();
        LanNode receiver = rig.Add();
        NetHelpers.Rig.Link(caller, receiver);

        _ = caller.Calls.StartCall(receiver.PeerId, CallMediaKind.Video);
        Assert.True(NetHelpers.WaitUntil(() => StateOf(receiver) == CallSessionState.IncomingRinging));

        NetHelpers.Await(caller.DisposeAsync().AsTask());

        Assert.True(NetHelpers.WaitUntil(() => StateOf(receiver) == CallSessionState.Ended));
    }

    [Fact]
    public void EndCall_ThenDispose_LeavesPeerWithEndedSession()
    {
        using NetHelpers.Rig rig = new();
        LanNode caller = rig.Add();
        LanNode receiver = rig.Add();
        NetHelpers.Rig.Link(caller, receiver);

        _ = caller.Calls.StartCall(receiver.PeerId, CallMediaKind.Audio);
        Assert.True(NetHelpers.WaitUntil(() => StateOf(receiver) == CallSessionState.IncomingRinging));
        receiver.Calls.AcceptIncoming();
        Assert.True(NetHelpers.WaitUntil(() => StateOf(caller) == CallSessionState.Accepted));

        // Scénario de la fermeture de fenêtre : EndCall puis arrêt immédiat du nœud.
        caller.Calls.EndCall();
        NetHelpers.Await(caller.DisposeAsync().AsTask());

        // Que « End » soit arrivé ou non, le pair doit conclure à la fin de l'appel.
        Assert.True(NetHelpers.WaitUntil(() => StateOf(receiver) == CallSessionState.Ended));
    }

    [Fact]
    public void HandlePeerDisconnected_UnrelatedPeer_KeepsSession()
    {
        using NetHelpers.Rig rig = new();
        LanNode caller = rig.Add();
        LanNode receiver = rig.Add();
        NetHelpers.Rig.Link(caller, receiver);
        _ = caller.Calls.StartCall(receiver.PeerId, CallMediaKind.Audio);
        Assert.True(NetHelpers.WaitUntil(() => StateOf(receiver) == CallSessionState.IncomingRinging));

        string stranger = new('a', receiver.PeerId.Length);
        Assert.False(receiver.Calls.HandlePeerDisconnected(stranger));
        Assert.Equal(CallSessionState.IncomingRinging, StateOf(receiver));
    }

    [Fact]
    public void HandlePeerDisconnected_WithoutSession_DoesNothing()
    {
        using NetHelpers.Rig rig = new();
        LanNode node = rig.Add();
        Assert.False(node.Calls.HandlePeerDisconnected(new string('b', node.PeerId.Length)));
        Assert.Null(node.Calls.ActiveSession);
    }

    [Fact]
    public void HandlePeerDisconnected_Twice_RaisesSingleEvent()
    {
        using NetHelpers.Rig rig = new();
        LanNode caller = rig.Add();
        LanNode receiver = rig.Add();
        NetHelpers.Rig.Link(caller, receiver);
        _ = caller.Calls.StartCall(receiver.PeerId, CallMediaKind.Audio);
        Assert.True(NetHelpers.WaitUntil(() => StateOf(receiver) == CallSessionState.IncomingRinging));

        int ended = 0;
        receiver.Calls.SessionChanged += (_, e) =>
        {
            if (e.Session?.State == CallSessionState.Ended)
            {
                _ = Interlocked.Increment(ref ended);
            }
        };

        Assert.True(receiver.Calls.HandlePeerDisconnected(caller.PeerId));
        Assert.False(receiver.Calls.HandlePeerDisconnected(caller.PeerId));
        Assert.Equal(1, Volatile.Read(ref ended));
    }
}
