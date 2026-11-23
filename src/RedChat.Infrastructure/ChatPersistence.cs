using System.Security.Cryptography;
using RedChat.Core;

namespace RedChat.Infrastructure;

/// <summary>Données de l'événement <see cref="ChatPersistence.SaveFailed"/>.</summary>
public sealed class SaveFailedEventArgs : EventArgs
{
    /// <summary>Crée les données d'événement.</summary>
    public SaveFailedEventArgs(string message) => Message = message;

    /// <summary>Message affichable à l'utilisateur : ce qui s'est passé et ce qui va se passer ensuite (sans chemin ni détail technique).</summary>
    public string Message { get; }
}

/// <summary>
/// Relie <see cref="ChatService"/> (et le carnet de contacts) à un <see cref="ProfileStore"/> :
/// restaure l'état au démarrage, puis l'enregistre automatiquement après chaque changement.
/// <list type="bullet">
/// <item><b>Différé</b> : les changements d'une même rafale (100 messages reçus d'un coup) ne produisent qu'UNE écriture, ce qui évite d'user le disque.</item>
/// <item><b>Sans perte</b> : si l'écriture échoue (disque plein, fichier verrouillé), l'état reste marqué « à enregistrer » et on réessaie
/// automatiquement avec un délai croissant ; l'interface est prévenue par <see cref="SaveFailed"/>.</item>
/// <item><b>Sans plantage</b> : aucune exception d'écriture ne sort du minuteur (elle terminerait l'application).</item>
/// <item><b>À la fermeture</b> : <see cref="Dispose"/> enregistre une dernière fois.</item>
/// </list>
/// Les abonnés aux événements de cette classe ne doivent pas lever d'exception.
/// </summary>
public sealed class ChatPersistence : IDisposable
{
    /// <summary>Délai par défaut entre le premier changement d'une rafale et l'écriture.</summary>
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromSeconds(1);

    /// <summary>Plafond du délai entre deux tentatives après un échec.</summary>
    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    private readonly ChatService _chat;
    private readonly ContactBook? _contacts;
    private readonly FavoriteConversations? _favorites;
    private readonly ProfileStore _store;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _debounce;
    private readonly object _gate = new(); // un seul enregistrement à la fois
    private ITimer? _timer;
    private int _chatDirty;
    private int _contactsDirty;
    private int _favoritesDirty;
    private int _timerArmed;
    private int _failures;
    private int _started;
    private int _disposed;

    /// <summary>Crée le lien (sans rien charger : appeler <see cref="Start"/>).</summary>
    public ChatPersistence(ChatService chat, ContactBook? contacts, ProfileStore store, TimeProvider? clock = null, TimeSpan? debounce = null, FavoriteConversations? favorites = null)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(store);
        _chat = chat;
        _contacts = contacts;
        _favorites = favorites;
        _store = store;
        _clock = clock ?? TimeProvider.System;
        _debounce = debounce ?? DefaultDebounce;
        if (_debounce < TimeSpan.Zero || _debounce > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(debounce), "Délai attendu entre 0 et 60 secondes.");
        }
    }

    /// <summary>Levé quand un enregistrement échoue ; une nouvelle tentative a lieu automatiquement.</summary>
    public event EventHandler<SaveFailedEventArgs>? SaveFailed;

    /// <summary>Levé quand un enregistrement réussit APRÈS un ou plusieurs échecs (pour retirer l'avertissement de l'interface).</summary>
    public event EventHandler? SaveRecovered;

    /// <summary>Message à montrer au démarrage si un fichier a été restauré ou mis de côté (jamais silencieux) ; <c>null</c> sinon.</summary>
    public string? StartupNotice { get; private set; }

    /// <summary>Vrai tant qu'il reste des changements non enregistrés.</summary>
    public bool HasUnsavedChanges => Volatile.Read(ref _chatDirty) == 1 || Volatile.Read(ref _contactsDirty) == 1 || Volatile.Read(ref _favoritesDirty) == 1;

    /// <summary>
    /// Restaure l'état enregistré puis démarre la sauvegarde automatique. À appeler UNE fois, avant toute connexion réseau.
    /// Un historique illisible ou incohérent est mis de côté (jamais supprimé) et l'application démarre à vide avec un avertissement.
    /// </summary>
    /// <exception cref="IOException">Fichier temporairement inaccessible : ne PAS démarrer à vide (on écraserait un historique sain). Prévenir l'utilisateur, puis recommencer avec de NOUVEAUX objets (service, carnet, persistance).</exception>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            throw new InvalidOperationException("Déjà démarré.");
        }

        lock (_gate)
        {
            ChatSnapshot? history = _store.Load();
            if (history is not null)
            {
                try
                {
                    _chat.ImportState(history);
                }
                catch (InvalidDataException)
                {
                    _store.QuarantineHistory();
                }
            }

            if (_contacts is not null)
            {
                ContactsSnapshot? saved = _store.LoadContacts();
                if (saved is not null)
                {
                    try
                    {
                        _contacts.Import(saved);
                    }
                    catch (InvalidDataException)
                    {
                        _store.QuarantineContacts();
                    }
                }
            }

            if (_favorites is not null)
            {
                FavoriteConversationsSnapshot? savedFavorites = _store.LoadFavorites();
                if (savedFavorites is not null)
                {
                    try
                    {
                        _favorites.Import(savedFavorites);
                    }
                    catch (InvalidDataException)
                    {
                        _store.QuarantineFavorites();
                    }
                }
            }
        }

        StartupNotice = _store.RecoveryNotice;
        _timer = _clock.CreateTimer(OnTimer, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _chat.StateChanged += OnChatChanged;
        if (_contacts is not null)
        {
            _contacts.Changed += OnContactsChanged;
        }

        if (_favorites is not null)
        {
            _favorites.Changed += OnFavoritesChanged;
        }
    }

    /// <summary>Enregistre tout de suite ce qui est en attente (sans effet s'il n'y a rien). Ne lève pas d'exception d'écriture : voir <see cref="SaveFailed"/>.</summary>
    public void Flush()
    {
        string? failure = null;
        bool recovered = false;
        lock (_gate)
        {
            bool chat = Interlocked.Exchange(ref _chatDirty, 0) == 1;
            bool contacts = Interlocked.Exchange(ref _contactsDirty, 0) == 1;
            bool favorites = Interlocked.Exchange(ref _favoritesDirty, 0) == 1;
            if (!chat && !contacts && !favorites)
            {
                return;
            }

            // Chaque écriture a son propre essai : l'échec de l'une ne doit pas faire croire que l'autre a échoué (ni l'inverse).
            // Les DEUX écritures sont toujours tentées : si on sautait la seconde après l'échec de la première, son drapeau
            // « à enregistrer » (déjà remis à zéro plus haut) serait perdu et ses changements ne seraient jamais écrits.
            string? chatFailure = chat ? TrySave(() => _store.Save(_chat.ExportState()), ref _chatDirty) : null;
            string? contactsFailure = contacts && _contacts is not null ? TrySave(() => _store.SaveContacts(_contacts.Export()), ref _contactsDirty) : null;
            string? favoritesFailure = favorites && _favorites is not null ? TrySave(() => _store.SaveFavorites(_favorites.Export()), ref _favoritesDirty) : null;
            failure = chatFailure ?? contactsFailure ?? favoritesFailure;

            if (failure is null)
            {
                recovered = Interlocked.Exchange(ref _failures, 0) > 0;
            }
            else
            {
                int n = Interlocked.Increment(ref _failures);
                Arm(RetryDelay(n));
            }
        }

        // Événements levés HORS du verrou : un abonné peut rappeler Flush() sans interblocage.
        if (failure is not null)
        {
            SaveFailed?.Invoke(this, new SaveFailedEventArgs(failure));
        }
        else if (recovered)
        {
            SaveRecovered?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Arrête la sauvegarde automatique et enregistre une dernière fois.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _chat.StateChanged -= OnChatChanged;
        if (_contacts is not null)
        {
            _contacts.Changed -= OnContactsChanged;
        }

        if (_favorites is not null)
        {
            _favorites.Changed -= OnFavoritesChanged;
        }

        _timer?.Dispose();
        if (Volatile.Read(ref _started) == 1)
        {
            Flush();
        }
    }

    private static string? TrySave(Action save, ref int dirtyFlag)
    {
        try
        {
            save();
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ProfileStoreException or CryptographicException)
        {
            // L'état reste « à enregistrer » : rien n'est perdu, la prochaine tentative réécrira tout.
            Volatile.Write(ref dirtyFlag, 1);
            return ex switch
            {
                ProfileStoreException => ex.Message,
                UnauthorizedAccessException => "Impossible d'enregistrer les données locales : droits insuffisants sur le dossier du profil. Nouvelle tentative automatique.",
                CryptographicException => "Impossible de protéger les données avant l'enregistrement. Nouvelle tentative automatique.",
                _ => "Impossible d'enregistrer les données locales (disque plein, ou fichier utilisé par un autre programme). Nouvelle tentative automatique.",
            };
        }
    }

    private TimeSpan RetryDelay(int failures)
    {
        // Délai doublé à chaque échec, plafonné : on ne martèle pas un disque plein, mais on ne renonce jamais.
        double factor = Math.Pow(2, Math.Min(failures, 10));
        TimeSpan delay = TimeSpan.FromTicks((long)Math.Min(_debounce.Ticks * factor, MaxRetryDelay.Ticks));
        return delay < _debounce ? _debounce : delay;
    }

    private void OnChatChanged(object? sender, EventArgs e)
    {
        Volatile.Write(ref _chatDirty, 1);
        Arm(_debounce);
    }

    private void OnContactsChanged(object? sender, EventArgs e)
    {
        Volatile.Write(ref _contactsDirty, 1);
        Arm(_debounce);
    }

    private void OnFavoritesChanged(object? sender, EventArgs e)
    {
        Volatile.Write(ref _favoritesDirty, 1);
        Arm(_debounce);
    }

    // Un seul déclenchement en attente à la fois : les changements suivants de la même rafale sont absorbés.
    private void Arm(TimeSpan delay)
    {
        if (Volatile.Read(ref _disposed) == 1 || Interlocked.CompareExchange(ref _timerArmed, 1, 0) != 0)
        {
            return;
        }

        _ = _timer?.Change(delay, Timeout.InfiniteTimeSpan);
    }

    private void OnTimer(object? state)
    {
        _ = Interlocked.Exchange(ref _timerArmed, 0);
        Flush();
    }
}
