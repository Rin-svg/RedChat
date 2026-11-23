# RedChat v19 — préparation de la négociation WebRTC

## Inclus dans cette étape

- Extension du protocole d’appel avec les signaux SDP `Offer` et `Answer` et les candidats `IceCandidate`.
- Acheminement de ces signaux uniquement via les connexions TCP déjà authentifiées et chiffrées.
- Contrôle de l’identité du pair, de l’identifiant d’appel, du mode média et de l’état `Accepted` avant de transmettre un signal à l’application.
- Limites explicites : SDP 64 Kio, candidat ICE 4 Kio, MID 64 caractères, index média borné.
- Tests de sérialisation/validation des signaux et test de transport entre deux nœuds.
- Version de protocole portée à 5 : les clients v18/protocole 4 doivent être mis à jour ensemble.

## Limites importantes

Cette livraison est une étape de signalisation, pas encore un appel audio/vidéo opérationnel. Elle ne capture pas le microphone/la webcam, ne crée pas de `RTCPeerConnection`, ne transporte pas de RTP et n’affiche pas de vidéo distante. L’intégration du moteur WebRTC et des périphériques Windows reste à faire dans la suite de v19.

## Vérification

L’environnement de génération ne dispose pas de .NET SDK. L’archive a été vérifiée comme ZIP, mais la compilation et les tests .NET doivent être exécutés sous Windows avec :

```powershell
dotnet restore .\RedChat.sln
dotnet build .\RedChat.sln -c Release -warnaserror
dotnet test .\RedChat.sln -c Release
```
