# v19 — La photo de profil du contact s'affiche dans le chat

## Symptôme
Après « Mon profil → Choisir une photo → Enregistrer », la photo n'apparaissait nulle part dans le chat.

## Cause (code lu, pas supposé)
- L'envoi fonctionnait : `PersonalProfileViewModel.Save` → `ProfileSaved` → `LanNode.SetLocalProfile` diffuse un `ProfileUpdate` aux pairs connectés, et `ContactBook.UpdatePublicProfile` le stocke côté récepteur.
- Mais l'affichage ignorait cette photo : les listes (discussions, amis) ne lisaient que `AvatarPath` (photo importée localement par l'utilisateur) et l'en-tête de conversation était un « RC » figé dans le XAML. Seul le panneau « Détails du contact » utilisait la photo publiée.

## Correctifs
- `ConversationViewModel.RemoteAvatarBytes` (copie des octets du profil public du contact ; null pour un groupe) + notification quand le carnet de contacts change.
- `MainWindow.xaml` : liste des discussions, liste des amis et en-tête de conversation affichent la photo publiée par le contact ; la photo importée localement garde la priorité (elle est dessinée au-dessus) ; sans photo, retour aux initiales. L'en-tête affiche désormais les initiales/photo du contact sélectionné au lieu de « RC ».
- 3 tests dans `ViewModelTests.cs` : exposition + notification + retrait de la photo, copie défensive, groupe sans photo. Total attendu : 501.

## Ce que ça ne fait pas
- La photo de TON profil n'est affichée nulle part dans ta propre fenêtre principale (seulement dans « Mon profil » et chez tes contacts). Ajout possible si souhaité.
- Les messages eux-mêmes n'ont pas d'avatar (pas de pastille par message).

## Vérification
`audit_csharp.py` : 0 alerte (50 fichiers) ; XAML bien formé. NON compilé ni affiché ici (pas de SDK/Windows). Test à deux instances : lancer `--profile alice` et `--profile bob`, mettre une photo dans l'une, enregistrer ; l'autre doit voir la photo dans la liste, l'en-tête et les détails.
