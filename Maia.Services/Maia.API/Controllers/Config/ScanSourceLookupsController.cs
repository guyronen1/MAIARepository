using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Maia.API.Controllers;

/// <summary>
/// Pickers for the Scan Source drawer: the configured connection-string KEYS (never
/// values) for Database sources, and a server-side folder browser for FileSystem /
/// FileContent sources. Server-side because the folder must be readable by the API /
/// worker host, and a browser can't hand back a full local path anyway. Admin-only —
/// source create/update is Admin, and listing the host's folders is more sensitive
/// than the Operator config reads. Directories only, never files.
/// </summary>
[ApiController]
[Route("api/config")]
[Authorize(Policy = "RequireAdmin")]
public class ScanSourceLookupsController(
    IConfiguration                        config,
    ILogger<ScanSourceLookupsController>  logger) : ControllerBase
{
    private const int MaxFolders = 500;

    private static readonly EnumerationOptions DirOptions = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible    = true,
        AttributesToSkip      = FileAttributes.Hidden | FileAttributes.System,
    };

    /// <summary>Connection-string keys from every config provider (appsettings.{Env}, env vars).</summary>
    [HttpGet("connection-names")]
    public IActionResult GetConnectionNames() =>
        Ok(config.GetSection("ConnectionStrings").GetChildren()
            .Where(c => !string.IsNullOrWhiteSpace(c.Value))
            .Select(c => c.Key)
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToArray());

    /// <summary>
    /// Lists the sub-folders of <paramref name="path"/>; no path → just the roots
    /// (<c>FolderBrowser:Roots</c> when configured, else every ready fixed/network drive).
    /// Folder errors are 400/404, not 403 — the client maps a plain 403 to a
    /// "no permission" toast, which would misdescribe an ACL problem on the folder.
    /// </summary>
    [HttpGet("browse-folders")]
    public IActionResult BrowseFolders([FromQuery] string? path)
    {
        var configuredRoots = ConfiguredRoots();
        var roots = (configuredRoots.Length > 0 ? configuredRoots : DriveRoots())
            .Select(r => new FolderEntry(r, r)).ToArray();

        if (string.IsNullOrWhiteSpace(path))
            return Ok(new FolderListing(null, null, [], roots, false));

        string full;
        try
        {
            if (!Path.IsPathFullyQualified(path.Trim()))
                return BadRequest(new { error = "InvalidPath", message = $"'{path}' is not an absolute path." });
            full = Path.GetFullPath(path.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return BadRequest(new { error = "InvalidPath", message = $"'{path}' is not a valid path." });
        }

        if (configuredRoots.Length > 0 && !configuredRoots.Any(r => IsUnder(full, r)))
            return BadRequest(new { error = "PathOutsideRoots",
                message = $"'{full}' is outside the folders MAIA is allowed to browse (FolderBrowser:Roots)." });

        if (!Directory.Exists(full))
            return NotFound(new { error = "FolderNotFound", message = $"Folder '{full}' does not exist or is not visible to the MAIA server." });

        List<string> dirs;
        try
        {
            dirs = Directory.EnumerateDirectories(full, "*", DirOptions).Take(MaxFolders + 1).ToList();
        }
        catch (UnauthorizedAccessException)
        {
            return BadRequest(new { error = "FolderAccessDenied",
                message = $"The MAIA server account has no access to '{full}'." });
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Folder browse failed for {Path}", full);
            return BadRequest(new { error = "FolderReadFailed", message = $"Could not read '{full}': {ex.Message}" });
        }

        var truncated = dirs.Count > MaxFolders;
        var folders = dirs.Take(MaxFolders)
            .Select(d => new FolderEntry(Path.GetFileName(d), d))
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // Up stops at the configured roots — the parent of a root is out of bounds.
        var parent = Directory.GetParent(full)?.FullName;
        if (parent is not null && configuredRoots.Length > 0 && !configuredRoots.Any(r => IsUnder(parent, r)))
            parent = null;

        return Ok(new FolderListing(full, parent, folders, roots, truncated));
    }

    private string[] ConfiguredRoots() =>
        config.GetSection("FolderBrowser:Roots").GetChildren()
            .Select(c => c.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => Path.GetFullPath(v!.Trim()))
            .ToArray();

    private static IEnumerable<string> DriveRoots() =>
        DriveInfo.GetDrives()
            .Where(d => d.DriveType is DriveType.Fixed or DriveType.Network && d.IsReady)
            .Select(d => d.RootDirectory.FullName);

    /// <summary>Trailing-separator compare so "C:\data" doesn't admit "C:\database".</summary>
    private static bool IsUnder(string full, string root)
    {
        static string WithSep(string p) => Path.EndsInDirectorySeparator(p) ? p : p + Path.DirectorySeparatorChar;
        return WithSep(full).StartsWith(WithSep(root), StringComparison.OrdinalIgnoreCase);
    }

    public sealed record FolderEntry(string Name, string FullPath);
    public sealed record FolderListing(string? Path, string? Parent, FolderEntry[] Folders, FolderEntry[] Roots, bool Truncated);
}
