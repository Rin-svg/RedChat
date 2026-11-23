# RedChat v12 — synchronisation du profil personnel

## Fonctionnalités

- Publication du profil personnel sur les connexions TCP authentifiées et chiffrées.
- Envoi automatique à la connexion et après chaque sauvegarde du profil local.
- Réception vérifiée : le PeerId déclaré dans le profil doit correspondre à l'identité authentifiée par le handshake.
- Champs synchronisés : nom public, statut, description et avatar PNG/JPEG.
- Les profils reçus sont conservés dans `contacts.dat`, avec la protection DPAPI et l'écriture atomique existantes.
- Le surnom local du carnet reste prioritaire et n'est jamais remplacé par le nom public reçu.
- Budget du cache des avatars distants : 5 Mio au total. Si le budget est atteint, le profil texte est conservé sans l'avatar supplémentaire.
- Affichage du statut et de la description publics dans les détails du contact.

## Protocole

La version du protocole passe de 1 à 2 et la trame maximale de 256 à 768 Kio pour permettre l'envoi d'un avatar de 512 Kio en base64 dans le JSON. Les clients v11 et antérieurs sont incompatibles avec v12 : mettre à jour les appareils qui doivent communiquer. Les profils ne sont jamais envoyés par les annonces UDP.

## Vérifications ajoutées

- Test de sérialisation et de validation d'un paquet de profil.
- Test du carnet : profil public distinct du surnom local.
- Test d'intégration : deux nœuds échangent leurs profils sur une connexion authentifiée.

## Validation

Le SDK .NET n'est pas disponible dans l'environnement de préparation. Exécuter sur Windows :

```powershell
dotnet restore .\RedChat.sln
dotnet build .\RedChat.sln -c Release -warnaserror
dotnet test .\RedChat.sln -c Release
dotnet run --project .\src\RedChat.Wpf\RedChat.Wpf.csproj
```
