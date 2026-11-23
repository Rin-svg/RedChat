using RedChat.Core;

namespace RedChat.Presentation;

/// <summary>Décide si un changement de conversation doit produire une notification locale.</summary>
public static class IncomingMessageNotificationPolicy
{
    /// <summary>Ne notifie que les nouveaux messages entrants d’une conversation qui n’est pas actuellement affichée.</summary>
    /// <param name="kind">Nature du changement reçu du service de conversation.</param>
    /// <param name="message">Message correspondant à l’événement, ou null s’il est introuvable.</param>
    /// <param name="conversationId">Identifiant de la conversation concernée.</param>
    /// <param name="selectedConversationId">Identifiant de la conversation affichée, ou null.</param>
    /// <returns>Vrai si une notification doit être présentée.</returns>
    public static bool ShouldNotify(
        ChatChangeKind kind,
        ChatMessage? message,
        string conversationId,
        string? selectedConversationId)
    {
        ArgumentNullException.ThrowIfNull(conversationId);
        return kind == ChatChangeKind.MessageAdded
            && message is { IsOutgoing: false, IsDeleted: false }
            && string.Equals(message.ConversationId, conversationId, StringComparison.Ordinal)
            && !string.Equals(selectedConversationId, conversationId, StringComparison.Ordinal);
    }
}
