using RedChat.Core;
using RedChat.Infrastructure;
using RedChat.Presentation;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Windows;
using SIPSorceryMedia.Abstractions;
using Vpx.Net;

namespace RedChat.Wpf.Calls;

/// <summary>
/// Moteur média WebRTC d'un appel RedChat privé. La signalisation reste assurée
/// par CallSignalingService, sur le transport authentifié et chiffré de RedChat.
/// </summary>
public sealed class WebRtcMediaEngine : IAsyncDisposable, ICallMediaControls
{
    private readonly CallSignalingService _signaling;
    private readonly CallSessionSnapshot _session;
    private readonly object _gate = new();
    private readonly List<RTCIceCandidateInit> _pendingRemoteCandidates = [];
    private RTCPeerConnection? _peerConnection;
    private WindowsAudioEndPoint? _audio;
    private WindowsVideoEndPoint? _video;
    // Sérialise démarrage/arrêt des périphériques et changements micro/caméra : les événements d'état WebRTC
    // peuvent arriver coup sur coup (connected puis failed) et ne doivent jamais s'entrecroiser.
    private readonly SemaphoreSlim _deviceLock = new(1, 1);
    private bool _remoteDescriptionSet;
    private int _disposed;
    private bool _devicesStarted;
    private bool _videoStarted;
    private bool _microphoneMuted;
    private bool _cameraPaused;

    /// <summary>Crée le moteur pour une session d'appel déjà acceptée.</summary>
    public WebRtcMediaEngine(CallSignalingService signaling, CallSessionSnapshot session)
    {
        _signaling = signaling ?? throw new ArgumentNullException(nameof(signaling));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        if (session.State != CallSessionState.Accepted)
        {
            throw new ArgumentException("Le moteur média exige un appel accepté.", nameof(session));
        }
    }

    /// <summary>État de connexion WebRTC lisible par l'interface.</summary>
    public event Action<string>? StatusChanged;

    /// <summary>Erreur de capture, de lecture ou de négociation média.</summary>
    public event Action<string>? MediaError;

    /// <summary>Image distante décodée (pixels, dimensions, stride et format), prête pour le rendu WPF.</summary>
    public event Action<byte[], uint, uint, int, VideoPixelFormatsEnum>? RemoteVideoFrameAvailable;

    /// <summary>
    /// Image de la caméra locale (pixels, dimensions, stride et format) pour la vignette de prévisualisation.
    /// Jamais déclenchée quand la caméra est en pause ou pendant un appel audio.
    /// </summary>
    public event Action<byte[], uint, uint, int, VideoPixelFormatsEnum>? LocalVideoFrameAvailable;

    /// <summary>Indique si le microphone est coupé.</summary>
    public bool IsMicrophoneMuted => _microphoneMuted;

    /// <summary>Indique si la caméra est en pause.</summary>
    public bool IsCameraPaused => _cameraPaused;

    /// <summary>Crée l'offre SDP d'un appel sortant accepté.</summary>
    public async Task StartOutgoingAsync()
    {
        ThrowIfDisposed();
        if (!_session.IsOutgoing)
        {
            throw new InvalidOperationException("Seul l'initiateur peut créer l'offre initiale.");
        }

        RTCPeerConnection peer = EnsurePeerConnection();
        RTCSessionDescriptionInit offer = peer.createOffer();
        await peer.setLocalDescription(offer).ConfigureAwait(false);
        _signaling.SendOffer(offer.sdp);
        StatusChanged?.Invoke("Offre WebRTC envoyée. Recherche du correspondant…");
    }

    /// <summary>Applique une offre, une réponse ou un candidat ICE reçu de l'autre appareil.</summary>
    public async Task HandleSignalAsync(CallSignalReceivedEventArgs signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        ThrowIfDisposed();
        if (signal.Session.CallId != _session.CallId || signal.Session.PeerId != _session.PeerId)
        {
            throw new ProtocolViolationException("Le signal WebRTC ne correspond pas à cet appel.");
        }

        RTCPeerConnection peer = EnsurePeerConnection();
        switch (signal.Action)
        {
            case CallControlAction.Offer:
            {
                if (_session.IsOutgoing)
                {
                    throw new ProtocolViolationException("Un initiateur ne peut pas recevoir une seconde offre initiale.");
                }
                SetRemoteDescription(peer, signal.Data, RTCSdpType.offer);
                await FlushRemoteCandidatesAsync(peer).ConfigureAwait(false);
                RTCSessionDescriptionInit answer = peer.createAnswer();
                await peer.setLocalDescription(answer).ConfigureAwait(false);
                _signaling.SendAnswer(answer.sdp);
                StatusChanged?.Invoke("Réponse WebRTC envoyée. Connexion média en cours…");
                break;
            }
            case CallControlAction.Answer:
            {
                if (!_session.IsOutgoing)
                {
                    throw new ProtocolViolationException("Un destinataire ne peut pas recevoir une réponse à sa propre offre.");
                }
                SetRemoteDescription(peer, signal.Data, RTCSdpType.answer);
                await FlushRemoteCandidatesAsync(peer).ConfigureAwait(false);
                StatusChanged?.Invoke("Réponse reçue. Connexion média en cours…");
                break;
            }
            case CallControlAction.IceCandidate:
            {
                if (!RTCIceCandidateInit.TryParse(signal.Data, out RTCIceCandidateInit? candidate) || candidate is null)
                {
                    throw new ProtocolViolationException("Candidat ICE illisible.");
                }
                lock (_gate)
                {
                    if (!_remoteDescriptionSet)
                    {
                        if (_pendingRemoteCandidates.Count >= 128)
                        {
                            throw new ProtocolViolationException("Trop de candidats ICE en attente.");
                        }
                        _pendingRemoteCandidates.Add(candidate);
                        return;
                    }
                }
                peer.addIceCandidate(candidate);
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(signal), "Action de signalisation média inconnue.");
        }
    }

    /// <summary>
    /// Coupe ou réactive la capture du microphone. Avant la connexion des périphériques, l'intention est mémorisée
    /// et appliquée au démarrage (le micro ne s'ouvre alors jamais « ouvert » contre la volonté de l'utilisateur).
    /// </summary>
    public async Task SetMicrophoneMutedAsync(bool muted)
    {
        ThrowIfDisposed();
        await _deviceLock.WaitAsync().ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_audio is not null && _devicesStarted)
            {
                if (muted)
                {
                    await _audio.PauseAudio().ConfigureAwait(false);
                }
                else
                {
                    await _audio.ResumeAudio().ConfigureAwait(false);
                }
            }
            _microphoneMuted = muted;
        }
        finally
        {
            _deviceLock.Release();
        }
        StatusChanged?.Invoke(muted ? "Microphone coupé." : "Microphone activé.");
    }

    /// <summary>Met en pause ou reprend la capture de la caméra (intention mémorisée avant la connexion).</summary>
    public async Task SetCameraPausedAsync(bool paused)
    {
        ThrowIfDisposed();
        if (_session.Media != CallMediaKind.Video)
        {
            throw new InvalidOperationException("La caméra n'est pas disponible pendant un appel audio.");
        }
        await _deviceLock.WaitAsync().ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_video is not null && _devicesStarted)
            {
                if (paused && _videoStarted)
                {
                    await _video.PauseVideo().ConfigureAwait(false);
                }
                else if (!paused && !_videoStarted)
                {
                    await _video.StartVideo().ConfigureAwait(false);
                    _videoStarted = true;
                }
                else if (!paused)
                {
                    await _video.ResumeVideo().ConfigureAwait(false);
                }
            }
            _cameraPaused = paused;
        }
        finally
        {
            _deviceLock.Release();
        }
        StatusChanged?.Invoke(paused ? "Caméra en pause." : "Caméra activée.");
    }

    /// <summary>Ferme proprement la connexion WebRTC et libère les périphériques.</summary>
    public async ValueTask DisposeAsync()
    {
        // Interlocked : Raccrocher et « appel terminé » reçu du pair peuvent déclencher la fermeture en même temps.
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        RTCPeerConnection? peer;
        lock (_gate)
        {
            peer = _peerConnection;
            _peerConnection = null;
            _pendingRemoteCandidates.Clear();
        }
        if (peer is not null)
        {
            peer.onicecandidate -= OnLocalIceCandidate;
            peer.onconnectionstatechange -= OnConnectionStateChanged;
            peer.Close("RedChat call ended");
            peer.Dispose();
        }
        await _deviceLock.WaitAsync().ConfigureAwait(false);
        try
        {
            _devicesStarted = false;
            _videoStarted = false;
            if (_video is not null)
            {
                await _video.CloseVideo().ConfigureAwait(false);
                _video = null;
            }
            if (_audio is not null)
            {
                await _audio.Close().ConfigureAwait(false);
                _audio = null;
            }
        }
        finally
        {
            _deviceLock.Release();
            _deviceLock.Dispose();
        }
    }

    private RTCPeerConnection EnsurePeerConnection()
    {
        lock (_gate)
        {
            if (_peerConnection is not null)
            {
                return _peerConnection;
            }

            RTCPeerConnection peer = new(null);
            WindowsAudioEndPoint audio = new(new AudioEncoder(), audioOutDeviceIndex: -1, audioInDeviceIndex: -1);
            audio.OnAudioSourceError += message => MediaError?.Invoke(message);
            audio.OnAudioSinkError += message => MediaError?.Invoke(message);
#pragma warning disable CS0618
            audio.OnAudioSourceEncodedSample += peer.SendAudio;
#pragma warning restore CS0618
            peer.OnAudioFormatsNegotiated += formats =>
            {
                if (formats.Count > 0)
                {
                    audio.SetAudioSourceFormat(formats[0]);
                    audio.SetAudioSinkFormat(formats[0]);
                }
            };
            peer.OnAudioFrameReceived += audio.GotEncodedMediaFrame;
            peer.addTrack(new MediaStreamTrack(audio.GetAudioSourceFormats(), MediaStreamStatusEnum.SendRecv));
            _audio = audio;

            if (_session.Media == CallMediaKind.Video)
            {
                VP8Codec codec = new();
                WindowsVideoEndPoint video = new(codec);
                video.RestrictFormats(format => format.Codec == VideoCodecsEnum.VP8);
                video.OnVideoSourceError += message => MediaError?.Invoke(message);
                // Prévisualisation locale : image brute de la caméra, avant encodage. Le stride se déduit de la taille
                // réelle du tampon (et non d'une hypothèse sur le format) ; l'interface revalide de toute façon l'image.
                video.OnVideoSourceRawSample += (durationMilliseconds, width, height, sample, pixelFormat) =>
                {
                    if (_cameraPaused || width <= 0 || height <= 0 || sample is null || sample.Length == 0)
                    {
                        return;
                    }
                    LocalVideoFrameAvailable?.Invoke(sample, (uint)width, (uint)height, sample.Length / height, pixelFormat);
                };
                video.OnVideoSourceEncodedSample += peer.SendVideo;
                video.OnVideoSinkDecodedSample += (sample, width, height, stride, pixelFormat) =>
                    RemoteVideoFrameAvailable?.Invoke(sample, width, height, stride, pixelFormat);
                peer.OnVideoFormatsNegotiated += formats =>
                {
                    if (formats.Count > 0)
                    {
                        video.SetVideoSourceFormat(formats[0]);
                        video.SetVideoSinkFormat(formats[0]);
                    }
                };
                peer.OnVideoFrameReceived += video.GotVideoFrame;
                peer.addTrack(new MediaStreamTrack(video.GetVideoSourceFormats(), MediaStreamStatusEnum.SendRecv));
                _video = video;
            }

            peer.onicecandidate += OnLocalIceCandidate;
            peer.onconnectionstatechange += OnConnectionStateChanged;
            _peerConnection = peer;
            return peer;
        }
    }

    private void OnLocalIceCandidate(RTCIceCandidate candidate)
    {
        try
        {
            _signaling.SendIceCandidate(candidate.toJSON(), candidate.sdpMid ?? candidate.sdpMLineIndex.ToString(System.Globalization.CultureInfo.InvariantCulture), candidate.sdpMLineIndex);
        }
        catch (Exception ex)
        {
            MediaError?.Invoke($"Échec d'envoi du candidat ICE : {ex.Message}");
        }
    }

    private void OnConnectionStateChanged(RTCPeerConnectionState state)
    {
        _ = StartOrStopDevicesAsync(state);
    }

    private async Task StartOrStopDevicesAsync(RTCPeerConnectionState state)
    {
        try
        {
            await _deviceLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    return;
                }
                if (state == RTCPeerConnectionState.connected)
                {
                    if (_audio is not null)
                    {
                        await _audio.Start().ConfigureAwait(false);
                        if (_microphoneMuted)
                        {
                            // L'utilisateur a coupé le micro avant la connexion : on l'applique dès le démarrage.
                            await _audio.PauseAudio().ConfigureAwait(false);
                        }
                    }
                    if (_video is not null && !_cameraPaused)
                    {
                        await _video.StartVideo().ConfigureAwait(false);
                        _videoStarted = true;
                    }
                    _devicesStarted = true;
                    StatusChanged?.Invoke("Appel WebRTC connecté.");
                }
                else if (state is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed)
                {
                    _devicesStarted = false;
                    _videoStarted = false;
                    if (_audio is not null)
                    {
                        await _audio.Close().ConfigureAwait(false);
                    }
                    if (_video is not null)
                    {
                        await _video.CloseVideo().ConfigureAwait(false);
                    }
                    StatusChanged?.Invoke(state == RTCPeerConnectionState.failed ? "Connexion média échouée." : "Appel terminé.");
                }
                else
                {
                    StatusChanged?.Invoke($"État WebRTC : {state}.");
                }
            }
            finally
            {
                _deviceLock.Release();
            }
        }
        catch (ObjectDisposedException)
        {
            // Fermeture concurrente de l'appel : rien à signaler à l'utilisateur.
        }
        catch (Exception ex)
        {
            MediaError?.Invoke($"Erreur périphérique média : {ex.Message}");
        }
    }

    private void SetRemoteDescription(RTCPeerConnection peer, string sdp, RTCSdpType type)
    {
        SetDescriptionResultEnum result = peer.setRemoteDescription(new RTCSessionDescriptionInit { sdp = sdp, type = type });
        if (result != SetDescriptionResultEnum.OK)
        {
            throw new InvalidOperationException($"La description distante a été refusée : {result}.");
        }
        lock (_gate)
        {
            _remoteDescriptionSet = true;
        }
    }

    private async Task FlushRemoteCandidatesAsync(RTCPeerConnection peer)
    {
        List<RTCIceCandidateInit> pending;
        lock (_gate)
        {
            pending = [.. _pendingRemoteCandidates];
            _pendingRemoteCandidates.Clear();
        }
        await Task.Yield();
        foreach (RTCIceCandidateInit candidate in pending)
        {
            peer.addIceCandidate(candidate);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }
}
