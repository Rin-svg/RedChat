# RedChat v14 — correctif 3

## Corrections de compilation WPF

- Ajout d'alias explicites pour conserver les types WPF (`MessageBox`, `Button`, `CheckBox`, `Clipboard`, `OpenFileDialog`) malgré la référence à Windows Forms nécessaire à l'icône de notification.
- `MainWindow` implémente désormais `IDisposable` et libère explicitement `NotifyIcon` et `ContextMenuStrip`, ce qui corrige l'analyse CA1001.
- Le nettoyage de l'icône et du menu reste effectué pendant la fermeture de la fenêtre et est idempotent.

Aucune modification du protocole réseau, du stockage, des conversations ou de la logique de notification n'est prévue dans ce correctif.

## Vérification

Le code source et l'archive ont été inspectés et l'intégrité ZIP est vérifiée. Le SDK .NET n'étant pas disponible dans l'environnement de préparation, la compilation Windows doit être confirmée sur la machine de développement avec `dotnet build .\\RedChat.sln -c Release -warnaserror`.
