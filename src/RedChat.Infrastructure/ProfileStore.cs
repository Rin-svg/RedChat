using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using RedChat.Core;
using RedChat.Infrastructure.Security;

namespace RedChat.Infrastructure;

/// <summary>
/// Erreur de stockage d'un profil. Le message est destiné à l'utilisateur : il dit ce qui s'est passé, sans chemin interne ni détail technique.
/// </summary>
public sealed class ProfileStoreException : Exception
{
    /// <summary>Crée l'exception.</summary>
    public ProfileStoreException()
        : base("Erreur de stockage du profil.")
    {
    }

    /// <summary>Crée l'exception avec un message affichable.</summary>
    public ProfileStoreException(string message)
        : base(message)
    {
    }

    /// <summary>Crée l'exception avec un message affichable et sa cause.</summary>
    public ProfileStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Enveloppe versionnée des statuts reçus stockés localement.</summary>
public sealed class ReceivedStatusesSnapshot
{
    /// <summary>Version du format.</summary>
    public int SchemaVersion { get; init; } = 1;
    /// <summary>Statuts reçus actifs.</summary>
    public List<ReceivedStatusRecord> Items { get; init; } = new();
}

/// <summary>Enregistrement de stockage indépendant des interfaces de collection du modèle réseau.</summary>
public sealed class ReceivedStatusRecord
{
    /// <summary>Identifiant unique.</summary>
    public string Id { get; init; } = string.Empty;
    /// <summary>Identité authentifiée de l'auteur.</summary>
    public string OwnerPeerId { get; init; } = string.Empty;
    /// <summary>Type du contenu.</summary>
    public StatusContentKind ContentKind { get; init; }
    /// <summary>Texte ou légende.</summary>
    public string Text { get; init; } = string.Empty;
    /// <summary>Octets du média éventuel.</summary>
    public byte[]? MediaBytes { get; init; }
    /// <summary>Date de création UTC.</summary>
    public DateTimeOffset CreatedAt { get; init; }
    /// <summary>Date d'expiration UTC.</summary>
    public DateTimeOffset ExpiresAt { get; init; }

    /// <summary>Construit un enregistrement depuis un statut vérifié.</summary>
    public static ReceivedStatusRecord FromStatus(EphemeralStatus status) => new()
    {
        Id = status.Id, OwnerPeerId = status.OwnerPeerId, ContentKind = status.ContentKind,
        Text = status.Text, MediaBytes = status.MediaBytes?.ToArray(), CreatedAt = status.CreatedAt, ExpiresAt = status.ExpiresAt,
    };

    /// <summary>Reconstruit un statut reçu avec audience locale neutre et sans reçus de vues distants.</summary>
    public EphemeralStatus ToStatus() => new()
    {
        Id = Id, OwnerPeerId = OwnerPeerId, ContentKind = ContentKind, Text = Text,
        MediaBytes = MediaBytes?.ToArray(), CreatedAt = CreatedAt, ExpiresAt = ExpiresAt,
        Audience = new StatusAudience(), ViewedByPeerIds = new HashSet<string>(StringComparer.Ordinal),
    };
}

/// <summary>Marqueur durable empêchant la réapparition d'un statut retiré par son auteur.</summary>
public sealed class ReceivedStatusTombstone
{
    /// <summary>Identifiant du statut retiré.</summary>
    public string StatusId { get; init; } = string.Empty;
    /// <summary>Identité authentifiée de l'auteur.</summary>
    public string OwnerPeerId { get; init; } = string.Empty;
    /// <summary>Expiration originelle du statut.</summary>
    public DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>Enveloppe versionnée des marqueurs de suppression reçus.</summary>
public sealed class ReceivedStatusTombstonesSnapshot
{
    /// <summary>Version du format.</summary>
    public int SchemaVersion { get; init; } = 1;
    /// <summary>Marqueurs encore actifs.</summary>
    public List<ReceivedStatusTombstone> Items { get; init; } = new();
}

/// <summary>File persistante des suppressions de statuts en attente de livraison.</summary>
public sealed class PendingStatusDeletesSnapshot
{
    /// <summary>Version du format.</summary>
    public int SchemaVersion { get; init; } = 1;
    /// <summary>Suppressions encore pertinentes.</summary>
    public List<PendingStatusDeleteRecord> Items { get; init; } = new();
}

/// <summary>Retrait d'un statut à transmettre à un contact après reconnexion.</summary>
public sealed class PendingStatusDeleteRecord
{
    /// <summary>Identifiant de publication.</summary>
    public string StatusId { get; init; } = string.Empty;
    /// <summary>Pair destinataire autorisé.</summary>
    public string RecipientPeerId { get; init; } = string.Empty;
    /// <summary>Expiration originelle du statut ; le tombstone n'est utile que jusque-là.</summary>
    public DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>
/// Stockage durable d'UN profil (<c>--profile nom</c>) : identité, historique et contacts, dans un dossier propre au profil.
/// <list type="bullet">
/// <item>Tout passe par un <see cref="ISecretProtector"/> (DPAPI en production) : rien de sensible n'est écrit en clair.</item>
/// <item>Toute écriture est atomique (<see cref="AtomicFile"/>) et garde la version précédente en « .bak ».</item>
/// <item>Un fichier illisible n'est JAMAIS supprimé ni écrasé : il est mis de côté (« .corrupt-… ») et on retombe sur la copie « .bak ».</item>
/// <item>L'identité n'est jamais régénérée en silence : la remplacer ferait croire à tous les contacts qu'on est un inconnu.</item>
/// </list>
/// <para>SÉCURITÉ : le nom de profil vient de la ligne de commande (non fiable) ; il est validé en liste blanche PUIS résolu avec
/// <see cref="SafePath"/>, pour que le dossier reste obligatoirement sous la racine.</para>
/// </summary>
public sealed class ProfileStore : IMessageStore
{
    /// <summary>Taille maximale du fichier d'identité (une clé PKCS#8 protégée fait quelques centaines d'octets).</summary>
    public const long MaxIdentityFileBytes = 16 * 1024;

    /// <summary>Taille maximale du fichier d'historique (64 Mio protégés). Au-delà : refus clair à l'enregistrement.</summary>
    public const long MaxHistoryFileBytes = 64L * 1024 * 1024;

    /// <summary>Taille maximale du fichier de contacts.</summary>
    public const long MaxContactsFileBytes = 8L * 1024 * 1024;

    /// <summary>Nom du fichier d'identité.</summary>
    public const string IdentityFileName = "identity.dat";

    /// <summary>Nom du fichier d'historique.</summary>
    public const string HistoryFileName = "history.dat";

    /// <summary>Nom du fichier de contacts.</summary>
    public const string ContactsFileName = "contacts.dat";

    /// <summary>Nom du fichier contenant les favoris locaux.</summary>
    public const string FavoritesFileName = "favorites.dat";

    /// <summary>Taille maximale du fichier des favoris protégés.</summary>
    public const long MaxFavoritesFileBytes = 128L * 1024;

    /// <summary>Nom du fichier contenant le profil public personnel.</summary>
    public const string PersonalProfileFileName = "personal-profile.dat";

    /// <summary>Nom du fichier contenant les statuts reçus, protégés par DPAPI.</summary>
    public const string ReceivedStatusesFileName = "received-statuses.dat";

    /// <summary>Nom du fichier des marqueurs de suppression reçus.</summary>
    public const string ReceivedStatusTombstonesFileName = "received-status-tombstones.dat";

    /// <summary>Nom du fichier des suppressions en attente.</summary>
    public const string PendingStatusDeletesFileName = "pending-status-deletes.dat";

    /// <summary>Taille maximale du fichier des statuts reçus protégés.</summary>
    public const long MaxReceivedStatusesFileBytes = 64L * 1024 * 1024;

    /// <summary>Taille maximale des marqueurs de suppression reçus.</summary>
    public const long MaxReceivedStatusTombstonesFileBytes = 128L * 1024;

    /// <summary>Taille maximale du fichier des suppressions en attente.</summary>
    public const long MaxPendingStatusDeletesFileBytes = 256L * 1024;

    /// <summary>Taille maximale du profil personnel protégé (photo incluse).</summary>
    public const long MaxPersonalProfileFileBytes = 768L * 1024;

    // Options mises en cache (les recréer à chaque appel est coûteux). Strictes : champs inconnus refusés, énumérations par nom
    // uniquement, profondeur bornée. Un fichier modifié à la main ou piégé est refusé au lieu d'être interprété de travers.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    private readonly ISecretProtector _protector;
    private readonly TimeProvider _clock;
    private readonly object _ioLock = new();
    private readonly List<string> _notices = new();

    /// <summary>Ouvre (et crée si besoin) le dossier du profil sous <paramref name="rootDirectory"/>.</summary>
    /// <exception cref="ArgumentException">Nom de profil invalide.</exception>
    /// <exception cref="UnsafePathException">Le dossier résolu sortirait de la racine.</exception>
    public ProfileStore(string rootDirectory, string profileName, ISecretProtector protector, TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentNullException.ThrowIfNull(protector);
        if (!InputValidator.IsValidProfileName(profileName))
        {
            throw new ArgumentException("Nom de profil invalide : 1 à 30 caractères parmi lettres, chiffres, « _ » et « - ».", nameof(profileName));
        }

        _protector = protector;
        _clock = clock ?? TimeProvider.System;
        DirectoryPath = SafePath.ResolveInside(Path.GetFullPath(rootDirectory), profileName);
        _ = Directory.CreateDirectory(DirectoryPath);
    }

    /// <summary>Dossier du profil (chemin absolu, sous la racine).</summary>
    public string DirectoryPath { get; }

    /// <summary>
    /// Message à montrer à l'utilisateur si un fichier a dû être restauré depuis sa copie ou mis de côté (jamais silencieux).
    /// <c>null</c> si tout s'est bien passé.
    /// </summary>
    public string? RecoveryNotice
    {
        get
        {
            lock (_ioLock)
            {
                return _notices.Count == 0 ? null : string.Join(" ", _notices);
            }
        }
    }

    // ───────────────────────────── Identité ─────────────────────────────

    /// <summary>
    /// Charge l'identité du profil ou, si le profil est neuf, en crée une et l'enregistre protégée.
    /// </summary>
    /// <exception cref="ProfileStoreException">Le fichier d'identité existe mais est illisible : on NE crée PAS une nouvelle identité à sa place.</exception>
    /// <exception cref="IOException">Lecture/écriture impossible (fichier verrouillé, disque plein).</exception>
    public LocalIdentity LoadOrCreateIdentity()
    {
        lock (_ioLock)
        {
            string path = Path.Combine(DirectoryPath, IdentityFileName);
            byte[]? blob;
            try
            {
                blob = AtomicFile.ReadBounded(path, MaxIdentityFileBytes);
            }
            catch (InvalidDataException ex)
            {
                throw new ProfileStoreException("Le fichier d'identité de ce profil est inutilisable (taille ou type de fichier inattendu). Il n'a pas été modifié.", ex);
            }

            return blob is null ? CreateIdentity(path) : OpenIdentity(blob);
        }
    }

    private LocalIdentity CreateIdentity(string path)
    {
        LocalIdentity identity = LocalIdentity.Create();
        byte[] pkcs8 = identity.ExportPrivateKey();
        try
        {
            AtomicFile.Write(path, _protector.Protect(pkcs8));
            return identity;
        }
        catch
        {
            // Pas d'identité utilisable si elle n'a pas pu être enregistrée : sinon le prochain démarrage en créerait une AUTRE.
            identity.Dispose();
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }
    }

    private LocalIdentity OpenIdentity(byte[] blob)
    {
        byte[]? pkcs8 = null;
        try
        {
            pkcs8 = _protector.Unprotect(blob);
            return LocalIdentity.FromPrivateKey(pkcs8);
        }
        catch (Exception ex) when (ex is CryptographicException or PlatformNotSupportedException or ArgumentException)
        {
            throw new ProfileStoreException(
                "L'identité de ce profil ne peut pas être lue (fichier altéré, ou profil créé par un autre compte Windows). Le fichier n'a pas été modifié.",
                ex);
        }
        finally
        {
            if (pkcs8 is not null)
            {
                CryptographicOperations.ZeroMemory(pkcs8);
            }
        }
    }

    // ───────────────────────────── Historique (IMessageStore) ─────────────────────────────

    /// <inheritdoc />
    public ChatSnapshot? Load() => LoadProtected<ChatSnapshot>(HistoryFileName, MaxHistoryFileBytes, "L'historique");

    /// <inheritdoc />
    /// <exception cref="ProfileStoreException">Historique trop volumineux.</exception>
    public void Save(ChatSnapshot snapshot) => SaveProtected(HistoryFileName, snapshot, MaxHistoryFileBytes, "L'historique");

    /// <summary>Met l'historique de côté (cas d'un contenu lisible mais incohérent). Ne supprime rien.</summary>
    public void QuarantineHistory() => QuarantineFile(HistoryFileName);

    // ───────────────────────────── Contacts ─────────────────────────────

    /// <summary>Charge les contacts ; <c>null</c> si aucun n'est enregistré.</summary>
    public ContactsSnapshot? LoadContacts() => LoadProtected<ContactsSnapshot>(ContactsFileName, MaxContactsFileBytes, "Les contacts");

    /// <summary>Enregistre les contacts.</summary>
    /// <exception cref="ProfileStoreException">Fichier trop volumineux.</exception>
    public void SaveContacts(ContactsSnapshot snapshot) => SaveProtected(ContactsFileName, snapshot, MaxContactsFileBytes, "Les contacts");

    /// <summary>Met le fichier de contacts de côté. Ne supprime rien.</summary>
    public void QuarantineContacts() => QuarantineFile(ContactsFileName);

    /// <summary>Charge les favoris locaux ; null si aucun fichier n'existe.</summary>
    public FavoriteConversationsSnapshot? LoadFavorites()
        => LoadProtected<FavoriteConversationsSnapshot>(FavoritesFileName, MaxFavoritesFileBytes, "Les favoris");

    /// <summary>Enregistre les favoris locaux de façon protégée et atomique.</summary>
    public void SaveFavorites(FavoriteConversationsSnapshot snapshot)
        => SaveProtected(FavoritesFileName, snapshot, MaxFavoritesFileBytes, "Les favoris");

    /// <summary>Met de côté un fichier de favoris incohérent sans le supprimer.</summary>
    public void QuarantineFavorites() => QuarantineFile(FavoritesFileName);

    /// <summary>Charge le profil public personnel associé au PeerId fourni ; null si aucun profil n'existe.</summary>
    public PersonalProfile? LoadPersonalProfile(string peerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(peerId);
        PersonalProfile? profile = LoadProtected<PersonalProfile>(PersonalProfileFileName, MaxPersonalProfileFileBytes, "Le profil personnel");
        if (profile is null)
        {
            return null;
        }

        try
        {
            return profile.Validate(peerId);
        }
        catch (InvalidDataException)
        {
            QuarantineFile(PersonalProfileFileName);
            QuarantineFile(PersonalProfileFileName + AtomicFile.BackupSuffix);
            return null;
        }
    }

    /// <summary>Enregistre atomiquement le profil public personnel, protégé au repos.</summary>
    public void SavePersonalProfile(PersonalProfile profile, string peerId)
    {
        ArgumentNullException.ThrowIfNull(profile);
        PersonalProfile validated = profile.Validate(peerId);
        SaveProtected(PersonalProfileFileName, validated, MaxPersonalProfileFileBytes, "Le profil personnel");
    }

    /// <summary>Charge les statuts reçus encore actifs. Les publications expirées ou incohérentes sont ignorées.</summary>
    public IReadOnlyList<EphemeralStatus> LoadReceivedStatuses(string localPeerId)
    {
        if (!InputValidator.IsValidPeerId(localPeerId))
        {
            throw new ArgumentException("Identité locale invalide.", nameof(localPeerId));
        }

        ReceivedStatusesSnapshot? snapshot = LoadProtected<ReceivedStatusesSnapshot>(
            ReceivedStatusesFileName, MaxReceivedStatusesFileBytes, "Les statuts reçus");
        if (snapshot is null)
        {
            return Array.Empty<EphemeralStatus>();
        }

        if (snapshot.SchemaVersion != 1 || snapshot.Items is null)
        {
            throw new ProfileStoreException("Le format du fichier des statuts reçus n'est pas pris en charge.");
        }

        DateTimeOffset now = _clock.GetUtcNow();
        List<EphemeralStatus> valid = new();
        long totalBytes = 0;
        foreach (ReceivedStatusRecord record in snapshot.Items.Take(100))
        {
            try
            {
                EphemeralStatus checkedItem = record.ToStatus().Validate(now);
                if (checkedItem.OwnerPeerId == localPeerId || !checkedItem.IsActive(now))
                {
                    continue;
                }

                totalBytes += checkedItem.MediaBytes?.LongLength ?? 0;
                if (totalBytes > 44L * 1024 * 1024)
                {
                    break;
                }

                valid.Add(checkedItem);
            }
            catch (InvalidDataException)
            {
                // Une entrée corrompue n'empêche pas de restaurer les autres statuts valides.
            }
        }

        return valid;
    }

    /// <summary>Enregistre les statuts reçus actifs avec chiffrement, sauvegarde atomique et copie .bak.</summary>
    public void SaveReceivedStatuses(IEnumerable<EphemeralStatus> statuses, string localPeerId)
    {
        ArgumentNullException.ThrowIfNull(statuses);
        if (!InputValidator.IsValidPeerId(localPeerId))
        {
            throw new ArgumentException("Identité locale invalide.", nameof(localPeerId));
        }

        DateTimeOffset now = _clock.GetUtcNow();
        List<ReceivedStatusRecord> valid = new();
        long totalBytes = 0;
        foreach (EphemeralStatus item in statuses.Take(101))
        {
            EphemeralStatus checkedItem = item.Validate(now);
            if (checkedItem.OwnerPeerId == localPeerId || !checkedItem.IsActive(now))
            {
                continue;
            }

            totalBytes += checkedItem.MediaBytes?.LongLength ?? 0;
            if (valid.Count >= 100 || totalBytes > 44L * 1024 * 1024)
            {
                throw new ProfileStoreException("Les statuts reçus dépassent la capacité de stockage autorisée. Supprimez des statuts avant de continuer.");
            }

            valid.Add(ReceivedStatusRecord.FromStatus(checkedItem));
        }

        SaveProtected(ReceivedStatusesFileName, new ReceivedStatusesSnapshot { Items = valid },
            MaxReceivedStatusesFileBytes, "Les statuts reçus");
    }

    /// <summary>Charge les marqueurs qui empêchent un ancien statut supprimé de réapparaître.</summary>
    public IReadOnlyList<ReceivedStatusTombstone> LoadReceivedStatusTombstones(string localPeerId)
    {
        if (!InputValidator.IsValidPeerId(localPeerId))
        {
            throw new ArgumentException("Identité locale invalide.", nameof(localPeerId));
        }
        ReceivedStatusTombstonesSnapshot? snapshot = LoadProtected<ReceivedStatusTombstonesSnapshot>(
            ReceivedStatusTombstonesFileName, MaxReceivedStatusTombstonesFileBytes, "Les suppressions reçues");
        if (snapshot is null)
        {
            return Array.Empty<ReceivedStatusTombstone>();
        }
        if (snapshot.SchemaVersion != 1 || snapshot.Items is null)
        {
            throw new ProfileStoreException("Le format des suppressions reçues n'est pas pris en charge.");
        }
        DateTimeOffset now = _clock.GetUtcNow();
        return snapshot.Items.Take(512).Where(item => item is not null
            && InputValidator.IsValidGuid(item.StatusId) && InputValidator.IsValidPeerId(item.OwnerPeerId)
            && item.OwnerPeerId != localPeerId && item.ExpiresAt > now && item.ExpiresAt <= now.AddHours(24)).ToArray();
    }

    /// <summary>Enregistre les marqueurs de suppression reçus sous chiffrement et écriture atomique.</summary>
    public void SaveReceivedStatusTombstones(IEnumerable<ReceivedStatusTombstone> tombstones, string localPeerId)
    {
        ArgumentNullException.ThrowIfNull(tombstones);
        if (!InputValidator.IsValidPeerId(localPeerId))
        {
            throw new ArgumentException("Identité locale invalide.", nameof(localPeerId));
        }
        ReceivedStatusTombstone[] supplied = tombstones.Take(513).ToArray();
        if (supplied.Length > 512)
        {
            throw new ProfileStoreException("Trop de marqueurs de suppression sont enregistrés.");
        }
        DateTimeOffset now = _clock.GetUtcNow();
        List<ReceivedStatusTombstone> valid = supplied.Where(item => item is not null
            && InputValidator.IsValidGuid(item.StatusId) && InputValidator.IsValidPeerId(item.OwnerPeerId)
            && item.OwnerPeerId != localPeerId && item.ExpiresAt > now && item.ExpiresAt <= now.AddHours(24))
            .GroupBy(item => (item.OwnerPeerId, item.StatusId))
            .Select(group => group.OrderByDescending(item => item.ExpiresAt).First()).ToList();
        SaveProtected(ReceivedStatusTombstonesFileName, new ReceivedStatusTombstonesSnapshot { Items = valid },
            MaxReceivedStatusTombstonesFileBytes, "Les suppressions reçues");
    }

    /// <summary>Charge les retraits encore valables destinés à des contacts hors ligne.</summary>
    public IReadOnlyList<PendingStatusDeleteRecord> LoadPendingStatusDeletes(string localPeerId)
    {
        if (!InputValidator.IsValidPeerId(localPeerId))
        {
            throw new ArgumentException("Identité locale invalide.", nameof(localPeerId));
        }
        PendingStatusDeletesSnapshot? snapshot = LoadProtected<PendingStatusDeletesSnapshot>(
            PendingStatusDeletesFileName, MaxPendingStatusDeletesFileBytes, "Les suppressions de statuts en attente");
        if (snapshot is null)
        {
            return Array.Empty<PendingStatusDeleteRecord>();
        }
        if (snapshot.SchemaVersion != 1 || snapshot.Items is null)
        {
            throw new ProfileStoreException("Le format des suppressions de statuts en attente n'est pas pris en charge.");
        }
        DateTimeOffset now = _clock.GetUtcNow();
        return snapshot.Items.Take(2048).Where(item => item is not null
            && InputValidator.IsValidGuid(item.StatusId) && InputValidator.IsValidPeerId(item.RecipientPeerId)
            && item.RecipientPeerId != localPeerId && item.ExpiresAt > now && item.ExpiresAt <= now.AddHours(24)).ToArray();
    }

    /// <summary>Enregistre atomiquement les retraits en attente sous chiffrement DPAPI.</summary>
    public void SavePendingStatusDeletes(IEnumerable<PendingStatusDeleteRecord> records, string localPeerId)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (!InputValidator.IsValidPeerId(localPeerId))
        {
            throw new ArgumentException("Identité locale invalide.", nameof(localPeerId));
        }
        PendingStatusDeleteRecord[] supplied = records.Take(2049).ToArray();
        if (supplied.Length > 2048)
        {
            throw new ProfileStoreException("Trop de suppressions de statuts sont en attente. Reconnectez les contacts pour vider la file.");
        }
        DateTimeOffset now = _clock.GetUtcNow();
        List<PendingStatusDeleteRecord> valid = supplied.Where(item => item is not null
            && InputValidator.IsValidGuid(item.StatusId) && InputValidator.IsValidPeerId(item.RecipientPeerId)
            && item.RecipientPeerId != localPeerId && item.ExpiresAt > now && item.ExpiresAt <= now.AddHours(24))
            .GroupBy(item => (item.StatusId, item.RecipientPeerId))
            .Select(group => group.OrderByDescending(item => item.ExpiresAt).First()).ToList();
        SaveProtected(PendingStatusDeletesFileName, new PendingStatusDeletesSnapshot { Items = valid },
            MaxPendingStatusDeletesFileBytes, "Les suppressions de statuts en attente");
    }

    // ───────────────────────────── Mécanique commune ─────────────────────────────

    private T? LoadProtected<T>(string fileName, long maxBytes, string label)
        where T : class
    {
        lock (_ioLock)
        {
            // audit-ok: fileName provient uniquement des constantes de cette classe ; DirectoryPath a été confiné sous la racine par SafePath dans le constructeur.
            string path = Path.Combine(DirectoryPath, fileName);
            string backup = path + AtomicFile.BackupSuffix;

            // Seules les erreurs « le CONTENU est mauvais » déclenchent la récupération. Une IOException (fichier verrouillé par un
            // antivirus, par exemple) est transitoire : on la laisse remonter, sans toucher au fichier (il est peut-être parfaitement sain).
            T? main = null;
            bool mainIsBad = false;
            try
            {
                main = ReadProtected<T>(path, maxBytes);
            }
            catch (Exception ex) when (ex is InvalidDataException or CryptographicException or JsonException)
            {
                mainIsBad = true;
            }

            if (main is not null)
            {
                return main;
            }

            // Fichier principal absent ou illisible : on tente la copie de la version précédente.
            T? fromBackup = null;
            try
            {
                fromBackup = ReadProtected<T>(backup, maxBytes);
            }
            catch (Exception ex) when (ex is InvalidDataException or CryptographicException or JsonException)
            {
                // Copie illisible aussi : on retombera sur « vide » ci-dessous, avec un avertissement.
                fromBackup = null;
            }

            if (mainIsBad)
            {
                string moved = Path.GetFileName(AtomicFile.Quarantine(path, _clock));
                _notices.Add(fromBackup is not null
                    ? $"{label} était illisible : la version précédente a été restaurée. L'ancien fichier est conservé sous le nom « {moved} »."
                    : $"{label} était illisible et a été mis de côté sous le nom « {moved} ». Démarrage à vide.");
            }
            else if (fromBackup is not null)
            {
                _notices.Add($"{label} : le fichier principal était absent, la version précédente a été restaurée.");
            }

            return fromBackup;
        }
    }

    private T? ReadProtected<T>(string path, long maxBytes)
        where T : class
    {
        byte[]? blob = AtomicFile.ReadBounded(path, maxBytes);
        if (blob is null)
        {
            return null;
        }

        byte[] plain = _protector.Unprotect(blob);
        try
        {
            return JsonSerializer.Deserialize<T>(plain, JsonOptions) ?? throw new InvalidDataException("Document vide.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private void SaveProtected<T>(string fileName, T value, long maxBytes, string label)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (_ioLock)
        {
            byte[] plain = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
            try
            {
                byte[] blob = _protector.Protect(plain);
                if (blob.Length > maxBytes)
                {
                    throw new ProfileStoreException($"{label} est devenu trop volumineux pour être enregistré. Réduisez la taille des données concernées.");
                }

                // audit-ok: fileName provient uniquement des constantes de cette classe ; DirectoryPath a été confiné sous la racine par SafePath dans le constructeur.
                AtomicFile.Write(Path.Combine(DirectoryPath, fileName), blob);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }
    }

    private void QuarantineFile(string fileName)
    {
        lock (_ioLock)
        {
            // audit-ok: fileName provient uniquement des constantes de cette classe ; DirectoryPath a été confiné sous la racine par SafePath dans le constructeur.
            string path = Path.Combine(DirectoryPath, fileName);
            if (File.Exists(path))
            {
                string moved = Path.GetFileName(AtomicFile.Quarantine(path, _clock));
                _notices.Add($"Un fichier du profil était incohérent : il est conservé sous le nom « {moved} » et on repart à vide.");
            }
        }
    }
}
