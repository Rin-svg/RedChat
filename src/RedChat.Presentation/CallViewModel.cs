using System.Windows.Input;
using RedChat.Core;
using RedChat.Infrastructure;

namespace RedChat.Presentation;

/// <summary>
/// État et commandes de l'interface d'appel (démarrer, répondre, raccrocher, micro, caméra).
/// POURQUOI un ViewModel : l'ancienne logique vivait dans MainWindow.xaml.cs et ouvrait une boîte de
/// dialogue modale à chaque événement ; ici l'état est testable sans WPF et les messages s'affichent
/// dans un bandeau non bloquant.
/// Règle de threads : les événements réseau arrivent sur un thread quelconque ; toute modification
/// d'état est renvoyée vers le thread de l'interface via <see cref="IUiDispatcher"/>.
/// </summary>
public sealed class CallViewModel : ObservableObject, IDisposable
{
    /// <summary>Longueur maximale d'un message affiché (un message média ne doit jamais déformer l'interface).</summary>
    private const int MaxNoticeLength = 300;

    private readonly ICallController _calls;
    private readonly IUiDispatcher _dispatcher;
    private readonly Func<string?> _selectedPrivatePeerId;
    private CallSessionSnapshot? _session;
    private ICallMediaControls? _media;
    private string? _statusText;
    private string? _errorText;
    private bool _microphoneMuted;
    private bool _cameraPaused;
    private bool _disposed;

    /// <summary>Crée le ViewModel d'appel.</summary>
    /// <param name="calls">Contrôleur d'appel (signalisation).</param>
    /// <param name="dispatcher">Accès au thread de l'interface.</param>
    /// <param name="selectedPrivatePeerId">Retourne le PeerId de la conversation privée affichée, ou null (aucune sélection ou groupe).</param>
    public CallViewModel(ICallController calls, IUiDispatcher dispatcher, Func<string?> selectedPrivatePeerId)
    {
        _calls = calls ?? throw new ArgumentNullException(nameof(calls));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _selectedPrivatePeerId = selectedPrivatePeerId ?? throw new ArgumentNullException(nameof(selectedPrivatePeerId));

        StartAudioCommand = new RelayCommand(() => StartCall(CallMediaKind.Audio), SetError, () => CanStartCall);
        StartVideoCommand = new RelayCommand(() => StartCall(CallMediaKind.Video), SetError, () => CanStartCall);
        AcceptCommand = new RelayCommand(_calls.AcceptIncoming, SetError, () => IsIncomingRinging);
        RejectCommand = new RelayCommand(_calls.RejectIncoming, SetError, () => IsIncomingRinging);
        HangupCommand = new RelayCommand(_calls.EndCall, SetError, () => IsCallActive);
        ToggleMicrophoneCommand = new AsyncRelayCommand(ToggleMicrophoneAsync, SetError, () => IsInCall && _media is not null);
        ToggleCameraCommand = new AsyncRelayCommand(ToggleCameraAsync, SetError, () => IsVideoCall && _media is not null);
        DismissNoticeCommand = new RelayCommand(DismissNotice);

        _session = _calls.ActiveSession;
        _calls.SessionChanged += OnSessionChanged;
    }

    /// <summary>Démarre un appel audio avec le contact affiché.</summary>
    public ICommand StartAudioCommand { get; }

    /// <summary>Démarre un appel vidéo avec le contact affiché.</summary>
    public ICommand StartVideoCommand { get; }

    /// <summary>Accepte l'appel entrant.</summary>
    public ICommand AcceptCommand { get; }

    /// <summary>Refuse l'appel entrant.</summary>
    public ICommand RejectCommand { get; }

    /// <summary>Termine ou annule l'appel en cours.</summary>
    public ICommand HangupCommand { get; }

    /// <summary>Coupe ou réactive le microphone.</summary>
    public ICommand ToggleMicrophoneCommand { get; }

    /// <summary>Met la caméra en pause ou la reprend.</summary>
    public ICommand ToggleCameraCommand { get; }

    /// <summary>Masque le bandeau d'information lorsque aucun appel n'est actif.</summary>
    public ICommand DismissNoticeCommand { get; }

    /// <summary>Vrai tant qu'un appel sonne ou est accepté.</summary>
    public bool IsCallActive => _session?.State is CallSessionState.OutgoingRinging or CallSessionState.IncomingRinging or CallSessionState.Accepted;

    /// <summary>Vrai si une invitation entrante attend une réponse.</summary>
    public bool IsIncomingRinging => _session?.State == CallSessionState.IncomingRinging;

    /// <summary>Vrai si l'appel est accepté (les boutons micro/caméra n'ont de sens que dans ce cas).</summary>
    public bool IsInCall => _session?.State == CallSessionState.Accepted;

    /// <summary>Vrai pendant un appel vidéo accepté.</summary>
    public bool IsVideoCall => IsInCall && _session?.Media == CallMediaKind.Video;

    /// <summary>Vrai si un appel peut être lancé : conversation privée affichée et aucun appel en cours.</summary>
    public bool CanStartCall => !IsCallActive && _selectedPrivatePeerId() is not null;

    /// <summary>Vrai si le microphone est coupé.</summary>
    public bool IsMicrophoneMuted => _microphoneMuted;

    /// <summary>Vrai si la caméra est en pause.</summary>
    public bool IsCameraPaused => _cameraPaused;

    /// <summary>Texte d'état de l'appel (non modal).</summary>
    public string? StatusText
    {
        get => _statusText;
        private set
        {
            if (SetProperty(ref _statusText, value))
            {
                OnPropertyChanged(nameof(IsBannerVisible));
            }
        }
    }

    /// <summary>Dernière erreur d'appel ou de média (non modale).</summary>
    public string? ErrorText
    {
        get => _errorText;
        private set
        {
            if (SetProperty(ref _errorText, value))
            {
                OnPropertyChanged(nameof(IsBannerVisible));
            }
        }
    }

    /// <summary>Vrai si le bandeau d'appel doit être affiché.</summary>
    public bool IsBannerVisible => IsCallActive || !string.IsNullOrEmpty(_statusText) || !string.IsNullOrEmpty(_errorText);

    /// <summary>Info-bulle du bouton microphone.</summary>
    public string MicrophoneToolTip => _microphoneMuted ? "Réactiver le microphone" : "Couper le microphone";

    /// <summary>Info-bulle du bouton caméra.</summary>
    public string CameraToolTip => _cameraPaused ? "Reprendre la caméra" : "Mettre la caméra en pause";

    /// <summary>
    /// Associe (ou dissocie, avec null) le moteur média de l'appel. À appeler sur le thread de l'interface.
    /// Réinitialise l'affichage micro/caméra d'après l'état réel du moteur.
    /// </summary>
    public void AttachMedia(ICallMediaControls? media)
    {
        _media = media;
        _microphoneMuted = media?.IsMicrophoneMuted ?? false;
        _cameraPaused = media?.IsCameraPaused ?? false;
        NotifyAll();
    }

    /// <summary>Réévalue la disponibilité du bouton d'appel (à appeler quand la conversation affichée change).</summary>
    public void RefreshAvailability()
    {
        OnPropertyChanged(nameof(CanStartCall));
        NotifyCommands();
    }

    /// <summary>Affiche un état média (ex. « connecté ») sans boîte de dialogue ; ignoré hors appel actif.</summary>
    public void ReportMediaStatus(string message)
    {
        string text = Truncate(message);
        _dispatcher.Post(() =>
        {
            if (_disposed || !IsCallActive)
            {
                return;
            }

            StatusText = text;
        });
    }

    /// <summary>Affiche une erreur média sans boîte de dialogue.</summary>
    public void ReportMediaError(string message)
    {
        string text = Truncate(message);
        _dispatcher.Post(() =>
        {
            if (!_disposed)
            {
                ErrorText = text;
            }
        });
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _calls.SessionChanged -= OnSessionChanged;
        if (ToggleMicrophoneCommand is IDisposable microphone)
        {
            microphone.Dispose();
        }

        if (ToggleCameraCommand is IDisposable camera)
        {
            camera.Dispose();
        }
    }

    private void OnSessionChanged(object? sender, CallSessionChangedEventArgs e)
    {
        CallSessionSnapshot? session = e.Session;
        _dispatcher.Post(() =>
        {
            if (!_disposed)
            {
                ApplySession(session);
            }
        });
    }

    private void ApplySession(CallSessionSnapshot? session)
    {
        _session = session;
        switch (session?.State)
        {
            case CallSessionState.OutgoingRinging:
                ErrorText = null;
                StatusText = "Appel en cours : en attente de réponse du correspondant…";
                break;
            case CallSessionState.IncomingRinging:
                ErrorText = null;
                StatusText = session.Media == CallMediaKind.Video ? "Appel vidéo entrant" : "Appel audio entrant";
                break;
            case CallSessionState.Accepted:
                StatusText = "Appel accepté : connexion média en cours…";
                break;
            case CallSessionState.Rejected:
                ResetMediaFlags();
                StatusText = "Appel refusé.";
                break;
            case CallSessionState.Busy:
                ResetMediaFlags();
                StatusText = "Le correspondant est occupé.";
                break;
            case CallSessionState.Ended:
                ResetMediaFlags();
                StatusText = "Appel terminé.";
                break;
            default:
                ResetMediaFlags();
                StatusText = null;
                break;
        }

        NotifyAll();
    }

    private void StartCall(CallMediaKind media)
    {
        string? peerId = _selectedPrivatePeerId();
        if (peerId is null)
        {
            ErrorText = "Sélectionnez d'abord une conversation privée pour appeler un contact.";
            return;
        }

        // L'erreur précédente est effacée par ApplySession dès que la nouvelle session sonne.
        _ = _calls.StartCall(peerId, media);
    }

    private async Task ToggleMicrophoneAsync(CancellationToken cancellationToken)
    {
        ICallMediaControls? media = _media;
        if (media is null)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        await media.SetMicrophoneMutedAsync(!_microphoneMuted).ConfigureAwait(true);
        _microphoneMuted = media.IsMicrophoneMuted;
        OnPropertyChanged(nameof(IsMicrophoneMuted));
        OnPropertyChanged(nameof(MicrophoneToolTip));
    }

    private async Task ToggleCameraAsync(CancellationToken cancellationToken)
    {
        ICallMediaControls? media = _media;
        if (media is null)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        await media.SetCameraPausedAsync(!_cameraPaused).ConfigureAwait(true);
        _cameraPaused = media.IsCameraPaused;
        OnPropertyChanged(nameof(IsCameraPaused));
        OnPropertyChanged(nameof(CameraToolTip));
    }

    private void DismissNotice()
    {
        if (IsCallActive)
        {
            ErrorText = null;
            return;
        }

        ErrorText = null;
        StatusText = null;
    }

    private void ResetMediaFlags()
    {
        _microphoneMuted = false;
        _cameraPaused = false;
    }

    /// <summary>Convertit une exception de commande en message lisible, sans détail technique inattendu.</summary>
    private void SetError(Exception exception)
    {
        ErrorText = exception is InvalidOperationException or IOException or ArgumentException
            ? Truncate(exception.Message)
            : "L'opération d'appel a échoué. Réessayez.";
    }

    private void NotifyAll()
    {
        OnPropertyChanged(nameof(IsCallActive));
        OnPropertyChanged(nameof(IsIncomingRinging));
        OnPropertyChanged(nameof(IsInCall));
        OnPropertyChanged(nameof(IsVideoCall));
        OnPropertyChanged(nameof(CanStartCall));
        OnPropertyChanged(nameof(IsBannerVisible));
        OnPropertyChanged(nameof(IsMicrophoneMuted));
        OnPropertyChanged(nameof(IsCameraPaused));
        OnPropertyChanged(nameof(MicrophoneToolTip));
        OnPropertyChanged(nameof(CameraToolTip));
        NotifyCommands();
    }

    private void NotifyCommands()
    {
        (StartAudioCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (StartVideoCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (AcceptCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (RejectCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (HangupCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (ToggleMicrophoneCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
        (ToggleCameraCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
    }

    private static string Truncate(string? message)
    {
        string text = message ?? string.Empty;
        return text.Length <= MaxNoticeLength ? text : text[..MaxNoticeLength];
    }
}
