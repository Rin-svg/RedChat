using RedChat.Core;

namespace RedChat.Infrastructure;

/// <summary>
/// Contrat minimal de pilotage d'un appel privé, consommé par les ViewModels.
/// POURQUOI une interface : <see cref="CallSignalingService"/> dépend d'un transport TCP réel ;
/// avec ce contrat, la logique d'interface d'appel se teste avec un faux contrôleur, sans réseau.
/// </summary>
public interface ICallController
{
    /// <summary>Session actuellement suivie, ou null.</summary>
    CallSessionSnapshot? ActiveSession { get; }

    /// <summary>Se déclenche à chaque création ou changement d'état de session (thread quelconque).</summary>
    event EventHandler<CallSessionChangedEventArgs>? SessionChanged;

    /// <summary>Envoie une invitation d'appel à un pair connecté et renvoie l'identifiant de session.</summary>
    string StartCall(string peerId, CallMediaKind media);

    /// <summary>Accepte l'invitation entrante.</summary>
    void AcceptIncoming();

    /// <summary>Refuse l'invitation entrante.</summary>
    void RejectIncoming();

    /// <summary>Termine ou annule la session en cours.</summary>
    void EndCall();
}
