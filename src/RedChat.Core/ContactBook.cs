using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace RedChat.Core;

/// <summary>Un contact : un PeerId (identité cryptographique) et le nom que L'UTILISATEUR lui a donné.</summary>
public sealed record Contact
{
    /// <summary>Identité du contact (SHA-256 de sa clé publique).</summary>
    public string PeerId { get; init; } = string.Empty;

    /// <summary>Nom choisi localement (jamais envoyé par le réseau). <c>null</c> tant que l'utilisateur n'a pas nommé ce contact.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Date de la première connexion réussie avec ce pair (ms Unix UTC).</summary>
    public long FirstSeenUnixMs { get; init; }

    /// <summary>Vrai si l'utilisateur a comparé le code de sécurité avec son contact (en personne, au téléphone…).</summary>
    public bool IsVerified { get; init; }

    /// <summary>Profil public reçu dans le canal chiffré, distinct du nom local choisi par l'utilisateur.</summary>
    public PersonalProfile? PublicProfile { get; init; }
}

/// <summary>Contacts tels qu'on les écrit sur disque.</summary>
public sealed record ContactsSnapshot
{
    /// <summary>Version du format de stockage.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Version du format de ce document.</summary>
    public int Version { get; init; } = CurrentVersion;

    /// <summary>Les contacts.</summary>
    public IReadOnlyList<Contact> Contacts { get; init; } = Array.Empty<Contact>();
}

/// <summary>
/// Carnet de contacts local (thread-safe). Principe « TOFU » (Trust On First Use) : un pair inconnu qui réussit le handshake est
/// enregistré à sa première apparition, NON vérifié. L'utilisateur peut ensuite le marquer « vérifié » après avoir comparé le
/// <see cref="SecurityCode"/> par un autre canal.
/// <para>SÉCURITÉ : le nom affiché ne vient JAMAIS du réseau, seulement de l'utilisateur. Un pair ne peut donc pas se faire passer
/// pour « Maman » ; et deux contacts ne peuvent pas porter le même nom (même visuellement), pour éviter toute confusion.</para>
/// </summary>
public sealed class ContactBook
{
    /// <summary>Nombre maximal de contacts (borne mémoire et taille de fichier).</summary>
    public const int MaxContacts = 5_000;

    /// <summary>Budget total des avatars publics conservés dans le carnet.</summary>
    public const int MaxPublicAvatarCacheBytes = 5 * 1024 * 1024;

    private readonly object _lock = new();
    private readonly string _self;
    private readonly TimeProvider _clock;
    private readonly Dictionary<string, Contact> _contacts = new(StringComparer.Ordinal);

    /// <summary>Crée un carnet vide pour l'identité locale.</summary>
    public ContactBook(string selfPeerId, TimeProvider? clock = null)
    {
        if (!InputValidator.IsValidPeerId(selfPeerId))
        {
            throw new ArgumentException("PeerId local invalide.", nameof(selfPeerId));
        }

        _self = selfPeerId;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Levé APRÈS libération du verrou à chaque modification (ajout, nom, vérification, suppression).</summary>
    public event EventHandler? Changed;

    /// <summary>Instantané des contacts, triés par nom puis par PeerId.</summary>
    public IReadOnlyList<Contact> GetAll()
    {
        lock (_lock)
        {
            return _contacts.Values
                .OrderBy(c => c.DisplayName ?? "\uFFFF", StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(c => c.PeerId, StringComparer.Ordinal)
                .ToArray();
        }
    }

    /// <summary>Le contact, ou <c>null</c> s'il est inconnu.</summary>
    public Contact? Find(string peerId)
    {
        lock (_lock)
        {
            return _contacts.TryGetValue(peerId, out Contact? c) ? c : null;
        }
    }

    /// <summary>Texte à afficher pour un pair : son nom local, sinon un identifiant court (8 premiers caractères du PeerId).</summary>
    public string DisplayLabel(string peerId)
    {
        Contact? c = Find(peerId);
        if (c?.DisplayName is { } name)
        {
            return name;
        }

        if (c?.PublicProfile?.DisplayName is { } publicName)
        {
            return publicName;
        }

        return InputValidator.IsValidPeerId(peerId) ? peerId[..8] : "?";
    }

    /// <summary>
    /// TOFU : enregistre le pair s'il est nouveau (non vérifié, sans nom) et retourne son contact.
    /// <paramref name="isNew"/> vaut vrai à la toute première apparition : l'interface peut alors proposer de nommer/vérifier.
    /// </summary>
    /// <exception cref="ArgumentException">PeerId invalide, ou c'est le nôtre.</exception>
    /// <exception cref="InvalidOperationException">Carnet plein.</exception>
    public Contact EnsureKnown(string peerId, out bool isNew)
    {
        RequireValidPeer(peerId);
        Contact contact;
        lock (_lock)
        {
            if (_contacts.TryGetValue(peerId, out Contact? existing))
            {
                isNew = false;
                return existing;
            }

            if (_contacts.Count >= MaxContacts)
            {
                throw new InvalidOperationException("Carnet de contacts plein.");
            }

            contact = new Contact { PeerId = peerId, FirstSeenUnixMs = _clock.GetUtcNow().ToUnixTimeMilliseconds() };
            _contacts[peerId] = contact;
            isNew = true;
        }

        RaiseChanged();
        return contact;
    }

    /// <summary>Met à jour le profil public reçu d'un pair après authentification du canal.</summary>
    public void UpdatePublicProfile(string peerId, PersonalProfile profile)
    {
        RequireValidPeer(peerId);
        ArgumentNullException.ThrowIfNull(profile);
        PersonalProfile validated = profile.Validate(peerId);
        lock (_lock)
        {
            Contact contact = RequireContact(peerId);
            int otherAvatarBytes = _contacts.Values
                .Where(c => c.PeerId != peerId)
                .Sum(c => c.PublicProfile?.AvatarBytes?.Length ?? 0);
            if (validated.AvatarBytes is { } avatar && otherAvatarBytes + avatar.Length > MaxPublicAvatarCacheBytes)
            {
                validated = validated with { AvatarBytes = null };
            }

            if (ProfilesEqual(contact.PublicProfile, validated))
            {
                return;
            }

            _contacts[peerId] = contact with { PublicProfile = validated };
        }

        RaiseChanged();
    }

    /// <summary>Nomme (ou renomme) un contact. <c>null</c> ou vide efface le nom.</summary>
    /// <exception cref="ArgumentException">Nom invalide (trop long, caractères de contrôle ou d'inversion de texte).</exception>
    /// <exception cref="InvalidOperationException">Contact inconnu, ou nom déjà porté par un autre contact.</exception>
    public void Rename(string peerId, string? displayName)
    {
        RequireValidPeer(peerId);
        string? normalized = null;
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            if (!InputValidator.TryNormalizeName(displayName, ProtocolLimits.MaxDisplayNameChars, out string clean))
            {
                throw new ArgumentException("Nom invalide (1 à 40 caractères, sans caractère de contrôle ni d'inversion de texte).", nameof(displayName));
            }

            normalized = clean;
        }

        lock (_lock)
        {
            Contact c = RequireContact(peerId);
            if (normalized is not null)
            {
                string key = NameKey(normalized);
                if (_contacts.Values.Any(o => o.PeerId != peerId && o.DisplayName is not null && NameKey(o.DisplayName) == key))
                {
                    throw new InvalidOperationException("Un autre contact porte déjà ce nom.");
                }
            }

            _contacts[peerId] = c with { DisplayName = normalized };
        }

        RaiseChanged();
    }

    /// <summary>Marque (ou démarque) un contact comme vérifié.</summary>
    /// <exception cref="InvalidOperationException">Contact inconnu.</exception>
    public void SetVerified(string peerId, bool verified)
    {
        RequireValidPeer(peerId);
        lock (_lock)
        {
            Contact c = RequireContact(peerId);
            _contacts[peerId] = c with { IsVerified = verified };
        }

        RaiseChanged();
    }

    /// <summary>Retire un contact (sans effet s'il est inconnu).</summary>
    public void Remove(string peerId)
    {
        bool removed;
        lock (_lock)
        {
            removed = _contacts.Remove(peerId);
        }

        if (removed)
        {
            RaiseChanged();
        }
    }

    /// <summary>Photographie pour l'enregistrement.</summary>
    public ContactsSnapshot Export()
    {
        lock (_lock)
        {
            return new ContactsSnapshot { Contacts = _contacts.Values.ToArray() };
        }
    }

    /// <summary>Restaure un carnet enregistré (carnet vide requis). Tout ou rien : un fichier incohérent est refusé en entier.</summary>
    /// <exception cref="InvalidDataException">Données invalides ou hostiles.</exception>
    /// <exception cref="InvalidOperationException">Le carnet contient déjà des contacts.</exception>
    public void Import(ContactsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        RequireStored(snapshot.Version == ContactsSnapshot.CurrentVersion, "Version de fichier non prise en charge.");
        RequireStored(snapshot.Contacts is not null && snapshot.Contacts.Count <= MaxContacts, "Nombre de contacts invalide.");

        Dictionary<string, Contact> loaded = new(StringComparer.Ordinal);
        HashSet<string> names = new(StringComparer.Ordinal);
        long publicAvatarBytes = 0;
        long newestAllowedMs = ProtocolLimits.StoredTimestampCeiling.ToUnixTimeMilliseconds(); // pas « maintenant + 1 jour » : horloge PC déréglée puis corrigée
        foreach (Contact? c in snapshot.Contacts!)
        {
            RequireStored(c is not null && InputValidator.IsValidPeerId(c.PeerId) && c.PeerId != _self, "Contact invalide.");
            RequireStored(c!.FirstSeenUnixMs > 0 && c.FirstSeenUnixMs <= newestAllowedMs, "Date de contact invalide.");
            if (c.DisplayName is not null)
            {
                RequireStored(
                    InputValidator.TryNormalizeName(c.DisplayName, ProtocolLimits.MaxDisplayNameChars, out string clean) && clean == c.DisplayName,
                    "Nom de contact invalide.");
                RequireStored(names.Add(NameKey(c.DisplayName)), "Deux contacts portent le même nom.");
            }

            if (c.PublicProfile is not null)
            {
                try
                {
                    PersonalProfile validatedProfile = c.PublicProfile.Validate(c.PeerId);
                    publicAvatarBytes += validatedProfile.AvatarBytes?.Length ?? 0;
                    RequireStored(publicAvatarBytes <= MaxPublicAvatarCacheBytes, "Cache d'avatars publics trop volumineux.");
                }
                catch (InvalidDataException ex)
                {
                    throw new InvalidDataException("Profil public de contact invalide.", ex);
                }
            }

            RequireStored(loaded.TryAdd(c.PeerId, c), "Contact en double.");
        }

        lock (_lock)
        {
            if (_contacts.Count != 0)
            {
                throw new InvalidOperationException("La restauration n'est possible que sur un carnet vide.");
            }

            foreach (KeyValuePair<string, Contact> kv in loaded)
            {
                _contacts[kv.Key] = kv.Value;
            }
        }
    }

    // Deux noms « visuellement identiques » (é composé ou décomposé, majuscules/minuscules, formes pleine largeur) doivent
    // être vus comme le même nom : sinon un utilisateur pourrait nommer deux contacts « Léa » sans pouvoir les distinguer.
    private static bool ProfilesEqual(PersonalProfile? left, PersonalProfile right) =>
        left is not null
        && left.PeerId == right.PeerId
        && left.DisplayName == right.DisplayName
        && left.Status == right.Status
        && left.About == right.About
        && ((left.AvatarBytes is null && right.AvatarBytes is null)
            || (left.AvatarBytes is not null && right.AvatarBytes is not null && left.AvatarBytes.AsSpan().SequenceEqual(right.AvatarBytes)));

    private static string NameKey(string name) =>
        name.Normalize(NormalizationForm.FormKC).ToUpperInvariant();

    private void RequireValidPeer(string peerId)
    {
        if (!InputValidator.IsValidPeerId(peerId) || peerId == _self)
        {
            throw new ArgumentException("PeerId invalide (ou identité locale).", nameof(peerId));
        }
    }

    private Contact RequireContact(string peerId) =>
        _contacts.TryGetValue(peerId, out Contact? c) ? c : throw new InvalidOperationException("Contact inconnu.");

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private static void RequireStored([DoesNotReturnIf(false)] bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"Contacts enregistrés invalides : {message}"));
        }
    }
}
