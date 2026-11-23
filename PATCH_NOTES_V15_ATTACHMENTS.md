# RedChat v15 — Pièces jointes

## Compatibilité
- Version du protocole portée à 3 : toutes les instances RedChat qui doivent communiquer entre elles doivent être mises à jour vers v15 ou une version compatible.

## Fonctionnalités
- Bouton 📎 dans la zone de rédaction pour sélectionner un fichier Windows.
- Envoi de fichiers jusqu'à 25 Mio dans les conversations privées et groupes.
- Découpage en blocs de 384 Kio transmis sur les connexions TCP authentifiées et chiffrées déjà utilisées par les messages.
- Réception dans `%LOCALAPPDATA%\\RedChat\\Profiles\\<profil>\\Attachments\\Incoming`.
- Reconstitution sur disque, contrôle des métadonnées, nom de fichier sans chemin, plafond de taille, limite de transferts simultanés et expiration des transferts incomplets après 10 minutes d'inactivité.
- Vérification SHA-256 avant de déplacer le fichier reçu vers son nom final.
- Nom unique en cas de collision ; aucun fichier reçu n'est exécuté ou ouvert automatiquement.
- Une progression en pourcentage est affichée pendant la mise en file des blocs.
- Un message texte `📎 nom (taille)` est ajouté à la conversation après la mise en file des blocs.
- Un indicateur d'état informe l'utilisateur de la réception et du chemin local du fichier.

## Limites connues
- Les destinataires doivent être connectés au début du transfert ; il n'y a pas encore de reprise après coupure ni de transfert différé hors ligne.
- Le transfert de groupe est refusé si un membre n'est pas connecté au départ ; une coupure réseau en cours de transfert peut néanmoins produire une réception partielle chez certains membres. Les blocs incomplets sont supprimés automatiquement.
- L'indicateur envoyé signifie que les blocs ont été placés dans la file TCP locale, pas que tous les destinataires ont confirmé la réception.
- Les pièces jointes ne sont pas encore intégrées à l'historique comme objets persistants : la conversation contient un message descriptif et le fichier reçu reste dans le dossier Incoming.
- Les fichiers reçus sont enregistrés comme fichiers ordinaires (non chiffrés DPAPI au repos) dans le dossier du profil Windows ; seul le transport réseau est chiffré.
- Trois tests sont ajoutés (validation du protocole, transfert multi-blocs, rejet d’une empreinte incorrecte). La suite devrait passer de 447 à 450 tests ; cette attente reste à confirmer sur Windows avec le SDK .NET 10.

## Sécurité
- Chaque bloc arrive dans le canal chiffré authentifié ; l'identité du pair provient du handshake, pas du paquet.
- Un fichier ne peut pas dépasser 25 Mio ; un bloc ne peut pas dépasser 384 Kio.
- Les transferts sont limités à trois réceptions simultanées et les fichiers temporaires sont supprimés à l'expiration ou à la fermeture.
- Le nom de fichier ne peut pas contenir de chemin ou de caractères de contrôle / réservés Windows.
- Le fichier final n'est publié qu'après vérification SHA-256.
