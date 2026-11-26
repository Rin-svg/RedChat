# RedChat

RedChat est une application de messagerie desktop Windows en C# / WPF / .NET 10. Elle communique directement entre appareils du même réseau local (P2P), sans serveur central. L'interface utilise un thème noir et rouge.

## Interface graphique

La fenêtre principale reprend la structure de la référence visuelle fournie : rail de navigation vertical, panneau Discussions/Amis avec recherche, conversation centrale et panneau de détails du contact repliable. Le thème utilise un bleu nuit profond pour les surfaces, avec le rouge comme accent principal. Le symbole du chapeau rouge est redessiné en vecteur dans le XAML pour porter la marque RedChat (le nom Red Hat/OpenShift n’est pas repris).

La recherche filtre les conversations par nom ou aperçu du dernier message, et le carnet d'amis par nom local ou PeerId. L’onglet Amis est alimenté indépendamment par le carnet persistant : il contient les pairs ayant réussi un handshake TCP authentifié, même sans conversation préalable. Une fiche permet de renommer localement le contact, de comparer son code de sécurité, de marquer explicitement l'identité vérifiée et d'ouvrir une conversation vide sans message artificiel. Les annonces UDP ne sont pas considérées comme des contacts authentifiés. Les actions d’appel audio restent volontairement désactivées : elles ne sont pas implémentées dans cette V1.

## Fonctionnalités disponibles dans cette version

- Conversations privées et groupes v1 (création, 2 à 256 membres au total).
- Messages texte, réponse, édition et suppression pour tous.
- Réactions, accusés de réception (envoyé / remis / lu) et indicateur de frappe.
- Découverte automatique par UDP (port 45821) et connexion manuelle par `IP:port` local si le broadcast est bloqué.
- Identités ECDSA P-256, handshake authentifié, clés de session dérivées par HKDF-SHA256 et canal AES-256-GCM.
- Carnet de contacts local, surnoms locaux distincts des profils publics reçus, code de sécurité et validation manuelle des contacts.
- Synchronisation P2P chiffrée des profils publics (nom, statut, description et avatar) après handshake authentifié ; cache distant protégé et conservé avec le carnet.
- Historique, file d'attente hors ligne et contacts persistés automatiquement, avec écriture atomique, sauvegarde `.bak` et récupération signalée.
- Protection des fichiers de profil par DPAPI `CurrentUser` sous Windows.
- Notifications Windows pour les nouveaux messages dans une conversation non affichée, avec ouverture directe de la conversation depuis la notification et réglage d’activation dans le menu de l’icône de zone de notification.

## Prérequis

- Windows 10/11.
- SDK .NET 10.
- Composants Windows Forms du SDK .NET (utilisés uniquement pour l’icône et le menu de la zone de notification).
- Accès NuGet lors de la première restauration (le projet WPF utilise `System.Security.Cryptography.ProtectedData`).

## Compiler, tester et lancer

Depuis le dossier contenant `RedChat.sln` :

```powershell
dotnet restore .\RedChat.sln
dotnet build .\RedChat.sln -c Release -warnaserror
dotnet test .\RedChat.sln -c Release

dotnet run --project .\src\RedChat.Wpf\RedChat.Wpf.csproj
```

Pour ouvrir un profil distinct :

```powershell
dotnet run --project .\src\RedChat.Wpf\RedChat.Wpf.csproj -- --profile famille
```

Un seul processus peut ouvrir un même profil à la fois. Le nom du profil accepte 1 à 30 lettres ASCII, chiffres, `_` et `-`.

## Réseau et pare-feu

- Autoriser le trafic UDP entrant/sortant sur le port `45821` pour la découverte.
- Autoriser les connexions TCP entrantes sur le port éphémère choisi par Windows.
- Les appareils doivent être sur un réseau local joignable. Si le broadcast est filtré, saisir l'adresse privée du pair sous la forme `192.168.1.20:50123`.
- Les annonces UDP ne sont pas une preuve d'identité : le handshake TCP authentifie le pair avant d'accepter la connexion.

## Données et sécurité

Les profils sont enregistrés sous `%LOCALAPPDATA%\RedChat\Profiles\<profil>`. L'identité, l'historique et les contacts sont protégés par DPAPI pour le compte Windows courant. Une identité illisible n'est jamais remplacée automatiquement. Si l'historique ne peut pas être lu, RedChat met le fichier de côté et affiche un avertissement ; il ne le supprime pas silencieusement.

DPAPI protège les données au repos contre les autres comptes Windows et la lecture hors ligne. Elle ne protège pas contre un logiciel malveillant exécuté sous le même compte Windows. Un profil protégé ne peut pas être déplacé tel quel vers un autre compte ou un autre PC.

## Limites de la V1

Les appels audio/vidéo, notes vocales, statuts, communautés, listes de diffusion, multi-appareils et sauvegarde cloud ne sont pas implémentés. Les groupes v1 ne permettent pas encore d'ajouter ou retirer des membres après leur création. Le réseau local est ouvert aux pairs qui réussissent le handshake : il n'existe pas encore de liste blanche d'invitations.

## Vérification dans l'environnement de développement

Le banc `tools/run-sandbox-tests.sh` exécute les tests Core, Infrastructure et Presentation sans télécharger xUnit, à condition de disposer du SDK .NET 10 et de définir `SKILL_DIR` vers les ressources `csharp-gui-dev` contenant `assets/sandbox-harness`. Le projet WPF et DPAPI doivent être compilés et vérifiés sur Windows.

## Profil personnel et synchronisation (v11/v12)

Le profil personnel reste indépendant de l'identité cryptographique : nom public, statut, description et photo PNG/JPEG. Les données
locales sont conservées dans `personal-profile.dat`, protégé avec le mécanisme de stockage existant. Depuis la v12, le profil est envoyé
aux contacts uniquement sur des connexions TCP authentifiées et chiffrées. Les profils reçus sont vérifiés contre le PeerId authentifié,
puis conservés dans le carnet de contacts protégé. Les surnoms choisis localement restent prioritaires et ne sont jamais écrasés par le
nom public du contact. Les avatars publics mis en cache sont limités à 5 Mio au total.

La v12 a introduit la version 2 du protocole. La v15 a introduit la version 3 pour les pièces jointes. La v18 introduit la version 4 pour la signalisation des appels : toutes les instances qui communiquent doivent être mises à jour vers v18 ou une version compatible. La découverte UDP reste non authentifiée et ne transporte aucun profil.

## Pièces jointes (v15)

Les fichiers jusqu'à 25 Mio sont transmis en blocs de 384 Kio sur le canal TCP authentifié et chiffré. La progression d’envoi est affichée, les réceptions sont vérifiées par SHA-256 et enregistrées dans `%LOCALAPPDATA%\RedChat\Profiles\<profil>\Attachments\Incoming`. Les destinataires doivent être connectés ; la reprise après coupure et le transfert hors ligne ne sont pas encore disponibles. Voir `PATCH_NOTES_V15_ATTACHMENTS.md`.


## v16 — Messages vocaux
La v16 ajoute l’enregistrement de messages vocaux WAV depuis le microphone Windows, l’envoi via le canal chiffré des pièces jointes et la lecture du dernier vocal reçu. Voir `PATCH_NOTES_V16_MESSAGES_VOCAUX.md` pour les limites et la procédure de validation.

## Appels privés (v18)

RedChat v18 ajoute la signalisation des appels audio/vidéo privés via les connexions authentifiées et chiffrées. Les invitations, acceptations, refus et fins d’appel sont pris en charge. Le flux audio/vidéo temps réel n’est pas encore intégré dans cette version. Le protocole passe à la version 4 : mettez à jour tous les appareils ensemble. Voir `PATCH_NOTES_V18_CALL_SIGNALING.md`.

## Installateur Windows
Pour produire `RedChat-Setup-<version>.exe` : `powershell -ExecutionPolicy Bypass -File .\setup\build-installer.ps1 -InstallInno` — voir `setup\README-INSTALLATEUR.md`.
