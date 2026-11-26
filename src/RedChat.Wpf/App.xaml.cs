using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Data;
using MessageBox = System.Windows.MessageBox;
using RedChat.Core;
using RedChat.Infrastructure;
using RedChat.Presentation;

namespace RedChat.Wpf;

/// <summary>Composition root WPF : protège le profil, restaure les données, puis démarre le réseau.</summary>
public partial class App : System.Windows.Application, IDisposable, IAsyncDisposable
{
    private Mutex? _profileMutex;
    private bool _ownsMutex;
    private LocalIdentity? _identity;
    private LanNode? _node;
    private ChatPersistence? _persistence;
    private int _shutdownStarted;

    // audit-ok: gestionnaire de l’événement Startup WPF ; toutes les exceptions sont capturées dans ce gestionnaire.
    private async void OnStartup(object sender, StartupEventArgs e)
    {
        ConfigureGlobalExceptionHandlers();
        try
        {
            string profile = ReadProfileName(e.Args);
            string mutexName = "Local\\RedChat.Profile." + profile.ToUpperInvariant();
            _profileMutex = new Mutex(initiallyOwned: false, name: mutexName);
            try
            {
                _ownsMutex = _profileMutex.WaitOne(millisecondsTimeout: 0);
            }
            catch (AbandonedMutexException)
            {
                // ROBUSTESSE : l'instance précédente a planté ; Windows nous transmet la propriété du mutex abandonné.
                _ownsMutex = true;
            }

            if (!_ownsMutex)
            {
                _profileMutex.Dispose();
                _profileMutex = null;
                MessageBox.Show("Ce profil RedChat est déjà ouvert. Fermez l'autre fenêtre ou choisissez un autre profil.",
                    "RedChat", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown(0);
                return;
            }

            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RedChat", "Profiles");
            ProfileStore store = new(root, profile, new DpapiSecretProtector());

            // Une identité illisible ne doit JAMAIS être remplacée : les contacts reconnaîtraient un nouvel utilisateur.
            _identity = store.LoadOrCreateIdentity();
            _node = new LanNode(_identity, new LanNodeOptions { AttachmentDirectory = Path.Combine(root, profile, "Attachments", "Incoming") });
            FavoriteConversations favorites = new();
            _persistence = new ChatPersistence(_node.Chat, _node.Contacts, store, favorites: favorites);
            PersonalProfileViewModel personalProfile = new(store, _identity.PeerId);
            _node.SetLocalProfile(personalProfile.CurrentProfile);
            personalProfile.ProfileSaved += (_, _) => _node?.SetLocalProfile(personalProfile.CurrentProfile);

            // Ordre impératif : restaurer AVANT d'ouvrir les sockets réseau.
            _persistence.Start();
            _node.Start();

            MainViewModel viewModel = new(_node, _persistence, new WpfUiDispatcher(Dispatcher), favorites, store);
            MainWindow window = new(viewModel, personalProfile, ShutdownServicesAsync);
            MainWindow = window;
            window.Show();
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(ex.Message, "RedChat — paramètres invalides", MessageBoxButton.OK, MessageBoxImage.Warning);
            await ShutdownServicesAsync();
            Shutdown(2);
        }
        catch (ProfileStoreException ex)
        {
            AppLogger.Write(ex);
            MessageBox.Show(ex.Message, "RedChat — profil inaccessible", MessageBoxButton.OK, MessageBoxImage.Error);
            await ShutdownServicesAsync();
            Shutdown(1);
        }
        catch (IOException ex)
        {
            AppLogger.Write(ex);
            MessageBox.Show(
                "RedChat n'a pas pu lire ou enregistrer le profil. Aucune connexion réseau n'a été démarrée afin de protéger votre historique. " +
                "Fermez les programmes qui pourraient verrouiller les fichiers, puis relancez l'application.",
                "RedChat — stockage indisponible", MessageBoxButton.OK, MessageBoxImage.Error);
            await ShutdownServicesAsync();
            Shutdown(1);
        }
        catch (Exception ex)
        {
            AppLogger.Write(ex);
            MessageBox.Show("RedChat n'a pas pu démarrer. Consultez le journal local pour obtenir le détail de l'incident.",
                "RedChat — erreur de démarrage", MessageBoxButton.OK, MessageBoxImage.Error);
            await ShutdownServicesAsync();
            Shutdown(1);
        }
    }

    /// <summary>Ferme dans l'ordre : dernière sauvegarde, réseau, identité et verrou du profil.</summary>
    public async Task ShutdownServicesAsync()
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0)
        {
            return;
        }

        try
        {
            await Task.Run(() => _persistence?.Dispose()).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if (_node is not null)
                {
                    await _node.DisposeAsync().ConfigureAwait(false);
                    _node = null;
                }
            }
            finally
            {
                _identity?.Dispose();
                _identity = null;
                ReleaseProfileMutex();
            }
        }
    }


    /// <inheritdoc />
    public void Dispose()
    {
        Task shutdown = ShutdownServicesAsync();
        if (Dispatcher.CheckAccess() && !shutdown.IsCompleted)
        {
            // ROBUSTESSE : la pompe imbriquée laisse le dispatcher traiter le ReleaseMutex
            // demandé par la continuation asynchrone, au lieu de bloquer le thread propriétaire.
            DispatcherFrame frame = new();
            _ = shutdown.ContinueWith(
                completedTask =>
                {
                    try
                    {
                        _ = Dispatcher.BeginInvoke(new Action(() => frame.Continue = false));
                    }
                    catch (InvalidOperationException)
                    {
                        frame.Continue = false;
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
        }

        shutdown.GetAwaiter().GetResult(); // audit-ok: DispatcherFrame maintient le dispatcher actif jusqu'à la fin du nettoyage.
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await ShutdownServicesAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Libère le mutex sur le thread qui l'a acquis : un Mutex Windows est lié à son thread propriétaire.
    /// Le chemin asynchrone revient ici depuis un thread de pool, donc le dispatcher WPF est utilisé.
    /// </summary>
    private void ReleaseProfileMutex()
    {
        Mutex? mutex = _profileMutex;
        if (mutex is null)
        {
            return;
        }

        try
        {
            if (_ownsMutex)
            {
                if (Dispatcher.CheckAccess())
                {
                    mutex.ReleaseMutex();
                }
                else
                {
                    Dispatcher.Invoke(mutex.ReleaseMutex);
                }
            }
        }
        finally
        {
            _ownsMutex = false;
            _profileMutex = null;
            mutex.Dispose();
        }
    }

    private static string ReadProfileName(string[] args)
    {
        string? profile = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] != "--profile")
            {
                throw new ArgumentException("Option inconnue. Utilisation : RedChat.exe [--profile nom].");
            }

            if (profile is not null || i + 1 >= args.Length)
            {
                throw new ArgumentException("L'option --profile doit apparaître une seule fois et être suivie d'un nom.");
            }

            profile = args[++i];
        }

        string result = profile ?? "default";
        if (!InputValidator.IsValidProfileName(result))
        {
            throw new ArgumentException("Nom de profil invalide : utilisez 1 à 30 lettres, chiffres, tirets ou caractères soulignés.");
        }

        return result;
    }

    private void ConfigureGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            AppLogger.Write(e.Exception);
            MessageBox.Show("Une erreur inattendue est survenue. RedChat va se fermer pour protéger vos données.",
                "RedChat — erreur inattendue", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = false;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception exception)
            {
                AppLogger.Write(exception);
            }
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLogger.Write(e.Exception);
            e.SetObserved();
        };
    }
}

/// <summary>Protection des secrets au repos par DPAPI, liée au compte Windows courant.</summary>
internal sealed class DpapiSecretProtector : ISecretProtector
{
    public byte[] Protect(byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        return ProtectedData.Protect(plaintext, optionalEntropy: null, DataProtectionScope.CurrentUser);
    }

    public byte[] Unprotect(byte[] protectedData)
    {
        ArgumentNullException.ThrowIfNull(protectedData);
        return ProtectedData.Unprotect(protectedData, optionalEntropy: null, DataProtectionScope.CurrentUser);
    }
}

/// <summary>Adaptateur WPF vers le dispatcher abstrait utilisé par les ViewModels.</summary>
internal sealed class WpfUiDispatcher : IUiDispatcher
{
    private readonly Dispatcher _dispatcher;

    public WpfUiDispatcher(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        try
        {
            _ = _dispatcher.BeginInvoke(action, DispatcherPriority.DataBind);
        }
        catch (InvalidOperationException) when (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            // La fenêtre est en cours de fermeture : il n'y a plus de vue à mettre à jour.
        }
    }
}

/// <summary>Journal local minimal, borné et sans contenu de message ni secret.</summary>
internal static class AppLogger
{
    private static readonly object Gate = new();
    private const long MaxLogBytes = 2 * 1024 * 1024;

    public static void Write(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        try
        {
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RedChat", "Logs");
            _ = Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "redchat.log");
            lock (Gate)
            {
                FileInfo info = new(path);
                if (info.Exists && info.Length > MaxLogBytes)
                {
                    string backup = path + ".1";
                    File.Move(path, backup, overwrite: true);
                }

                Exception? current = exception;
                while (current is not null)
                {
                    string line = $"{DateTimeOffset.UtcNow:O} {current.GetType().Name}: {current.Message}{Environment.NewLine}{current.StackTrace}{Environment.NewLine}";
                    File.AppendAllText(path, line);
                    current = current.InnerException;
                }
            }
        }
        catch (IOException)
        {
            // Un journal indisponible ne doit jamais provoquer un second crash.
        }
        catch (UnauthorizedAccessException)
        {
            // Un refus d'accès au dossier de logs ne bloque pas la fermeture de l'application.
        }
    }
}


/// <summary>Convertit une date Unix millisecondes en heure locale courte pour les bulles de message.</summary>
public sealed class UnixTimeConverter : IValueConverter
{
    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is long milliseconds && milliseconds > 0)
        {
            try
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).ToLocalTime().ToString("HH:mm", culture);
            }
            catch (ArgumentOutOfRangeException)
            {
                return string.Empty;
            }
        }

        return string.Empty;
    }

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("La conversion inverse n'est pas prise en charge.");
}
