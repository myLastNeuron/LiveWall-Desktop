using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Principal;
using Microsoft.Win32;

namespace LiveWallSetup;

/// <summary>Everything the setup window does, minus the window.</summary>
internal static class Install
{
    internal const string AppName = "LiveWall";
    // derived from the csproj <Version>: one place to bump, no copy to drift
    internal static string AppVersion => typeof(Install).Assembly.GetName().Version!.ToString(3);

    private const string DotNetUrl = "https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe";
    private const string WebView2Url = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";
    private const string WebView2ClientId = "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";

    internal static string DefaultDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);

    internal static string StartMenuLnk => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk");

    internal static string DesktopLnk => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk");

    internal static Task RunAsync(string dir, bool desktopShortcut, Action<int, string> report) => Task.Run(async () =>
    {
        report(0, "Creating install folder...");
        Directory.CreateDirectory(dir);
        try { File.WriteAllText(Path.Combine(dir, ".writetest"), ""); File.Delete(Path.Combine(dir, ".writetest")); }
        catch (Exception ex) { throw new InvalidOperationException($"Cannot write to {dir}\n\n{ex.Message}"); }

        report(5, "Copying LiveWall...");
        StopApp(); // an upgrade must be able to overwrite the running exe
        ExtractPayload(dir);

        // Dependencies the app needs that this PC may not have yet.
        if (!HasDesktopRuntime())
        {
            report(10, "Downloading .NET 8 Desktop Runtime...");
            await InstallDownload(DotNetUrl, ".exe", "/install /quiet /norestart", report, 10, 70, elevate: true);
        }

        if (!HasWebView2())
        {
            report(70, "Downloading Microsoft Edge WebView2 Runtime...");
            await InstallDownload(WebView2Url, ".exe", "/silent /install", report, 70, 92, elevate: false);
        }

        report(93, "Creating shortcuts...");
        var exe = Path.Combine(dir, AppName + ".exe");
        CreateShortcut(StartMenuLnk, exe);
        if (desktopShortcut) CreateShortcut(DesktopLnk, exe);
        else if (File.Exists(DesktopLnk)) File.Delete(DesktopLnk);

        report(96, "Registering uninstaller...");
        var uninstallExe = Path.Combine(dir, "Uninstall.exe");
        var self = Environment.ProcessPath!;
        if (!string.Equals(self, uninstallExe, StringComparison.OrdinalIgnoreCase))
            File.Copy(self, uninstallExe, overwrite: true);
        RegisterUninstall(dir, uninstallExe);

        report(100, "Done.");
    });

    private static void StopApp()
    {
        foreach (var p in Process.GetProcessesByName(AppName))
        {
            try { p.Kill(); p.WaitForExit(3000); } catch { /* already gone */ }
        }
    }

    private static void ExtractPayload(string dir)
    {
        var zip = Path.Combine(Path.GetTempPath(), "LiveWallSetup-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            using var res = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip")
                ?? throw new InvalidOperationException("The setup file is damaged (payload missing).");
            using (var f = File.Create(zip)) res.CopyTo(f);
            ZipFile.ExtractToDirectory(zip, dir, overwriteFiles: true);
        }
        finally { try { File.Delete(zip); } catch { /* temp file */ } }
    }

    /// <summary>Download an installer, run it silently, then delete it.</summary>
    private static async Task InstallDownload(string url, string ext, string args,
        Action<int, string> report, int from, int to, bool elevate)
    {
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ext);
        try
        {
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) })
            using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength ?? 0;
                using var src = await resp.Content.ReadAsStreamAsync();
                using var dst = File.Create(file);
                var buf = new byte[81920];
                long done = 0;
                int n;
                while ((n = await src.ReadAsync(buf)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n));
                    done += n;
                    if (total > 0) report(from + (int)((to - from - 8) * done / total), $"Downloading... {done * 100 / total}%");
                }
            }

            report(to - 8, "Installing dependencies... (this can take a few minutes)");
            var psi = new ProcessStartInfo(file, args) { UseShellExecute = elevate && !IsAdmin() };
            if (psi.UseShellExecute) psi.Verb = "runas";
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start the dependency installer.");
            await p.WaitForExitAsync();
            // 3010 / 1641 mean the runtime installed but wants a reboot; treating those as failures
            // aborted setup before the shortcuts and the uninstall entry were written
            if (p.ExitCode is not (0 or 3010 or 1641))
                throw new InvalidOperationException(
                    $"The dependency installer failed (exit code {p.ExitCode}).\n\nYou can install it manually:\n{url}");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException(
                "Windows blocked the dependency installer (permission denied).\n\n" +
                $"Install it manually, then run this setup again:\n{url}");
        }
        finally { try { File.Delete(file); } catch { /* temp file */ } }
    }

    private static bool IsAdmin()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>LiveWall targets net8.0-windows, but a newer major runtime runs it too.</summary>
    private static bool HasDesktopRuntime()
    {
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "dotnet"),
            Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? "",
        };
        return roots.Any(r => r.Length > 0 && HasMajor(Path.Combine(r, "shared", "Microsoft.WindowsDesktop.App"), 8));
    }

    private static bool HasMajor(string dir, int major)
    {
        if (!Directory.Exists(dir)) return false;
        foreach (var sub in Directory.EnumerateDirectories(dir))
            if (Version.TryParse(Path.GetFileName(sub).Split('-')[0], out var v) && v.Major >= major) return true;
        return false;
    }

    private static bool HasWebView2()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
                using (var baseKey = RegistryKey.OpenBaseKey(hive, view))
                using (var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\EdgeUpdate\Clients\" + WebView2ClientId))
                    if (key?.GetValue("pv") is string pv && pv.Length > 0) return true;

        // Registry-free fallback for unusual installs.
        return Directory.Exists(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "EdgeWebView", "Application"));
    }

    private static void CreateShortcut(string lnk, string target)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("Windows Script Host is unavailable, cannot create shortcuts.");
        dynamic shell = Activator.CreateInstance(type)!;
        dynamic sc = shell.CreateShortcut(lnk);
        sc.TargetPath = target;
        sc.WorkingDirectory = Path.GetDirectoryName(target);
        sc.IconLocation = target + ",0";
        sc.Description = "Animated desktop wallpaper";
        sc.Save();
    }

    private static void RegisterUninstall(string dir, string uninstallExe)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + AppName)
            ?? throw new InvalidOperationException("Could not write the uninstall entry.");
        key.SetValue("DisplayName", AppName);
        key.SetValue("DisplayVersion", AppVersion);
        key.SetValue("DisplayIcon", Path.Combine(dir, AppName + ".exe"));
        key.SetValue("InstallLocation", dir);
        key.SetValue("UninstallString", '"' + uninstallExe + '"');
        key.SetValue("Publisher", AppName);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        var bytes = new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        key.SetValue("EstimatedSize", (int)(bytes / 1024), RegistryValueKind.DWord);
    }

    /// <summary>Runs as Uninstall.exe: remove app, shortcuts, registry entry, then the folder itself.</summary>
    internal static void Uninstall(string dir)
    {
        if (MessageBox.Show(
                $"Remove {AppName} from this PC?\n\n{dir}",
                "Uninstall " + AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        StopApp();

        foreach (var lnk in new[] { StartMenuLnk, DesktopLnk })
        {
            try { if (File.Exists(lnk)) File.Delete(lnk); } catch { /* shortcut in use */ }
        }

        try { Registry.CurrentUser.DeleteSubKeyTree(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + AppName, false); }
        catch { /* nothing to remove */ }

        // Start-with-Windows points at the exe being removed; Windows would fail to launch it at logon.
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", writable: true);
            run?.DeleteValue(AppName, throwOnMissingValue: false);
        }
        catch { /* nothing to remove */ }

        // This exe lives inside dir, so let cmd delete it after we exit.
        Process.Start(new ProcessStartInfo("cmd.exe",
            $"/c ping -n 3 127.0.0.1 >nul & rd /s /q \"{dir}\"")
        { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath() });
    }
}
