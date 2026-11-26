# Installateur Windows de RedChat

Produit un seul fichier `RedChat-Setup-<version>.exe` que l'on peut copier sur n'importe quel PC Windows 10 (1809) ou 11, 64 bits.
Aucun prérequis sur le PC cible : le runtime .NET est inclus dans l'installateur.

## 1. Construire l'installateur (sur ton PC de développement)

Prérequis : SDK .NET 10 (déjà installé) et **Inno Setup 6.3 ou plus récent** (gratuit).

```powershell
# depuis la racine du projet (là où se trouve RedChat.sln)
powershell -ExecutionPolicy Bypass -File .\setup\build-installer.ps1 -InstallInno
```

- `-InstallInno` installe Inno Setup avec winget s'il manque (à la main : <https://jrsoftware.org/isdl.php>). Inutile ensuite.
- Le script : lance les tests, publie l'application (`artifacts\publish\win-x64`), compile `setup\RedChat.iss`, calcule le SHA-256.
- Résultat : `setup\Output\RedChat-Setup-1.0.0.exe` et `RedChat-Setup-1.0.0.exe.sha256`.
- Autres options : `-Version 1.1.0` (sinon lue dans `<Version>` de `src\RedChat.Wpf\RedChat.Wpf.csproj`), `-SkipTests`.
- Dans Rider : *View > Tool Windows > Terminal*. Si `dotnet` n'est pas reconnu, le script cherche tout seul dans `%USERPROFILE%\.dotnet`.

Pour une **nouvelle version** : change `<Version>` dans `RedChat.Wpf.csproj`, relance le script, diffuse le nouvel `.exe`.
Ne change **jamais** `AppId` dans `RedChat.iss` : c'est lui qui permet à une version plus récente de remplacer l'ancienne.

## 2. Ce que fait l'installateur

| Sujet | Comportement |
|---|---|
| Droits | Par défaut **sans administrateur**, dans `%LOCALAPPDATA%\Programs\RedChat`. Une boîte de dialogue propose aussi « pour tous les utilisateurs » (`Program Files`, droits admin). |
| Raccourcis | Menu Démarrer toujours ; Bureau en option (décoché). |
| Pare-feu | Case « Autoriser RedChat dans le pare-feu » **uniquement en mode administrateur** : règle entrante limitée à `RedChat.exe` et au profil réseau **Privé**. Sans administrateur, Windows pose la question au premier lancement : répondre « Réseaux privés ». |
| Mise à jour | Lancer un installateur de version supérieure : remplacement sur place (RedChat est fermé proprement s'il tourne). Conversations, contacts et identité sont conservés. |
| Désinstallation | Via *Paramètres > Applications*. Le programme est supprimé ; **les données ne le sont que si tu réponds Oui** à la question (par défaut : Non). |
| Données utilisateur | `%LOCALAPPDATA%\RedChat` (profils, identité chiffrée DPAPI, pièces jointes, statuts, journaux). |

Installation silencieuse (déploiement, tests) :

```powershell
.\RedChat-Setup-1.0.0.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CURRENTUSER
.\RedChat-Setup-1.0.0.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /ALLUSERS /TASKS="firewall"   # exige un terminal administrateur
```

## 3. Avertissement Windows SmartScreen (important)

L'installateur n'est **pas signé numériquement** (il faut pour cela acheter un certificat de signature de code). Au premier
lancement, Windows affiche « Windows a protégé votre ordinateur » : clique sur **Informations complémentaires** puis
**Exécuter quand même**. Un antivirus peut aussi réagir à un programme non signé et peu répandu.
- Pour des amis : donne-leur aussi le fichier `.sha256` ; ils vérifient avec `Get-FileHash .\RedChat-Setup-1.0.0.exe` que l'empreinte est identique.
- Pour supprimer l'avertissement : certificat de signature de code (ou service de signature géré), puis activer `SignTool=` dans `RedChat.iss`.

## 4. Liste d'essais à faire avant de diffuser (rien de tout cela n'a pu être exécuté par l'assistant)

1. Installer sur **un autre PC ou une machine virtuelle Windows propre** (sans SDK .NET) : l'application doit se lancer.
2. Menu Démarrer : raccourci présent, icône rouge de RedChat.
3. Installer **deux PC** et vérifier la découverte/connexion sur le même réseau local (pare-feu !).
4. Installer une version plus récente par-dessus : les conversations sont toujours là.
5. Mode administrateur + case pare-feu : `netsh advfirewall firewall show rule name=RedChat` affiche la règle ; après désinstallation, elle a disparu.
6. Désinstaller en répondant **Non** : le dossier `%LOCALAPPDATA%\RedChat` existe encore. Puis refaire en répondant **Oui** : il est supprimé.
7. Fermer RedChat pendant l'installation d'une mise à jour : aucune erreur « fichier utilisé ».

## 5. Fichiers

- `build-installer.ps1` — publication + compilation + empreinte.
- `RedChat.iss` — script Inno Setup (texte en français, UTF-8 avec BOM : ne pas enregistrer sans BOM).
- `INFO_AVANT_INSTALLATION.txt` — informations affichées avant l'installation.
- `INFO_APRES_INSTALLATION.txt` — conseils affichés à la fin de l'installation.
- `LICENSE.txt` — contrat de licence affiché par l'assistant ; les champs entre crochets doivent être complétés avant diffusion.
- `RedChat-Setup.ico` — icône de l'installateur.
- `tools/make-icon.py` — régénère `src\RedChat.Wpf\Assets\redchat.ico` depuis l'emblème de `MainWindow.xaml` (`pip install pillow`).
- `Output\` et `..\artifacts\` — générés, à ne pas versionner.
