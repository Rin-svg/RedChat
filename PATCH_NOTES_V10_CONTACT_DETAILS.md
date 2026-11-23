# RedChat v10 — correction des actions de fiche contact

## Problème traité
Le panneau de détails pouvait être visible alors qu'aucun `SelectedAddressBookContact` n'était réellement sélectionné. Dans ce cas, les handlers photo retournaient silencieusement et les commandes WPF liées au contact n'avaient aucune cible. Le champ du nom pouvait alors laisser saisir du texte sans qu'un contact soit modifié.

## Changements
- Le bouton de détails vérifie maintenant qu'un contact est sélectionné et tente de le résoudre depuis la conversation privée active ou la sélection du carnet.
- Importer/retirer une photo résout explicitement le contact actif ; en l'absence de cible ou de photo, l'interface explique pourquoi l'action ne peut pas continuer.
- Le renommage et le basculement de vérification passent par des handlers explicites, vérifient la cible, exécutent les commandes du `ContactViewModel` et affichent un retour de succès/erreur.
- La sauvegarde du carnet reste assurée par `ChatPersistence` après l'événement `ContactBook.Changed` ; elle est automatique et différée.
- Aucun changement du protocole réseau : les avatars restent locaux à l'appareil.

## Validation
- XAML parsé comme XML.
- Vérification statique des noms de handlers et des appels.
- La compilation WPF et les tests .NET doivent être confirmés sur Windows ; aucun SDK .NET n'est disponible dans l'environnement de préparation.
