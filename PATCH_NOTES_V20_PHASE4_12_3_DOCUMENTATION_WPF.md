# Correctif v20 — documentation WPF

- Ajout du commentaire XML du constructeur `StatusCenterWindow(MainViewModel)`.
- Ajout de la documentation du paramètre `viewModel`.
- Aucun changement fonctionnel et aucune règle d'analyse désactivée.

## Vérification

L'environnement de préparation ne dispose pas du SDK .NET. La compilation doit être confirmée sur Windows avec `dotnet build .\RedChat.sln -c Release -warnaserror`, puis les tests avec `dotnet test .\RedChat.sln -c Release`.
