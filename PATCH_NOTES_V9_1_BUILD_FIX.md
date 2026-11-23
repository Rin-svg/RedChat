# RedChat v9.1 — correctif de compilation

## Correction

- Ajout du commentaire XML de documentation à `ConversationViewModel.AvatarInitials`.
- Corrige l'erreur `CS1591` lors d'une compilation avec `-warnaserror`.

## Validation

- Modification ciblée d'un commentaire XML uniquement ; aucun comportement applicatif n'est modifié.
- La compilation .NET et les tests doivent être relancés sur Windows : le SDK .NET n'est pas disponible dans l'environnement de préparation.

## Commandes

```powershell
dotnet restore .\RedChat.sln
dotnet build .\RedChat.sln -c Release -warnaserror
dotnet test .\RedChat.sln -c Release
dotnet run --project .\src\RedChat.Wpf\RedChat.Wpf.csproj
```
