namespace RedChat.Infrastructure.Security;

/// <summary>
/// Levée lorsqu'un chemin fourni par l'extérieur (saisie, fichier, archive, réseau) est refusé
/// parce qu'il sortirait du dossier autorisé ou contient un élément dangereux.
/// </summary>
/// <remarks>
/// POURQUOI une exception dédiée : l'appelant peut l'attraper séparément des erreurs d'E/S ordinaires
/// (<see cref="IOException"/>) pour afficher un message adapté et journaliser une tentative suspecte.
/// SÉCURITÉ : le message ne contient JAMAIS le chemin du dossier de base (information interne).
/// </remarks>
public sealed class UnsafePathException : Exception
{
    /// <summary>Crée l'exception avec un message sûr à afficher à l'utilisateur.</summary>
    public UnsafePathException(string message) : base(message)
    {
    }

    /// <summary>Constructeur sans paramètre, requis par les analyseurs pour une exception publique.</summary>
    public UnsafePathException() : base("Chemin refusé pour des raisons de sécurité.")
    {
    }

    /// <summary>Crée l'exception avec un message et une exception interne.</summary>
    public UnsafePathException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
