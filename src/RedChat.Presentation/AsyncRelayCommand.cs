using System.Diagnostics;
using System.Windows.Input;

namespace RedChat.Presentation;

/// <summary>Commande asynchrone ré-entrance protégée, annulable et sans exception non observée.</summary>
public sealed class AsyncRelayCommand : ICommand, IDisposable
{
    private readonly Func<object?, CancellationToken, Task> _execute;
    private readonly Predicate<object?>? _canExecute;
    private readonly Action<Exception>? _onError;
    private readonly object _gate = new();
    private CancellationTokenSource? _executionCts;
    private int _isRunning;
    private int _disposed;

    /// <summary>Crée une commande asynchrone.</summary>
    public AsyncRelayCommand(
        Func<CancellationToken, Task> execute,
        Action<Exception>? onError = null,
        Func<bool>? canExecute = null)
        : this((_, token) => execute(token), onError, canExecute is null ? null : _ => canExecute())
    {
        ArgumentNullException.ThrowIfNull(execute);
    }

    /// <summary>Crée une commande asynchrone avec paramètre.</summary>
    public AsyncRelayCommand(
        Func<object?, CancellationToken, Task> execute,
        Action<Exception>? onError = null,
        Predicate<object?>? canExecute = null)
    {
        ArgumentNullException.ThrowIfNull(execute);
        _execute = execute;
        _onError = onError;
        _canExecute = canExecute;
    }

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged;

    /// <summary>Vrai pendant l'exécution de la commande.</summary>
    public bool IsRunning => Volatile.Read(ref _isRunning) == 1;

    /// <inheritdoc />
    public bool CanExecute(object? parameter)
    {
        if (Volatile.Read(ref _disposed) != 0 || IsRunning)
        {
            return false;
        }

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
    // audit-ok: ICommand impose void Execute ; ExecuteAsync capture toutes les exceptions avant le retour au dispatcher.
    public async void Execute(object? parameter) => await ExecuteAsync(parameter).ConfigureAwait(true);

    /// <summary>Exécute la commande et expose une Task utilisable directement dans les tests.</summary>
    public async Task ExecuteAsync(object? parameter = null)
    {
        if (!CanExecute(parameter) || Interlocked.CompareExchange(ref _isRunning, 1, 0) != 0)
        {
            return;
        }

        CancellationTokenSource cts = new();
        lock (_gate)
        {
            _executionCts = cts;
        }

        try
        {
            RaiseCanExecuteChanged();
            await _execute(parameter, cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Annulation demandée par l'utilisateur : ce n'est pas une erreur à afficher.
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }
        finally
        {
            lock (_gate)
            {
                _executionCts = null;
            }

            cts.Dispose();
            Volatile.Write(ref _isRunning, 0);
            RaiseCanExecuteChanged();
        }
    }

    /// <summary>Demande l'annulation de l'opération en cours.</summary>
    public void Cancel()
    {
        lock (_gate)
        {
            try
            {
                _executionCts?.Cancel();
            }
            catch (Exception ex)
            {
                ReportError(ex);
            }
        }
    }

    /// <summary>Demande à la vue de réévaluer CanExecute.</summary>
    public void NotifyCanExecuteChanged() => RaiseCanExecuteChanged();

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Cancel();
            RaiseCanExecuteChanged();
        }
    }

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
            Trace.TraceError("Le gestionnaire d'erreur d'une commande asynchrone a échoué ({0}).", callbackException.GetType().Name);
        }
    }
}
