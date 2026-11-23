namespace RedChat.Infrastructure;

/// <summary>
/// Lecture et écriture de fichiers qui ne perdent jamais de données.
/// <para>ÉCRITURE : on écrit dans un fichier temporaire du MÊME dossier, on force l'écriture sur le disque, puis on remplace l'ancien
/// fichier d'un seul geste (<see cref="File.Replace(string, string, string?, bool)"/>). Une coupure de courant ou un plantage laisse donc
/// soit l'ancienne version complète, soit la nouvelle complète, jamais un fichier tronqué. L'ancienne version est gardée en « .bak ».</para>
/// </summary>
internal static class AtomicFile
{
    /// <summary>Suffixe de la copie de la version précédente.</summary>
    public const string BackupSuffix = ".bak";

    /// <summary>Écrit <paramref name="content"/> dans <paramref name="path"/> de façon atomique.</summary>
    /// <exception cref="IOException">Écriture impossible (disque plein, fichier verrouillé par un autre programme…). L'ancien fichier est intact.</exception>
    public static void Write(string path, byte[] content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);

        string? directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
        {
            throw new ArgumentException("Chemin sans dossier.", nameof(path));
        }

        // Nom aléatoire : pas de nom prévisible qu'un autre programme pourrait occuper à l'avance.
        // audit-ok: chemin interne (jamais saisi par l'utilisateur) ; nom du fichier temporaire = nom du fichier cible + GUID aléatoire, dans le même dossier.
        string temp = Path.Combine(directory, Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            // CreateNew : refuse d'écraser un fichier existant. WriteThrough + Flush(true) : les octets sont sur le disque, pas seulement en cache.
            using (FileStream stream = new(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                File.Replace(temp, path, path + BackupSuffix, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, path);
            }
        }
        finally
        {
            TryDelete(temp);
        }
    }

    /// <summary>
    /// Lit un fichier entier SANS jamais dépasser <paramref name="maxBytes"/> en mémoire (un fichier de 4 Go ne doit pas être chargé).
    /// Retourne <c>null</c> si le fichier n'existe pas.
    /// </summary>
    /// <exception cref="InvalidDataException">Fichier trop gros, ou lien symbolique/jonction (refusé : il pourrait pointer ailleurs).</exception>
    /// <exception cref="IOException">Lecture impossible (fichier verrouillé…) : cas transitoire, à ne PAS traiter comme une corruption.</exception>
    public static byte[]? ReadBounded(string path, long maxBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        FileInfo info = new(path);
        if (!info.Exists)
        {
            return null;
        }

        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Lien symbolique refusé.");
        }

        if (info.Length > maxBytes)
        {
            throw new InvalidDataException("Fichier trop volumineux.");
        }

        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        // La taille est relue sur le flux ouvert : le fichier a pu grossir entre le test et l'ouverture.
        if (stream.Length > maxBytes)
        {
            throw new InvalidDataException("Fichier trop volumineux.");
        }

        byte[] buffer = new byte[stream.Length];
        stream.ReadExactly(buffer);
        return buffer;
    }

    /// <summary>Met un fichier de côté (jamais supprimé : l'utilisateur ou le support peut encore en tirer quelque chose). Retourne le nouveau chemin.</summary>
    public static string Quarantine(string path, TimeProvider clock)
    {
        string stamp = clock.GetUtcNow().ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
        string target = path + ".corrupt-" + stamp + "-" + Guid.NewGuid().ToString("N")[..6];
        File.Move(path, target);
        return target;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Nettoyage au mieux : un résidu « .tmp » est inoffensif (nom aléatoire, jamais relu) et ne doit pas masquer l'erreur d'origine.
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }
    }
}
