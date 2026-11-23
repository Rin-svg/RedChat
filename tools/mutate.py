#!/usr/bin/env python3
"""Test de mutation manuel (skill csharp-gui-dev, testing.md §9).
Chaque mutation casse volontairement UN endroit du code de production ; la suite de tests DOIT échouer.
Usage : mutate.py <premier> <dernier>   (numéros de mutation, inclus)"""
import pathlib, re, subprocess, sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
SRC = ROOT / "src"

# (id, fichier, ancien texte, nouveau texte, description)
M = [
 (1, "RedChat.Core/ChatServiceState.cs", 'RequireStored(m.SenderId == _self, "Message sortant d\'un autre auteur.");', 'RequireStored(true, "Message sortant d\'un autre auteur.");', "import : auteur d'un message sortant non vérifié"),
 (2, "RedChat.Core/ChatServiceState.cs", "if (_convs.Count != 0 || _outbox.Count != 0)", "if (_convs.Count < 0)", "import : service non vide accepté"),
 (3, "RedChat.Core/ChatService.cs", "            StateChanged?.Invoke(this, EventArgs.Empty);", "            if (events.Count > 0) { StateChanged?.Invoke(this, EventArgs.Empty); }", "StateChanged muet si seul l'outbox change"),
 (4, "RedChat.Core/ChatServiceState.cs", "participants.Contains(r.Key, StringComparer.Ordinal) && InputValidator.IsValidReaction(r.Value)", "InputValidator.IsValidReaction(r.Value)", "import : réaction d'un étranger acceptée"),
 (5, "RedChat.Core/ChatServiceState.cs", "Reactions = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(m.Reactions, StringComparer.Ordinal)),", "Reactions = m.Reactions,", "import : dictionnaire de l'instantané conservé (pas de copie)"),
 (6, "RedChat.Core/ContactBook.cs", "name.Normalize(NormalizationForm.FormKC).ToUpperInvariant()", "name", "contacts : noms visuellement identiques non détectés"),
 (7, "RedChat.Core/SecurityCode.cs", "string.CompareOrdinal(peerIdA, peerIdB) < 0 ? (peerIdA, peerIdB) : (peerIdB, peerIdA)", "(peerIdA, peerIdB)", "code de sécurité : plus symétrique"),
 (8, "RedChat.Core/ContactBook.cs", "if (_contacts.Values.Any(o => o.PeerId != peerId && o.DisplayName is not null && NameKey(o.DisplayName) == key))", "if (_contacts.Count < 0)", "contacts : doublons de noms acceptés"),
 (9, "RedChat.Infrastructure/ProfileStore.cs", "when (ex is InvalidDataException or CryptographicException or JsonException)\n            {\n                mainIsBad = true;", "when (ex is InvalidDataException or CryptographicException or JsonException or IOException)\n            {\n                mainIsBad = true;", "verrou transitoire pris pour une corruption"),
 (10, "RedChat.Infrastructure/ProfileStore.cs", "return blob is null ? CreateIdentity(path) : OpenIdentity(blob);", "try { return blob is null ? CreateIdentity(path) : OpenIdentity(blob); } catch (ProfileStoreException) { return CreateIdentity(path); }", "identité illisible remplacée en silence par une nouvelle"),
 (11, "RedChat.Infrastructure/AtomicFile.cs", "File.Replace(temp, path, path + BackupSuffix, ignoreMetadataErrors: true);", "File.Copy(temp, path, overwrite: true);", "écriture non atomique, sans copie .bak"),
 (12, "RedChat.Infrastructure/ChatPersistence.cs", "string? contactsFailure = contacts && _contacts is not null ?", "string? contactsFailure = chatFailure is null && contacts && _contacts is not null ?", "contacts sautés (et perdus) si l'historique échoue"),
 (13, "RedChat.Infrastructure/ChatPersistence.cs", "Interlocked.CompareExchange(ref _timerArmed, 1, 0) != 0)", "Interlocked.CompareExchange(ref _timerArmed, 1, 0) < 0)", "plus d'anti-rafale"),
 (14, "RedChat.Infrastructure/ChatPersistence.cs", "Volatile.Write(ref dirtyFlag, 1);", "Volatile.Write(ref dirtyFlag, dirtyFlag);", "état non remarqué « à enregistrer » après un échec"),
 (15, "RedChat.Infrastructure/ProfileStore.cs", "DirectoryPath = SafePath.ResolveInside(Path.GetFullPath(rootDirectory), profileName);", "DirectoryPath = Path.Combine(Path.GetFullPath(rootDirectory), profileName);", "SafePath contourné"),
 (16, "RedChat.Infrastructure/AtomicFile.cs", "File.Move(path, target);", "File.Delete(path);", "quarantaine = suppression"),
 (17, "RedChat.Infrastructure/ProfileStore.cs", "UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,", "UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,", "champs inconnus du JSON acceptés"),
 (18, "RedChat.Infrastructure/ProfileStore.cs", "AtomicFile.Write(path, _protector.Protect(pkcs8));", "AtomicFile.Write(path, _protector.Protect(pkcs8).Length > 0 ? pkcs8 : pkcs8);", "clé privée écrite en clair"),
 (19, "RedChat.Infrastructure/ChatPersistence.cs", "TimeSpan.FromTicks((long)Math.Min(_debounce.Ticks * factor, MaxRetryDelay.Ticks))", "TimeSpan.FromTicks((long)(_debounce.Ticks * factor))", "délai de reprise non plafonné"),
 (20, "RedChat.Infrastructure/ChatPersistence.cs", "if (Volatile.Read(ref _started) == 1)\n        {\n            Flush();", "if (Volatile.Read(ref _started) == 2)\n        {\n            Flush();", "pas de dernière sauvegarde à la fermeture"),
 (21, "RedChat.Infrastructure/ChatPersistence.cs", "                    _store.QuarantineHistory();", "                    _ = 0;", "historique incohérent non mis de côté"),
 (22, "RedChat.Infrastructure/AtomicFile.cs", "Length > maxBytes", "Length > maxBytes * 1000", "taille du fichier non bornée (les DEUX contrôles affaiblis)"),

 (23, "RedChat.Core/ChatServiceState.cs", "long newestAllowedMs = ProtocolLimits.StoredTimestampCeiling.ToUnixTimeMilliseconds();", "long newestAllowedMs = (Now() + ProtocolLimits.MaxFutureSkew).ToUnixTimeMilliseconds();", "import : retour de la borne now+1j sur les messages"),
 (24, "RedChat.Core/ChatServiceState.cs", "DateTimeOffset packetClock = ProtocolLimits.StoredTimestampCeiling - ProtocolLimits.MaxFutureSkew;", "DateTimeOffset packetClock = Now();", "import : retour de la borne now+1j sur les paquets en attente"),
 (25, "RedChat.Core/ContactBook.cs", "long newestAllowedMs = ProtocolLimits.StoredTimestampCeiling.ToUnixTimeMilliseconds();", "long newestAllowedMs = (_clock.GetUtcNow() + ProtocolLimits.MaxFutureSkew).ToUnixTimeMilliseconds();", "contacts : retour de la borne now+1j"),
]

def run_suite():
    r = subprocess.run(["./tools/run-sandbox-tests.sh"], cwd=ROOT, capture_output=True, text=True, timeout=300)
    out = r.stdout + r.stderr
    fails = re.findall(r"ÉCHEC\s+(\S+)", out)
    m = re.search(r"Résultat : (\d+) réussis, (\d+) échoués", out)
    errs = re.findall(r"error [A-Z]+\d+", out)
    return fails, m, errs, out

lo, hi = int(sys.argv[1]), int(sys.argv[2])
survivors, build = [], []
for mid, rel, old, new, desc in M:
    if not (lo <= mid <= hi):
        continue
    path = SRC / rel
    original = path.read_text(encoding="utf-8")
    if original.count(old) not in (1, 2):
        print(f"[{mid:02d}] ?? motif trouvé {original.count(old)} fois : {rel} — mutation ignorée")
        continue
    try:
        path.write_text(original.replace(old, new), encoding="utf-8")
        fails, m, errs, out = run_suite()
    finally:
        path.write_text(original, encoding="utf-8")   # TOUJOURS restaurer
    if m is None:
        build.append(mid)
        print(f"[{mid:02d}] ERREUR DE COMPILATION ({errs[:2]}) — {desc}")
    elif fails:
        names = sorted({f.split('(')[0] for f in fails})
        print(f"[{mid:02d}] TUÉE  par {len(fails):3d} test(s) — {desc}\n        ex. {', '.join(names[:2])}")
    else:
        survivors.append(mid)
        print(f"[{mid:02d}] SURVIVANTE !!  — {desc}")
print("\nSurvivantes :", survivors or "aucune", "| erreurs de compilation :", build or "aucune")
