# RedChat v11 — Mon profil (première étape locale)

## Ajouts

- Modèle `PersonalProfile` distinct de l'identité cryptographique.
- Champs publics : nom (1–40 caractères), statut (80 caractères), description (280 caractères) et avatar PNG/JPEG (512 Kio maximum).
- Association stricte du profil au `PeerId` local : un profil ne peut pas être enregistré pour une autre identité.
- Fichier séparé `personal-profile.dat`, protégé avec le protecteur existant et écrit via le mécanisme atomique de `ProfileStore`.
- `PersonalProfileViewModel` pour charger, valider et sauvegarder le profil.
- Fenêtre WPF « Mon profil », accessible depuis le rail de navigation, avec sélection/suppression de photo et copie du PeerId.
- Tests d'infrastructure pour la persistance, la normalisation, l'association au PeerId et le rejet d'avatar invalide.

## Limites explicites de cette étape

- Le profil est actuellement local à l'appareil ; aucun paquet réseau de profil n'est encore émis ou accepté.
- Les contacts ne voient donc pas encore le nom public ni la photo de ce profil.
- Le code de sécurité reste calculé pour une paire de correspondants et est consulté depuis une conversation.
- L'éditeur ne remplace ni ne régénère l'identité cryptographique.

## Validation

Compilation et tests Windows à exécuter sur la machine de développement :

```powershell
dotnet restore .\RedChat.sln
dotnet build .\RedChat.sln -c Release -warnaserror
dotnet test .\RedChat.sln -c Release
```

Cette archive n'est pas déclarée compilée : l'environnement de génération ne dispose pas du SDK .NET. La validation utilisateur de v10 (428 tests) reste le dernier résultat confirmé.
