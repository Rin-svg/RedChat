namespace RedChat.Core;

/// <summary>
/// Règles métier du transfert d'un message vers une autre conversation.
/// Aucun accès réseau ni disque : la règle est testable sans WPF et sans <see cref="ChatService"/>.
/// </summary>
public static class MessageForwardPolicy
{
    /// <summary>
    /// Indique pourquoi un message ne peut pas être transféré vers la conversation cible.
    /// </summary>
    /// <param name="message">Message d'origine, ou null s'il est introuvable.</param>
    /// <param name="targetConversationId">Conversation de destination (PeerId ou GUID de groupe).</param>
    /// <returns>Un texte d'erreur affichable, ou null si le transfert est autorisé.</returns>
    public static string? Validate(ChatMessage? message, string? targetConversationId)
    {
        if (message is null)
        {
            return "Message introuvable.";
        }

        if (message.IsDeleted)
        {
            return "Un message supprimé ne peut pas être transféré.";
        }

        if (string.IsNullOrWhiteSpace(targetConversationId) || targetConversationId.Length > 64)
        {
            return "Conversation de destination invalide.";
        }

        if (string.Equals(message.ConversationId, targetConversationId, StringComparison.Ordinal))
        {
            return "Choisis une autre conversation que celle d'origine.";
        }

        if (!InputValidator.IsValidMessageText(message.Text))
        {
            return "Ce message ne peut pas être transféré (texte vide ou invalide).";
        }

        return null;
    }
}