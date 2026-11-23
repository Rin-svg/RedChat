# RedChat v4 — refonte visuelle de l'interface

## Direction artistique
- Structure inspirée de la capture de référence : rail latéral, liste des conversations, zone de discussion, panneau de détails repliable.
- Palette bleu nuit / rouge : fond `#0B1422`, surfaces `#142238` et `#1C2B40`, accent `#F21D45`, bulles sortantes `#8B1730`.
- Emblème RedChat dessiné en vecteur dans le XAML à partir de la forme de chapeau rouge visible sur le logo fourni. Le nom Red Hat/OpenShift n'est pas repris.

## Interactions ajoutées ou reliées
- Recherche effective par nom de conversation et aperçu du dernier message.
- Onglet « Discussions » et filtre « Amis » (conversations privées 1-à-1 déjà existantes uniquement).
- Panneau de détails du contact ouvert/fermé avec le bouton `⋮`.
- Raccourci de création de groupe dans l'en-tête et dans la liste.
- Saisie de message transmet l'indicateur de frappe via le ViewModel existant.
- Bouton de fermeture, fenêtre « À propos » et identifiants d'automatisation sur les contrôles principaux.
- Appel audio affiché mais désactivé : cette fonctionnalité n'est pas implémentée dans la V1.

## Vérification
- Audit C# : 0 alerte sur 37 fichiers.
- XML `.xaml` / `.csproj` : parse valide.
- Archive ZIP : intégrité vérifiée.
- Build WPF et tests de la v4 non exécutés ici : SDK `dotnet` indisponible. Le build Windows utilisateur reste obligatoire.


## Correctif v4.1 — compilation WPF
- Correction de `MC3072` dans `MainWindow.xaml` : `Padding` n’est pas une propriété de `StackPanel` en WPF.
- La zone de composition est maintenant contenue dans un `Border` portant le fond et le padding ; le `StackPanel` interne conserve l’empilement.
- XAML reparsé après modification. Build Windows et tests à relancer.


## Correctif v4.2 — CS0029 dans App.xaml.cs
- Dans `Dispose()`, le paramètre de la continuation `ContinueWith` s'appelait `_`.
- L'affectation `_ = Dispatcher.BeginInvoke(...)` était donc interprétée comme une affectation au paramètre `Task`, alors que `BeginInvoke` retourne un `DispatcherOperation`.
- Le paramètre a été renommé `completedTask` afin que `_ =` soit un discard et que le `DispatcherOperation` soit explicitement ignoré.
- Le comportement de la pompe DispatcherFrame et la libération du mutex sur le thread propriétaire restent inchangés.
- Compilation Windows à confirmer par l'utilisateur.


## Refonte v5 — direction visuelle premium
- Recomposition de la palette : dégradés bleu nuit, surfaces en relief et accents rouge framboise.
- Boutons globaux avec surfaces arrondies, états hover/pressed et opacité désactivée.
- Navigation latérale plus généreuse, cartes et zones de recherche plus douces, sélection des conversations arrondie.
- Bulles de messages plus aérées et plus larges ; panneau de conversation avec davantage d'espace.
- État vide central conçu pour expliquer comment démarrer une conversation au lieu de laisser une grande zone noire.
- État vide de la liste de conversations ajouté.
- Le composeur est masqué tant qu'aucune conversation n'est sélectionnée ; la barre d'édition n'apparaît que pendant une édition effective.
- Aucune fonctionnalité réseau/métier n'a été remplacée ; les commandes, bindings et handlers existants sont conservés.
- Validation locale : App.xaml et MainWindow.xaml parsés comme XML. Build WPF et tests non exécutés dans cet environnement ; confirmer sur Windows.
