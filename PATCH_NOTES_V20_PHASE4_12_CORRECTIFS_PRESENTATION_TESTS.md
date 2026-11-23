# Correctifs — compilation Presentation et tests Core

## Modifications
- Déclaration publique de `MainViewModel.ReceivedStatuses`, déjà initialisée dans le constructeur et utilisée par les traitements des statuts reçus.
- Ajout d'accolades aux instructions `foreach` / `if` signalées par IDE0011 dans `MainViewModel.cs`.
- Remplacement des expressions `new()` sans type cible dans `EphemeralStatusTests.cs` par `new StatusAudience(...)` et `new EphemeralStatus(...)` explicites.

## Vérification
Les sources ont été modifiées et l'archive a été vérifiée au niveau ZIP. La compilation .NET et les tests n'ont pas été exécutés dans cet environnement ; ils doivent être relancés sous Windows avec les commandes du fichier de prise en main.
