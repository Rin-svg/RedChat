using RedChat.Core;

namespace RedChat.Infrastructure;

/// <summary>États de signalisation d’un appel privé. Les états Connected ne signifient pas que le média est établi.</summary>
public enum CallSessionState
{
    /// <summary>Aucun appel actif.</summary>
    Idle = 0,
    /// <summary>Invitation sortante en attente.</summary>
    OutgoingRinging = 1,
    /// <summary>Invitation entrante en attente de réponse.</summary>
    IncomingRinging = 2,
    /// <summary>Invitation acceptée ; le média peut être négocié séparément.</summary>
    Accepted = 3,
    /// <summary>Invitation refusée.</summary>
    Rejected = 4,
    /// <summary>Appel terminé.</summary>
    Ended = 5,
    /// <summary>Le correspondant est occupé.</summary>
    Busy = 6,
}

/// <summary>Instantané d’une session d’appel.</summary>
/// <param name="CallId">Identifiant unique de la session.</param>
/// <param name="PeerId">Identité authentifiée du correspondant.</param>
/// <param name="Media">Mode audio ou vidéo demandé.</param>
/// <param name="State">État de la signalisation.</param>
/// <param name="IsOutgoing">Indique si l’appareil local a initié l’appel.</param>
public sealed record CallSessionSnapshot(string CallId, string PeerId, CallMediaKind Media, CallSessionState State, bool IsOutgoing);

/// <summary>Événement émis à chaque changement de session d’appel.</summary>
public sealed class CallSignalReceivedEventArgs : EventArgs
{
    /// <summary>Crée les données d’un signal WebRTC reçu.</summary>
    public CallSignalReceivedEventArgs(CallSessionSnapshot session, CallControlAction action, string data, string? mid, int? mLineIndex)
    {
        Session = session;
        Action = action;
        Data = data;
        Mid = mid;
        MLineIndex = mLineIndex;
    }

    /// <summary>Session d’appel authentifiée concernée.</summary>
    public CallSessionSnapshot Session { get; }

    /// <summary>Type de signal SDP ou ICE.</summary>
    public CallControlAction Action { get; }

    /// <summary>Description SDP ou candidat ICE.</summary>
    public string Data { get; }

    /// <summary>MID SDP pour un candidat ICE.</summary>
    public string? Mid { get; }

    /// <summary>Index de ligne média pour un candidat ICE.</summary>
    public int? MLineIndex { get; }
}

/// <summary>Événement de changement de session d’appel.</summary>
public sealed class CallSessionChangedEventArgs : EventArgs
{
    /// <summary>Crée les données du changement.</summary>
    /// <param name="session">Nouvelle session, ou null.</param>
    public CallSessionChangedEventArgs(CallSessionSnapshot? session) => Session = session;

    /// <summary>Nouvelle session d’appel.</summary>
    public CallSessionSnapshot? Session { get; }
}

/// <summary>
/// Signalisation d’appels privés sur les connexions déjà authentifiées et chiffrées de RedChat.
/// Cette classe ne transporte pas encore le flux audio/vidéo : elle gère uniquement invitation et cycle de vie.
/// </summary>
public sealed class CallSignalingService : ICallController
{
    private readonly object _gate = new();
    private readonly string _localPeerId;
    private readonly TcpTransport _transport;
    private readonly TimeProvider _clock;
    private CallSessionSnapshot? _active;

    /// <summary>Crée le service de signalisation pour une identité locale et son transport.</summary>
    /// <param name="localPeerId">PeerId de l’appareil local.</param>
    /// <param name="transport">Transport TCP authentifié.</param>
    /// <param name="clock">Horloge injectable pour les tests.</param>
    public CallSignalingService(string localPeerId, TcpTransport transport, TimeProvider? clock = null)
    {
        if (!InputValidator.IsValidPeerId(localPeerId))
        {
            throw new ArgumentException("PeerId local invalide.", nameof(localPeerId));
        }
        _localPeerId = localPeerId;
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _clock = clock ?? TimeProvider.System;
        // POURQUOI : le paquet « End » peut se perdre (fermeture brutale, coupure réseau, plantage du pair).
        // La perte de la connexion TCP authentifiée est alors le seul signal fiable que l'appel est fini.
        // Ce service a la même durée de vie que le transport (tous deux détenus par LanNode) : pas de désabonnement nécessaire.
        _transport.PeerDisconnected += OnPeerDisconnected;
    }

    /// <summary>Se déclenche lorsqu’une session est créée ou change d’état.</summary>
    public event EventHandler<CallSessionChangedEventArgs>? SessionChanged;

    /// <summary>Se déclenche lorsqu’une description SDP ou un candidat ICE est reçu du pair authentifié.</summary>
    public event EventHandler<CallSignalReceivedEventArgs>? SignalReceived;

    /// <summary>Session actuellement suivie, ou null.</summary>
    public CallSessionSnapshot? ActiveSession
    {
        get
        {
            lock (_gate)
            {
                return _active;
            }
        }
    }

    /// <summary>Envoie une invitation à un pair déjà connecté.</summary>
    /// <param name="peerId">PeerId du correspondant.</param>
    /// <param name="media">Mode audio ou vidéo.</param>
    /// <returns>Identifiant de la session créée.</returns>
    public string StartCall(string peerId, CallMediaKind media)
    {
        if (!InputValidator.IsValidPeerId(peerId) || string.Equals(peerId, _localPeerId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Contact d’appel invalide.", nameof(peerId));
        }
        if (!Enum.IsDefined(media))
        {
            throw new ArgumentOutOfRangeException(nameof(media));
        }
        if (!_transport.IsConnected(peerId))
        {
            throw new InvalidOperationException("Le contact doit être connecté avant l’appel.");
        }

        CallSessionSnapshot session;
        lock (_gate)
        {
            if (_active is { State: CallSessionState.OutgoingRinging or CallSessionState.IncomingRinging or CallSessionState.Accepted })
            {
                throw new InvalidOperationException("Un autre appel est déjà en cours.");
            }
            session = new CallSessionSnapshot(Guid.NewGuid().ToString("D"), peerId, media, CallSessionState.OutgoingRinging, true);
            _active = session;
        }
        if (!Send(session, CallControlAction.Invite))
        {
            lock (_gate)
            {
                _active = null;
            }
            throw new IOException("Impossible de placer l’invitation dans la file réseau.");
        }
        RaiseChanged(session);
        return session.CallId;
    }

    /// <summary>Accepte l’invitation entrante actuellement affichée.</summary>
    public void AcceptIncoming()
    {
        CallSessionSnapshot session = RequireState(CallSessionState.IncomingRinging);
        if (!Send(session, CallControlAction.Accept))
        {
            throw new IOException("Impossible d’envoyer l’acceptation.");
        }
        session = session with { State = CallSessionState.Accepted };
        SetActive(session);
    }

    /// <summary>Refuse l’invitation entrante actuellement affichée.</summary>
    public void RejectIncoming()
    {
        CallSessionSnapshot session = RequireState(CallSessionState.IncomingRinging);
        if (!Send(session, CallControlAction.Reject))
        {
            throw new IOException("Impossible d’envoyer le refus.");
        }
        SetActive(session with { State = CallSessionState.Rejected });
    }

    /// <summary>Termine ou annule la session en cours.</summary>
    public void EndCall()
    {
        CallSessionSnapshot? session;
        lock (_gate)
        {
            session = _active;
        }
        if (session is null || session.State is CallSessionState.Ended or CallSessionState.Rejected or CallSessionState.Busy)
        {
            return;
        }
        _ = Send(session, CallControlAction.End);
        SetActive(session with { State = CallSessionState.Ended });
    }

    /// <summary>Envoie l’offre SDP de l’initiateur après acceptation de l’appel.</summary>
    /// <param name="sdp">Description SDP locale.</param>
    public void SendOffer(string sdp) => SendMediaSignal(CallControlAction.Offer, sdp, null, null);

    /// <summary>Envoie la réponse SDP du destinataire.</summary>
    /// <param name="sdp">Description SDP locale.</param>
    public void SendAnswer(string sdp) => SendMediaSignal(CallControlAction.Answer, sdp, null, null);

    /// <summary>Envoie un candidat ICE découvert localement.</summary>
    /// <param name="candidate">Valeur du candidat ICE.</param>
    /// <param name="mid">MID SDP auquel le candidat appartient.</param>
    /// <param name="mLineIndex">Index de ligne média.</param>
    public void SendIceCandidate(string candidate, string mid, int mLineIndex) =>
        SendMediaSignal(CallControlAction.IceCandidate, candidate, mid, mLineIndex);

    private void SendMediaSignal(CallControlAction action, string data, string? mid, int? mLineIndex)
    {
        CallSessionSnapshot session;
        lock (_gate)
        {
            if (_active is not { State: CallSessionState.Accepted } current)
            {
                throw new InvalidOperationException("La négociation média exige un appel accepté.");
            }
            session = current;
        }

        if (action is not (CallControlAction.Offer or CallControlAction.Answer or CallControlAction.IceCandidate))
        {
            throw new ArgumentOutOfRangeException(nameof(action));
        }

        Packet packet = new()
        {
            Kind = PacketKind.CallControl,
            Id = Guid.NewGuid().ToString("D"),
            ConversationId = session.PeerId,
            SentAtUnixMs = _clock.GetUtcNow().ToUnixTimeMilliseconds(),
            CallId = session.CallId,
            CallAction = action,
            CallMedia = session.Media,
            CallSignalData = data,
            CallSignalMid = mid,
            CallSignalMLineIndex = mLineIndex,
        };
        if (!_transport.TrySend(session.PeerId, packet))
        {
            throw new IOException("Impossible d’envoyer le signal WebRTC.");
        }
    }

    /// <summary>Traite une commande reçue sur une session TCP déjà authentifiée.</summary>
    /// <param name="authenticatedPeerId">Identité du pair prouvée par le handshake.</param>
    /// <param name="packet">Paquet de signalisation validé.</param>
    public void Receive(string authenticatedPeerId, Packet packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (!InputValidator.IsValidPeerId(authenticatedPeerId) || string.Equals(authenticatedPeerId, _localPeerId, StringComparison.Ordinal))
        {
            throw new ProtocolViolationException("Identité du correspondant invalide.");
        }
        if (packet.Kind != PacketKind.CallControl)
        {
            throw new ArgumentException("Paquet de signalisation attendu.", nameof(packet));
        }
        if (!string.Equals(packet.ConversationId, _localPeerId, StringComparison.Ordinal))
        {
            throw new ProtocolViolationException("La signalisation d’appel ne cible pas cet appareil.");
        }

        CallSessionSnapshot? changed = null;
        if (packet.CallAction == CallControlAction.Invite)
        {
            lock (_gate)
            {
                if (_active is { State: CallSessionState.OutgoingRinging or CallSessionState.IncomingRinging or CallSessionState.Accepted })
                {
                    _ = SendRaw(authenticatedPeerId, packet.CallId!, packet.CallMedia!.Value, CallControlAction.Busy);
                    return;
                }
                changed = new CallSessionSnapshot(packet.CallId!, authenticatedPeerId, packet.CallMedia!.Value, CallSessionState.IncomingRinging, false);
                _active = changed;
            }
            RaiseChanged(changed);
            return;
        }

        if (packet.CallAction is CallControlAction.Offer or CallControlAction.Answer or CallControlAction.IceCandidate)
        {
            CallSessionSnapshot session;
            lock (_gate)
            {
                if (_active is not { State: CallSessionState.Accepted } current || current.CallId != packet.CallId || current.PeerId != authenticatedPeerId || current.Media != packet.CallMedia)
                {
                    throw new ProtocolViolationException("Signal média sans session acceptée correspondante.");
                }
                session = current;
            }
            SignalReceived?.Invoke(this, new CallSignalReceivedEventArgs(session, packet.CallAction.Value, packet.CallSignalData!, packet.CallSignalMid, packet.CallSignalMLineIndex));
            return;
        }

        lock (_gate)
        {
            if (_active is null || _active.CallId != packet.CallId || _active.PeerId != authenticatedPeerId)
            {
                return;
            }
            CallSessionState next = packet.CallAction switch
            {
                CallControlAction.Accept => CallSessionState.Accepted,
                CallControlAction.Reject => CallSessionState.Rejected,
                CallControlAction.End => CallSessionState.Ended,
                CallControlAction.Busy => CallSessionState.Busy,
                _ => _active.State,
            };
            changed = _active with { State = next };
            _active = changed;
        }
        RaiseChanged(changed);
    }

    private void OnPeerDisconnected(object? sender, PeerConnectionEventArgs e) => HandlePeerDisconnected(e.PeerId);

    /// <summary>
    /// Termine la session en cours si elle concerne le pair dont la connexion vient de tomber.
    /// Sans effet pour un autre pair, une session absente ou déjà terminée (aucun événement en double).
    /// </summary>
    /// <param name="peerId">Pair dont la connexion authentifiée vient d'être perdue.</param>
    /// <returns>True si une session a été terminée par cet appel.</returns>
    internal bool HandlePeerDisconnected(string peerId)
    {
        CallSessionSnapshot? ended;
        lock (_gate)
        {
            if (_active is not { State: CallSessionState.OutgoingRinging or CallSessionState.IncomingRinging or CallSessionState.Accepted } current
                || !string.Equals(current.PeerId, peerId, StringComparison.Ordinal))
            {
                return false;
            }
            ended = current with { State = CallSessionState.Ended };
            _active = ended;
        }
        RaiseChanged(ended);
        return true;
    }

    private CallSessionSnapshot RequireState(CallSessionState state)
    {
        lock (_gate)
        {
            if (_active is null || _active.State != state || _active.IsOutgoing)
            {
                throw new InvalidOperationException("Aucun appel entrant dans cet état.");
            }
            return _active;
        }
    }

    private void SetActive(CallSessionSnapshot session)
    {
        lock (_gate)
        {
            _active = session;
        }
        RaiseChanged(session);
    }

    private bool Send(CallSessionSnapshot session, CallControlAction action) =>
        SendRaw(session.PeerId, session.CallId, session.Media, action);

    private bool SendRaw(string peerId, string callId, CallMediaKind media, CallControlAction action) =>
        _transport.TrySend(peerId, new Packet
        {
            Kind = PacketKind.CallControl,
            Id = Guid.NewGuid().ToString("D"),
            ConversationId = peerId,
            SentAtUnixMs = _clock.GetUtcNow().ToUnixTimeMilliseconds(),
            CallId = callId,
            CallAction = action,
            CallMedia = media,
        });

    private void RaiseChanged(CallSessionSnapshot? session) => SessionChanged?.Invoke(this, new CallSessionChangedEventArgs(session));
}
