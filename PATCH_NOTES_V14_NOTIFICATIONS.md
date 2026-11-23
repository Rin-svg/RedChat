# RedChat v14 — Notifications Windows

## Fonctionnalités ajoutées
- Une notification de la zone de notification Windows apparaît lorsqu'un nouveau message entrant arrive dans une conversation différente de celle affichée.
- La notification affiche le nom local du contact ou le titre du groupe, sans exposer le contenu du message.
- Un clic sur la notification restaure RedChat et ouvre directement la conversation concernée.
- Le menu contextuel de l'icône RedChat dans la zone de notification permet d'ouvrir l'application, d'activer/désactiver les notifications pendant la session et de quitter proprement.
- Un double-clic sur l'icône de la zone de notification restaure la fenêtre.

## Architecture
- La présentation émet un événement MVVM typé après vérification du type de changement et du message entrant.
- `IncomingMessageNotificationPolicy` isole la règle métier et la rend testable sans WPF.
- L'adaptateur Windows reste dans `RedChat.Wpf` avec `System.Windows.Forms.NotifyIcon` ; aucune dépendance de notification tierce n'est ajoutée.
- Les événements et l'icône de notification sont libérés lors de la fermeture de la fenêtre.

## Tests ajoutés
- Notification d'un message entrant dans une autre conversation.
- Absence de notification pour un message sortant.
- Absence de notification pour la conversation actuellement affichée.
- Absence de notification pour une mise à jour ou un message introuvable.

## Limites connues
- Le réglage d'activation/désactivation est conservé uniquement pendant la session actuelle ; il sera rendu persistant dans une prochaine version.
- L'affichage dépend des réglages de notifications Windows et du comportement de la zone de notification. L'icône RedChat reste disponible dans la zone de notification tant que l'application tourne.
- Le clic de notification et l'affichage réel doivent être validés sur Windows 10/11.
- Les tests .NET et la compilation WPF n'ont pas pu être exécutés dans l'environnement de préparation : le SDK `dotnet` n'y est pas installé.

## Correctif de compilation WPF (CS0104)

- Qualification explicite de `System.Windows.Application` dans la classe `App` afin de lever l’ambiguïté introduite par la référence Windows Forms nécessaire à l’icône de zone de notification.
- Aucun changement de comportement de l’application.
