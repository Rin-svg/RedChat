using RedChat.Core;
using RedChat.Infrastructure;

namespace RedChat.Presentation;

/// <summary>Modèle de vue du profil personnel ; il ne modifie jamais l'identité cryptographique.</summary>
public sealed class PersonalProfileViewModel : ObservableObject
{
    private readonly ProfileStore _store;
    private readonly string _peerId;
    private string _displayName;
    private string _status;
    private string _about;
    private byte[]? _avatarBytes;
    private string? _error;
    private string? _notice;

    /// <summary>Charge les informations personnelles existantes ou initialise un profil public local.</summary>
    public PersonalProfileViewModel(ProfileStore store, string peerId)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        ArgumentException.ThrowIfNullOrWhiteSpace(peerId);
        _peerId = peerId;
        PersonalProfile profile = store.LoadPersonalProfile(peerId) ?? PersonalProfile.CreateDefault(peerId);
        _displayName = profile.DisplayName;
        _status = profile.Status;
        _about = profile.About;
        _avatarBytes = profile.AvatarBytes?.ToArray();
        SaveCommand = new RelayCommand(Save, ex => Error = ex.Message);
        RemoveAvatarCommand = new RelayCommand(() => AvatarBytes = null);
    }

    /// <summary>Profil public courant prêt à être diffusé sur les connexions authentifiées.</summary>
    public PersonalProfile CurrentProfile => new PersonalProfile()
    {
        PeerId = _peerId,
        DisplayName = DisplayName,
        Status = Status,
        About = About,
        AvatarBytes = _avatarBytes?.ToArray(),
    }.Validate(_peerId);

    /// <summary>Déclenché après enregistrement réussi du profil.</summary>
    public event EventHandler? ProfileSaved;

    /// <summary>Nom public.</summary>
    public string DisplayName { get => _displayName; set => SetProperty(ref _displayName, value ?? string.Empty); }
    /// <summary>Statut court.</summary>
    public string Status { get => _status; set => SetProperty(ref _status, value ?? string.Empty); }
    /// <summary>Description publique.</summary>
    public string About { get => _about; set => SetProperty(ref _about, value ?? string.Empty); }
    /// <summary>Photo locale du profil (PNG/JPEG).</summary>
    public byte[]? AvatarBytes
    {
        get => _avatarBytes?.ToArray();
        set
        {
            if (SetProperty(ref _avatarBytes, value?.ToArray()))
            {
                OnPropertyChanged(nameof(HasAvatar));
            }
        }
    }
    /// <summary>Indique si une photo est définie.</summary>
    public bool HasAvatar => _avatarBytes is { Length: > 0 };
    /// <summary>PeerId public et stable.</summary>
    public string PeerId => _peerId;
    /// <summary>Erreur de validation ou de sauvegarde.</summary>
    public string? Error { get => _error; private set => SetProperty(ref _error, value); }
    /// <summary>Confirmation de sauvegarde.</summary>
    public string? Notice { get => _notice; private set => SetProperty(ref _notice, value); }
    /// <summary>Enregistre le profil personnel.</summary>
    public RelayCommand SaveCommand { get; }
    /// <summary>Retire la photo du profil.</summary>
    public RelayCommand RemoveAvatarCommand { get; }

    private void Save()
    {
        Error = null;
        Notice = null;
        PersonalProfile profile = new()
        {
            PeerId = _peerId,
            DisplayName = DisplayName,
            Status = Status,
            About = About,
            AvatarBytes = _avatarBytes?.ToArray(),
        };
        _store.SavePersonalProfile(profile, _peerId);
        Notice = "Profil personnel enregistré et partagé avec les contacts connectés.";
        ProfileSaved?.Invoke(this, EventArgs.Empty);
    }
}
