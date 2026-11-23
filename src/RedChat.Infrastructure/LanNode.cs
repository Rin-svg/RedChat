using System.Net;
using System.Net.Sockets;
using RedChat.Core;
using ProtocolViolationException = RedChat.Core.ProtocolViolationException;

namespace RedChat.Infrastructure;

/// <summary>Réglages d'un nœud (les valeurs par défaut conviennent à l'application ; le reste sert aux tests).</summary>
public sealed class LanNodeOptions
{
    /// <summary>Interface d'écoute TCP (défaut : toutes).</summary>
    public IPAddress? BindAddress { get; init; }

    /// <summary>Active la découverte UDP.</summary>
    public bool EnableDiscovery { get; init; } = true;

    /// <summary>Port UDP de découverte.</summary>
    public int DiscoveryPort { get; init; } = UdpDiscovery.DefaultPort;

    /// <summary>Destination des annonces (défaut : diffusion générale).</summary>
    public IPAddress? DiscoveryTarget { get; init; }

    /// <summary>Période des annonces.</summary>
    public TimeSpan? DiscoveryInterval { get; init; }

    /// <summary>Dossier des fichiers reçus (défaut : LocalAppData/RedChat/Attachments/Incoming).</summary>
    public string? AttachmentDirectory { get; init; }
}

/// <summary>
/// Façade : une identité + un <see cref="ChatService"/> + un <see cref="TcpTransport"/> + la découverte UDP, câblés ensemble.
/// C'est l'unique objet que les ViewModels auront à connaître.
/// </summary>
public sealed class LanNode : IAsyncDisposable
{
    private readonly LocalIdentity _identity;
    private readonly LanNodeOptions _options;
    private readonly TimeProvider _clock;
    private readonly CancellationTokenSource _cts = new();
    private static readonly HashSet<string> ReservedStatusMediaNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };
    private UdpDiscovery? _discovery;
    private PersonalProfile? _localProfile;
    private int _disposed;

    /// <summary>Crée le nœud (sans le démarrer).</summary>
    public LanNode(LocalIdentity identity, LanNodeOptions? options = null, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        _identity = identity;
        _options = options ?? new LanNodeOptions();
        _clock = clock ?? TimeProvider.System;
        Transport = new TcpTransport(identity, _options.BindAddress, _clock);
        Chat = new ChatService(identity.PeerId, Transport, _clock);
        Contacts = new ContactBook(identity.PeerId, _clock);
        string attachmentsPath = _options.AttachmentDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RedChat", "Attachments", "Incoming");
        Attachments = new AttachmentTransferService(identity.PeerId, Transport, Chat, attachmentsPath);
        Statuses = new StatusTransferService(identity.PeerId, _clock);
        Calls = new CallSignalingService(identity.PeerId, Transport, _clock);
        Transport.Attach(Chat);
        Transport.PeerConnected += OnPeerConnected;
        Transport.PacketReceived += OnPacketReceived;
    }

    /// <summary>PeerId local (identité cryptographique de ce nœud).</summary>
    public string PeerId => _identity.PeerId;

    /// <summary>Service de conversation.</summary>
    public ChatService Chat { get; }

    /// <summary>Transport TCP.</summary>
    public TcpTransport Transport { get; }

    /// <summary>Transfert sécurisé de pièces jointes sur les connexions TCP authentifiées.</summary>
    public AttachmentTransferService Attachments { get; }

    /// <summary>Réception vérifiée des statuts éphémères reçus sur les connexions authentifiées.</summary>
    public StatusTransferService Statuses { get; }

    /// <summary>Signalisation des appels privés sur les connexions chiffrées existantes (sans média temps réel dans cette version).</summary>
    public CallSignalingService Calls { get; }

    /// <summary>Carnet de contacts local. Tout pair qui réussit le handshake y est enregistré à sa première apparition (TOFU), non vérifié.</summary>
    public ContactBook Contacts { get; }

    /// <summary>Résultat d'une tentative de diffusion d'un statut aux seuls contacts autorisés et actuellement connectés.</summary>
    public sealed record StatusPublishResult(int AuthorizedContacts, int ConnectedContacts, int QueuedContacts, int FailedContacts);

    /// <summary>
    /// Publie un statut privé. L'audience est évaluée localement pour chaque contact ; aucune liste d'audience n'est envoyée.
    /// Les destinataires hors ligne ne reçoivent pas encore de file de rattrapage et sont comptés comme non transmis.
    /// </summary>
    public StatusPublishResult PublishStatus(EphemeralStatus status, string? mediaName = null)
    {
        ArgumentNullException.ThrowIfNull(status);
        EphemeralStatus validated = status.Validate(_clock.GetUtcNow(), isLocalOwner: true);
        if (validated.OwnerPeerId != PeerId)
        {
            throw new ArgumentException("Le statut doit appartenir à l'identité locale.", nameof(status));
        }

        Contact[] contacts = Contacts.GetAll().ToArray();
        HashSet<string> knownPeerIds = contacts.Select(contact => contact.PeerId).ToHashSet(StringComparer.Ordinal);
        if (validated.Audience.PeerIds.Any(peerId => !knownPeerIds.Contains(peerId)))
        {
            throw new ArgumentException("L'audience contient un identifiant qui n'est pas dans le carnet de contacts.", nameof(status));
        }

        string[] authorized = contacts.Where(contact => validated.Audience.Allows(contact.PeerId))
            .Select(contact => contact.PeerId).ToArray();
        HashSet<string> connected = Transport.GetConnectedPeerIds().ToHashSet(StringComparer.Ordinal);
        int connectedAuthorized = authorized.Count(connected.Contains);
        int queued = 0;
        int failed = 0;
        byte[]? media = validated.MediaBytes;
        string? hash = media is null ? null : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(media));
        byte[][] mediaChunks = media is null ? Array.Empty<byte[]>() : SplitStatusMedia(media);

        foreach (string peerId in authorized.Where(connected.Contains))
        {
            DateTimeOffset sentAt = _clock.GetUtcNow();
            Packet metadata = new()
            {
                Kind = PacketKind.StatusPublish, Id = Guid.NewGuid().ToString("D"), ConversationId = peerId,
                SentAtUnixMs = sentAt.ToUnixTimeMilliseconds(), StatusId = validated.Id,
                StatusContentKind = validated.ContentKind, StatusText = validated.Text,
                StatusMediaName = media is null ? null : ValidateMediaName(mediaName),
                StatusMediaSize = media?.LongLength, StatusMediaSha256 = hash,
                StatusCreatedAtUnixMs = validated.CreatedAt.ToUnixTimeMilliseconds(),
                StatusExpiresAtUnixMs = validated.ExpiresAt.ToUnixTimeMilliseconds(),
            };

            if (!Transport.TrySend(peerId, metadata))
            {
                failed++;
                continue;
            }

            bool allQueued = true;
            if (media is not null && hash is not null)
            {
                for (int index = 0; index < mediaChunks.Length; index++)
                {
                    Packet packet = new()
                    {
                        Kind = PacketKind.StatusMediaChunk, Id = Guid.NewGuid().ToString("D"), ConversationId = peerId,
                        SentAtUnixMs = _clock.GetUtcNow().ToUnixTimeMilliseconds(), StatusId = validated.Id,
                        StatusMediaSize = media.LongLength, StatusMediaSha256 = hash,
                        StatusChunkIndex = index, StatusChunkCount = mediaChunks.Length, StatusChunkData = mediaChunks[index],
                    };
                    if (!Transport.TrySend(peerId, packet))
                    {
                        allQueued = false;
                        break;
                    }
                }
            }

            if (allQueued)
            {
                queued++;
            }
            else
            {
                failed++;
            }
        }

        return new StatusPublishResult(authorized.Length, connectedAuthorized, queued, failed + (authorized.Length - connectedAuthorized));
    }

    /// <summary>Envoie un reçu de consultation à l’auteur d’un statut reçu.</summary>
    public bool SendStatusViewReceipt(string ownerPeerId, string statusId)
    {
        if (!InputValidator.IsValidPeerId(ownerPeerId) || ownerPeerId == PeerId || !InputValidator.IsValidGuid(statusId))
        {
            throw new ArgumentException("Destinataire ou identifiant de statut invalide.");
        }

        Packet packet = new()
        {
            Kind = PacketKind.StatusViewReceipt,
            Id = Guid.NewGuid().ToString("D"),
            ConversationId = ownerPeerId,
            SentAtUnixMs = _clock.GetUtcNow().ToUnixTimeMilliseconds(),
            StatusId = statusId,
        };
        return Transport.TrySend(ownerPeerId, packet);
    }

    /// <summary>Diffuse le retrait d’un statut aux seuls contacts qui avaient été autorisés lors de sa publication.</summary>
    public int DeleteStatus(string statusId, IEnumerable<string> authorizedPeerIds)
    {
        if (!InputValidator.IsValidGuid(statusId))
        {
            throw new ArgumentException("Identifiant de statut invalide.", nameof(statusId));
        }
        ArgumentNullException.ThrowIfNull(authorizedPeerIds);
        string[] recipients = authorizedPeerIds.Distinct(StringComparer.Ordinal).ToArray();
        if (recipients.Length > 256 || recipients.Any(id => !InputValidator.IsValidPeerId(id) || id == PeerId))
        {
            throw new ArgumentException("Liste de destinataires invalide.", nameof(authorizedPeerIds));
        }

        int sent = 0;
        foreach (string peerId in recipients)
        {
            Packet packet = new()
            {
                Kind = PacketKind.StatusDelete, Id = Guid.NewGuid().ToString("D"),
                ConversationId = peerId, SentAtUnixMs = _clock.GetUtcNow().ToUnixTimeMilliseconds(),
                StatusId = statusId,
            };
            if (Transport.TrySend(peerId, packet))
            {
                sent++;
            }
        }
        return sent;
    }

    /// <summary>Envoie un retrait à un seul contact authentifié et actuellement connecté.</summary>
    public bool SendStatusDeleteToPeer(string peerId, string statusId)
    {
        if (!InputValidator.IsValidPeerId(peerId) || peerId == PeerId || !InputValidator.IsValidGuid(statusId))
        {
            throw new ArgumentException("Destinataire ou identifiant de statut invalide.");
        }
        Packet packet = new()
        {
            Kind = PacketKind.StatusDelete, Id = Guid.NewGuid().ToString("D"),
            ConversationId = peerId, SentAtUnixMs = _clock.GetUtcNow().ToUnixTimeMilliseconds(), StatusId = statusId,
        };
        return Transport.TrySend(peerId, packet);
    }

    private static byte[][] SplitStatusMedia(byte[] media)
    {
        int count = (media.Length + ProtocolLimits.StatusMediaChunkBytes - 1) / ProtocolLimits.StatusMediaChunkBytes;
        byte[][] chunks = new byte[count][];
        for (int index = 0; index < count; index++)
        {
            int offset = index * ProtocolLimits.StatusMediaChunkBytes;
            int length = Math.Min(ProtocolLimits.StatusMediaChunkBytes, media.Length - offset);
            chunks[index] = media.AsSpan(offset, length).ToArray();
        }

        return chunks;
    }

    private static string ValidateMediaName(string? mediaName)
    {
        string candidate = string.IsNullOrWhiteSpace(mediaName) ? "status-media" : Path.GetFileName(mediaName) ?? "status-media";
        string baseName = candidate.Split('.')[0];
        if (candidate.Length is 0 or > ProtocolLimits.MaxStatusMediaNameChars
            || candidate.Any(char.IsControl) || candidate.Any(ch => "<>:\"/\\|?*".Contains(ch))
            || candidate.EndsWith('.') || candidate.EndsWith(' ') || candidate is "." or ".."
            || ReservedStatusMediaNames.Contains(baseName))
        {
            return "status-media";
        }

        return candidate;
    }

    /// <summary>Configure le profil public local et le diffuse aux connexions authentifiées existantes.</summary>
    public void SetLocalProfile(PersonalProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        PersonalProfile validated = profile.Validate(PeerId);
        _localProfile = validated;
        foreach (string peerId in Transport.GetConnectedPeerIds())
        {
            SendProfile(peerId, validated);
        }
    }

    /// <summary>Découverte : un pair est apparu (que la connexion automatique ait lieu ou non).</summary>
    public event EventHandler<PeerDiscoveredEventArgs>? PeerDiscovered;

    /// <summary>Démarre l'écoute TCP puis, si activée, la découverte.</summary>
    public void Start()
    {
        Transport.Start();
        if (_options.EnableDiscovery)
        {
            _discovery = new UdpDiscovery(_identity.PeerId, Transport.Port, _options.DiscoveryPort, _options.DiscoveryTarget, _options.BindAddress, _options.DiscoveryInterval);
            _discovery.PeerDiscovered += OnDiscovered;
            _discovery.Start();
        }
    }

    /// <summary>
    /// Connexion manuelle « IP:port » (secours quand la diffusion est bloquée). Refuse toute adresse hors réseau local et tout nom DNS.
    /// Retourne le PeerId authentifié du pair.
    /// </summary>
    public Task<string> ConnectManualAsync(string endpointText, CancellationToken ct = default)
    {
        if (!InputValidator.TryParseLanEndpoint(endpointText, out IPEndPoint? ep) || ep is null)
        {
            throw new ArgumentException("Adresse invalide : IP de réseau local et port 1024-65535 attendus (ex. 192.168.1.20:50123).", nameof(endpointText));
        }

        return Transport.ConnectAsync(ep, expectedPeerId: null, ct);
    }

    /// <summary>Arrête tout. Peut être appelé plusieurs fois sans effet supplémentaire.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        _discovery?.Dispose();
        await Transport.DisposeAsync().ConfigureAwait(false);
        Attachments.Dispose();
        Statuses.Dispose();
        _cts.Dispose();
    }

    private void OnPeerConnected(object? sender, PeerConnectionEventArgs e)
    {
        try
        {
            _ = Contacts.EnsureKnown(e.PeerId, out _);
        }
        catch (InvalidOperationException)
        {
            // Carnet plein : la conversation fonctionne quand même (le pair s'affichera sous son identifiant court).
        }

        if (_localProfile is { } profile)
        {
            SendProfile(e.PeerId, profile);
        }
    }

    private void OnPacketReceived(object? sender, PeerPacketEventArgs e)
    {
        if (e.Packet.Kind == PacketKind.AttachmentChunk)
        {
            Attachments.Receive(e.PeerId, e.Packet);
            return;
        }

        if (e.Packet.Kind == PacketKind.CallControl)
        {
            Calls.Receive(e.PeerId, e.Packet);
            return;
        }

        if (e.Packet.Kind is PacketKind.StatusPublish or PacketKind.StatusMediaChunk or PacketKind.StatusViewReceipt or PacketKind.StatusDelete)
        {
            Statuses.Receive(e.PeerId, e.Packet);
            return;
        }

        if (e.Packet.Kind != PacketKind.ProfileUpdate)
        {
            return;
        }

        Packet packet = e.Packet;
        if (!string.Equals(packet.ProfilePeerId, e.PeerId, StringComparison.Ordinal))
        {
            throw new ProtocolViolationException("Le profil reçu ne correspond pas à l'identité authentifiée.");
        }

        PersonalProfile profile = new PersonalProfile()
        {
            PeerId = packet.ProfilePeerId!,
            DisplayName = packet.ProfileDisplayName!,
            Status = packet.ProfileStatus!,
            About = packet.ProfileAbout!,
            AvatarBytes = packet.ProfileAvatarBytes,
        }.Validate(e.PeerId);
        try
        {
            _ = Contacts.EnsureKnown(e.PeerId, out _);
            Contacts.UpdatePublicProfile(e.PeerId, profile);
        }
        catch (InvalidOperationException)
        {
            // Carnet plein : ne pas interrompre une conversation déjà authentifiée.
        }
    }

    private void SendProfile(string peerId, PersonalProfile profile)
    {
        Packet packet = new()
        {
            Kind = PacketKind.ProfileUpdate,
            Id = Guid.NewGuid().ToString("D"),
            ConversationId = PeerId,
            SentAtUnixMs = _clock.GetUtcNow().ToUnixTimeMilliseconds(),
            ProfilePeerId = profile.PeerId,
            ProfileDisplayName = profile.DisplayName,
            ProfileStatus = profile.Status,
            ProfileAbout = profile.About,
            ProfileAvatarBytes = profile.AvatarBytes?.ToArray(),
        };
        _ = Transport.TrySend(peerId, packet);
    }

    private void OnDiscovered(object? sender, PeerDiscoveredEventArgs e)
    {
        PeerDiscovered?.Invoke(this, e);

        // Un seul des deux pairs ouvre la connexion (le plus petit PeerId) : évite le double appel simultané dans le cas courant.
        // Si ce pair n'entend pas l'autre, l'autre ne se connectera jamais seul : la connexion manuelle reste le secours.
        if (string.CompareOrdinal(_identity.PeerId, e.PeerId) < 0 && !Transport.IsConnected(e.PeerId))
        {
            _ = Task.Run(() => TryConnectAsync(e), CancellationToken.None);
        }
    }

    private async Task TryConnectAsync(PeerDiscoveredEventArgs e)
    {
        try
        {
            _ = await Transport.ConnectAsync(e.Endpoint, e.PeerId, _cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ProtocolViolationException or IOException or SocketException or TimeoutException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            // le pair n'est pas joignable ou n'est pas celui qu'il prétend être : on réessaiera à sa prochaine annonce.
        }
    }
}
