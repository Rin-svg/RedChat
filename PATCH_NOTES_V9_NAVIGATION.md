# RedChat v9 — navigation et actions de la barre

## Boutons raccordés
- Accueil : revient à la liste des discussions récentes.
- Favoris : affiche les conversations marquées comme favorites depuis le bouton étoile de la conversation active.
- Étoile dans l'en-tête d'une conversation : ajoute/retire la conversation des favoris et met à jour son état visuel.
- Notifications : recherche les conversations avec messages non lus, ouvre la plus récente et indique le nombre de messages non lus. Affiche un message clair si tout est lu.
- Paramètres : propose d'ouvrir dans l'Explorateur le dossier local RedChat ou le sous-dossier des avatars.
- Téléphone : ouvre une information explicite sur l'état des appels audio. Aucun appel n'est simulé.

## Limites connues
- Les favoris sont en mémoire pendant la session seulement ; ils sont réinitialisés au redémarrage. Leur persistance est une prochaine amélioration.
- Les notifications sont une consultation des messages non lus, pas encore des notifications Windows en temps réel.
- Les appels audio ne sont pas implémentés.
- Le bouton Paramètres donne accès aux données locales ; il ne s'agit pas encore d'un panneau complet de préférences.

## Validation
- Les fichiers XAML ont été analysés comme XML valides.
- Le SDK .NET n'est pas disponible dans l'environnement de préparation : compilation et tests .NET non exécutés ici. À valider sur Windows avec les commandes du HANDOFF.
