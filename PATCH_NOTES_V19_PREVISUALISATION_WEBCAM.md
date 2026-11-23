# v19 — Prévisualisation de la caméra locale

## Ajouts
- `WebRtcMediaEngine` : événement `LocalVideoFrameAvailable`, alimenté par `WindowsVideoEndPoint.OnVideoSourceRawSample` (image brute avant encodage), silencieux si la caméra est en pause.
- `MainWindow.xaml.cs` : la fenêtre d'appel vidéo contient l'image distante plein cadre + une vignette locale (180 px, bas droite, bordure rouge, **effet miroir**). Création unique via `EnsureCallVideoWindow()`. La vignette est masquée quand la caméra est mise en pause. Une seule image locale en attente dans le dispatcher (les autres sont abandonnées) ; le tampon est copié avant affichage.
- Durcissement : toute image (distante ou locale) est validée avant `WritePixels` — un tampon trop court ou un stride incohérent provoquait une exception dans le dispatcher (arrêt de l'application). La règle est dans `RedChat.Presentation/VideoFrameGuard.cs` (pure, testable).

## Tests ajoutés (10 cas, `VideoFrameGuardTests.cs`, Presentation.Tests) — total attendu : 498 (488 + 10)

## Hypothèses NON vérifiées (SIPSorcery indisponible dans l'environnement de travail)
- `WindowsVideoEndPoint.OnVideoSourceRawSample` existe avec la signature `(uint durationMs, int width, int height, byte[] sample, VideoPixelFormatsEnum format)`. Si le build échoue sur cette ligne, coller l'erreur : correction d'une ligne.
- Le format des images brutes est `Bgr`. Sinon la vignette reste invisible (aucune erreur) : à constater à l'exécution.

## Statut de vérification
- `audit_csharp.py src` : 0 alerte (50 fichiers).
- NON compilé, NON testé (pas de SDK .NET ici). Le correctif précédent (fermeture d'appel, 488 tests) n'est lui-même pas encore validé sous Windows : cette archive l'inclut.

## Correctif de compilation (retour Windows)
`RedChat.Wpf` : CS0104 (`Point` ambigu) et CS0176 (`HorizontalAlignment.Right` masqué par la propriété de la fenêtre) → types qualifiés `System.Windows.*`. Les 498 tests passaient déjà sous Windows.
