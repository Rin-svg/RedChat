using System.Collections.ObjectModel;
using RedChat.Core;
using RedChat.Infrastructure;

namespace RedChat.Presentation;

/// <summary>Modèle de vue racine : conversations, découverte LAN, connexion manuelle et état de persistance.</summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private const int MaxDiscoveredPeersShown = 100;
    private readonly LanNode _node;
    private readonly ChatPersistence _persistence;
    private readonly IUiDispatcher _dispatcher;
    private readonly FavoriteConversations _favorites;
    private readonly ProfileStore _profileStore;
    private readonly Dictionary<string, ConversationViewModel> _conversationById = new(StringComparer.Ordinal);
    private readonly HashSet<string> _deletedReceivedStatusKeys = new(StringComparer.Ordinal);
    private string _connectEndpoint = string.Empty;
    private string? _error;
    private string? _saveWarning;
    private string? _connectionNotice;
    private string? _attachmentNotice;
    private string? _lastReceivedVoicePath;
    private ConversationViewModel? _selectedConversation;
    private ContactViewModel? _selectedContact;
    private ContactViewModel? _selectedAddressBookContact;
    private PersonalProfile _selfProfile;
    private bool _disposed;

    /// <summary>Assemble le modèle racine après le chargement du profil et avant le démarrage du nœud.</summary>
    public MainViewModel(LanNode node, ChatPersistence persistence, IUiDispatcher dispatcher, FavoriteConversations favorites, ProfileStore profileStore)
    {
        _node = node ?? throw new ArgumentNullException(nameof(node));
        _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _favorites = favorites ?? throw new ArgumentNullException(nameof(favorites));
        _profileStore = profileStore ?? throw new ArgumentNullException(nameof(profileStore));
        _selfProfile = _profileStore.LoadPersonalProfile(_node.PeerId) ?? PersonalProfile.CreateDefault(_node.PeerId);
        StartupNotice = persistence.StartupNotice;
        Conversations = new ObservableCollection<ConversationViewModel>();
        Contacts = new ObservableCollection<ContactViewModel>();
        DiscoveredPeers = new ObservableCollection<DiscoveredPeerViewModel>();
        ReceivedStatuses = new ObservableCollection<ReceivedStatusViewModel>();
        VoiceMessages = new ObservableCollection<VoiceMessageViewModel>();
        ReceivedAttachments = new ObservableCollection<AttachmentReceivedEventArgs>();
        VoiceMessages.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasVoiceMessages));
        ReceivedAttachments.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasReceivedAttachments));
        OutgoingVoiceDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RedChat", "VoiceHistory", "Outgoing");
        LoadVoiceHistory();
        foreach (ReceivedStatusTombstone tombstone in _profileStore.LoadReceivedStatusTombstones(_node.PeerId))
        {
            _deletedReceivedStatusKeys.Add(MakeReceivedStatusKey(tombstone.OwnerPeerId, tombstone.StatusId));
        }
        foreach (EphemeralStatus status in _profileStore.LoadReceivedStatuses(_node.PeerId))
        {
            if (_deletedReceivedStatusKeys.Contains(MakeReceivedStatusKey(status.OwnerPeerId, status.Id)))
            {
                continue;
            }
            string name = _node.Contacts.Find(status.OwnerPeerId)?.DisplayName
                ?? _node.Contacts.Find(status.OwnerPeerId)?.PublicProfile?.DisplayName
                ?? (status.OwnerPeerId.Length > 8 ? status.OwnerPeerId[..8] : status.OwnerPeerId);
            ReceivedStatuses.Add(new ReceivedStatusViewModel(status, name));
        }
        ReceivedStatuses.CollectionChanged += OnReceivedStatusesCollectionChanged;
        ConnectCommand = new AsyncRelayCommand(ConnectAsync, SetError, () => !string.IsNullOrWhiteSpace(ConnectEndpoint));
        // Créé avant tout accès à SelectedConversation : le setter de la sélection rafraîchit l'état d'appel.
        Call = new CallViewModel(_node.Calls, _dispatcher, GetSelectedPrivatePeerId);
        _node.Chat.Changed += OnChatChanged;
        _node.Attachments.FileReceived += OnAttachmentReceived;
        _node.Attachments.ViewOnceAttachmentOpened += OnViewOnceAttachmentOpened;
        _node.Attachments.ProgressChanged += OnAttachmentProgressChanged;
        _node.Statuses.StatusReceived += OnStatusReceived;
        _node.Statuses.StatusViewed += OnStatusViewed;
        _node.Statuses.StatusDeleted += OnStatusDeleted;
        _node.Transport.PeerConnected += OnStatusPeerConnected;
        _node.Contacts.Changed += OnContactsChanged;
        _node.PeerDiscovered += OnPeerDiscovered;
        _persistence.SaveFailed += OnSaveFailed;
        _persistence.SaveRecovered += OnSaveRecovered;
        RefreshConversations();
        RefreshContacts();
        RefreshStatusAvailability();
    }

    /// <summary>Identité cryptographique locale utilisée pour signer logiquement les statuts via le canal authentifié.</summary>
    public string LocalPeerId => _node.PeerId;

    /// <summary>Profil public local actuellement affiché dans l'application.</summary>
    public PersonalProfile SelfProfile => _selfProfile;

    /// <summary>Contacts connus, utilisés pour construire l'audience des statuts.</summary>
    public IReadOnlyList<Contact> GetStatusContacts() => _node.Contacts.GetAll();

    /// <summary>Diffuse un statut aux seuls contacts autorisés et actuellement connectés.</summary>
    public LanNode.StatusPublishResult PublishStatus(EphemeralStatus status, string? mediaName = null) => _node.PublishStatus(status, mediaName);

    /// <summary>Envoie à l’auteur le reçu correspondant à l’ouverture d’un statut reçu.</summary>
    public bool MarkReceivedStatusViewed(string ownerPeerId, string statusId) => _node.SendStatusViewReceipt(ownerPeerId, statusId);

    /// <summary>Enregistre durablement un retrait puis le transmet aux contacts autorisés connectés ou à leur reconnexion.</summary>
    public int DeleteLocalStatus(string statusId, IEnumerable<string> authorizedPeerIds, DateTimeOffset expiresAt)
    {
        if (!InputValidator.IsValidGuid(statusId))
        {
            throw new ArgumentException("Identifiant de statut invalide.", nameof(statusId));
        }
        ArgumentNullException.ThrowIfNull(authorizedPeerIds);
        string[] recipients = authorizedPeerIds.Distinct(StringComparer.Ordinal).ToArray();
        if (recipients.Length > 256 || recipients.Any(id => !InputValidator.IsValidPeerId(id) || id == _node.PeerId))
        {
            throw new ArgumentException("Liste de destinataires invalide.", nameof(authorizedPeerIds));
        }
        List<PendingStatusDeleteRecord> pending = _profileStore.LoadPendingStatusDeletes(_node.PeerId).ToList();
        foreach (string peerId in recipients)
        {
            pending.RemoveAll(item => item.StatusId == statusId && item.RecipientPeerId == peerId);
            pending.Add(new PendingStatusDeleteRecord { StatusId = statusId, RecipientPeerId = peerId, ExpiresAt = expiresAt });
        }
        _profileStore.SavePendingStatusDeletes(pending, _node.PeerId);
        return FlushStatusDeletes(recipients);
    }

    /// <summary>Déclenché lorsqu’un contact confirme avoir consulté l’un de nos statuts.</summary>
    public event EventHandler<StatusViewedEventArgs>? LocalStatusViewed;

    /// <summary>Service de transfert de pièces jointes associé au nœud courant.</summary>
    public AttachmentTransferService Attachments => _node.Attachments;

    /// <summary>Signalisation des appels privés via le canal authentifié et chiffré.</summary>
    public CallSignalingService Calls => _node.Calls;

    /// <summary>État et commandes de l'interface d'appel (bandeau non modal, micro, caméra, raccrocher).</summary>
    public CallViewModel Call { get; }

    /// <summary>Favoris de conversations persistés localement sur cet appareil.</summary>
    public FavoriteConversations Favorites => _favorites;

    /// <summary>Signalé lorsqu’un nouveau message arrive dans une conversation qui n’est pas affichée.</summary>
    public event EventHandler<IncomingMessageNotificationEventArgs>? IncomingMessageReceived;

    /// <summary>Conversations triées de la plus récente à la plus ancienne.</summary>
    public ObservableCollection<ConversationViewModel> Conversations { get; }

    /// <summary>Carnet indépendant des conversations, alimenté après handshake authentifié.</summary>
    public ObservableCollection<ContactViewModel> Contacts { get; }

    /// <summary>Contact actuellement sélectionné dans le carnet.</summary>
    public ContactViewModel? SelectedAddressBookContact
    {
        get => _selectedAddressBookContact;
        set => SetProperty(ref _selectedAddressBookContact, value);
    }

    /// <summary>Pairs récemment découverts par UDP ; leur identité reste non authentifiée avant le handshake.</summary>
    public ObservableCollection<DiscoveredPeerViewModel> DiscoveredPeers { get; }

    /// <summary>Statuts reçus des contacts, conservés localement jusqu'à leur expiration ou leur retrait.</summary>
    public ObservableCollection<ReceivedStatusViewModel> ReceivedStatuses { get; }

    /// <summary>Historique local des messages vocaux reçus et envoyés.</summary>
    public ObservableCollection<VoiceMessageViewModel> VoiceMessages { get; }

    /// <summary>Pièces jointes reçues pendant cette session, affichées dans la conversation.</summary>
    public ObservableCollection<AttachmentReceivedEventArgs> ReceivedAttachments { get; }

    /// <summary>Indique si des pièces jointes récentes peuvent être ouvertes.</summary>
    public bool HasReceivedAttachments => ReceivedAttachments.Count > 0;

    private string OutgoingVoiceDirectory { get; }

    /// <summary>Conversation actuellement affichée.</summary>
    public ConversationViewModel? SelectedConversation
    {
        get => _selectedConversation;
        set
        {
            if (SetProperty(ref _selectedConversation, value))
            {
                SelectedContact = value is not null && !value.Info.IsGroup && _node.Contacts.Find(value.Info.Id) is { } contact
                    ? new ContactViewModel(contact, _node.PeerId, _node.Contacts)
                    : null;
                SelectedAddressBookContact = value is not null && !value.Info.IsGroup
                    ? Contacts.FirstOrDefault(c => c.PeerId == value.Info.Id)
                    : null;
                value?.MarkRead();
                value?.RefreshMessages();
                Call.RefreshAvailability();
            }
        }
    }

    /// <summary>Contact associé à la conversation sélectionnée, ou null pour un groupe/non-contact.</summary>
    public ContactViewModel? SelectedContact
    {
        get => _selectedContact;
        private set => SetProperty(ref _selectedContact, value);
    }

    /// <summary>Adresse IP locale et port TCP saisis sous la forme IP:port.</summary>
    public string ConnectEndpoint
    {
        get => _connectEndpoint;
        set
        {
            if (SetProperty(ref _connectEndpoint, value ?? string.Empty))
            {
                ConnectCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>Avertissement de récupération affiché au premier démarrage après restauration/quarantaine.</summary>
    public string? StartupNotice { get; }

    /// <summary>Avertissement persistant tant que l'enregistrement automatique échoue.</summary>
    public string? SaveWarning
    {
        get => _saveWarning;
        private set => SetProperty(ref _saveWarning, value);
    }

    /// <summary>Retour de la dernière tentative de connexion.</summary>
    public string? ConnectionNotice
    {
        get => _connectionNotice;
        private set => SetProperty(ref _connectionNotice, value);
    }

    /// <summary>État du dernier transfert de pièce jointe.</summary>
    public string? AttachmentNotice
    {
        get => _attachmentNotice;
        private set => SetProperty(ref _attachmentNotice, value);
    }

    /// <summary>Chemin du dernier message vocal WAV reçu sur cet appareil.</summary>
    public string? LastReceivedVoicePath
    {
        get => _lastReceivedVoicePath;
        private set
        {
            if (SetProperty(ref _lastReceivedVoicePath, value))
            {
                OnPropertyChanged(nameof(HasVoiceMessages));
            }
        }
    }

    /// <summary>Indique si au moins un message vocal archivé est disponible pour lecture.</summary>
    public bool HasVoiceMessages => VoiceMessages.Any(message => File.Exists(message.FilePath));

    /// <summary>Archive une copie locale d'un vocal envoyé afin qu'il reste réécoutable après l'envoi.</summary>
    public void ArchiveOutgoingVoice(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Le fichier vocal à archiver est introuvable.", sourcePath);
        }

        Directory.CreateDirectory(OutgoingVoiceDirectory);
        string destination = Path.Combine(OutgoingVoiceDirectory, $"vocal-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.wav");
        File.Copy(sourcePath, destination);
        AddVoiceMessage(destination, Path.GetFileName(sourcePath), "Envoyé");
    }

    private void LoadVoiceHistory()
    {
        LoadVoiceFiles(_node.Attachments.IncomingDirectory, "Reçu");
        LoadVoiceFiles(OutgoingVoiceDirectory, "Envoyé");
    }

    private void LoadVoiceFiles(string directory, string direction)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (string path in Directory.EnumerateFiles(directory, "*.wav", SearchOption.TopDirectoryOnly))
        {
            AddVoiceMessage(path, Path.GetFileName(path), direction);
        }
    }

    private void AddVoiceMessage(string path, string fileName, string direction)
    {
        if (VoiceMessages.Any(message => string.Equals(message.FilePath, path, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        DateTime modified = File.Exists(path) ? File.GetLastWriteTime(path) : DateTime.Now;
        VoiceMessages.Insert(0, new VoiceMessageViewModel(path, fileName, direction, modified));
    }

    /// <summary>Dernière erreur d'action à afficher.</summary>
    public string? Error
    {
        get => _error;
        private set => SetProperty(ref _error, value);
    }

    /// <summary>Commande de connexion manuelle annulable et protégée contre le double-clic.</summary>
    public AsyncRelayCommand ConnectCommand { get; }

    /// <summary>Envoie un fichier dans une conversation en utilisant le canal TCP chiffré.</summary>
    public async Task SendAttachmentAsync(string conversationId, string filePath, bool isViewOnce = false, CancellationToken cancellationToken = default)
    {
        await _node.Attachments.SendFileAsync(conversationId, filePath, isViewOnce, cancellationToken).ConfigureAwait(true);
        if (!_disposed)
        {
            AttachmentNotice = isViewOnce
                ? "Média envoyé en vue unique."
                : "Fichier placé dans les files d'envoi des destinataires.";
        }
    }

    /// <summary>Consomme une pièce jointe à vue unique après son affichage.</summary>
    public bool ConsumeViewOnceAttachment(AttachmentReceivedEventArgs attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        bool acknowledged = _node.Attachments.ConsumeViewOnceAttachment(attachment);
        _ = ReceivedAttachments.Remove(attachment);
        AttachmentNotice = acknowledged
            ? $"Média consulté une fois : {attachment.FileName}"
            : "Le média a été supprimé après consultation, mais la confirmation n’a pas pu être envoyée.";
        return acknowledged;
    }

    /// <summary>Consomme un message texte à vue unique après son affichage.</summary>
    public void ConsumeViewOnceMessage(string conversationId, string messageId) =>
        _node.Chat.ConsumeViewOnceMessage(conversationId, messageId);

    /// <summary>Crée un ViewModel de groupe relié au service courant.</summary>
    public CreateGroupViewModel CreateGroupViewModel() => new(_node.Chat, _node.Contacts);

    /// <summary>Supprime localement une conversation et son favori éventuel.</summary>
    public void RemoveConversation(string conversationId)
    {
        _favorites.Remove(conversationId);
        _node.Chat.RemoveConversation(conversationId);
        if (SelectedConversation?.Info.Id == conversationId)
        {
            SelectedConversation = null;
        }

        RefreshConversations();
    }

    /// <summary>Retire un contact du carnet local ; son historique de discussion est conservé.</summary>
    public void RemoveContact(string peerId)
    {
        _node.Contacts.Remove(peerId);
        if (SelectedAddressBookContact?.PeerId == peerId)
        {
            SelectedAddressBookContact = null;
        }

        RefreshContacts();
    }

    /// <summary>Sélectionne une fiche du carnet.</summary>
    public void SelectAddressBookContact(string peerId)
    {
        SelectedAddressBookContact = Contacts.FirstOrDefault(c => c.PeerId == peerId);
    }

    /// <summary>Ouvre une conversation privée depuis une fiche du carnet, sans message factice.</summary>
    public void OpenAddressBookContact(string peerId)
    {
        if (_node.Contacts.Find(peerId) is null)
        {
            throw new InvalidOperationException("Ce contact n'existe plus dans le carnet.");
        }

        ConversationInfo info = _node.Chat.EnsureDirectConversation(peerId);
        RefreshConversations();
        SelectedConversation = _conversationById[info.Id];
    }

    /// <summary>Ouvre la conversation locale de notes personnelles.</summary>
    public void OpenSelfConversation()
    {
        ConversationInfo info = _node.Chat.EnsureSelfConversation();
        RefreshConversations();
        SelectedConversation = _conversationById[info.Id];
    }

    /// <summary>Actualise l'identité publique locale affichée dans la conversation personnelle.</summary>
    public void UpdateSelfProfile(PersonalProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _selfProfile = profile.Validate(_node.PeerId);
        foreach (ConversationViewModel conversation in _conversationById.Values)
        {
            conversation.SetSelfProfile(_selfProfile);
        }
    }

    /// <summary>Rafraîchit la liste à partir des instantanés thread-safe du service.</summary>
    public void RefreshConversations()
    {
        if (_disposed)
        {
            return;
        }

        IReadOnlyList<ConversationInfo> infos = _node.Chat.GetConversations();
        HashSet<string> liveIds = infos.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        foreach (string removedId in _conversationById.Keys.Where(id => !liveIds.Contains(id)).ToArray())
        {
            _conversationById[removedId].Dispose();
            _conversationById.Remove(removedId);
        }

        foreach (ConversationInfo info in infos)
        {
            if (!_conversationById.TryGetValue(info.Id, out ConversationViewModel? vm))
            {
                vm = new ConversationViewModel(info, _node.Chat, _node.Contacts, _dispatcher,
                    selfPeerId: _node.PeerId, selfProfile: _selfProfile);
                _conversationById.Add(info.Id, vm);
            }
            else
            {
                vm.RefreshMessages();
            }
        }

        ConversationViewModel? selected = SelectedConversation;
        Conversations.Clear();
        foreach (ConversationViewModel vm in _conversationById.Values
            .OrderByDescending(c => c.LastActivityUnixMs)
            .ThenBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            Conversations.Add(vm);
        }

        if (selected is not null && _conversationById.TryGetValue(selected.Info.Id, out ConversationViewModel? current))
        {
            SelectedConversation = current;
        }
        else if (selected is not null)
        {
            SelectedConversation = null;
        }
        RefreshStatusAvailability();
    }

    /// <summary>PeerId de la conversation privée affichée, ou null pour un groupe ou en l'absence de sélection.</summary>
    private string? GetSelectedPrivatePeerId() =>
        _selectedConversation is { } conversation && !conversation.Info.IsGroup ? conversation.Info.Id : null;

    /// <summary>Libère les abonnements et commandes lorsque la fenêtre principale se ferme.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ReceivedStatuses.CollectionChanged -= OnReceivedStatusesCollectionChanged;
        _node.Chat.Changed -= OnChatChanged;
        _node.Attachments.FileReceived -= OnAttachmentReceived;
        _node.Attachments.ViewOnceAttachmentOpened -= OnViewOnceAttachmentOpened;
        _node.Attachments.ProgressChanged -= OnAttachmentProgressChanged;
        _node.Statuses.StatusReceived -= OnStatusReceived;
        _node.Statuses.StatusViewed -= OnStatusViewed;
        _node.Statuses.StatusDeleted -= OnStatusDeleted;
        _node.Transport.PeerConnected -= OnStatusPeerConnected;
        _node.Contacts.Changed -= OnContactsChanged;
        _node.PeerDiscovered -= OnPeerDiscovered;
        _persistence.SaveFailed -= OnSaveFailed;
        _persistence.SaveRecovered -= OnSaveRecovered;
        ConnectCommand.Dispose();
        Call.Dispose();
        foreach (ConversationViewModel vm in _conversationById.Values)
        {
            vm.Dispose();
        }

        _conversationById.Clear();
        Contacts.Clear();
    }

    /// <summary>Reconstruit la collection observable depuis l'instantané thread-safe du carnet.</summary>
    public void RefreshContacts()
    {
        if (_disposed)
        {
            return;
        }

        string? selectedPeerId = SelectedAddressBookContact?.PeerId;
        Contacts.Clear();
        foreach (Contact contact in _node.Contacts.GetAll())
        {
            Contacts.Add(new ContactViewModel(contact, _node.PeerId, _node.Contacts));
        }

        SelectedAddressBookContact = selectedPeerId is null
            ? null
            : Contacts.FirstOrDefault(c => c.PeerId == selectedPeerId);
        RefreshStatusAvailability();
    }

    private void OnReceivedStatusesCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        RefreshStatusAvailability();

    private void RefreshStatusAvailability()
    {
        HashSet<string> owners = ReceivedStatuses
            .Where(status => status.ExpiresAt > DateTimeOffset.UtcNow)
            .Select(status => status.OwnerPeerId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (ContactViewModel contact in Contacts)
        {
            contact.SetHasReceivedStatus(owners.Contains(contact.PeerId));
        }
        foreach (ConversationViewModel conversation in _conversationById.Values)
        {
            conversation.SetHasReceivedStatus(!conversation.Info.IsGroup && owners.Contains(conversation.Info.Id));
        }
    }

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        string endpoint = ConnectEndpoint;
        string peerId = await _node.ConnectManualAsync(endpoint, cancellationToken).ConfigureAwait(true);
        ConnectionNotice = $"Connexion établie avec {peerId[..8]}.";
        Error = null;
        RefreshConversations();
    }

    private void OnAttachmentProgressChanged(object? sender, AttachmentProgressEventArgs e) => _dispatcher.Post(() =>
    {
        if (!_disposed)
        {
            AttachmentNotice = $"Envoi de {e.FileName} : {e.Percentage} %";
        }
    });

    private void OnAttachmentReceived(object? sender, AttachmentReceivedEventArgs e) => _dispatcher.Post(() =>
    {
        if (!_disposed)
        {
            ReceivedAttachments.Insert(0, e);
            AttachmentNotice = $"Fichier reçu : {e.FileName} — {e.FilePath}";
            if (e.IsViewOnce)
            {
                AttachmentNotice = $"Média reçu en vue unique : {e.FileName}";
                return;
            }
            if (string.Equals(Path.GetExtension(e.FileName), ".wav", StringComparison.OrdinalIgnoreCase))
            {
                LastReceivedVoicePath = e.FilePath;
                AddVoiceMessage(e.FilePath, e.FileName, "Reçu");
                AttachmentNotice = $"Message vocal reçu : {e.FileName}";
            }
        }
    });

    private void OnViewOnceAttachmentOpened(string transferId) => _dispatcher.Post(() =>
    {
        if (!_disposed)
        {
            AttachmentNotice = $"Votre média en vue unique a été consulté (transfert {transferId[..8]}).";
        }
    });

    private void OnChatChanged(object? sender, ChatChangedEventArgs e) => _dispatcher.Post(() =>
    {
        if (_disposed)
        {
            return;
        }

        RefreshConversations();
        if (e.Kind == ChatChangeKind.MessageAdded
            && e.MessageId is not null
            && !string.Equals(SelectedConversation?.Info.Id, e.ConversationId, StringComparison.Ordinal)
            && _conversationById.TryGetValue(e.ConversationId, out ConversationViewModel? conversation))
        {
            ChatMessage? message = _node.Chat.GetMessages(e.ConversationId)
                .FirstOrDefault(candidate => candidate.Id == e.MessageId);
            if (IncomingMessageNotificationPolicy.ShouldNotify(
                e.Kind, message, e.ConversationId, SelectedConversation?.Info.Id))
            {
                IncomingMessageReceived?.Invoke(this,
                    new IncomingMessageNotificationEventArgs(e.ConversationId, conversation.DisplayName));
            }
        }

        if (SelectedConversation?.Info.Id == e.ConversationId)
        {
            SelectedConversation.RefreshMessages();
        }
    });

    /// <summary>Retire les statuts expirés et persiste la collection restante.</summary>
    public void PruneExpiredReceivedStatuses()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ReceivedStatusViewModel[] expired = ReceivedStatuses.Where(item => item.ExpiresAt <= now).ToArray();
        foreach (ReceivedStatusViewModel item in expired)
        {
            ReceivedStatuses.Remove(item);
        }

        if (expired.Length > 0)
        {
            SaveReceivedStatuses();
        }
    }

    private void SaveReceivedStatuses()
    {
        try
        {
            _profileStore.SaveReceivedStatuses(ReceivedStatuses.Select(item => item.Status), _node.PeerId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ProfileStoreException)
        {
            SaveWarning = "Les statuts reçus n'ont pas pu être enregistrés sur cet appareil. Vérifie l'espace disque et les autorisations.";
        }
    }

    private void OnStatusPeerConnected(object? sender, PeerConnectionEventArgs e)
    {
        _dispatcher.Post(() =>
        {
            if (_disposed)
            {
                return;
            }
            try
            {
                _ = FlushStatusDeletes(new[] { e.PeerId });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ProfileStoreException)
            {
                SaveWarning = "Les demandes de suppression en attente n'ont pas pu être synchronisées. Elles seront réessayées à la prochaine connexion.";
            }
        });
    }

    private int FlushStatusDeletes(IEnumerable<string> peerIds)
    {
        HashSet<string> targets = peerIds.ToHashSet(StringComparer.Ordinal);
        List<PendingStatusDeleteRecord> pending = _profileStore.LoadPendingStatusDeletes(_node.PeerId).ToList();
        int queued = 0;
        // On conserve les tombstones jusqu'à l'expiration : TrySend confirme seulement la mise en file
        // locale, pas la réception distante. Une reconnexion peut donc déclencher une nouvelle tentative.
        foreach (PendingStatusDeleteRecord item in pending)
        {
            if (targets.Contains(item.RecipientPeerId) && _node.SendStatusDeleteToPeer(item.RecipientPeerId, item.StatusId))
            {
                queued++;
            }
        }
        _profileStore.SavePendingStatusDeletes(pending, _node.PeerId);
        return queued;
    }

    private void OnStatusViewed(object? sender, StatusViewedEventArgs e) => _dispatcher.Post(() =>
    {
        if (!_disposed)
        {
            LocalStatusViewed?.Invoke(this, e);
        }
    });

    private void OnStatusDeleted(object? sender, StatusDeletedEventArgs e) => _dispatcher.Post(() =>
    {
        if (_disposed)
        {
            return;
        }
        string key = MakeReceivedStatusKey(e.OwnerPeerId, e.StatusId);
        _deletedReceivedStatusKeys.Add(key);
        try
        {
            List<ReceivedStatusTombstone> tombstones = _profileStore.LoadReceivedStatusTombstones(_node.PeerId).ToList();
            ReceivedStatusViewModel? matching = ReceivedStatuses.FirstOrDefault(status =>
                status.Id == e.StatusId && status.OwnerPeerId == e.OwnerPeerId);
            DateTimeOffset expiresAt = matching?.ExpiresAt ?? DateTimeOffset.UtcNow.AddHours(24);
            tombstones.RemoveAll(item => item.OwnerPeerId == e.OwnerPeerId && item.StatusId == e.StatusId);
            tombstones.Add(new ReceivedStatusTombstone { OwnerPeerId = e.OwnerPeerId, StatusId = e.StatusId, ExpiresAt = expiresAt });
            _profileStore.SaveReceivedStatusTombstones(tombstones, _node.PeerId);
            if (matching is not null)
            {
                ReceivedStatuses.Remove(matching);
                SaveReceivedStatuses();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ProfileStoreException)
        {
            SaveWarning = "Le retrait reçu n'a pas pu être conservé durablement. Il pourrait réapparaître après redémarrage.";
        }
    });

    private static string MakeReceivedStatusKey(string ownerPeerId, string statusId) => ownerPeerId + ":" + statusId;

    private void OnStatusReceived(object? sender, StatusReceivedEventArgs e) => _dispatcher.Post(() =>
    {
        if (_disposed || e.Status.ExpiresAt <= DateTimeOffset.UtcNow
            || _deletedReceivedStatusKeys.Contains(MakeReceivedStatusKey(e.SenderPeerId, e.Status.Id))
            || ReceivedStatuses.Any(item => item.Id == e.Status.Id && item.OwnerPeerId == e.SenderPeerId))
        {
            return;
        }

        string senderName = _node.Contacts.Find(e.SenderPeerId)?.DisplayName
            ?? _node.Contacts.Find(e.SenderPeerId)?.PublicProfile?.DisplayName
            ?? (e.SenderPeerId.Length > 8 ? e.SenderPeerId[..8] : e.SenderPeerId);
        ReceivedStatuses.Insert(0, new ReceivedStatusViewModel(e.Status, senderName));
        while (ReceivedStatuses.Count > 100)
        {
            ReceivedStatuses.RemoveAt(ReceivedStatuses.Count - 1);
        }

        SaveReceivedStatuses();
    });

    private void OnContactsChanged(object? sender, EventArgs e) => _dispatcher.Post(() =>
    {
        if (_disposed)
        {
            return;
        }

        if (SelectedConversation is not null && !SelectedConversation.Info.IsGroup && _node.Contacts.Find(SelectedConversation.Info.Id) is { } contact)
        {
            SelectedContact = new ContactViewModel(contact, _node.PeerId, _node.Contacts);
        }

        RefreshContacts();
        foreach (ConversationViewModel vm in _conversationById.Values)
        {
            vm.RefreshMessages();
        }

        RefreshConversations();
    });

    private void OnPeerDiscovered(object? sender, PeerDiscoveredEventArgs e) => _dispatcher.Post(() =>
    {
        if (_disposed || DiscoveredPeers.Any(p => p.PeerId == e.PeerId))
        {
            return;
        }

        if (DiscoveredPeers.Count >= MaxDiscoveredPeersShown)
        {
            DiscoveredPeers.RemoveAt(0);
        }

        DiscoveredPeers.Add(new DiscoveredPeerViewModel(e.PeerId, e.Endpoint.ToString()));
    });

    private void OnSaveFailed(object? sender, SaveFailedEventArgs e) => _dispatcher.Post(() =>
    {
        if (!_disposed)
        {
            SaveWarning = e.Message;
        }
    });

    private void OnSaveRecovered(object? sender, EventArgs e) => _dispatcher.Post(() =>
    {
        if (!_disposed)
        {
            SaveWarning = null;
        }
    });

    private void SetError(Exception exception) => Error = exception.Message;
}

/// <summary>Annonce de découverte UDP, en attente d'une authentification TCP.</summary>
/// <param name="PeerId">Identifiant annoncé, non authentifié à ce stade.</param>
/// <param name="Endpoint">Adresse locale IP:port annoncée.</param>
public sealed record DiscoveredPeerViewModel(string PeerId, string Endpoint);

/// <summary>Informations non sensibles nécessaires à l’affichage d’une notification de nouveau message.</summary>
/// <remarks>Contient l’identifiant de la conversation à ouvrir après un clic et son nom local (contact ou groupe).</remarks>
public sealed class IncomingMessageNotificationEventArgs : EventArgs
{
    /// <summary>Crée les informations d’une notification entrante.</summary>
    public IncomingMessageNotificationEventArgs(string conversationId, string conversationName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationName);
        ConversationId = conversationId;
        ConversationName = conversationName;
    }

    /// <summary>Identifiant de la conversation à ouvrir après un clic.</summary>
    public string ConversationId { get; }

    /// <summary>Nom local du contact ou titre du groupe.</summary>
    public string ConversationName { get; }
}

/// <summary>Présentation en mémoire d'un statut entrant validé par le service réseau.</summary>
public sealed class ReceivedStatusViewModel
{
    /// <summary>Crée le modèle de présentation d'un statut reçu.</summary>
    /// <param name="status">Statut éphémère validé et reçu depuis le réseau.</param>
    /// <param name="senderName">Nom d'affichage local de l'expéditeur.</param>
    public ReceivedStatusViewModel(EphemeralStatus status, string senderName)
    {
        ArgumentNullException.ThrowIfNull(status);
        Status = status;
        Id = status.Id;
        OwnerPeerId = status.OwnerPeerId;
        SenderName = senderName;
        Text = status.Text;
        ContentLabel = status.ContentKind switch
        {
            StatusContentKind.Photo => "Photo reçue",
            StatusContentKind.Video => "Vidéo reçue",
            _ => "Texte reçu",
        };
        MediaBytes = status.MediaBytes?.ToArray();
        MediaSizeLabel = MediaBytes is null ? string.Empty : $"Média : {MediaBytes.Length / 1024d:0} Ko";
        CreatedAt = status.CreatedAt;
        ExpiresAt = status.ExpiresAt;
    }

    /// <summary>Statut reçu complet, conservé pour les opérations de consultation.</summary>
    public EphemeralStatus Status { get; }

    /// <summary>Identifiant unique du statut.</summary>
    public string Id { get; }

    /// <summary>Identifiant du pair propriétaire du statut.</summary>
    public string OwnerPeerId { get; }

    /// <summary>Nom d'affichage de l'expéditeur.</summary>
    public string SenderName { get; }

    /// <summary>Texte associé au statut, s'il existe.</summary>
    public string Text { get; }

    /// <summary>Libellé lisible du type de contenu reçu.</summary>
    public string ContentLabel { get; }

    /// <summary>Octets du média reçu, ou <see langword="null"/> pour un statut sans média.</summary>
    public byte[]? MediaBytes { get; }

    /// <summary>Taille du média formatée pour l'affichage.</summary>
    public string MediaSizeLabel { get; }

    /// <summary>Date et heure de création du statut.</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>Date et heure d'expiration du statut.</summary>
    public DateTimeOffset ExpiresAt { get; }

    /// <summary>Libellé localisé indiquant la réception et l'expiration du statut.</summary>
    public string ExpiresLabel => $"Reçu le {CreatedAt.ToLocalTime():dd/MM à HH:mm} · expire le {ExpiresAt.ToLocalTime():dd/MM à HH:mm}";
}
