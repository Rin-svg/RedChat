# RedChat — Patch v7 : carnet d'amis autonome

## Fonctionnalités
- L'onglet **Amis** affiche désormais `LanNode.Contacts.GetAll()` via une collection observable dédiée ; il ne filtre plus les conversations privées.
- Recherche de contact par nom local ou PeerId.
- Sélection d'une fiche, avatar à initiales, indicateur de vérification et panneau de détails.
- Renommage local et bascule de vérification via les commandes existantes de `ContactViewModel`.
- Ouverture d'une conversation privée depuis une fiche, même si aucun message n'a encore été envoyé.
- Nouvelle méthode `ChatService.EnsureDirectConversation` : création idempotente, validation du PeerId, aucun message artificiel, sauvegarde déclenchée seulement à la création effective.
- La collection contacts se rafraîchit après modification du carnet. Les changements continuent d'être sauvegardés par `ChatPersistence`.

## Sécurité et limites
- Les contacts sont ajoutés par `LanNode` après handshake TCP authentifié (TOFU), pas à partir d'une annonce UDP.
- Aucun faux statut « en ligne » n'est affiché ; la découverte et l'authentification restent distinctes.
- Le nom est local et n'est jamais fourni par le pair.
- Le code de sécurité est affiché pour comparaison hors bande ; la vérification reste une action explicite.
- Les avatars sont encore des initiales générées localement ; l'import d'images personnalisées est une évolution ultérieure.

## Tests ajoutés
- Création d'une conversation privée vide.
- Idempotence (pas de doublon, pas de deuxième événement ni sauvegarde inutile).
- Rejet d'un PeerId invalide et de sa propre identité.

## Vérifications locales
- Audit C# : 0 alerte sur 37 fichiers.
- Tous les XAML et csproj passent le parseur XML.
- Noms XAML sans doublons ; 12 gestionnaires d'événements référencés existent.
- Le SDK `dotnet` n'est pas installé dans l'environnement de préparation : build et tests .NET non exécutés ici.
