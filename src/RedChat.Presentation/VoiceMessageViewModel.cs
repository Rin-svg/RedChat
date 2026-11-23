namespace RedChat.Presentation;

/// <summary>Représente un message vocal archivé localement et disponible pour une nouvelle écoute.</summary>
public sealed class VoiceMessageViewModel
{
    /// <summary>Crée une entrée d'historique vocal.</summary>
    public VoiceMessageViewModel(string filePath, string fileName, string direction, DateTime lastWriteTime)
    {
        FilePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        FileName = fileName ?? throw new ArgumentNullException(nameof(fileName));
        Direction = direction ?? throw new ArgumentNullException(nameof(direction));
        LastWriteTime = lastWriteTime;
    }

    /// <summary>Chemin du fichier audio local.</summary>
    public string FilePath { get; }

    /// <summary>Nom du fichier vocal.</summary>
    public string FileName { get; }

    /// <summary>Indique si le vocal a été envoyé ou reçu.</summary>
    public string Direction { get; }

    /// <summary>Date locale de réception ou d'archivage.</summary>
    public DateTime LastWriteTime { get; }

    /// <summary>Libellé affiché dans la liste des vocaux.</summary>
    public string DisplayLabel => $"{Direction} · {FileName} · {LastWriteTime:dd/MM/yyyy HH:mm}";
}
