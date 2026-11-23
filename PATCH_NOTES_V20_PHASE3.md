# RedChat v20 — phase 3 : gestion locale, accès stickers et entrée Statuts

## Ajouts

- Suppression locale d'une discussion privée ou d'un groupe depuis le bouton corbeille de l'en-tête.
- Retrait du favori associé et purge des paquets encore en attente pour cette conversation sur cet appareil.
- Suppression d'un contact depuis le panneau « Détails du contact ». L'historique privé est conservé ; le retrait du carnet n'envoie aucune révocation au pair distant.
- Ajout d'un sélecteur « Stickers expressifs » près du champ de message : 12 symboles expressifs peuvent être envoyés directement comme messages Unicode. Ils sont compatibles avec le protocole actuel, mais ce ne sont pas encore des images/stickers personnalisés.
- Ajout d'un accès « Statuts » dans la barre latérale. Pour éviter toute ambiguïté, il indique explicitement que la publication/consultation P2P n'est pas encore raccordée.
- Test unitaire ajouté pour la suppression d'une conversation et la purge de sa file locale.

## Limites / non livré

- Les statuts ne sont pas encore publiables : le modèle métier de phase 2 n'est pas connecté aux paquets réseau, au transfert média, au stockage et à l'interface de consultation. Le bouton Statuts est un point d'entrée informatif, pas un centre de statuts fonctionnel.
- Les stickers graphiques (fichiers image, packs, aperçu image dans les bulles) ne sont pas encore livrés. Le sélecteur actuel envoie des caractères Unicode agrandis.
- La suppression d'une conversation est locale uniquement : elle ne supprime pas le groupe ni son historique chez les autres membres.

## Vérifications réalisées dans cet environnement

- Tous les fichiers XAML du projet sont bien formés XML.
- SDK .NET absent : restore, build `-warnaserror` et tests xUnit non exécutés. Ne pas considérer cette archive comme validée sous Windows.
