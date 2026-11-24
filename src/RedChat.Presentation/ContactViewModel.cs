using RedChat.Core;

namespace RedChat.Presentation;

/// <summary>État et actions locales d'un contact, dont le code de sécurité à comparer hors bande.</summary>
public sealed class ContactViewModel : ObservableObject
{
    private readonly ContactBook _contacts;
    private readonly string _selfPeerId;
    private readonly string _peerId;
    private string _displayName;
    private string? _error;
    private bool _hasReceivedStatus;

    /// <summary>Crée le modèle de vue d'un contact existant.</summary>
    public ContactViewModel(Contact contact, string selfPeerId, ContactBook contacts)
    {
        ArgumentNullException.ThrowIfNull(contact);
        _contacts = contacts ?? throw new ArgumentNullException(nameof(contacts));
        if (!InputValidator.IsValidPeerId(selfPeerId) || !InputValidator.IsValidPeerId(contact.PeerId) || selfPeerId == contact.PeerId)
        {
            throw new ArgumentException("Identités de contact invalides.");
        }

        _selfPeerId = selfPeerId;
        _peerId = contact.PeerId;
        _displayName = contact.DisplayName ?? string.Empty;
        RenameCommand = new RelayCommand(Rename, SetError);
        ToggleVerifiedCommand = new RelayCommand(ToggleVerified, SetError);
    }

    /// <summary>Identité cryptographique du contact.</summary>
    public string PeerId => _peerId;

    /// <summary>Indique si ce contact a au moins un statut encore disponible.</summary>
    public bool HasReceivedStatus
    {
        get => _hasReceivedStatus;
        private set => SetProperty(ref _hasReceivedStatus, value);
    }

    /// <summary>Met à jour l'indicateur de statut reçu.</summary>
    public void SetHasReceivedStatus(bool value) => HasReceivedStatus = value;

    /// <summary>Chemin de l'avatar importé localement, s'il existe.</summary>
    public string? AvatarPath
    {
        get
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RedChat", "Avatars", _peerId + ".png");
            return File.Exists(path) ? path : null;
        }
    }

    /// <summary>Notifie l'interface après import ou suppression de l'avatar.</summary>
    public void NotifyAvatarChanged() => OnPropertyChanged(nameof(AvatarPath));

    /// <summary>Initiales utilisées par l'avatar local.</summary>
    public string AvatarInitials
    {
        get
        {
            string label = DisplayLabel;
            if (label.Length >= 8 && label.All(Uri.IsHexDigit))
            {
                return label[..2].ToUpperInvariant();
            }

            string[] parts = label.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return parts.Length > 1
                ? string.Concat(char.ToUpperInvariant(parts[0][0]), char.ToUpperInvariant(parts[^1][0]))
                : label[..Math.Min(2, label.Length)].ToUpperInvariant();
        }
    }

    /// <summary>Date de première connexion authentifiée.</summary>
    public long FirstSeenUnixMs => _contacts.Find(_peerId)?.FirstSeenUnixMs ?? 0;

    /// <summary>Nom choisi sur cet appareil.</summary>
    public string DisplayName
    {
        get => _displayName;
        set => SetProperty(ref _displayName, value ?? string.Empty);
    }

    /// <summary>Nom affiché localement, sinon nom public reçu, sinon identifiant court.</summary>
    public string DisplayLabel => _contacts.Find(_peerId)?.DisplayName
        ?? _contacts.Find(_peerId)?.PublicProfile?.DisplayName
        ?? (_peerId.Length >= 8 ? _peerId[..8] : _peerId);

    /// <summary>Statut public annoncé par le contact.</summary>
    public string RemoteStatus => _contacts.Find(_peerId)?.PublicProfile?.Status ?? string.Empty;

    /// <summary>Description publique annoncée par le contact.</summary>
    public string RemoteAbout => _contacts.Find(_peerId)?.PublicProfile?.About ?? string.Empty;

    /// <summary>Avatar public reçu, si disponible.</summary>
    public byte[]? RemoteAvatarBytes => _contacts.Find(_peerId)?.PublicProfile?.AvatarBytes?.ToArray();

    /// <summary>Indique si le contact a publié un profil personnel.</summary>
    public bool HasPublicProfile => _contacts.Find(_peerId)?.PublicProfile is not null;

    /// <summary>Rafraîchit les propriétés liées au profil distant.</summary>
    public void RefreshPublicProfile()
    {
        OnPropertyChanged(nameof(DisplayLabel));
        OnPropertyChanged(nameof(RemoteStatus));
        OnPropertyChanged(nameof(RemoteAbout));
        OnPropertyChanged(nameof(RemoteAvatarBytes));
        OnPropertyChanged(nameof(HasPublicProfile));
        OnPropertyChanged(nameof(AvatarInitials));
    }

    /// <summary>Code de sécurité symétrique à comparer avec le contact.</summary>
    public string SecurityCode => RedChat.Core.SecurityCode.Compute(_selfPeerId, _peerId);

    /// <summary>État de vérification actuel.</summary>
    public bool IsVerified => _contacts.Find(_peerId)?.IsVerified ?? false;

    /// <summary>Dernière erreur à afficher.</summary>
    public string? Error
    {
        get => _error;
        private set => SetProperty(ref _error, value);
    }

    /// <summary>Commande de renommage local.</summary>
    public RelayCommand RenameCommand { get; }

    /// <summary>Commande d'activation/désactivation de la vérification.</summary>
    public RelayCommand ToggleVerifiedCommand { get; }

    private void Rename()
    {
        _contacts.Rename(_peerId, DisplayName);
        Error = null;
        OnPropertyChanged(nameof(DisplayLabel));
        OnPropertyChanged(nameof(AvatarInitials));
    }

    private void ToggleVerified()
    {
        _contacts.SetVerified(_peerId, !IsVerified);
        OnPropertyChanged(nameof(IsVerified));
        Error = null;
    }

    private void SetError(Exception exception) => Error = exception.Message;
}
