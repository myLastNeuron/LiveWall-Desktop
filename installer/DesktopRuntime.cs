namespace LiveWallSetup;

/// <summary>
/// The runtime contract between LiveWall and setup.
/// </summary>
/// <remarks>
/// LiveWall publishes framework-dependent as net8.0-windows. Its generated runtimeconfig
/// requests Microsoft.WindowsDesktop.App 8.0.0 and does not opt into cross-major roll-forward,
/// so a 9.x or 10.x Desktop Runtime is not a substitute. Setup requires a stable 8.x runtime
/// and installs one when none is present.
/// </remarks>
internal static class DesktopRuntime
{
    internal const int RequiredMajor = 8;
    internal const string FrameworkName = "Microsoft.WindowsDesktop.App";

    /// <summary>The x64 dotnet roots an apphost launched from this setup can resolve.</summary>
    internal static IReadOnlyList<string> DefaultRoots()
    {
        var roots = new List<string>();

        // The setup and the app are win-x64. Program Files (x86) is deliberately not
        // probed: a 32-bit Desktop Runtime cannot host the x64 apphost.
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
            roots.Add(Path.Combine(programFiles, "dotnet"));

        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(dotnetRoot))
            roots.Add(dotnetRoot);

        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static bool IsSatisfied(IEnumerable<string> roots) =>
        roots.Any(root => !string.IsNullOrWhiteSpace(root) &&
                          HasSupportedVersion(Path.Combine(root, "shared", FrameworkName)));

    /// <summary>True when the framework folder contains a stable, exact-major 8.x runtime.</summary>
    internal static bool HasSupportedVersion(string frameworkDir)
    {
        if (!Directory.Exists(frameworkDir)) return false;

        try
        {
            foreach (var sub in Directory.EnumerateDirectories(frameworkDir))
            {
                var name = Path.GetFileName(sub);

                // Preview/RC builds are not a stable install target and are not selected by
                // the app's default roll-forward policy.
                if (name.Contains('-')) continue;

                if (Version.TryParse(name, out var version) && version.Major == RequiredMajor)
                    return true;
            }
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }

        return false;
    }
}
