# RedChat v20 — phase 2 : fondations des statuts éphémères

## Ajout de cette phase

- Modèle métier `EphemeralStatus` : texte, photo PNG/JPEG ou vidéo MP4/WebM, validation des signatures de fichiers, taille média plafonnée à 20 Mio, horodatages UTC et expiration stricte après 24 heures.
- Modèle `StatusAudience` : `AllContacts` (défaut), `AllExcept` et `OnlySelected`, avec validation des PeerId, interdiction d'inclure l'auteur et contrôle d'accès par destinataire.
- Validation de la liste des vues côté propriétaire ; une copie reçue d'un autre pair ne peut pas déclarer des vues arbitraires.
- Tests unitaires ajoutés pour les trois modes de confidentialité, l'expiration et les sélections invalides.

## Ce qui n'est PAS encore livré

Cette phase est une fondation métier et ne rend pas encore les statuts utilisables dans l'interface :
- aucun paquet réseau de publication/vue/suppression n'est encore ajouté ;
- aucun stockage protégé des statuts ni nettoyage planifié n'est encore branché ;
- aucun transfert P2P des médias de statut n'est encore branché ;
- aucune interface de publication, de consultation ou de liste des vues n'est encore ajoutée.
- les stickers graphiques ne sont pas encore implémentés (le sélecteur d'émojis Unicode de la phase 1 reste disponible).

Le protocole reste donc en version 5 dans cette archive. Il ne faut surtout pas déployer une évolution de protocole tant que les paquets et leur compatibilité n'ont pas été intégrés et testés.

## Validation

- Build et tests .NET non exécutés dans l'environnement de préparation : SDK `dotnet` absent.
- Les tests xUnit ajoutés doivent être exécutés sous Windows avec la suite complète.
