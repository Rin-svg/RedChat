# RedChat v16 — Messages vocaux

## Fonctionnalités
- Enregistrement depuis le microphone Windows en WAV PCM, 16 kHz, mono, 16 bits.
- Le bouton 🎙 démarre l’enregistrement ; le bouton ■ termine puis envoie le vocal dans la conversation sélectionnée.
- Durée maximale : cinq minutes.
- Envoi par le service de pièces jointes v15 sur le canal TCP authentifié et chiffré, avec contrôle SHA-256.
- Les fichiers WAV reçus sont reconnus comme messages vocaux ; le bouton ▶ lit le dernier vocal reçu dans le lecteur intégré de Windows.
- Aucun enregistrement n’est conservé dans le dossier temporaire après l’envoi ou la fermeture normale de l’application.

## Limites
- Les messages vocaux utilisent le transfert de fichiers v15 : tous les destinataires doivent être connectés.
- Le vocal est reçu comme fichier WAV dans le dossier Incoming ; le bouton ▶ lit le dernier vocal reçu sur cet appareil.
- Les vocaux ne sont pas encore des objets dédiés de l’historique ; l’historique affiche le marqueur de pièce jointe.
- La capture dépend d’un microphone Windows disponible et autorisé.
- Cette version ne contient pas encore les appels audio/vidéo en temps réel.

## Dépendance
- NAudio 2.2.1, utilisée uniquement dans le projet WPF pour la capture microphone.

## Compatibilité réseau
Aucun changement de protocole : v16 réutilise le protocole v3 de RedChat v15. Les appareils doivent déjà utiliser une version compatible v15 ou ultérieure.
