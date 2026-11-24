using System.Text;
using System.Text.Json;

namespace LiveWallSetup;

/// <summary>
/// Owns the install folder contract: a folder is LiveWall's only when it is empty or it contains
/// a valid marker written by this product. The marker is also the exact uninstall file list.
/// </summary>
internal static class InstallOwnership
{
    internal const string ProductId = "livewall-desktop";
    internal const string MarkerFileName = ".livewall-install.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    internal sealed class Marker
    {
        public string ProductId { get; set; } = "";
        public string InstallPath { get; set; } = "";
        public string Version { get; set; } = "";
        public List<string> Files { get; set; } = new();
        public List<string> Directories { get; set; } = new();
    }

    internal static string MarkerPath(string dir) => Path.Combine(dir, MarkerFileName);

    /// <summary>Normalizes, then rejects roots, Windows itself, traversal, and non-local paths.</summary>
    internal static string NormalizePath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException("Choose an install folder.");

        var expanded = Environment.ExpandEnvironmentVariables(raw.Trim());
        if (expanded.IndexOf('\0') >= 0)
            throw new InvalidOperationException("The install folder contains an invalid character.");

        var segments = expanded.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(s => s == ".."))
            throw new InvalidOperationException("The install folder cannot contain a parent-directory ('..') path segment.");

        if (!Path.IsPathFullyQualified(expanded))
            throw new InvalidOperationException("Use an absolute install folder, for example D:\\LiveWall.");

        var full = Path.GetFullPath(expanded);
        if (full.StartsWith(@"\\", StringComparison.Ordinal))
            throw new InvalidOperationException("LiveWall installs on a local drive, not a network or device path.");

        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root) || root.Length < 2 || root[1] != ':')
            throw new InvalidOperationException("LiveWall installs on a local drive, not a network or device path.");

        var normalized = full.TrimEnd('\\', '/');
        if (string.Equals(normalized, root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Pick a folder below a drive root, for example D:\\LiveWall.");

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrWhiteSpace(windows))
        {
            windows = Path.GetFullPath(windows).TrimEnd('\\', '/');
            if (IsSameOrChild(normalized, windows))
                throw new InvalidOperationException("Windows system folders cannot be used as the LiveWall install folder.");
        }

        return normalized;
    }

    /// <summary>The shared preflight for both the setup window and the --silent path.</summary>
    internal static string ValidateInstallTarget(string? raw)
    {
        var dir = NormalizePath(raw);
        if (!Directory.Exists(dir)) return dir;

        var marker = ReadMarker(dir);
        if (!Directory.EnumerateFileSystemEntries(dir).Any()) return dir;

        if (marker is null)
            throw new InvalidOperationException(
                $"The folder already contains files and is not a LiveWall install:\n\n{dir}\n\n" +
                "Choose an empty folder or an existing LiveWall install.");

        return dir;
    }

    internal static Marker? ReadMarker(string dir)
    {
        var path = MarkerPath(dir);
        if (!File.Exists(path)) return null;

        try
        {
            var info = new FileInfo(path);
            if (info.Length is <= 0 or > 1_048_576) return null;

            var marker = JsonSerializer.Deserialize<Marker>(File.ReadAllText(path), JsonOptions);
            if (marker is null || !string.Equals(marker.ProductId, ProductId, StringComparison.Ordinal)) return null;
            if (!Version.TryParse(marker.Version, out _)) return null;
            if (!string.Equals(NormalizePath(marker.InstallPath), dir, StringComparison.OrdinalIgnoreCase)) return null;

            marker.Files = NormalizeRelativeList(dir, marker.Files, allowRoot: false);
            marker.Directories = NormalizeRelativeList(dir, marker.Directories, allowRoot: true);
            return marker;
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static void WriteMarker(string dir, string version, IEnumerable<string> installedFiles, Marker? previous)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in installedFiles)
            files.Add(RelativePath(dir, file, allowRoot: false));

        if (previous is not null)
        {
            foreach (var file in previous.Files) files.Add(RelativePath(dir, file, allowRoot: false));
            foreach (var child in previous.Directories) dirs.Add(RelativePath(dir, child, allowRoot: true));
        }

        files.Remove(MarkerFileName);
        foreach (var file in files)
            for (var child = Path.GetDirectoryName(file); !string.IsNullOrEmpty(child); child = Path.GetDirectoryName(child))
                dirs.Add(child);

        var marker = new Marker
        {
            ProductId = ProductId,
            InstallPath = dir,
            Version = version,
            Files = files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList(),
            Directories = dirs.OrderByDescending(d => d.Length).ThenBy(d => d, StringComparer.OrdinalIgnoreCase).ToList(),
        };

        var json = JsonSerializer.Serialize(marker, JsonOptions);
        var path = MarkerPath(dir);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temp); } catch { /* best effort */ }
        }
    }

    /// <summary>Deletes only the validated paths recorded in the marker, then the marker itself.</summary>
    internal static bool DeleteOwnedFiles(string dir, Marker marker, Action<string>? log = null)
    {
        var files = new List<string>();
        var dirs = new List<string>();
        try
        {
            foreach (var file in marker.Files)
                files.Add(ResolvePath(dir, file, allowRoot: false));
            foreach (var child in marker.Directories)
                dirs.Add(ResolvePath(dir, child, allowRoot: true));
        }
        catch (Exception ex)
        {
            log?.Invoke("refused invalid marker path: " + ex.Message);
            return false;
        }

        var markerPath = MarkerPath(dir);
        var failed = false;
        foreach (var full in files)
        {
            if (string.Equals(full, markerPath, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                if (!File.Exists(full))
                {
                    log?.Invoke("file already absent: " + full);
                    continue;
                }
                File.Delete(full);
                log?.Invoke("removed file: " + full);
            }
            catch (Exception ex)
            {
                failed = true;
                log?.Invoke("could not remove file: " + full + " (" + ex.Message + ")");
            }
        }

        if (failed) return false;

        foreach (var full in dirs.OrderByDescending(d => d.Length))
        {
            try
            {
                if (Directory.Exists(full) && !Directory.EnumerateFileSystemEntries(full).Any())
                {
                    Directory.Delete(full, recursive: false);
                    log?.Invoke("removed directory: " + full);
                }
            }
            catch (Exception ex)
            {
                log?.Invoke("left directory in place: " + full + " (" + ex.Message + ")");
            }
        }

        try
        {
            File.Delete(markerPath);
            log?.Invoke("removed marker: " + markerPath);
        }
        catch (Exception ex)
        {
            log?.Invoke("could not remove marker: " + markerPath + " (" + ex.Message + ")");
            return false;
        }

        try
        {
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir, recursive: false);
                log?.Invoke("removed empty install folder: " + dir);
            }
            else
            {
                log?.Invoke("kept install folder because it contains unowned entries: " + dir);
            }
        }
        catch (Exception ex)
        {
            log?.Invoke("kept install folder: " + dir + " (" + ex.Message + ")");
        }

        return true;
    }

    private static string RelativePath(string dir, string value, bool allowRoot)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException("The ownership marker contains an empty path.");

        var raw = value.Trim();
        if (raw.IndexOf('\0') >= 0 || Path.IsPathRooted(raw))
            throw new InvalidOperationException("The ownership marker contains an absolute path.");

        var segments = raw.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(s => s == ".."))
            throw new InvalidOperationException("The ownership marker contains a parent-directory path.");

        var full = Path.GetFullPath(Path.Combine(dir, raw));
        if (!IsSameOrChild(full, dir))
            throw new InvalidOperationException("The ownership marker points outside the install folder.");
        if (!allowRoot && string.Equals(full.TrimEnd('\\', '/'), dir, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The ownership marker contains the install folder as a file.");

        var relative = Path.GetRelativePath(dir, full);
        if (relative == ".") return relative;
        return relative;
    }

    private static string ResolvePath(string dir, string value, bool allowRoot) =>
        Path.GetFullPath(Path.Combine(dir, RelativePath(dir, value, allowRoot)));

    private static List<string> NormalizeRelativeList(string dir, IEnumerable<string>? values, bool allowRoot)
    {
        var result = new List<string>();
        foreach (var value in values ?? Array.Empty<string>())
        {
            var relative = RelativePath(dir, value, allowRoot);
            if (relative != "." && !result.Contains(relative, StringComparer.OrdinalIgnoreCase)) result.Add(relative);
        }
        return result;
    }

    private static bool IsSameOrChild(string path, string parent)
    {
        parent = parent.TrimEnd('\\', '/');
        path = path.TrimEnd('\\', '/');
        return string.Equals(path, parent, StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
