# RedChat v18 — Signalisation des appels privés

- Ajout de paquets de signalisation `CallControl` (invitation, acceptation, refus, fin et occupé).
- Signalisation transportée par les connexions TCP déjà authentifiées et chiffrées.
- Ajout d’un service de session d’appel privé et d’une invite WPF pour les appels entrants.
- Le bouton d’appel audio lance maintenant une invitation réseau au lieu d’afficher le message « non implémenté ».
- Les appels de groupe ne sont pas pris en charge.
- **Limite importante :** cette version ne transporte pas encore le son ni la vidéo en direct. L’acceptation confirme uniquement la signalisation. La couche média WebRTC et les périphériques audio/vidéo restent l’étape suivante.
- Version de protocole portée à 4 : tous les appareils doivent utiliser une version compatible.

Vérifications à effectuer sur Windows :

```powershell
dotnet restore .\RedChat.sln
dotnet build .\RedChat.sln -c Release -warnaserror
dotnet test .\RedChat.sln -c Release
dotnet run --project .\src\RedChat.Wpf\RedChat.Wpf.csproj
```
