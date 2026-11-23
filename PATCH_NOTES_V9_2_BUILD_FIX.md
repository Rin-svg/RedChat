# RedChat v9.2 — correctif System.IO

## Correction
Ajout de `using System.IO;` dans `src/RedChat.Wpf/MainWindow.xaml.cs`. Cela résout les références non qualifiées à `FileInfo`, `InvalidDataException`, `FileStream`, `File`, `Path`, `Directory`, `FileMode`, `FileAccess`, `FileShare` et `IOException`.

Aucun comportement fonctionnel n’a été modifié.

## Validation
Le correctif a été inspecté dans le code source. La compilation .NET/WPF n’est pas disponible dans l’environnement de préparation ; exécuter les commandes de validation sur Windows.
