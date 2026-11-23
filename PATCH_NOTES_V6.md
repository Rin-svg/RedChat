# RedChat v6 — finition conversationnelle

## Modifications
- Avatars à initiales dérivées du nom local du contact ou du titre de groupe ; les initiales se mettent à jour quand le contact est renommé.
- Même identité visuelle dans la liste, l'en-tête de conversation et le panneau de détails.
- Hiérarchie hover/sélection corrigée dans la liste : la sélection reste visuellement prioritaire au survol.
- Bulles entrantes et sortantes différenciées avec coins asymétriques, bordure fine et accent dégradé.
- Alignement du groupe de message complet à droite pour les messages sortants.
- Barre d'actions (répondre, réagir, modifier, supprimer) discrète et révélée au survol du message.
- Panneau de détails : statut de vérification visuellement plus lisible. Il reflète l'état réel du ContactBook, sans simuler une présence en ligne.

## Limites non masquées
- Pas de présence « en ligne » ajoutée : le code actuel ne fournit pas de statut fiable de pair connecté à la couche de présentation.
- Pas d'images d'avatar persistées ou synchronisées : les initiales sont un fallback déterministe et local.
- Pas d'animation artificielle ni de faux boutons d'appel activés.

## Vérifications locales
- App.xaml et MainWindow.xaml analysés comme XML valide.
- Build .NET/WPF et tests non exécutés dans cet environnement ; validation Windows requise.
