# RedChat v20 — Historique des messages vocaux

## Modifications
- Ajout d'une liste « Historique des messages vocaux » dans la fenêtre principale, avec un bouton « Écouter » pour chaque fichier WAV.
- Chargement au démarrage des anciens vocaux présents dans le dossier local des pièces jointes reçues.
- Conservation des nouveaux vocaux envoyés dans `%LOCALAPPDATA%/RedChat/VoiceHistory/Outgoing` afin de pouvoir les réécouter après fermeture de RedChat.
- Les vocaux reçus sont ajoutés à l'historique dès la fin de leur réception.
- Le bouton compact de la barre de rédaction lit désormais le vocal le plus récent de l'historique.

## Limites
- Les anciens vocaux envoyés avant cette version ne peuvent être réintégrés que si leur fichier WAV existe encore : les fichiers temporaires d'enregistrement des anciennes versions étaient supprimés après l'envoi.
- Les vocaux reçus restent réécoutables tant que leurs fichiers sont présents dans le dossier Incoming.
- L'historique est local à cet appareil ; il ne synchronise pas les fichiers audio entre appareils.
