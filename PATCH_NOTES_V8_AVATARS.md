# RedChat v8 — avatars locaux

## Fonctionnalités
- Import d'une image PNG/JPEG depuis le panneau de détails d'un contact.
- Limite d'entrée : 5 Mio ; dimensions décodées contrôlées (32 à 4096 px), décodage ramené à 512 px de large.
- Réencodage en PNG pour normaliser le format et retirer les métadonnées non nécessaires.
- Stockage local dans `%LOCALAPPDATA%\RedChat\Avatars\<PeerId>.png`.
- Suppression de l'avatar depuis la fiche du contact.
- Affichage de l'image dans le carnet, la fiche et la liste des conversations privées ; les groupes gardent leurs initiales.
- Aucun avatar n'est transmis par le protocole réseau. Chaque utilisateur choisit sa propre image locale pour chaque contact.

## Sécurité et limites
- Les noms de fichiers sont dérivés du PeerId validé, pas du nom du contact ou du nom de fichier importé.
- Les données sont décodées puis réencodées ; le fichier source n'est pas copié tel quel.
- Les avatars ne sont pas chiffrés par DPAPI : ils sont des images locales visibles par le compte Windows courant et protégées par les ACL du profil utilisateur.
- L'import dépend des codecs WPF Windows ; PNG et JPEG sont proposés dans le sélecteur.
- Build et tests Windows non exécutés dans l'environnement de préparation. À confirmer sur le poste de développement.
