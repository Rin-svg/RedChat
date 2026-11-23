# RedChat v13 — Favoris persistants

## Fonctionnalité
- Les conversations privées et les groupes peuvent être ajoutés ou retirés des favoris.
- Les favoris sont conservés après fermeture et redémarrage de RedChat.
- Les favoris sont propres au profil local et ne sont jamais transmis aux contacts.
- La liste est limitée à 1 000 entrées ; les identifiants sont validés (PeerId ou GUID de groupe).

## Stockage et robustesse
- Nouveau fichier protégé DPAPI : `favorites.dat`.
- Écriture atomique avec sauvegarde précédente via le mécanisme `ProfileStore` existant.
- Sauvegarde différée et regroupée avec l'historique et les contacts.
- Restauration depuis le fichier de sauvegarde et mise en quarantaine d'un contenu incohérent via le mécanisme existant.
- Sauvegarde finale à la fermeture.

## Tests ajoutés
- Ajout/retrait et notification de changement.
- Validation des identifiants et rejet des doublons/version inconnue.
- Aller-retour de persistance des favoris privés et de groupes entre deux sessions.

## Limite de validation
La compilation et les tests .NET doivent être exécutés sur Windows avec le SDK .NET 10. Ils n'ont pas été exécutés dans l'environnement de préparation, où `dotnet` n'est pas installé.
