namespace LiveWallSetup;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        var silent = args.Contains("--silent");
        var cleanup = Array.IndexOf(args, "--cleanup");
        if (cleanup >= 0)
        {
            if (args.Length > cleanup + 2 && int.TryParse(args[cleanup + 2], out var parentProcessId))
                Environment.Exit(Install.CleanupAfterExit(args[cleanup + 1], parentProcessId, silent));
            Environment.Exit(2);
        }

        var self = Environment.ProcessPath ?? "";
        var uninstall = args.Contains("--uninstall") ||
                        Path.GetFileNameWithoutExtension(self).Equals("Uninstall", StringComparison.OrdinalIgnoreCase);

        if (uninstall)
        {
            // The uninstaller sits in the install folder it is removing.
            Environment.Exit(Install.Uninstall(Path.GetDirectoryName(self)!, silent) ? 0 : 2);
        }

        // Unattended: LiveWallSetup.exe --silent <folder> [--desktop]
        var s = Array.IndexOf(args, "--silent");
        if (s >= 0)
        {
            var log = Path.Combine(Path.GetTempPath(), "livewall-setup.log");
            File.WriteAllText(log, "");
            try
            {
                if (args.Length <= s + 1) throw new InvalidOperationException("Missing install folder after --silent.");
                Install.RunAsync(args[s + 1], args.Contains("--desktop"),
                    (pct, text) => File.AppendAllText(log, $"{pct,3}% {text}\r\n")).GetAwaiter().GetResult();
                File.AppendAllText(log, "done\r\n");
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                File.AppendAllText(log, "failed: " + ex.Message + "\r\n");
                Environment.Exit(1);
            }
        }

        Application.Run(new SetupForm());
    }
}
