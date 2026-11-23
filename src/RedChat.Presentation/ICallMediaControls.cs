namespace RedChat.Presentation;

/// <summary>
/// Commandes média d'un appel en cours (micro, caméra), implémentées par le moteur WebRTC côté WPF.
/// POURQUOI ici : le ViewModel d'appel ne doit pas connaître SIPSorcery ni WPF, pour rester testable hors Windows.
/// </summary>
public interface ICallMediaControls
{
    /// <summary>Vrai si le microphone est (ou sera, dès le démarrage des périphériques) coupé.</summary>
    bool IsMicrophoneMuted { get; }

    /// <summary>Vrai si la caméra est (ou sera) en pause.</summary>
    bool IsCameraPaused { get; }

    /// <summary>Coupe ou réactive le microphone.</summary>
    Task SetMicrophoneMutedAsync(bool muted);

    /// <summary>Met la caméra en pause ou la reprend.</summary>
    Task SetCameraPausedAsync(bool paused);
}
