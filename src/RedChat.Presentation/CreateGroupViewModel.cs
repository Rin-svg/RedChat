using System.Collections.ObjectModel;
using RedChat.Core;

namespace RedChat.Presentation;

/// <summary>Prépare et crée un groupe v1 à partir des contacts connus.</summary>
public sealed class CreateGroupViewModel : ObservableObject
{
    private readonly ChatService _chat;
    private string _name = string.Empty;
    private string? _error;
    private ConversationInfo? _createdGroup;

    /// <summary>Crée le modèle de vue de création de groupe.</summary>
    public CreateGroupViewModel(ChatService chat, ContactBook contacts)
    {
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        ArgumentNullException.ThrowIfNull(contacts);
        Contacts = new ObservableCollection<Contact>(contacts.GetAll());
        SelectedPeerIds = new ObservableCollection<string>();
        SelectedPeerIds.CollectionChanged += OnSelectionChanged;
        CreateCommand = new RelayCommand(Create, SetError, () => CanCreate);
    }

    /// <summary>Contacts disponibles pour ajouter des membres.</summary>
    public ObservableCollection<Contact> Contacts { get; }

    /// <summary>PeerId sélectionnés comme membres du nouveau groupe.</summary>
    public ObservableCollection<string> SelectedPeerIds { get; }

    /// <summary>Nom du groupe.</summary>
    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value ?? string.Empty))
            {
                CreateCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>Dernière erreur de création.</summary>
    public string? Error
    {
        get => _error;
        private set => SetProperty(ref _error, value);
    }

    /// <summary>Groupe créé, ou null avant la création.</summary>
    public ConversationInfo? CreatedGroup
    {
        get => _createdGroup;
        private set => SetProperty(ref _createdGroup, value);
    }

    /// <summary>Vrai si le nom et la sélection satisfont les règles de base.</summary>
    public bool CanCreate =>
        InputValidator.TryNormalizeName(Name, ProtocolLimits.MaxGroupNameChars, out _)
        && SelectedPeerIds.Count is >= 1 and <= ProtocolLimits.MaxGroupMembers - 1
        && SelectedPeerIds.All(InputValidator.IsValidPeerId)
        && SelectedPeerIds.Distinct(StringComparer.Ordinal).Count() == SelectedPeerIds.Count;

    /// <summary>Commande de création du groupe.</summary>
    public RelayCommand CreateCommand { get; }

    /// <summary>Ajoute ou retire un contact de la sélection.</summary>
    public void SetSelected(string peerId, bool selected)
    {
        if (!InputValidator.IsValidPeerId(peerId))
        {
            throw new ArgumentException("Identifiant de contact invalide.", nameof(peerId));
        }

        if (selected && !SelectedPeerIds.Contains(peerId, StringComparer.Ordinal))
        {
            SelectedPeerIds.Add(peerId);
        }
        else if (!selected)
        {
            _ = SelectedPeerIds.Remove(peerId);
        }

        CreateCommand.NotifyCanExecuteChanged();
    }

    private void OnSelectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        CreateCommand.NotifyCanExecuteChanged();

    private void Create()
    {
        CreatedGroup = _chat.CreateGroup(Name, SelectedPeerIds.ToArray());
        Error = null;
    }

    private void SetError(Exception exception) => Error = exception.Message;
}
