# RedChat v20 — bouton de lecture des messages vocaux reçus

## Modification
- Ajout d'un bouton explicite **« Lire le vocal »** dans la zone de notification des pièces jointes.
- Le bouton n'apparaît que lorsqu'un fichier vocal WAV reçu est disponible (`HasReceivedVoice`).
- Le bouton réutilise le lecteur audio existant (`PlayVoice_Click` / `MediaPlayer`) et lit le dernier vocal reçu.
- Le bouton compact déjà présent dans la barre de rédaction est conservé.

## Limite fonctionnelle connue
Les messages vocaux sont actuellement transférés comme pièces jointes WAV. Le modèle de message de conversation ne contient pas encore de référence persistante au fichier audio ; cette correction rend la lecture visible depuis la notification de réception, mais ne crée pas encore une bulle audio indépendante pour chaque message vocal dans l'historique.

## Vérification
- Modification XAML contrôlée statiquement.
- Archive ZIP vérifiée avec `unzip -t`.
- Compilation WPF et test manuel de lecture à effectuer sur Windows.
