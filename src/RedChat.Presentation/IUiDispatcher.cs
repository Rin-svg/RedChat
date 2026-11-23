namespace RedChat.Presentation;

/// <summary>Point d'entrée minimal pour revenir sur le thread de l'interface sans référencer WPF.</summary>
public interface IUiDispatcher
{
    /// <summary>Planifie une action sur le thread propriétaire de l'interface.</summary>
    void Post(Action action);
}

/// <summary>Dispatcher synchrone réservé aux tests et aux scénarios sans interface graphique.</summary>
public sealed class ImmediateUiDispatcher : IUiDispatcher
{
    /// <inheritdoc />
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        action();
    }
}
