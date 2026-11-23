# v19 — Fin propre d'un appel à la fermeture de la fenêtre

## Problème
`MainWindow.OnClosing` arrêtait le réseau sans terminer la session d'appel : le pair n'était jamais prévenu et restait en « appel en cours ». De plus, `CallSignalingService` ignorait la perte de connexion d'un pair.

## Correctifs
- `MainWindow.xaml.cs` : au début de la fermeture, `EndCall()` (sans effet s'il n'y a pas d'appel) puis libération du moteur média **attendue**, puis délai de 300 ms (`CallEndFlushDelay`) pour laisser la file d'écriture émettre « End » (uniquement si un appel était en cours).
- `DisposeCallMediaEngineAsync` renvoie maintenant la libération réellement en cours (avant, si `SessionChanged` l'avait déjà lancée en tâche de fond, la fermeture ne l'attendait pas). Les erreurs de libération sont journalisées.
- `CallSignalingService` : abonnement à `TcpTransport.PeerDisconnected` ; `HandlePeerDisconnected(peerId)` termine la session (sonnerie ou acceptée) du pair concerné, une seule fois. Filet de sécurité si « End » est perdu (plantage, coupure réseau, fermeture brutale).

## Tests ajoutés (6, `CallLifecycleTests.cs`, Infrastructure.Tests) — total attendu : 488
Déconnexion pendant appel accepté / pendant la sonnerie ; EndCall puis arrêt immédiat ; pair étranger ignoré ; sans session ; double notification = un seul événement.

## Statut de vérification
- `audit_csharp.py src` : 0 alerte (49 fichiers).
- NON compilé, NON testé par l'assistant (pas de SDK .NET dans cet environnement). À valider sous Windows : `dotnet build .\RedChat.sln -c Release -warnaserror` puis `dotnet test .\RedChat.sln -c Release`.
- Non testé : le comportement réel de la fenêtre WPF à la fermeture pendant un appel (nécessite deux appareils).
