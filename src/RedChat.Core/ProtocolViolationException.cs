namespace RedChat.Core;

/// <summary>
/// Le pair distant (ou des données reçues) ont violé le protocole : trame trop grande, signature fausse, rejeu, JSON invalide…
/// Le gestionnaire réseau doit couper la connexion. Le message est destiné au JOURNAL (jamais affiché tel quel à l'utilisateur).
/// </summary>
public sealed class ProtocolViolationException : Exception
{
    /// <summary>Crée l'exception sans message.</summary>
    public ProtocolViolationException() { }

    /// <summary>Crée l'exception avec un message.</summary>
    public ProtocolViolationException(string message) : base(message) { }

    /// <summary>Crée l'exception avec un message et une cause.</summary>
    public ProtocolViolationException(string message, Exception innerException) : base(message, innerException) { }
}
