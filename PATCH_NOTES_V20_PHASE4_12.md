# RedChat v20 — Phase 4.12 : synchronisation des suppressions après reconnexion

## Changements

- Ajout d'une file durable de suppressions sortantes (`pending-status-deletes.dat`) dans `ProfileStore`.
- La file est protégée par le `ISecretProtector` du profil (DPAPI dans l'application Windows), écrite atomiquement et limitée à 2 048 entrées.
- Chaque entrée contient uniquement l'identifiant du statut, le destinataire autorisé et l'expiration originelle. Les entrées expirées sont écartées au chargement.
- Les suppressions sont réessayées lorsqu'un pair s'authentifie à nouveau. Les entrées restent conservées jusqu'à expiration, car `TcpTransport.TrySend` confirme seulement la mise en file locale et non un accusé de réception distant.
- Les destinataires autorisés sont maintenant figés au moment de la publication pour les nouveaux statuts. Un contact ajouté après publication ne reçoit donc pas rétroactivement une demande de suppression.
- Ajout de tombstones entrants persistants (`received-status-tombstones.dat`) protégés au repos, limités à 512 entrées et à la durée de vie restante du statut (24 h maximum).
- Un statut supprimé par son auteur authentifié ne peut pas réapparaître après redémarrage à la suite d'un ancien paquet de publication rejoué.
- Les fichiers des tombstones et de la file sortante utilisent le mécanisme `LoadProtected` / `SaveProtected` de `ProfileStore` : chiffrement, sauvegarde `.bak` et écriture atomique.
- La version du protocole reste inchangée : aucun nouveau type de paquet n'a été ajouté.

## Limites restantes

- Aucun accusé de réception applicatif `StatusDeleteAck` n'est encore défini. Les suppressions sont donc conservées et renvoyées à chaque reconnexion jusqu'à l'expiration ; le nombre affiché dans l'interface représente les paquets mis en file, pas une preuve d'effacement distant.
- Le rattrapage des publications elles-mêmes pour les contacts hors ligne n'est pas encore implémenté.
- Les anciens statuts locaux qui n'ont pas de champ `AuthorizedPeerIds` utilisent une reconstruction de l'audience depuis les contacts actuellement connus ; la liste exacte de l'époque de publication n'est pas récupérable pour ces anciens enregistrements.
- La suppression ne peut pas effacer les captures d'écran ni les copies exportées hors de RedChat.
- Les tests automatisés et les essais multi-appareils restent nécessaires.

## Vérifications dans cet environnement

- Vérification statique des appels, des abonnements/désabonnements d'événements et des bornes de collections.
- Analyse XML de tous les fichiers XAML de `src`.
- Vérification des décomptes de délimiteurs dans les fichiers C# modifiés (contrôle indicatif, pas un compilateur).
- Archive contrôlée avec `unzip -t`.
- SDK .NET absent : `dotnet build`, `dotnet test` et analyseurs non exécutés. Aucune affirmation de compilation n'est faite.
