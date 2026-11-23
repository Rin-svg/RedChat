using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace RedChat.Infrastructure.Security;

/// <summary>
/// Transforme un chemin relatif <b>non fiable</b> (saisi par l'utilisateur, lu dans un fichier,
/// nom d'une entrée de zip, réponse réseau…) en chemin absolu <b>garanti à l'intérieur</b> d'un
/// dossier autorisé.
/// </summary>
/// <remarks>
/// <para>
/// MENACE COUVERTE : « path traversal ». Sans cette vérification, un nom comme <c>..\..\Windows\x.dll</c>
/// ou un chemin absolu permet d'écrire ou de lire n'importe où sur le disque.
/// </para>
/// <para>
/// APPROCHE : liste blanche stricte. On refuse tout ce qui est ambigu ou exotique plutôt que d'essayer
/// de le « corriger » : corriger silencieusement une entrée hostile masque l'attaque et ouvre des contournements.
/// </para>
/// <para>
/// Aucune dépendance externe ; fonctionne sous Windows, macOS et Linux.
/// </para>
/// </remarks>
public static class SafePath
{
    // LIMITE : 240 caractères pour la partie relative. Windows plafonne historiquement le chemin complet
    // à 260 caractères (MAX_PATH) ; on garde de la marge pour le dossier de base.
    /// <summary>Longueur maximale (en caractères) acceptée pour la partie relative d'un chemin.</summary>
    public const int MaxRelativePathLength = 240;

    // Noms de périphériques réservés par Windows : « CON », « NUL.txt »… désignent un périphérique, pas un fichier.
    // On les refuse partout (même sous Linux) pour qu'un fichier créé ici reste copiable vers Windows.
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    // Caractères interdits dans un nom : ceux de Windows (< > : " | ? *) PLUS ceux que le système courant refuse.
    // Contrairement à Path.GetInvalidFileNameChars() seul, la partie Windows est la même sur tous les systèmes
    // (pas de surprise selon la machine). Le « : » est refusé car il introduit un lecteur (C:) ou un flux
    // alternatif NTFS (fichier.txt:cache). SearchValues : ensemble précalculé, recherche rapide et réutilisée.
    private static readonly SearchValues<char> InvalidNameChars =
        SearchValues.Create("<>:\"|?*" + new string(Path.GetInvalidFileNameChars()));

    /// <summary>
    /// Résout <paramref name="untrustedRelativePath"/> dans <paramref name="baseDirectory"/> ou lève une exception.
    /// </summary>
    /// <param name="baseDirectory">Dossier autorisé (fiable, choisi par le programme, pas par l'utilisateur).</param>
    /// <param name="untrustedRelativePath">Chemin relatif non fiable.</param>
    /// <param name="rejectReparsePoints">
    /// Refuser les liens symboliques et jonctions déjà présents sous le dossier de base (par défaut : oui).
    /// </param>
    /// <returns>Chemin absolu, strictement à l'intérieur de <paramref name="baseDirectory"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="baseDirectory"/> est vide.</exception>
    /// <exception cref="UnsafePathException">Le chemin est refusé ; le message est affichable à l'utilisateur.</exception>
    public static string ResolveInside(
        string baseDirectory, string? untrustedRelativePath, bool rejectReparsePoints = true)
    {
        if (!TryResolveInside(baseDirectory, untrustedRelativePath, out string? fullPath, out string? reason,
                rejectReparsePoints))
        {
            throw new UnsafePathException(reason);
        }

        return fullPath;
    }

    /// <summary>
    /// Version sans exception de <see cref="ResolveInside"/> : utile pour la validation de formulaire,
    /// où un chemin invalide est un cas normal et non exceptionnel.
    /// </summary>
    /// <param name="baseDirectory">Dossier autorisé (fiable).</param>
    /// <param name="untrustedRelativePath">Chemin relatif non fiable.</param>
    /// <param name="fullPath">Chemin absolu sûr si le résultat est <c>true</c>.</param>
    /// <param name="failureReason">Raison affichable (sans chemin interne) si le résultat est <c>false</c>.</param>
    /// <param name="rejectReparsePoints">Refuser les liens symboliques / jonctions existants.</param>
    /// <returns><c>true</c> si le chemin est sûr.</returns>
    /// <exception cref="ArgumentException"><paramref name="baseDirectory"/> est vide (erreur de programmation, pas de saisie).</exception>
    public static bool TryResolveInside(
        string baseDirectory,
        string? untrustedRelativePath,
        [NotNullWhen(true)] out string? fullPath,
        [NotNullWhen(false)] out string? failureReason,
        bool rejectReparsePoints = true)
    {
        // Le dossier de base vient du programme : un dossier vide est un BUG, pas une saisie invalide.
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        fullPath = null;

        // --- 1. Contrôles sur le texte brut (avant toute interprétation par le système de fichiers) ---------
        if (string.IsNullOrWhiteSpace(untrustedRelativePath))
        {
            failureReason = "Le chemin est vide.";
            return false;
        }

        if (untrustedRelativePath.Length > MaxRelativePathLength)
        {
            failureReason = "Le chemin est trop long.";
            return false;
        }

        foreach (char c in untrustedRelativePath)
        {
            // ROBUSTESSE : '\0' tronque les chemins dans certaines API natives (« a.txt\0.exe » ressemble à « a.txt »).
            // Les autres caractères de contrôle (retours à la ligne…) n'ont rien à faire dans un nom de fichier.
            if (char.IsControl(c))
            {
                failureReason = "Le chemin contient un caractère interdit.";
                return false;
            }
        }

        // Un chemin absolu (C:\x, \\serveur\partage, /etc/passwd) écraserait le dossier de base dans Path.Combine.
        // On teste aussi '/' et '\' initiaux explicitement, quel que soit le système d'exploitation.
        if (Path.IsPathRooted(untrustedRelativePath)
            || untrustedRelativePath[0] is '/' or '\\')
        {
            failureReason = "Le chemin doit être relatif (sans lecteur ni barre oblique initiale).";
            return false;
        }

        // --- 2. Contrôle segment par segment ------------------------------------------------------------------
        // On traite '/' ET '\' comme séparateurs sur tous les systèmes : un fichier créé ici doit rester
        // valide partout, et « ..\x » ne doit pas passer sous Linux sous prétexte que '\' n'y est pas un séparateur.
        string[] rawSegments = untrustedRelativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (rawSegments.Length == 0)
        {
            failureReason = "Le chemin est vide.";
            return false;
        }

        var segments = new List<string>(rawSegments.Length);
        foreach (string segment in rawSegments)
        {
            if (segment == ".")
            {
                continue; // « . » = dossier courant : sans effet, on l'ignore.
            }

            // SÉCURITÉ : « .. » est refusé tel quel, même quand il resterait à l'intérieur (« a/../b »).
            // C'est plus simple à raisonner et à prouver sûr : aucune remontée n'est jamais acceptée.
            if (segment == "..")
            {
                failureReason = "Le chemin ne peut pas remonter dans l'arborescence (« .. »).";
                return false;
            }

            // Windows supprime silencieusement les points et espaces finals : « CON. » et « CON » sont le même nom,
            // « fichier. » et « fichier » désignent le même fichier. Source classique de contournement.
            if (segment.EndsWith('.') || segment.EndsWith(' '))
            {
                failureReason = "Un nom ne peut pas se terminer par un point ou un espace.";
                return false;
            }

            if (segment.AsSpan().ContainsAny(InvalidNameChars))
            {
                failureReason = "Le chemin contient un caractère interdit (< > : \" | ? *).";
                return false;
            }

            // « NUL.txt » est aussi un périphérique : on compare la partie avant le premier point.
            string nameWithoutExtension = segment.Split('.')[0].TrimEnd();
            if (ReservedDeviceNames.Contains(nameWithoutExtension))
            {
                failureReason = "Ce nom est réservé par le système.";
                return false;
            }

            segments.Add(segment);
        }

        if (segments.Count == 0)
        {
            failureReason = "Le chemin est vide.";
            return false;
        }

        // --- 3. Résolution et vérification de confinement -----------------------------------------------------
        string root = Path.GetFullPath(baseDirectory);
        string candidate = Path.GetFullPath(Path.Combine(root, string.Join(Path.DirectorySeparatorChar, segments)));

        // Défense en profondeur : même si les contrôles ci-dessus avaient un trou, ce test final tranche.
        if (!IsInside(root, candidate))
        {
            failureReason = "Le chemin sort du dossier autorisé.";
            return false;
        }

        // --- 4. Liens symboliques / jonctions ------------------------------------------------------------------
        // Un lien déjà présent (« docs » → « C:\Windows ») ferait aboutir un chemin apparemment innocent hors du
        // dossier autorisé. On refuse de traverser tout point d'analyse existant sous la base.
        if (rejectReparsePoints && TraversesReparsePoint(root, candidate))
        {
            failureReason = "Le chemin traverse un lien symbolique, ce qui n'est pas autorisé.";
            return false;
        }

        fullPath = candidate;
        failureReason = null;
        return true;
    }

    /// <summary>
    /// Indique si <paramref name="fullPath"/> se trouve <b>strictement à l'intérieur</b> de
    /// <paramref name="baseDirectory"/> (le dossier de base lui-même renvoie <c>false</c>).
    /// </summary>
    /// <remarks>
    /// PIÈGE ÉVITÉ : comparer avec un simple <c>StartsWith("C:\Data")</c> accepterait <c>C:\DataEvil\x</c>.
    /// On force donc un séparateur final sur la base avant de comparer.
    /// Utile aussi pour valider un chemin renvoyé par une boîte de dialogue « Ouvrir… ».
    /// </remarks>
    /// <param name="baseDirectory">Dossier autorisé.</param>
    /// <param name="fullPath">Chemin à contrôler (sera normalisé avec <see cref="Path.GetFullPath(string)"/>).</param>
    /// <returns><c>true</c> si le chemin est à l'intérieur du dossier.</returns>
    public static bool IsInside(string baseDirectory, string fullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);

        string root = Path.GetFullPath(baseDirectory);
        if (!Path.EndsInDirectorySeparator(root))
        {
            root += Path.DirectorySeparatorChar;
        }

        string candidate = Path.GetFullPath(fullPath);

        // Les systèmes de fichiers de Windows et macOS ignorent la casse par défaut ; Linux la respecte.
        StringComparison comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return candidate.Length > root.Length && candidate.StartsWith(root, comparison);
    }

    /// <summary>Parcourt chaque composant existant entre la base et la cible, à la recherche d'un lien.</summary>
    private static bool TraversesReparsePoint(string root, string candidate)
    {
        string relative = Path.GetRelativePath(root, candidate);
        string current = root;

        foreach (string part in relative.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, part);

            // ROBUSTESSE : FileSystemInfo.Attributes d'un élément inexistant vaut -1 (tous les bits à 1),
            // ce qui ferait croire à un lien. On teste donc l'existence d'abord, et on s'arrête dès qu'un
            // composant n'existe pas (rien d'existant en dessous : aucun lien possible).
            if (!File.Exists(current) && !Directory.Exists(current))
            {
                return false;
            }

            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }
        }

        return false;
    }
}
