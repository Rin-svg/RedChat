# RedChat.Core — explication détaillée

Ce guide parcourt les 19 fichiers source de `src\RedChat.Core`. Les lignes sont expliquées par déclaration et par bloc d’instructions : chaque bloc est relié à son rôle dans le programme, sans répéter la syntaxe identique à chaque occurrence. Les fichiers générés dans `bin` et `obj` ne font pas partie du code source à expliquer.

## 1. Rôle de Core

`RedChat.Core` contient les règles centrales de RedChat : identités, messages, validation, protocole, chiffrement, contacts et logique de conversation. Il ne dépend ni de WPF, ni du stockage concret, ni du réseau réel. Infrastructure fournit les adaptateurs réseau et disque ; Presentation affiche l’état de Core.

Le parcours d’un message est, en résumé :

1. `InputValidator` contrôle le texte et `ChatService` construit un `Packet`.
2. `PacketSerializer` valide puis sérialise le paquet en JSON.
3. `Handshake` authentifie les identités et dérive les clés de session.
4. `SecureChannel` scelle le paquet ; `FrameCodec` délimite les octets sur TCP.
5. À la réception, les octets sont ouverts, désérialisés, validés et autorisés par `ChatService.Receive`.

## 2. Projet et limites communes

### `RedChat.Core.csproj`

Fichier : `src\RedChat.Core\RedChat.Core.csproj`

- Le SDK .NET standard et `net10.0` construisent une bibliothèque portable, non une application graphique.
- `RootNamespace` fixe l’espace de noms `RedChat.Core`.
- `InternalsVisibleTo` expose les éléments internes au projet de tests Core, sans les rendre publics à l’application.
- Le projet n’a pas de dépendance NuGet supplémentaire : il utilise les bibliothèques .NET.

### `ProtocolLimits.cs`

Ce fichier centralise les bornes que tous les autres composants réutilisent :

- `ProtocolVersion = 6` : version échangée au handshake ; des versions incompatibles peuvent être refusées.
- `MaxFrameBytes = 768 * 1024` : taille maximale de trame TCP. La valeur borne les allocations et tient compte du JSON et du base64.
- `MaxHandshakeFieldBytes`, `HandshakeNonceBytes` et `SignatureBytes` fixent les tailles de champs du handshake : 512, 32 et 64 octets.
- `AesKeyBytes`, `AesNonceBytes`, `AesTagBytes` décrivent AES-256-GCM : clé de 32 octets, nonce de 12, tag d’authentification de 16.
- `CounterBytes` et `SealOverheadBytes` calculent le surcoût des messages chiffrés : compteur de 8 octets + tag.
- `MaxAttachmentBytes` et `AttachmentChunkBytes` limitent fichier et blocs ; le transfert est borné en taille et morcelé.
- `StatusMediaChunkBytes`, `MaxStatusMediaNameChars` bornent les médias et noms de statuts.
- `MaxAttachmentFileNameChars`, `MaxCallSdpChars`, `MaxIceCandidateChars` et `MaxIceMidChars` limitent les métadonnées et messages d’appel.
- `MaxTextChars`, `MaxDisplayNameChars`, `MaxGroupNameChars`, `MaxGroupMembers`, `MaxReactionChars` et `MaxProfileNameChars` fixent les limites fonctionnelles et de saisie.
- `MaxJsonDepth` borne la profondeur du JSON ; `MinPort` interdit les ports système lors d’une connexion manuelle.
- `HandshakeTimeout` limite le temps accordé à la négociation à dix secondes.
- `MaxFutureSkew` tolère une date réseau jusqu’à un jour dans le futur.
- `StoredTimestampCeiling` est une borne absolue pour les dates restaurées depuis le disque. Elle évite de rejeter l’historique si l’horloge du PC a été déréglée puis corrigée.

### `ProtocolViolationException.cs`

Cette exception représente une violation du protocole par un pair ou des données reçues :

- Les constructeurs permettent une erreur générique, un message journalisable, ou un message avec cause interne.
- Le commentaire rappelle que le gestionnaire réseau doit couper la connexion plutôt que présenter les détails techniques directement à l’utilisateur.
- Les parseurs et validateurs l’utilisent pour uniformiser les erreurs de taille, de format, de signature ou d’autorisation.

## 3. Encodage et transport des octets

### `ByteCodec.cs`

`ByteWriter` et `ByteReader` construisent et lisent les petits messages binaires du handshake :

- `ByteWriter` accumule les octets dans une liste.
- `WriteByte` ajoute un octet.
- `WriteFixed` ajoute un champ de taille fixée par le protocole, sans préfixe de longueur.
- `WriteField` écrit la longueur sur deux octets big-endian, refuse un champ supérieur à `MaxHandshakeFieldBytes`, puis écrit son contenu.
- `ToArray` retourne le tableau final.
- `ByteReader` conserve le tableau d’entrée et une position courante `_pos`.
- `ReadByte` vérifie qu’il reste un octet avant de le retourner.
- `ReadFixed(length)` refuse les tailles négatives ou dépassant la fin du message, copie la tranche et avance la position.
- `ReadField` reconstitue la longueur u16 en big-endian, applique la limite du protocole, puis lit le champ.
- `EnsureEnd` refuse les octets résiduels : un message valide doit être consommé entièrement.

Chaque accès est borné afin qu’une donnée tronquée ou malformée devienne une `ProtocolViolationException`, pas un accès hors limites.

### `FrameCodec.cs`

TCP transporte un flux continu et ne conserve pas les frontières d’un appel `Write`. `FrameCodec` ajoute donc un en-tête de longueur de quatre octets :

- `WriteFrameAsync` contrôle le flux et exige une charge utile non vide qui ne dépasse pas `MaxFrameBytes`.
- Il encode la longueur en entier non signé big-endian, écrit l’en-tête puis la charge utile, et vide le flux.
- `ReadFrameAsync` lit l’en-tête en entier. Si la connexion se ferme avant le premier octet, elle retourne `null` (fermeture propre).
- Un en-tête partiel, une longueur nulle ou une longueur trop grande déclenche une violation de protocole.
- La taille est validée avant l’allocation du tableau de charge utile.
- La charge utile est lue entièrement ; un flux qui se termine trop tôt produit une erreur de trame tronquée.
- `ReadUpToAsync` répète les lectures car un `Stream.ReadAsync` peut retourner moins d’octets que demandé.

Ce codec encadre les trames, mais le chiffrement proprement dit est assuré par `SecureChannel`.

## 4. Identité, authentification et chiffrement de bout en bout

### `Identity.cs`

#### `PeerIdentity`

- `P256Oid` identifie la courbe elliptique P-256 autorisée.
- `ComputePeerId` calcule le SHA-256 de la clé publique encodée en SubjectPublicKeyInfo, puis le rend en hexadécimal minuscule. Le PeerId est donc lié à la clé publique.
- `ImportPublicKey` importe une clé publique reçue, exige que tous les octets aient été consommés, et refuse toute courbe autre que P-256.
- La ré-exportation doit être byte-à-byte identique à l’entrée. Cette canonicalisation garantit qu’une même clé ne reçoit pas plusieurs PeerId selon son encodage.
- Les erreurs cryptographiques, ASN.1 et de format sont converties en `ProtocolViolationException`; l’objet de clé est libéré en cas d’échec.
- `VerifySignature` exige exactement 64 octets, puis vérifie la signature ECDSA sur SHA-256 au format IEEE P1363 fixe `r || s`.

#### `LocalIdentity`

- L’objet détient la clé privée ECDSA ; elle n’est pas publiée sur le réseau.
- Son constructeur calcule et expose la clé publique SPKI et son PeerId.
- `Create` génère une nouvelle paire de clés P-256.
- `FromPrivateKey` importe une clé PKCS#8 déchiffrée par l’appelant et vérifie la courbe. Si l’import échoue, il libère la clé avant de relancer l’erreur.
- `ExportPrivateKey` produit le PKCS#8 pour que l’appelant le protège immédiatement avant le stockage.
- `Sign` signe les données avec ECDSA-SHA256 au format P1363.
- `Dispose` libère la ressource cryptographique native.

### `Handshake.cs`

Le handshake établit des secrets de session et prouve la possession des clés d’identité. Il comporte trois messages :

1. Initiateur → répondeur : clé ECDH éphémère et nonce.
2. Répondeur → initiateur : clé ECDH éphémère, nonce, identité publique et signature du transcript.
3. Initiateur → répondeur : identité publique et signature liant le transcript au rôle initiateur et à l’identité du répondeur.

#### `HandshakeCrypto`

- Les constantes de type distinguent Hello1, Hello2 et Hello3.
- `ResponderLabel`, `InitiatorLabel` et `KeyInfo` séparent cryptographiquement les usages et les rôles.
- `RandomNonce` produit 32 octets aléatoires.
- `NewEphemeral` crée une nouvelle clé ECDH P-256 pour cette connexion ; elle est distincte de l’identité persistante.
- `ExportEphemeral` encode la clé publique éphémère.
- `ImportEphemeral` importe la clé reçue, vérifie P-256 et l’encodage canonique, puis convertit les erreurs en violation de protocole.
- `Transcript` assemble les deux clés éphémères et les deux nonces dans un format à champs explicites, puis calcule SHA-256. Les signatures portent ainsi sur les deux côtés de la négociation.
- `ResponderSignedData` préfixe le transcript avec l’étiquette répondeur.
- `InitiatorSignedData` inclut l’étiquette initiateur, le transcript et le hash de la clé publique du répondeur.
- `DeriveKeys` calcule le secret partagé ECDH, applique HKDF-SHA256 avec le transcript comme sel et `KeyInfo` comme contexte, puis découpe le résultat en deux clés directionnelles. Le secret brut est effacé de la mémoire dans `finally`.
- `BuildHello1/2/3` sérialisent le type de message et ses champs selon le format binaire.
- `ParseHello1/2/3` relisent les champs et exigent l’absence d’octets en trop.
- `ExpectType` refuse un message d’un autre type.
- `Concat` construit les données signées en copiant chaque composant dans un seul tableau.

#### `HandshakeInitiator`

- Le constructeur exige une identité locale. Un PeerId distant attendu optionnel est validé, utile quand une découverte réseau annonçait déjà un identifiant précis.
- `Start` refuse un second démarrage, génère la clé éphémère et le nonce, puis produit Hello1.
- `ProcessHello2` vérifie l’ordre d’appel et décode la réponse.
- Il calcule le PeerId de la clé publique reçue, refuse une connexion à soi-même et compare l’identité attendue si elle est renseignée.
- Il recalcule le transcript, importe la clé d’identité du répondeur et vérifie sa signature. Une clé ou signature invalide arrête l’établissement.
- Il importe la clé ECDH distante, dérive les deux clés, signe à son tour le transcript lié au répondeur, puis crée `SecureChannel` avec la clé initiateur→répondeur pour l’envoi et l’autre pour la réception.
- Il conserve le PeerId authentifié, efface les copies de clés de session temporaires, puis retourne Hello3.
- `Dispose` détruit la clé ECDH éphémère.

#### `HandshakeResponder`

- Le constructeur retient l’identité locale du répondeur.
- `ProcessHello1` n’accepte qu’un premier message, importe la clé éphémère de l’initiateur, crée ses propres clé et nonce, calcule le transcript et les clés directionnelles, puis signe le transcript en tant que répondeur et retourne Hello2.
- `ProcessHello3` vérifie que Hello1 a déjà été traité et qu’aucun canal n’existe.
- Il calcule le PeerId de l’initiateur, refuse sa propre identité, puis vérifie la signature initiateur sur le transcript et sur la clé publique du répondeur.
- Seulement après cette vérification, il construit le canal avec les clés dans le sens inverse de l’initiateur et publie le PeerId distant.
- Il efface ensuite les tableaux contenant les clés directionnelles.
- `Dispose` libère la clé ECDH éphémère.

Les clés sont éphémères et dérivées par connexion : connaître plus tard une clé d’identité ne suffit pas à recalculer le secret partagé des anciennes sessions. L’authentification à la première connexion reste toutefois de type TOFU ; la comparaison manuelle du code de sécurité protège contre une substitution initiale d’identité.

### `SecureChannel.cs`

`SecureChannel` chiffre et authentifie chaque message avec AES-256-GCM :

- Le constructeur crée une instance AES-GCM pour l’envoi et une autre pour la réception, chacune avec sa clé directionnelle.
- `MaxPlaintextBytes` soustrait du plafond de trame le compteur et le tag d’authentification.
- `Seal` refuse les textes trop longs, verrouille les compteurs pour le multi-threading et refuse toute utilisation après `Dispose`.
- Il refuse l’épuisement du compteur, car réutiliser un nonce AES-GCM avec la même clé serait dangereux.
- Le message scellé commence par le compteur big-endian. Le nonce de 12 octets est formé avec ce compteur et des zéros initiaux.
- `Encrypt` chiffre le clair, produit un tag de 16 octets et authentifie le compteur comme données associées (AAD). L’en-tête reste visible mais ne peut pas être falsifié sans échec d’authentification.
- Le compteur d’envoi n’avance qu’après le chiffrement réussi.
- `Open` vérifie d’abord la longueur, puis exige exactement le compteur de réception attendu. Cela bloque rejeu, réordonnancement et saut de messages.
- Il reconstruit le nonce, sépare le chiffré du tag, puis déchiffre en validant le compteur comme AAD.
- Une erreur d’authentification est convertie en `ProtocolViolationException`; le compteur ne progresse pas lorsqu’un paquet échoue.
- `Dispose` détruit les deux objets AES-GCM sous verrou.

Le compteur fonctionne aussi comme anti-rejeu. Cette garantie dépend de la livraison ordonnée de TCP ; à la reconnexion, un nouveau handshake crée de nouvelles clés et de nouveaux compteurs.

### `SecurityCode.cs`

- Le code de sécurité se calcule à partir des deux PeerId, après validation qu’ils sont distincts et valides.
- Les PeerId sont triés ordinalement pour obtenir le même résultat des deux côtés.
- `DomainTag` sépare ce calcul des autres usages du hachage.
- SHA-512 est appliqué au tag et aux deux identifiants.
- Les 60 premiers octets du hash sont découpés en douze groupes de cinq octets (40 bits chacun).
- Chaque groupe est ramené modulo 100 000 puis affiché sur cinq chiffres, zéros initiaux inclus.
- Le résultat est constitué de 12 groupes que les personnes peuvent comparer par un canal différent de la connexion.

## 5. Validation des entrées et données publiques

### `InputValidator.cs`

Cette classe est la barrière centrale ; les validations de l’interface seules ne suffisent pas.

- Les expressions régulières générées compilent des règles strictes pour les profils et PeerId. `\A` et `\z` interdisent notamment un saut de ligne final.
- `IsValidProfileName` accepte seulement 1 à 30 caractères ASCII, chiffres, `_` et `-`.
- `IsValidPeerId` exige 64 caractères hexadécimaux minuscules.
- `IsValidGuid` accepte uniquement la représentation GUID « D » à 36 caractères.
- `TryNormalizeName` supprime les espaces en bordure, limite la longueur, refuse les chaînes vides et vérifie chaque caractère contre les contrôles et les caractères invisibles/bidi.
- `IsValidMessageText` refuse le vide, les textes trop longs et les caractères de contrôle à l’exception de nouvelle ligne, retour chariot et tabulation.
- `IsValidReaction` limite la taille et refuse contrôle, espaces, marqueurs directionnels et caractères invisibles.
- `IsBidiOrInvisible` repère les caractères pouvant masquer ou inverser visuellement le texte ; les caractères nécessaires aux emojis composés sont explicitement laissés possibles ailleurs.
- `TryParseLanEndpoint` analyse `IP:port`, impose une longueur maximale, un port à partir de `MinPort` et une adresse LAN.
- `IsLanAddress` convertit d’abord les IPv4 encapsulées en IPv6. Elle accepte IPv4 privée, boucle locale et lien-local ; côté IPv6, boucle locale, lien-local et adresse locale unique. Les adresses publiques et familles inconnues sont refusées.
- `IsDisallowedInName` regroupe le filtre des noms contre les contrôles et caractères bidi/invisibles.

### `PersonalProfile.cs`

`PersonalProfile` est un record immuable de données publiques, séparé de l’identité cryptographique :

- `SchemaVersion` versionne le format ; `PeerId` lie le profil à une identité.
- `DisplayName`, `Status`, `About` et `AvatarBytes` contiennent les informations partagées.
- `Validate(expectedPeerId)` exige la version 1 et une correspondance exacte du PeerId.
- Le statut et la description doivent être présents ; le nom est normalisé et limité à 40 caractères.
- Le statut est rogné, peut être vide, sinon est normalisé et limité à 80 caractères.
- La description est rognée, limitée à 280 caractères et ne peut contenir certains contrôles ou caractères invisibles.
- L’avatar doit mesurer au plus 512 Kio et être non vide s’il existe.
- `IsPng` et `IsJpeg` vérifient les signatures binaires, et non seulement l’extension du fichier.
- La méthode retourne une copie normalisée et clone les octets de l’avatar pour éviter que l’appelant modifie la donnée après validation.
- `CreateDefault` crée un profil initial lié au PeerId fourni.

## 6. Modèles de messages et paquets réseau

### `ChatModels.cs`

- `MessageStatus` représente la progression monotone d’un message sortant : Pending, Sent, Delivered, Read. Les valeurs sont ordonnées pour permettre les comparaisons.
- `ChatChangeKind` décrit les événements que l’interface doit refléter : conversation, message, mise à jour, frappe, suppression.
- `ChatChangedEventArgs` transporte la nature du changement et, si disponibles, l’identifiant du message et le pair.
- `ChatMessage` est un record immuable : le service remplace l’objet entier lors d’un changement, au lieu de muter ses propriétés.
- Les propriétés du message identifient le message, la conversation, l’auteur, le texte et la date UTC de l’expéditeur.
- Les booléens indiquent sens entrant/sortant, édition, suppression, vue unique, ouverture et lecture locale.
- Les propriétés calculées `CanOpenViewOnce`, `ViewOnceLabel` et `Status` portent `[JsonIgnore]` : elles sont dérivées et ne doivent pas être restaurées depuis une donnée manipulable.
- `Reactions` mappe une identité par emoji ; `Receipts` mappe chaque destinataire vers son état. Les dictionnaires exposés sont en lecture seule.
- `ConversationInfo` décrit une conversation personnelle, privée ou de groupe ; seuls les groupes portent titre, membres et créateur.
- `ITransport` est le port réseau abstrait. `TrySend` ne doit pas bloquer, car ChatService peut l’appeler sous verrou. L’Infrastructure fournit la réalisation TCP chiffrée.

### `Packet.cs`

- Les énumérations `PacketKind`, `CallControlAction`, `CallMediaKind` et `ReceiptState` donnent des valeurs nommées au protocole : texte, accusés, groupes, profils, appels, pièces jointes, statuts, etc.
- `Packet` est un modèle « à plat » : un seul objet rassemble les propriétés possibles, et `Kind` détermine quelles propriétés doivent être présentes.
- Les champs communs sont le type, l’identifiant, la conversation et l’horodatage.
- Les champs texte couvrent messages, édition, vue unique et réponse.
- Les champs d’accusé/réaction/suppression ciblent un message par GUID.
- Les champs de groupe transportent le nom et les PeerId des membres.
- Les champs de profil transportent l’identité publique, son nom, son statut, sa description et l’avatar.
- Les champs de pièce jointe donnent transfert, nom de fichier, taille, hash SHA-256, index/count et octets du bloc.
- Les champs d’appel transportent l’identifiant, l’action, audio/vidéo et les données SDP/ICE avec MID et index.
- Les champs de statut transportent métadonnées, dates, média, hash et blocs.
- Les champs d’audience de statut sont explicitement réservés et doivent rester absents du réseau pour ne pas révéler la liste privée des contacts.
- `[JsonIgnore(Condition = WhenWritingDefault)]` évite d’écrire certains booléens faux dans le JSON.

### `PacketSerializer.cs`

Ce fichier est la frontière stricte entre octets JSON et objets de protocole.

**Options JSON**

- Les noms de propriétés sont camelCase ; leur casse est sensible.
- Les valeurs nulles ne sont pas écrites.
- Les propriétés JSON inconnues sont refusées, ce qui évite d’accepter silencieusement une forme inattendue.
- La profondeur maximale vient de `ProtocolLimits.MaxJsonDepth`.
- Les enums sont sérialisées comme chaînes camelCase ; les nombres ne sont pas acceptés.
- `ReservedAttachmentNames` bloque les noms de périphériques Windows lors des transferts.

**Entrée/sortie**

- `Serialize` refuse un objet null, appelle `Validate`, puis sérialise en UTF-8. Le programme ne produit pas volontairement de paquet qui échouerait à sa propre validation.
- `Deserialize` refuse un JSON vide ou supérieur à la taille de trame, convertit les erreurs `JsonException` en `ProtocolViolationException`, refuse un résultat null, puis valide l’objet.

**Validation commune**

- `Validate` exige un type enum connu, un GUID de paquet valide, une date positive qui n’est pas trop dans le futur, et un identifiant de conversation PeerId ou GUID.
- Le `switch` applique ensuite une liste blanche de règles correspondant exactement à chaque type :
  - `Text` : texte valide, réponse éventuelle GUID, vue unique uniquement en privé.
  - `Receipt` : message ciblé et état d’accusé connu.
  - `Typing` : aucun champ supplémentaire requis.
  - `GroupCreate` : GUID de groupe, nom déjà normalisé, nombre de membres borné, PeerId valides et distincts.
  - `Reaction`, `Delete`, `Edit` : identifiant visé valide et contenu spécifique valide.
  - `ViewOnceOpened` et `AttachmentViewOnceOpened` : conversation privée et cible/transfert valide.
  - `AttachmentChunk` : validation détaillée du fichier et de son bloc.
  - `CallControl` : conversation privée, session, action et type audio/vidéo ; chaque action autorise un ensemble précis de champs. Offre/réponse requièrent un SDP borné ; candidat ICE exige données, MID et index dans les limites.
  - `StatusPublish` et `StatusMediaChunk` : validations détaillées décrites ci-dessous.
  - `StatusViewReceipt` et `StatusDelete` : événement privé et identifiant de statut valide.
  - `ProfileUpdate` : champs publics présents, PeerId du profil égal à la conversation, puis réutilisation de `PersonalProfile.Validate`.
- Les données de profil invalides sont enveloppées dans une violation de protocole.

**Publications et blocs de statuts**

- `ValidateStatusPublish` exige une conversation privée, un ID GUID, un type de contenu valide et un texte borné sans contrôles dangereux.
- Les dates sont bornées, converties en UTC, et doivent respecter l’expiration de 24 heures ; un statut expiré ou daté trop loin dans le futur est refusé.
- Les règles d’audience privées doivent être nulles dans le paquet : seuls les destinataires choisis reçoivent la publication.
- Un statut texte exige du texte et refuse les métadonnées de média.
- Un statut photo/vidéo exige nom sûr, taille autorisée et SHA-256 de 64 caractères hexadécimaux.
- `ValidateStatusMediaChunk` valide l’ID, taille totale, hash, index, nombre de blocs calculé depuis la taille et longueur attendue de ce bloc.

**Pièces jointes**

- `ValidateAttachmentChunk` vérifie le transfert GUID, le nom sans chemin, la taille totale, le SHA-256, l’index/count et la longueur exacte des octets du bloc.
- La vue unique est limitée aux conversations privées et aux extensions média autorisées.
- `IsSafeAttachmentName` refuse les chemins, séparateurs, contrôles, caractères Windows spéciaux, points/espaces terminaux et noms réservés.
- `IsViewOnceMediaName` autorise une liste d’extensions image/vidéo connue ; cela filtre le type annoncé mais ne remplace pas la vérification réelle du contenu média.
- `Require` centralise l’échec : toute règle fausse lève une `ProtocolViolationException`.

## 7. Logique de conversation

### `ChatService.cs`

`ChatService` contient les règles d’envoi, réception, autorisation et file d’attente, sans ouvrir de socket ni écrire directement sur disque.

**État et construction**

- `MaxOutboxPerPeer` borne à 1000 paquets la file d’un destinataire hors ligne.
- La classe privée `Conv` associe une `ConversationInfo` à la liste ordonnée de ses messages.
- `_lock` protège l’état partagé ; `_self` identifie l’utilisateur courant ; `_transport` envoie au réseau ; `_clock` rend le temps déterministe en tests.
- `_convs` et `_outbox` contiennent les conversations et files par pair.
- Le constructeur valide les dépendances et le PeerId local, puis injecte l’horloge.
- `Changed` notifie l’interface des événements visibles ; `StateChanged` notifie la persistance même lorsqu’une modification ne produit pas d’événement d’interface.
- Les méthodes de lecture retournent des tableaux instantanés sous verrou, plutôt que d’exposer les collections internes.

**Création et suppression**

- `SendText` valide texte et réponse, obtient ou crée une conversation, refuse la vue unique hors privé, réserve la place nécessaire dans les files, construit paquet et message local avec accusés Pending, puis enfile le paquet pour chaque destinataire.
- `EnsureDirectConversation` crée ou retourne une discussion privée sans envoyer de message factice.
- `EnsureSelfConversation` crée une conversation locale de notes sans destinataire réseau.
- `RemoveConversation` valide l’identifiant, enlève la conversation et filtre les paquets en attente liés à celle-ci dans toutes les files, puis notifie l’interface.
- `CreateGroup` normalise le nom, valide et déduplique les membres, ajoute l’utilisateur local, réserve les files, crée le GUID du groupe puis envoie un paquet de création à chaque membre.

**Actions sur les messages**

- `React` valide l’emoji, exige que le message existe et ne soit pas supprimé, remplace la réaction de l’utilisateur local puis diffuse le contrôle.
- `EditMessage` exige un texte valide et un message sortant non supprimé, remplace le record avec `IsEdited = true` et diffuse l’édition.
- `DeleteMessage` autorise uniquement la suppression d’un message sortant et le transforme en tombstone, effaçant texte, réponse et réactions avant diffusion.
- `ConsumeViewOnceMessage` exige un message privé entrant admissible, le supprime localement et tente d’envoyer une confirmation à l’auteur.
- `MarkRead` marque chaque message entrant non lu et envoie un accusé « lu » (sauf contenu supprimé).
- `SendTyping` envoie un paquet de frappe aux destinataires connectés, sans file d’attente : une indication périmée n’a pas d’intérêt.

**Connexion et files hors ligne**

- `OnPeerConnected` réinsère les messages dont l’état `Sent` n’a jamais été confirmé, puis vide la file dans l’ordre.
- `RequeueUnconfirmed` parcourt les conversations adressées au pair. Si un message a été transmis mais n’a pas d’accusé, il recrée le paquet avec le même ID ; le destinataire peut le dédupliquer et ré-accuser.
- `Enqueue` crée si nécessaire la file, impose sa limite et appelle `Flush`. Certains accusés sont « best effort » : s’ils ne peuvent pas entrer dans une file pleine, la réception n’est pas bloquée.
- `Flush` tente d’envoyer le premier paquet ; si le pair est absent, il conserve la file et l’ordre. Après réussite, il retire le paquet et fait passer le reçu d’un texte de Pending à Sent.
- `EnsureOutboxRoom` pré-vérifie tous les destinataires d’une opération multi-destinataires afin de ne pas commencer une diffusion qui échouerait en cours de route.
- `Recipients` renvoie aucun destinataire pour les notes personnelles, les membres distants pour un groupe et l’autre PeerId pour une discussion privée.

**Réception et autorisations**

- `Receive(senderPeerId, packet)` reçoit l’identité authentifiée fournie par le handshake, non une identité déclarée dans le paquet.
- Il vérifie l’expéditeur, revalide le paquet (défense en profondeur) et refuse les paquets de profil/pièce jointe qui relèvent d’autres services.
- Les créations de groupe passent par `ReceiveGroupCreate`. Les autres paquets sont résolus vers une conversation puis envoyés à `Dispatch`.
- `ResolveIncoming` exige que le paquet privé soit adressé à l’utilisateur local ; un texte peut créer automatiquement une nouvelle conversation. Pour un groupe, celui-ci doit exister et l’expéditeur doit en être membre.
- `ReceiveGroupCreate` exige que l’expéditeur et le destinataire figurent parmi les membres. Une retransmission du même créateur est ignorée ; une tentative de détournement d’un ID de groupe est rejetée.
- `Dispatch` gère chaque type attendu :
  - Un texte déjà vu par ID est dédupliqué mais ré-accusé ; sinon il est ajouté et un reçu Delivered est envoyé.
  - Un reçu passe à `ApplyReceipt`.
  - Typing produit un événement d’interface.
  - Réaction met à jour la réaction de l’expéditeur.
  - Édition et suppression appellent `RequireAuthor`, puis modifient seulement le message de son auteur.
  - La confirmation de vue unique n’est acceptée que pour un message privé sortant correspondant.
  - Un type inattendu est une violation de protocole.
- `ApplyReceipt` ignore un ID de message inconnu (course/réconciliation), mais rejette un accusé émis par un pair qui ne figure pas parmi les destinataires. L’état ne peut jamais régresser de Read vers Delivered.

**Aides et notifications**

- `Now` utilise l’horloge injectée ; `NewId` produit un GUID « D ».
- `GetOrCreateOutgoingConv` réutilise la conversation ou ne crée qu’un PeerId valide non local.
- `RequireConv` signale une conversation absente.
- `Control` complète un paquet de contrôle avec un ID et une date, et choisit l’identifiant de conversation que le destinataire attend.
- `BroadcastControl` envoie le contrôle aux destinataires du groupe ou du pair.
- `SendReceipt` construit l’accusé livré/lu et l’enfile en best effort.
- `ConvKeyForOutgoing` retrouve la conversation locale correcte à partir de la forme privée ou groupe du paquet.
- `Update` remplace un message si connu ; un message inconnu est ignoré car il peut arriver après suppression/purge.
- `RequireAuthor` interdit de modifier/supprimer le message d’un autre auteur.
- `Tombstone` efface les éléments privés tout en conservant la trace que le message a été supprimé.
- `SetReaction` et `SetReceipt` créent de nouveaux dictionnaires, remplacent l’entrée d’une personne et les exposent en lecture seule.
- `Raise` prépare un événement sans l’émettre immédiatement.
- `Fire` émet les événements après la libération du verrou ; son `finally` déclenche `StateChanged` afin qu’une exception d’abonné n’empêche pas la sauvegarde de l’état déjà modifié.

### `ChatServiceState.cs`

Cette partie `partial` gère l’import/export durable. Le fichier sauvegardé est considéré non fiable : il peut être corrompu ou modifié.

- `MaxStoredConversations` et `MaxStoredMessagesPerConversation` bornent la mémoire à restaurer.
- `ExportState` prend sous verrou une photographie cohérente des conversations, messages et files non vides.
- `ImportState` construit d’abord toute la nouvelle structure dans des dictionnaires temporaires, puis l’installe d’un seul geste si le service est vide.
- `BuildStoredState` vérifie la version et les collections, borne leur nombre, puis valide chaque conversation et file.
- Les dates persistées sont comparées à une borne absolue, pas à l’horloge courante ; cela évite de perdre un historique légitime après correction de l’horloge du PC.
- Chaque paquet en attente repasse dans `PacketSerializer.Validate`; l’échec devient `InvalidDataException`.
- `BuildStoredConversation` vérifie les formes autorisées : conversation personnelle, groupe (membres valides, utilisateur présent, créateur membre) ou discussion privée.
- Il copie défensivement les membres, les messages et les dictionnaires pour ne pas partager des collections que l’appelant pourrait ensuite modifier.
- `BuildStoredMessage` vérifie ID unique, conversation, auteur, date, texte, réponse, vue unique, reçus, réactions et relations de participants.
- Un message sortant doit avoir exactement un reçu par destinataire ; un entrant ne peut avoir de reçus locaux et son auteur doit appartenir à la conversation.
- Un tombstone ne peut conserver réactions, réponse ou marqueur d’édition.
- Les dictionnaires de réactions et reçus sont reconstruits comme `ReadOnlyDictionary`.
- `RequireStored` centralise le refus par `InvalidDataException`. L’import est tout ou rien : aucune donnée partiellement restaurée ne reste en mémoire.

## 8. Contacts, favoris et statuts

### `ContactBook.cs`

- `Contact` associe un PeerId, un nom local, une date de première connexion, un indicateur de vérification et un profil public distant.
- `ContactsSnapshot` enveloppe la liste de contacts avec une version de format.
- `ContactBook` applique le modèle TOFU : première identité authentifiée enregistrée, mais non vérifiée tant que l’utilisateur n’a pas comparé le code de sécurité.
- Le nom local est séparé du nom public reçu ; un pair ne peut donc pas imposer un nom trompeur à l’interface.
- `_lock`, `_contacts`, `_self` et `_clock` protègent l’état, identifient l’utilisateur et rendent le temps testable.
- Le constructeur valide l’identité locale ; `Changed` est émis après libération du verrou.
- `GetAll` retourne un instantané trié ; `Find` renvoie le contact ou null.
- `DisplayLabel` privilégie le surnom local, puis le profil public, puis un préfixe du PeerId.
- `EnsureKnown` refuse l’identité locale et les PeerId invalides, retourne le contact existant ou ajoute un contact non vérifié avec date courante.
- `UpdatePublicProfile` valide l’association profil/PeerId ; le budget total d’avatars est limité à 5 Mio. Si le budget serait dépassé, le profil est conservé sans avatar.
- `ProfilesEqual` compare tous les champs et les octets d’avatar pour éviter une notification/écriture inutile.
- `Rename` normalise le surnom, garantit qu’il est unique après normalisation Unicode Form KC et casse ignorée.
- `SetVerified` marque ou démarque la vérification utilisateur.
- `Remove` ne notifie que si un contact a vraiment été supprimé.
- `Export` retourne un instantané.
- `Import` valide entièrement version, nombre, PeerId, date, nom, unicité, profil et total des avatars avant de modifier le carnet vide.
- `NameKey` normalise en Form KC et majuscules invariantes pour réduire les noms visuellement équivalents.
- `RequireValidPeer` rejette le PeerId local ainsi que les identifiants invalides ; `RequireContact` lève si le contact est absent.
- `RequireStored` uniforme les erreurs d’un instantané invalide.

### `FavoriteConversations.cs`

- `FavoriteConversationsSnapshot` contient la version et les IDs de conversation.
- `FavoriteConversations` conserve les favoris uniquement sur l’appareil ; ils ne sont pas transmis aux pairs.
- `MaxFavorites` borne la collection à 1000 ; `_gate` et `_ids` protègent l’accès concurrent.
- `Contains` valide l’ID avant recherche ; `GetAll` retourne une copie triée.
- `Add` refuse les doublons sans événement et lève si le plafond est atteint ; les événements sont émis hors verrou.
- `Remove` ne déclenche `Changed` que si un élément a réellement été retiré.
- `Toggle` inverse l’état sous verrou et renvoie le nouvel état.
- `Export` produit un instantané ; `Import` exige une version reconnue, une liste bornée, des IDs valides et distincts, puis une collection vide.
- Un ID favori est soit un PeerId, soit un GUID de groupe.

### `EphemeralStatus.cs`

- `StatusAudienceMode` représente partage à tous, tous sauf une liste, ou liste sélectionnée ; `StatusContentKind` distingue texte, photo et vidéo.
- `StatusAudience.Validate` vérifie l’identité propriétaire, le mode, le nombre et la validité des PeerId, interdit de s’inclure soi-même et vérifie que la liste est cohérente avec le mode.
- `Allows` applique la règle d’inclusion/exclusion à un pair donné.
- `EphemeralStatus` contient l’auteur, le contenu, les dates, l’audience et les vues. Durée de vie : 24 heures ; média maximal : 20 Mio.
- `Validate(now, isLocalOwner)` contrôle version, IDs, enums, dates UTC, expiration exacte, décalage d’horloge, texte, média, audience et vues.
- Un statut texte exige un texte non vide et aucun média ; photo/vidéo exige des octets non vides, une taille bornée et la signature attendue.
- `ViewedByPeerIds` ne peut être non vide que pour la copie de l’auteur local, afin qu’un pair ne prétende pas transmettre les vues d’autres utilisateurs.
- La méthode retourne une copie indépendante des collections et octets média.
- `IsActive` vérifie que l’instant courant se trouve dans l’intervalle création incluse / expiration exclue.
- `HasExpectedSignature` délègue à `IsPng`, `IsJpeg`, `IsMp4`, `IsWebM`, qui vérifient les signatures de format attendues.

### `ChatSnapshot.cs`

- `ConversationSnapshot` stocke la description de conversation et sa liste ordonnée de messages.
- `OutboxSnapshot` stocke le destinataire et ses paquets en attente, dans leur ordre d’envoi.
- `ChatSnapshot` versionne l’ensemble et contient historique + outbox dans un instantané unique, pour maintenir leur cohérence après redémarrage.
- `IMessageStore` abstrait le stockage durable : `Load` retourne un état ou null ; `Save` doit remplacer de manière atomique.
- Core ne connaît pas le chemin ni le mécanisme de protection : cette abstraction garde le domaine testable sans disque.

## 9. Vue d’ensemble du chiffrement

Le chiffrement réseau concerne les conversations qui passent entre deux instances RedChat :

- `Handshake.cs` authentifie les deux clés d’identité par signatures ECDSA et calcule une clé partagée ECDH éphémère, puis deux clés directionnelles HKDF.
- `SecureChannel.cs` chiffre et authentifie les données de chaque trame avec AES-256-GCM, un nonce compteur et détection de rejeu.
- `FrameCodec.cs` fournit seulement les frontières de messages au flux TCP ; il n’ajoute pas de cryptographie.
- `PacketSerializer.cs` valide le contenu après déchiffrement ; validation et chiffrement ont des rôles distincts.
- `IMessageStore` et `ISecretProtector` concernent le stockage local, pas le chiffrement réseau de bout en bout.
- Les statuts d’appels sont des paquets de signalisation ; le média temps réel d’appel n’est pas transmis par ce canal de conversation.
