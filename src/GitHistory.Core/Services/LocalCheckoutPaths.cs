namespace GitHistory.Core.Services;

/// <summary>Maps Git's slash-delimited paths into a linked Windows checkout without accepting path traversal.</summary>
public static class LocalCheckoutPaths
{
    public static string NormalizeRoot(string localFolder)
    {
        if (string.IsNullOrWhiteSpace(localFolder) || !Path.IsPathFullyQualified(localFolder))
            throw new ArgumentException("Choose an absolute path to an existing local Git checkout.", nameof(localFolder));
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(localFolder));
    }

    public static string Resolve(string localFolder, string? repositoryPath)
    {
        string root = NormalizeRoot(localFolder);
        if (string.IsNullOrEmpty(repositoryPath)) return root;
        if (Path.IsPathRooted(repositoryPath) || repositoryPath.Contains('\\'))
            throw new ArgumentException("The selected Git path cannot be mapped safely to a Windows checkout.", nameof(repositoryPath));

        string[] segments = repositoryPath.Split('/');
        foreach (string segment in segments)
        {
            if (segment.Length == 0 || segment is "." or ".." || segment.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                segment.EndsWith(' ') || segment.EndsWith('.') || segment.Any(character => character < 32 || "<>:\"|?*".Contains(character)) || IsDeviceName(segment))
                throw new ArgumentException("The selected Git path cannot be represented safely in a Windows checkout.", nameof(repositoryPath));
        }
        string fullPath = Path.GetFullPath(Path.Combine([root, .. segments]));
        EnsureWithinRoot(root, fullPath);
        return fullPath;
    }

    public static bool IsWithinRoot(string localFolder, string target)
    {
        string root = NormalizeRoot(localFolder);
        string path = Path.GetFullPath(target);
        return path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public static void EnsureWithinRoot(string localFolder, string target)
    {
        if (!IsWithinRoot(localFolder, target))
            throw new ArgumentException("The selected path or symbolic link leaves the linked local checkout.", nameof(target));
    }

    private static bool IsDeviceName(string segment)
    {
        string stem = segment.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '1' and <= '9');
    }
}
