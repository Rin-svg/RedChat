namespace RedChat.Core;

/// <summary>Instantané durable des conversations marquées comme favorites sur cet appareil.</summary>
public sealed record FavoriteConversationsSnapshot
{
    /// <summary>Version du format de stockage.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Version du document.</summary>
    public int Version { get; init; } = CurrentVersion;

    /// <summary>Identifiants de conversations privées (PeerId) ou de groupes (GUID).</summary>
    public IReadOnlyList<string> ConversationIds { get; init; } = Array.Empty<string>();
}

/// <summary>Collection thread-safe des favoris locaux. Les favoris ne sont jamais transmis au réseau.</summary>
public sealed class FavoriteConversations
{
    /// <summary>Nombre maximal de favoris persistés.</summary>
    public const int MaxFavorites = 1_000;

    private readonly object _gate = new();
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);

    /// <summary>Levé après chaque ajout ou retrait réel.</summary>
    public event EventHandler? Changed;

    /// <summary>Vérifie si une conversation est favorite.</summary>
    public bool Contains(string conversationId)
    {
        ValidateId(conversationId);
        lock (_gate)
        {
            return _ids.Contains(conversationId);
        }
    }

    /// <summary>Retourne un instantané trié des identifiants favoris.</summary>
    public IReadOnlyList<string> GetAll()
    {
        lock (_gate)
        {
            return _ids.OrderBy(id => id, StringComparer.Ordinal).ToArray();
        }
    }

    /// <summary>Ajoute un favori ; renvoie false s'il était déjà présent.</summary>
    public bool Add(string conversationId)
    {
        ValidateId(conversationId);
        lock (_gate)
        {
            if (_ids.Contains(conversationId))
            {
                return false;
            }

            if (_ids.Count >= MaxFavorites)
            {
                throw new InvalidOperationException("La liste des favoris est pleine.");
            }

            _ids.Add(conversationId);
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Retire un favori ; renvoie false s'il était absent.</summary>
    public bool Remove(string conversationId)
    {
        ValidateId(conversationId);
        bool removed;
        lock (_gate)
        {
            removed = _ids.Remove(conversationId);
        }

        if (removed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        return removed;
    }

    /// <summary>Ajoute ou retire un favori et renvoie son nouvel état.</summary>
    public bool Toggle(string conversationId)
    {
        ValidateId(conversationId);
        bool added;
        lock (_gate)
        {
            if (_ids.Remove(conversationId))
            {
                added = false;
            }
            else
            {
                if (_ids.Count >= MaxFavorites)
                {
                    throw new InvalidOperationException("La liste des favoris est pleine.");
                }

                _ids.Add(conversationId);
                added = true;
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return added;
    }

    /// <summary>Crée l'instantané destiné au stockage local.</summary>
    public FavoriteConversationsSnapshot Export()
        => new() { ConversationIds = GetAll() };

    /// <summary>Restaure un instantané dans une collection vide. Toute donnée incohérente est refusée.</summary>
    public void Import(FavoriteConversationsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Version != FavoriteConversationsSnapshot.CurrentVersion
            || snapshot.ConversationIds is null
            || snapshot.ConversationIds.Count > MaxFavorites)
        {
            throw new InvalidDataException("Instantané de favoris invalide.");
        }

        HashSet<string> loaded = new(StringComparer.Ordinal);
        foreach (string? id in snapshot.ConversationIds)
        {
            if (!IsValidId(id) || !loaded.Add(id!))
            {
                throw new InvalidDataException("Identifiant favori invalide ou dupliqué.");
            }
        }

        lock (_gate)
        {
            if (_ids.Count != 0)
            {
                throw new InvalidOperationException("La collection de favoris n'est pas vide.");
            }

            foreach (string id in loaded)
            {
                _ids.Add(id);
            }
        }
    }

    private static void ValidateId(string conversationId)
    {
        if (!IsValidId(conversationId))
        {
            throw new ArgumentException("Identifiant de conversation invalide.", nameof(conversationId));
        }
    }

    private static bool IsValidId(string? id)
        => id is not null && (InputValidator.IsValidPeerId(id) || Guid.TryParseExact(id, "D", out _));
}
