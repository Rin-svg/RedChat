using System.Security.Cryptography;
using RedChat.Core;

namespace RedChat.Infrastructure;

/// <summary>Progression d'un envoi de pièce jointe.</summary>
public sealed class AttachmentProgressEventArgs : EventArgs
{
    /// <summary>Crée les informations de progression.</summary>
    public AttachmentProgressEventArgs(string fileName, long sentBytes, long totalBytes)
    {
        FileName = fileName;
        SentBytes = sentBytes;
        TotalBytes = totalBytes;
        Percentage = totalBytes == 0 ? 0 : (int)Math.Min(100, sentBytes * 100 / totalBytes);
    }

    /// <summary>Nom du fichier.</summary>
    public string FileName { get; }

    /// <summary>Octets placés dans les files d'envoi locales.</summary>
    public long SentBytes { get; }

    /// <summary>Nombre total d'octets à placer dans les files d'envoi de tous les destinataires.</summary>
    public long TotalBytes { get; }

    /// <summary>Progression de 0 à 100 pour cent.</summary>
    public int Percentage { get; }
}

/// <summary>Annonce qu'un fichier a été reçu et vérifié avant d'être placé dans le dossier des pièces jointes.</summary>
public sealed class AttachmentReceivedEventArgs : EventArgs
{
    /// <summary>Crée les informations de réception.</summary>
    public AttachmentReceivedEventArgs(string senderPeerId, string conversationId, string fileName, string filePath, long fileSize)
    {
        SenderPeerId = senderPeerId;
        ConversationId = conversationId;
        FileName = fileName;
        FilePath = filePath;
        FileSize = fileSize;
    }

    /// <summary>Pair authentifié ayant envoyé le fichier.</summary>
    public string SenderPeerId { get; }

    /// <summary>Conversation concernée.</summary>
    public string ConversationId { get; }

    /// <summary>Nom d'origine, sans chemin.</summary>
    public string FileName { get; }

    /// <summary>Chemin local du fichier vérifié.</summary>
    public string FilePath { get; }

    /// <summary>Taille en octets.</summary>
    public long FileSize { get; }
}

/// <summary>
/// Transfert de fichiers par blocs sur les sessions TCP déjà authentifiées et chiffrées.
/// Les fichiers ne sont jamais exécutés automatiquement ; les réceptions sont écrites dans un dossier dédié et vérifiées par SHA-256.
/// </summary>
public sealed class AttachmentTransferService : IDisposable
{
    private const int MaxConcurrentIncomingTransfers = 3;
    private static readonly TimeSpan TransferLifetime = TimeSpan.FromMinutes(10);
    private static readonly HashSet<string> ReservedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "CLOCK$", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };
    private readonly string _selfPeerId;
    private readonly TcpTransport _transport;
    private readonly ChatService _chat;
    private readonly string _incomingDirectory;
    private readonly object _gate = new();
    private readonly Dictionary<string, IncomingTransfer> _incoming = new(StringComparer.Ordinal);
    private readonly Timer _cleanupTimer;
    private bool _disposed;

    private sealed class IncomingTransfer : IDisposable
    {
        public required string ConversationId { get; init; }
        public required string FileName { get; init; }
        public required long FileSize { get; init; }
        public required string Sha256 { get; init; }
        public required int ChunkCount { get; init; }
        public required string TemporaryPath { get; init; }
        public required FileStream Stream { get; init; }
        public required bool[] ReceivedChunks { get; init; }
        public DateTimeOffset LastUpdated { get; set; }
        public int ReceivedCount { get; set; }

        public void Dispose()
        {
            try { Stream.Dispose(); }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            try { File.Delete(TemporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Crée le service de transfert pour un nœud et son dossier de réception.</summary>
    public AttachmentTransferService(string selfPeerId, TcpTransport transport, ChatService chat, string incomingDirectory)
    {
        if (!InputValidator.IsValidPeerId(selfPeerId))
        {
            throw new ArgumentException("PeerId local invalide.", nameof(selfPeerId));
        }

        _selfPeerId = selfPeerId;
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        _incomingDirectory = Path.GetFullPath(incomingDirectory ?? throw new ArgumentNullException(nameof(incomingDirectory)));
        Directory.CreateDirectory(_incomingDirectory);
        CleanupAbandonedTemporaryFiles();
        _cleanupTimer = new Timer(_ =>
        {
            lock (_gate)
            {
                if (!_disposed)
                {
                    CleanupExpiredTransfers();
                }
            }
        }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>Levé après réception et vérification SHA-256 d'un fichier.</summary>
    public event EventHandler<AttachmentReceivedEventArgs>? FileReceived;

    /// <summary>Levé après la mise en file de chaque bloc sortant.</summary>
    public event EventHandler<AttachmentProgressEventArgs>? ProgressChanged;

    /// <summary>Dossier dans lequel les pièces jointes reçues sont enregistrées.</summary>
    public string IncomingDirectory => _incomingDirectory;

    /// <summary>
    /// Envoie un fichier à tous les membres de la conversation. Les destinataires doivent être connectés ; aucun envoi partiel
    /// volontaire vers un groupe hors ligne n'est tenté. Le fichier est limité à 25 Mio.
    /// </summary>
    public async Task SendFileAsync(string conversationId, string sourcePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ObjectDisposedException.ThrowIf(_disposed, this);

        ConversationInfo? info = _chat.GetConversations().FirstOrDefault(c => c.Id == conversationId);
        if (info is null)
        {
            if (!InputValidator.IsValidPeerId(conversationId) || conversationId == _selfPeerId)
            {
                throw new InvalidOperationException("Conversation introuvable.");
            }

            info = _chat.EnsureDirectConversation(conversationId);
        }

        string fileName = Path.GetFileName(sourcePath);
        if (!IsSafeFileName(fileName))
        {
            throw new InvalidDataException("Nom de fichier non pris en charge.");
        }

        FileInfo fileInfo = new(sourcePath);
        if (!fileInfo.Exists || fileInfo.Length is <= 0 or > ProtocolLimits.MaxAttachmentBytes)
        {
            throw new InvalidDataException("Le fichier doit peser entre 1 octet et 25 Mio.");
        }

        string[] recipients = info.IsGroup
            ? info.Members.Where(member => member != _selfPeerId).ToArray()
            : new[] { info.Id };
        if (recipients.Length == 0 || recipients.Any(peer => !_transport.IsConnected(peer)))
        {
            throw new IOException("Tous les destinataires doivent être connectés pour envoyer cette pièce jointe.");
        }

        byte[] content = await File.ReadAllBytesAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        if (content.LongLength != fileInfo.Length)
        {
            throw new IOException("Le fichier a changé pendant sa lecture. Recommencez l'envoi.");
        }

        string transferId = Guid.NewGuid().ToString("D");
        string sha256 = Convert.ToHexString(SHA256.HashData(content));
        int chunkCount = checked((content.Length + ProtocolLimits.AttachmentChunkBytes - 1) / ProtocolLimits.AttachmentChunkBytes);

        long totalBytes = checked(content.LongLength * recipients.Length);
        long sentBytes = 0;
        foreach (string recipient in recipients)
        {
            string recipientConversationId = info.IsGroup ? info.Id : recipient;
            for (int index = 0; index < chunkCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(_disposed, this);
                int offset = index * ProtocolLimits.AttachmentChunkBytes;
                int length = Math.Min(ProtocolLimits.AttachmentChunkBytes, content.Length - offset);
                Packet packet = new()
                {
                    Kind = PacketKind.AttachmentChunk,
                    Id = Guid.NewGuid().ToString("D"),
                    ConversationId = recipientConversationId,
                    SentAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    AttachmentTransferId = transferId,
                    AttachmentFileName = fileName,
                    AttachmentFileSize = content.LongLength,
                    AttachmentSha256 = sha256,
                    AttachmentChunkIndex = index,
                    AttachmentChunkCount = chunkCount,
                    AttachmentChunkData = content.AsSpan(offset, length).ToArray(),
                };

                if (!_transport.TrySend(recipient, packet))
                {
                    throw new IOException("La connexion a été interrompue pendant le transfert. Le fichier n'a pas été confirmé comme envoyé.");
                }

                sentBytes += length;
                ProgressChanged?.Invoke(this, new AttachmentProgressEventArgs(fileName, sentBytes, totalBytes));
            }
        }

        _chat.SendText(conversationId, $"📎 {fileName} ({content.LongLength:N0} octets)");
    }

    /// <summary>Traite un bloc provenant d'un pair dont l'identité a été vérifiée par le handshake TCP.</summary>
    public void Receive(string senderPeerId, Packet packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (packet.Kind != PacketKind.AttachmentChunk)
        {
            throw new ArgumentException("Un paquet de bloc de fichier est attendu.", nameof(packet));
        }

        PacketSerializer.Validate(packet, DateTimeOffset.UtcNow);
        ValidateConversation(senderPeerId, packet.ConversationId);
        AttachmentReceivedEventArgs? completed = null;
        string key = senderPeerId + ":" + packet.AttachmentTransferId;

        lock (_gate)
        {
            CleanupExpiredTransfers();
            if (!_incoming.TryGetValue(key, out IncomingTransfer? transfer))
            {
                if (packet.AttachmentChunkIndex != 0)
                {
                    throw new ProtocolViolationException("Un transfert doit commencer par le premier bloc.");
                }

                if (_incoming.Count >= MaxConcurrentIncomingTransfers)
                {
                    throw new ProtocolViolationException("Trop de transferts de fichiers simultanés.");
                }

                string temporaryPath = Path.Combine(_incomingDirectory, "." + Guid.NewGuid().ToString("N") + ".part");
                FileStream stream = new(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 64 * 1024, FileOptions.RandomAccess);
                try
                {
                    stream.SetLength(packet.AttachmentFileSize!.Value);
                    transfer = new IncomingTransfer
                    {
                        ConversationId = packet.ConversationId,
                        FileName = packet.AttachmentFileName!,
                        FileSize = packet.AttachmentFileSize.Value,
                        Sha256 = packet.AttachmentSha256!,
                        ChunkCount = packet.AttachmentChunkCount!.Value,
                        TemporaryPath = temporaryPath,
                        Stream = stream,
                        ReceivedChunks = new bool[packet.AttachmentChunkCount.Value],
                        LastUpdated = DateTimeOffset.UtcNow,
                    };
                    _incoming.Add(key, transfer);
                }
                catch
                {
                    stream.Dispose();
                    try { File.Delete(temporaryPath); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    throw;
                }
            }
            else if (transfer.FileName != packet.AttachmentFileName
                || transfer.FileSize != packet.AttachmentFileSize
                || transfer.Sha256 != packet.AttachmentSha256
                || transfer.ChunkCount != packet.AttachmentChunkCount
                || transfer.ConversationId != packet.ConversationId)
            {
                transfer.Dispose();
                _incoming.Remove(key);
                throw new ProtocolViolationException("Les métadonnées du transfert ont changé en cours de réception.");
            }

            int chunkIndex = packet.AttachmentChunkIndex!.Value;
            transfer.LastUpdated = DateTimeOffset.UtcNow;
            if (!transfer.ReceivedChunks[chunkIndex])
            {
                long offset = (long)chunkIndex * ProtocolLimits.AttachmentChunkBytes;
                transfer.Stream.Position = offset;
                transfer.Stream.Write(packet.AttachmentChunkData!);
                transfer.ReceivedChunks[chunkIndex] = true;
                transfer.ReceivedCount++;
            }

            if (transfer.ReceivedCount == transfer.ChunkCount)
            {
                transfer.Stream.Flush(flushToDisk: true);
                transfer.Stream.Dispose();
                string actualHash;
                using (FileStream read = new(transfer.TemporaryPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    actualHash = Convert.ToHexString(SHA256.HashData(read));
                }

                if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actualHash), Convert.FromHexString(transfer.Sha256)))
                {
                    File.Delete(transfer.TemporaryPath);
                    _incoming.Remove(key);
                    throw new InvalidDataException("L'intégrité du fichier reçu n'est pas valide ; le fichier a été rejeté.");
                }

                string destination = GetUniqueDestination(transfer.FileName);
                File.Move(transfer.TemporaryPath, destination);
                completed = new AttachmentReceivedEventArgs(senderPeerId, transfer.ConversationId, transfer.FileName, destination, transfer.FileSize);
                _incoming.Remove(key);
            }
        }

        if (completed is not null)
        {
            FileReceived?.Invoke(this, completed);
        }
    }

    /// <summary>Ferme et supprime les transferts incomplets.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _cleanupTimer.Dispose();
            foreach (IncomingTransfer transfer in _incoming.Values)
            {
                transfer.Dispose();
            }

            _incoming.Clear();
        }

        GC.SuppressFinalize(this);
    }

    private void ValidateConversation(string senderPeerId, string conversationId)
    {
        if (!InputValidator.IsValidPeerId(senderPeerId) || senderPeerId == _selfPeerId)
        {
            throw new ProtocolViolationException("Expéditeur de pièce jointe invalide.");
        }

        if (InputValidator.IsValidPeerId(conversationId))
        {
            if (conversationId != _selfPeerId)
            {
                throw new ProtocolViolationException("Pièce jointe adressée à un autre pair.");
            }

            return;
        }

        ConversationInfo? group = _chat.GetConversations().FirstOrDefault(c => c.Id == conversationId && c.IsGroup);
        if (group is null || !group.Members.Contains(senderPeerId, StringComparer.Ordinal))
        {
            throw new ProtocolViolationException("Pièce jointe reçue depuis un groupe inconnu ou un expéditeur non membre.");
        }
    }

    private void CleanupAbandonedTemporaryFiles()
    {
        DateTime cutoff = DateTime.UtcNow - TransferLifetime;
        foreach (string path in Directory.EnumerateFiles(_incomingDirectory, ".*.part", SearchOption.TopDirectoryOnly))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(path) < cutoff)
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
                // Le fichier temporaire sera réévalué lors d'un prochain démarrage.
            }
            catch (UnauthorizedAccessException)
            {
                // Ne jamais empêcher l'ouverture de la messagerie pour un résidu inaccessible.
            }
        }
    }

    private void CleanupExpiredTransfers()
    {
        DateTimeOffset cutoff = DateTimeOffset.UtcNow - TransferLifetime;
        foreach (string key in _incoming.Where(pair => pair.Value.LastUpdated < cutoff).Select(pair => pair.Key).ToArray())
        {
            _incoming[key].Dispose();
            _incoming.Remove(key);
        }
    }

    private string GetUniqueDestination(string fileName)
    {
        string safeName = SanitizeFileName(fileName);
        string destination = Path.Combine(_incomingDirectory, safeName);
        if (!File.Exists(destination))
        {
            return destination;
        }

        string stem = Path.GetFileNameWithoutExtension(safeName);
        string extension = Path.GetExtension(safeName);
        for (int index = 2; index < 10_000; index++)
        {
            destination = Path.Combine(_incomingDirectory, $"{stem} ({index}){extension}");
            if (!File.Exists(destination))
            {
                return destination;
            }
        }

        throw new IOException("Impossible de choisir un nom unique pour le fichier reçu.");
    }

    private static bool IsSafeFileName(string name) =>
        name.Length is > 0 and <= ProtocolLimits.MaxAttachmentFileNameChars
        && name == Path.GetFileName(name)
        && !name.Any(char.IsControl)
        && !name.Any(ch => "<>:\"/\\|?*".Contains(ch))
        && !name.EndsWith('.') && !name.EndsWith(' ')
        && name is not "." and not ".."
        && !ReservedFileNames.Contains(name.Split('.')[0]);

    private static string SanitizeFileName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(ch => invalid.Contains(ch) || char.IsControl(ch) ? '_' : ch).ToArray());
    }
}
