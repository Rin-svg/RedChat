# Correctif v20 — documentation XML de la présentation des statuts reçus

## Changements
- Ajout des commentaires XML manquants sur le constructeur et les propriétés publiques de `ReceivedStatusViewModel`.
- Aucun changement de logique fonctionnelle.
- Aucun avertissement, analyseur ou règle de compilation désactivé.

## Vérification
Le journal Windows communiqué indique que les projets Core et Infrastructure compilent et que 454 tests réussissent. Les 12 erreurs restantes étaient des diagnostics CS1591 dans `MainViewModel.cs`. Le correctif ajoute la documentation demandée ; il reste à confirmer la compilation sur Windows.

Commandes :
```powershell
dotnet build .\RedChat.sln -c Release -warnaserror
dotnet test .\RedChat.sln -c Release
```
