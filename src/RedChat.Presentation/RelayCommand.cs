using System.Diagnostics;
using System.Windows.Input;

namespace RedChat.Presentation;

/// <summary>Commande synchrone ICommand, indépendante de WPF, qui capture les erreurs dans le ViewModel.</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Predicate<object?>? _canExecute;
    private readonly Action<Exception>? _onError;

    /// <summary>Crée une commande synchrone.</summary>
    public RelayCommand(Action execute, Action<Exception>? onError = null, Func<bool>? canExecute = null)
        : this(_ => execute(), onError, canExecute is null ? null : _ => canExecute())
    {
        ArgumentNullException.ThrowIfNull(execute);
    }

    /// <summary>Crée une commande synchrone avec paramètre.</summary>
    public RelayCommand(Action<object?> execute, Action<Exception>? onError = null, Predicate<object?>? canExecute = null)
    {
        ArgumentNullException.ThrowIfNull(execute);
        _execute = execute;
        _onError = onError;
        _canExecute = canExecute;
    }

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged;

    /// <inheritdoc />
    public bool CanExecute(object? parameter)
    {
        try
        {
            return _canExecute?.Invoke(parameter) ?? true;
        }
        catch (Exception ex)
        {
            ReportError(ex);
            return false;
        }
    }

    /// <inheritdoc />
    public void Execute(object? parameter)
    {
        try
        {
            if (CanExecute(parameter))
            {
                _execute(parameter);
            }
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }
    }

    /// <summary>Demande à la vue de réévaluer CanExecute.</summary>
    public void NotifyCanExecuteChanged() => RaiseCanExecuteChanged();

    private void RaiseCanExecuteChanged()
    {
        EventHandler? handlers = CanExecuteChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler handler in handlers.GetInvocationList().Cast<EventHandler>())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                ReportError(ex);
            }
        }
    }

    private void ReportError(Exception exception)
    {
        try
        {
            _onError?.Invoke(exception);
        }
        catch (Exception callbackException)
        {
            Trace.TraceError("Le gestionnaire d'erreur d'une commande a échoué ({0}).", callbackException.GetType().Name);
        }
    }
}
