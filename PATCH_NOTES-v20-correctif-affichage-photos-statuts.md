# Correctif v20 — affichage des photos de statut

- Ajout de l'aperçu des photos dans la liste des statuts publiés sur cet appareil.
- Amélioration de l'aperçu des photos reçues dans la fenêtre des statuts d'un contact.
- Conservation des proportions originales lors du décodage des images.
- Ajout d'un message explicatif lorsque la liste des statuts reçus d'un contact est vide.
- Les vidéos restent identifiées comme vidéos ; leur lecture intégrée n'est pas ajoutée par ce correctif.

## Limite réseau existante
Les statuts ne sont transmis qu'aux contacts autorisés et connectés au moment de la publication. Les contacts hors ligne ne reçoivent pas encore automatiquement les statuts après reconnexion.

## Vérifications effectuées
- XML de `StatusCenterWindow.xaml` valide.
- Compilation .NET non exécutée : le SDK `dotnet` n'est pas disponible dans l'environnement de préparation.
