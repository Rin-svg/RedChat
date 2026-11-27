# RedChat.Infrastructure — explication détaillée

Ce document explique les fichiers de `src\RedChat.Infrastructure` abordés dans l’explication : le projet, `ISecretProtector`, `AtomicFile`, `UnsafePathException` et `SafePath`. Les lignes sont expliquées par déclaration et par bloc d’instructions afin de relier chaque opération à son rôle.

## 1. Rôle de la couche Infrastructure

`RedChat.Infrastructure` réalise les opérations concrètes dont l’application a besoin : accès aux fichiers, stockage local, communications TCP/UDP et transferts. Elle s’appuie sur `RedChat.Core`, qui porte les règles métier et les composants de protocole et de chiffrement. Infrastructure ne contient pas l’interface WPF.

## 2. `RedChat.Infrastructure.csproj`

Fichier : `src\RedChat.Infrastructure\RedChat.Infrastructure.csproj`

- `<Project Sdk="Microsoft.NET.Sdk">` déclare un projet .NET utilisant le SDK standard.
- `<TargetFramework>net10.0</TargetFramework>` cible .NET 10. Infrastructure ne cible pas WPF et reste indépendante de l’interface.
- `<RootNamespace>RedChat.Infrastructure</RootNamespace>` définit l’espace de noms par défaut des types du projet.
- `<ProjectReference Include="..\RedChat.Core\RedChat.Core.csproj" />` référence Core. Infrastructure peut ainsi utiliser ses modèles, ses validateurs et ses types de protocole.
- `<InternalsVisibleTo Include="RedChat.Infrastructure.Tests" />` autorise les tests Infrastructure à examiner les types internes.
- `<InternalsVisibleTo Include="RedChat.Core.Tests" />` donne le même accès au banc de tests Core, qui compile certains tests avec les outils de sandbox.
- Les commentaires du projet précisent que la couche utilise les API standard de .NET pour le réseau et qu’aucun paquet NuGet additionnel n’est requis.

## 3. `ISecretProtector.cs` — protéger les données au repos

Fichier : `src\RedChat.Infrastructure\ISecretProtector.cs`

### Interface `ISecretProtector`

- `using System.Security.Cryptography;` importe les types cryptographiques nécessaires aux signatures et exceptions du fichier.
- `namespace RedChat.Infrastructure;` place les types dans l’espace de noms Infrastructure.
- `public interface ISecretProtector` définit le contrat de protection sans imposer une technologie précise.
- `Protect(byte[] plaintext)` reçoit des données lisibles et retourne leur représentation protégée, destinée au stockage.
- `Unprotect(byte[] protectedData)` restaure les données. Une `CryptographicException` signale une donnée altérée ou incompatible avec ce protecteur.

En production, l’application fournit un protecteur Windows basé sur DPAPI. Cette interface permet à Infrastructure de gérer le stockage sans dépendre directement de DPAPI.

### `InsecureTestSecretProtector`

Cette classe est exclusivement destinée aux tests : elle ne chiffre pas.

- `internal sealed class InsecureTestSecretProtector` limite son usage au projet et aux assemblies de tests autorisés.
- `Magic` contient une signature préfixe `INSECURE-TEST-ONLY:` pour identifier sans ambiguïté le format de test.
- `Protect` vérifie que l’entrée n’est pas nulle, calcule son empreinte SHA-256, puis concatène la signature, les données en clair et l’empreinte.
- `Unprotect` vérifie la taille minimale et la signature avant d’extraire les données.
- `CryptographicOperations.FixedTimeEquals` compare l’empreinte recalculée à celle stockée sans comparaison dépendante du premier octet différent.
- Si la signature ou l’empreinte ne correspond pas, la méthode lève `CryptographicException`.

L’empreinte détecte une altération, mais ne rend pas les données secrètes. La visibilité `internal` est une précaution pour empêcher que l’application utilise cette implémentation par erreur.

## 4. `AtomicFile.cs` — lecture et écriture de fichiers robustes

Fichier : `src\RedChat.Infrastructure\AtomicFile.cs`

- `namespace RedChat.Infrastructure;` place les utilitaires dans le bon espace de noms.
- `internal static class AtomicFile` fournit des méthodes statiques internes, sans créer d’objet.
- `BackupSuffix = ".bak"` définit le suffixe de la sauvegarde précédente.

### `Write(string path, byte[] content)`

Cette méthode remplace un fichier sans exposer une version partiellement écrite.

1. `ThrowIfNullOrWhiteSpace(path)` rejette un chemin absent ou blanc ; `ThrowIfNull(content)` rejette un contenu nul.
2. `Path.GetDirectoryName(path)` récupère le dossier parent. S’il n’existe pas dans le chemin, une `ArgumentException` est levée.
3. Le chemin temporaire combine le dossier, le nom cible, un GUID aléatoire et l’extension `.tmp`. Le temporaire reste dans le même dossier que la destination, condition utile pour le remplacement atomique.
4. `FileStream` utilise `CreateNew` pour refuser d’écraser un temporaire existant, `FileShare.None` pour empêcher son partage, et `WriteThrough` pour demander l’écriture directe.
5. `stream.Write(content)` écrit les octets, puis `Flush(flushToDisk: true)` demande leur vidage effectif sur disque.
6. Si la destination existe, `File.Replace` substitue le temporaire et conserve l’ancienne version dans le fichier `.bak`.
7. Si la destination n’existe pas, `File.Move` place le nouveau fichier à son emplacement.
8. Le bloc `finally` appelle `TryDelete(temp)` même en cas d’erreur. Le nettoyage ne masque pas l’exception initiale.

Après une interruption pendant l’écriture, la destination est donc soit l’ancienne version complète, soit la nouvelle version complète ; elle n’est pas remplacée par le contenu temporaire partiel.

### `ReadBounded(string path, long maxBytes)`

Cette méthode lit un fichier entier en imposant une taille maximale.

1. Elle vérifie que `path` est renseigné, puis crée un `FileInfo`.
2. Si le fichier n’existe pas, elle retourne `null` : l’absence est distincte d’une erreur de lecture.
3. Elle refuse `FileAttributes.ReparsePoint`, ce qui bloque les liens symboliques et jonctions.
4. Elle compare `info.Length` à `maxBytes` avant allocation, pour ne pas charger un fichier excessivement grand en mémoire.
5. Elle ouvre ensuite un flux en lecture, partagé uniquement avec d’autres lecteurs.
6. Elle vérifie de nouveau `stream.Length` après l’ouverture : le fichier a pu changer entre le premier contrôle et l’ouverture.
7. Elle alloue un tableau de la taille vérifiée et `ReadExactly` exige que tous les octets annoncés soient lus.
8. Elle retourne les octets. Les erreurs d’accès restent des erreurs d’E/S, et ne sont pas assimilées à une corruption.

### `Quarantine(string path, TimeProvider clock)`

- Formate l’heure UTC sous la forme `yyyyMMddHHmmss`.
- Ajoute cette date et six caractères aléatoires au nom du fichier.
- `File.Move` déplace l’ancien fichier vers ce nouveau nom plutôt que de le supprimer.
- Retourne le chemin où le fichier a été mis de côté, pour permettre un avertissement ou une récupération.

### `TryDelete(string path)`

- Vérifie que le temporaire existe avant de le supprimer.
- Ignore spécifiquement `IOException` et `UnauthorizedAccessException` pendant ce nettoyage.
- Ces erreurs de nettoyage ne doivent pas remplacer l’erreur principale ayant causé la sortie de `Write`.
- Un temporaire résiduel, avec son nom aléatoire, n’est pas relu comme un fichier de données normal.

## 5. `UnsafePathException.cs` — signaler un chemin dangereux

Fichier : `src\RedChat.Infrastructure\Security\UnsafePathException.cs`

- Le namespace `RedChat.Infrastructure.Security` isole les outils de sécurité liés aux chemins.
- `UnsafePathException : Exception` crée un type d’erreur dédié aux chemins non sûrs.
- Le constructeur `UnsafePathException(string message)` transporte un message explicatif que l’interface peut afficher.
- Le constructeur sans paramètre fournit le message générique « Chemin refusé pour des raisons de sécurité. »
- Le constructeur `UnsafePathException(string message, Exception innerException)` conserve la cause originale pour le diagnostic.

L’exception dédiée permet à l’appelant de distinguer un refus de sécurité d’une erreur ordinaire d’E/S. Son message ne doit pas révéler le chemin interne autorisé.

## 6. `SafePath.cs` — confiner un chemin sous un dossier autorisé

Fichier : `src\RedChat.Infrastructure\Security\SafePath.cs`

`SafePath` traite comme non fiable tout chemin venant d’une saisie, d’un fichier, d’une archive ou du réseau. Son but est d’empêcher le *path traversal* : par exemple, une entrée `..\..\Windows\fichier` ne doit pas permettre de sortir du dossier prévu.

### Constantes et règles globales

- `MaxRelativePathLength = 240` limite la longueur de la partie relative et laisse de la marge pour le chemin complet Windows.
- `ReservedDeviceNames` contient les noms de périphériques Windows (`CON`, `NUL`, `COM1`, `LPT1`, etc.). La comparaison ignore la casse.
- `InvalidNameChars` rassemble les caractères interdits par Windows ainsi que ceux interdits par le système courant. Le caractère `:` est notamment bloqué pour empêcher la notation d’un lecteur ou d’un flux alternatif NTFS.
- `SearchValues<char>` pré-calcule l’ensemble utilisé lors des vérifications de caractères.

### `ResolveInside(...)`

- Reçoit le dossier de base fiable, le chemin relatif non fiable et l’option de rejet des liens, activée par défaut.
- Appelle `TryResolveInside` pour effectuer les contrôles.
- Si la validation échoue, lève `UnsafePathException` avec la raison de refus.
- Si elle réussit, retourne le chemin absolu confiné.

Cette version est pratique lorsque tout chemin invalide doit interrompre l’opération.

### `TryResolveInside(...)`

Cette version retourne `true` ou `false` au lieu de lever une exception pour un chemin invalide ; elle est adaptée à une validation de formulaire.

**Paramètres et sorties**

- `baseDirectory` doit être non vide : il provient du programme, et un oubli indique un bug d’appel.
- `untrustedRelativePath` est le texte à contrôler.
- `fullPath` reçoit le chemin absolu uniquement si la validation réussit.
- `failureReason` reçoit un message sûr à afficher lorsque la validation échoue.
- `rejectReparsePoints` demande le refus des liens symboliques et jonctions déjà présents.
- `[NotNullWhen(true)]` et `[NotNullWhen(false)]` aident l’analyseur nullable à comprendre quelles sorties sont non nulles selon le résultat.

**Étape 1 — texte brut**

1. Vérifie le dossier de base et initialise `fullPath` à `null`.
2. Refuse un chemin vide ou composé d’espaces.
3. Refuse une entrée plus longue que 240 caractères.
4. Parcourt les caractères et refuse les caractères de contrôle, notamment NUL et les retours à la ligne.
5. Refuse les chemins absolus, les lecteurs et les séparateurs initiaux `/` ou `\`.

Ces contrôles ont lieu avant l’interprétation du chemin par le système de fichiers.

**Étape 2 — segments**

1. Découpe l’entrée sur `/` et `\`, même si l’application tourne sur un système où l’un de ces caractères n’est pas un séparateur natif.
2. Ignore les segments `.` car ils désignent le dossier courant.
3. Refuse chaque segment exactement égal à `..` : aucune remontée dans l’arborescence n’est autorisée.
4. Refuse les noms terminés par un point ou un espace. Windows peut supprimer ces caractères et faire correspondre un autre nom.
5. Refuse tout caractère interdit par `InvalidNameChars`.
6. Retire l’extension pour vérifier aussi des noms comme `NUL.txt`, puis refuse les périphériques réservés.
7. Ajoute chaque segment propre à la liste utilisée pour construire le chemin final.
8. Si aucun segment utile ne reste, refuse l’entrée vide ou constituée seulement de `.`.

**Étape 3 — résolution et confinement**

1. `Path.GetFullPath(baseDirectory)` normalise la racine autorisée.
2. `Path.Combine` assemble la racine avec les segments validés, puis `GetFullPath` normalise le résultat.
3. `IsInside(root, candidate)` effectue un contrôle final indépendant : si le résultat sort de la racine, la méthode échoue.

Ce dernier contrôle complète les règles segment par segment et protège contre une erreur dans ces règles.

**Étape 4 — liens symboliques et jonctions**

- Si l’option est active, `TraversesReparsePoint(root, candidate)` inspecte les éléments déjà présents entre la racine et la destination.
- Si un de ces éléments est un `ReparsePoint`, le chemin est refusé : un lien pourrait rediriger vers un dossier extérieur.
- Si aucun contrôle ne bloque le chemin, `fullPath` reçoit le candidat, `failureReason` est mis à `null`, et la méthode retourne `true`.

### `IsInside(baseDirectory, fullPath)`

- Vérifie que les deux chemins sont renseignés.
- Normalise le dossier de base avec `GetFullPath`.
- Ajoute un séparateur final à la racine s’il en manque.
- Normalise aussi le candidat.
- Choisit une comparaison sans distinction de casse sur Windows et macOS, avec distinction de casse sur Linux.
- Retourne `true` uniquement si le candidat commence par la racine suivie de son séparateur et est plus long que la racine.

Le séparateur évite le faux positif classique : une racine `C:\Data` ne doit pas accepter `C:\DataEvil\fichier`.

### `TraversesReparsePoint(root, candidate)`

- Calcule la partie relative entre la racine et le candidat.
- Avance composant par composant en construisant le chemin courant.
- Vérifie d’abord que l’élément existe. Cette précaution évite de traiter les attributs particuliers d’un chemin inexistant comme s’il s’agissait d’un lien.
- Arrête la recherche lorsqu’un composant n’existe pas : aucun élément ultérieur ne peut déjà exister en dessous de lui.
- Retourne `true` dès qu’un composant existant porte l’attribut `ReparsePoint`; sinon retourne `false`.

## 7. Comment ces fichiers travaillent ensemble

1. `SafePath` valide et confine un chemin issu d’une source non fiable.
2. `AtomicFile` effectue ensuite des lectures bornées ou écrit des fichiers de façon résistante aux interruptions.
3. `ISecretProtector` définit comment les données sensibles sont protégées avant leur sauvegarde. En production, l’application fournit l’implémentation DPAPI.
4. `UnsafePathException` permet de signaler clairement qu’un chemin a été refusé pour des raisons de sécurité.

Ces fichiers forment le socle du stockage local. Les fichiers suivants à étudier sont `ProfileStore.cs` — qui orchestre les fichiers de profil — puis `ChatPersistence.cs`, et enfin les composants réseau TCP/UDP et de transfert.
