namespace Maia.Core.Scanning;

/// <summary>Folder-containment check shared by the folder browser and the source viewer.</summary>
public static class PathBoundary
{
    /// <summary>
    /// True when <paramref name="full"/> is <paramref name="root"/> or inside it.
    /// Both must already be full paths (<see cref="Path.GetFullPath(string)"/>).
    /// Trailing-separator compare so "C:\data" doesn't admit "C:\database".
    /// </summary>
    public static bool IsUnder(string full, string root)
    {
        static string WithSep(string p) => Path.EndsInDirectorySeparator(p) ? p : p + Path.DirectorySeparatorChar;
        return WithSep(full).StartsWith(WithSep(root), StringComparison.OrdinalIgnoreCase);
    }
}
