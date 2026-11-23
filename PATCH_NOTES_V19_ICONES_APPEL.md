# v19 — Icônes Segoe Fluent pour les boutons d'appel

## Changement (XAML uniquement, aucun code C# ni test modifié)
Les 5 boutons d'appel de l'en-tête de conversation (appel audio, appel vidéo, micro, caméra, raccrocher) n'utilisent plus d'emoji/symboles Unicode mais des glyphes **Segoe Fluent Icons** (Windows 11) avec repli sur **Segoe MDL2 Assets** (Windows 10 1809+, mêmes points de code) :
appel audio E717 · appel vidéo E714 · micro E720 · caméra E722 · raccrocher E711.
- Styles : `CallIconButton` (base), `CallMicrophoneButton` (fond rouge sombre quand le micro est coupé, via `Call.IsMicrophoneMuted`), `CallCameraButton` (idem via `Call.IsCameraPaused`), `CallHangupButton` (fond rouge).
- L'état « coupé / en pause » est donc signalé par la couleur ET par l'info-bulle existante.
- Ajout de `AutomationProperties.Name` sur ces boutons (un lecteur d'écran lirait sinon le caractère privé du glyphe).

## Hors périmètre (inchangé)
Les autres symboles de l'interface (rail latéral, favoris, pièce jointe, message vocal, envoi, réaction) restent des caractères Unicode ; à traiter plus tard si souhaité.

## Statut de vérification
- XAML vérifié bien formé (analyse XML). NON compilé, NON affiché (pas de SDK ni de Windows ici).
- Hypothèses non vérifiées : points de code des glyphes (E717/E714/E720/E722/E711, connus de Segoe MDL2 Assets) ; rendu réel à constater visuellement. Si un bouton affiche un carré, noter lequel : un seul point de code à changer.
- Précédent correctif (erreurs CS0104/CS0176 de la vignette webcam) toujours à reconfirmer à la compilation.
