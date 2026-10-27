namespace LiveWallSetup;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        var self = Environment.ProcessPath ?? "";
        var uninstall = args.Contains("--uninstall") ||
                        Path.GetFileNameWithoutExtension(self).Equals("Uninstall", StringComparison.OrdinalIgnoreCase);

        if (uninstall)
        {
            // The uninstaller sits in the install folder it is removing.
            Install.Uninstall(Path.GetDirectoryName(self)!);
            return;
        }

        // Unattended: LiveWallSetup.exe --silent <folder> [--desktop]
        var s = Array.IndexOf(args, "--silent");
        if (s >= 0 && args.Length > s + 1)
        {
            var log = Path.Combine(Path.GetTempPath(), "livewall-setup.log");
            File.WriteAllText(log, "");
            try
            {
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
