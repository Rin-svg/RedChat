# RedChat v20 — première phase des améliorations

## Changements inclus

- Fenêtre « Créer un groupe » alignée sur la palette RedChat : fond sombre, titres et libellés clairs, champ de saisie contrasté, bordure accentuée, liste des contacts sur surface sombre et message d'erreur lisible.
- Ajout d'un sélecteur d'émojis dans la zone de rédaction. Le clic insère l'émoji Unicode dans le brouillon courant ; l'envoi continue d'utiliser le message texte chiffré existant, sans changement de protocole.
- Le sélecteur est accessible au clavier/souris via un bouton identifié pour l'automatisation UI.

## Non inclus dans cette phase

- Les stickers graphiques (images WebP/PNG avec rendu dédié et transfert/réception comme contenu de message) ne sont pas simulés par de simples caractères emoji.
- Les statuts éphémères ne sont pas encore implémentés. Ils nécessitent un nouveau type de paquet, une gestion d'expiration 24 h, une persistance locale, une diffusion P2P aux contacts, un affichage des vues et une politique d'audience. Ce changement doit faire évoluer la version du protocole et être déployé sur tous les appareils RedChat concernés.

## Validation

- XML XAML analysé avec succès pour `CreateGroupWindow.xaml`, `MainWindow.xaml` et `App.xaml`.
- Build WPF et tests .NET non exécutés dans cet environnement : SDK `dotnet` absent. Ne pas considérer cette archive comme compilée ou testée sous Windows.
