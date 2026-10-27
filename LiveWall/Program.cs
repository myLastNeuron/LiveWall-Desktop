using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace LiveWall;

internal static class Program
{
    internal static readonly string AppDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiveWall");

    private const string InstanceEvent = @"Local\LiveWall.ShowGui";

    [STAThread]
    private static void Main(string[] args)
    {
        Directory.CreateDirectory(AppDir);

        // A second launch (tray icon already running) just reveals the existing window.
        using var mutex = new Mutex(true, @"Local\LiveWall.SingleInstance", out var first);
        if (!first)
        {
            try { EventWaitHandle.OpenExisting(InstanceEvent).Set(); } catch { /* first run still starting */ }
            return;
        }

        using var show = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceEvent);
        ApplicationConfiguration.Initialize();

        var app = new TrayApp(show);
        if (!args.Contains("--background")) app.ShowGui(); // autostart enters quietly
        Application.Run(app);
    }
}

internal static class Media
{
    internal const string DialogFilter =
        "Wallpapers|*.mp4;*.webm;*.mkv;*.mov;*.m4v;*.gif;*.html;*.htm|All files|*.*";

    private static readonly HashSet<string> Exts = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".webm", ".mkv", ".mov", ".m4v", ".gif", ".html", ".htm" };

    internal static bool Supported(string path) => Exts.Contains(Path.GetExtension(path));

    internal static string Kind(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".gif" => "gif",
        ".html" or ".htm" => "page",
        _ => "video",
    };

    internal static bool IsVideo(string path) => Kind(path) == "video";
}

/// <summary>One WebView2 environment for both windows — one browser process instead of two.</summary>
internal static class Web
{
    private static Task<CoreWebView2Environment>? _shared;

    internal static Task<CoreWebView2Environment> Shared
    {
        get
        {
            // a faulted task must not be cached: one locked profile would break the
            // GUI and the wallpaper for the rest of the session
            if (_shared is null || _shared.IsFaulted || _shared.IsCanceled)
                _shared = CoreWebView2Environment.CreateAsync(
                    browserExecutableFolder: null,
                    userDataFolder: Path.Combine(AppContext.BaseDirectory, "LiveWall.exe.WebView2"),
                    options: null);
            return _shared;
        }
    }
}

/// <summary>
/// A screen-sized copy of a video. Decoding 4K for a 1080p desktop is the whole GPU bill,
/// so a file larger than the screen is re-encoded once and cached.
/// </summary>
internal static class Proxy
{
    private static readonly string Dir = Path.Combine(Program.AppDir, "proxy");

    // shortcut: copies are never pruned; add an LRU sweep once a library holds many 4K files.

    internal static int TargetHeight() => Math.Max(240, Screen.PrimaryScreen?.Bounds.Height ?? 1080);

    internal static string? Cached(string source, int height)
    {
        var path = NameFor(source, height);
        return path is not null && File.Exists(path) ? path : null;
    }

    internal static string? NameFor(string source, int height)
    {
        try
        {
            var info = new FileInfo(source);
            var stem = Path.GetFileNameWithoutExtension(source);
            foreach (var bad in Path.GetInvalidFileNameChars()) stem = stem.Replace(bad, '_');
            // size in the name: a re-recorded file at the same path gets a fresh copy
            return Path.Combine(Dir, $"{stem}-{info.Length}-{height}p.mp4");
        }
        catch { return null; }
    }

    /// <summary>Blocks while ffmpeg transcodes. Returns the cached copy, or null when not worth it.</summary>
    internal static string? Build(string source, int height)
    {
        var target = NameFor(source, height);
        var ffmpeg = Tool("ffmpeg");
        var ffprobe = Tool("ffprobe");
        if (target is null || ffmpeg is null || ffprobe is null) return null; // no ffmpeg: play the original

        if (Probe(ffprobe, source) is not { } size || size.h <= height) return null; // already screen-sized

        var tmp = target + ".part.mp4";
        var args = $"-y -loglevel error -i \"{source}\" -vf scale=-2:{height} -c:v libx264 -preset veryfast " +
                   $"-crf 22 -pix_fmt yuv420p -an -movflags +faststart \"{tmp}\"";
        try { Directory.CreateDirectory(Dir); } catch { return null; }
        if (Capture(ffmpeg, args) is null || !File.Exists(tmp)) { TryDelete(tmp); return null; }
        try { File.Move(tmp, target, overwrite: true); } catch { TryDelete(tmp); return null; }
        return target;
    }

    private static (int w, int h)? Probe(string ffprobe, string source)
    {
        var text = Capture(ffprobe,
            $"-v error -select_streams v:0 -show_entries stream=width,height -of csv=p=0:s=x \"{source}\"");
        var parts = text?.Trim().Split('x');
        return parts is { Length: 2 } && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h)
            ? (w, h)
            : null;
    }

    private static string? Capture(string exe, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null) return null;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync(); // both at once: draining one pipe
            if (!p.WaitForExit(20 * 60 * 1000))           // while the child fills the other deadlocks
            {
                try { p.Kill(); } catch { }
                return null;
            }
            var output = stdout.GetAwaiter().GetResult();
            _ = stderr.GetAwaiter().GetResult();
            return p.ExitCode == 0 ? output : null;
        }
        catch { return null; }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    private static string? Tool(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var exe = Path.Combine(dir.Trim('"', ' '), name + ".exe");
                if (File.Exists(exe)) return exe;
            }
            catch { /* malformed PATH entry */ }
        }
        return null;
    }
}

/// <summary>Tray icon, persisted choices, and the once-a-second watchdog.</summary>
internal sealed class TrayApp : ApplicationContext
{
    private readonly Control _anchor = new(); // marshals signals onto the UI thread
    private readonly System.Windows.Forms.Timer _watch = new() { Interval = 1000 };
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _pauseItem;
    private MainWindow? _gui;
    private WallpaperWindow? _wall;
    private DateTime _nextRotate = DateTime.MaxValue;
    private IntPtr _fullscreen;   // window covering the desktop right now
    private IntPtr _playOverride; // the one the user pressed Play over
    private bool _powerOverride;  // Play pressed while on battery
    private bool _locked;         // session locked or disconnected: nothing is on screen
    private string? _optimizing;  // wallpaper being re-encoded right now
    private (string path, int height)? _optimizeNext; // newest request waiting for the slot

    internal AppState State { get; } = AppState.Load();

    /// <summary>True while the watchdog is holding the wallpaper paused.</summary>
    internal bool AutoPaused { get; private set; }

    /// <summary>Why <see cref="AutoPaused"/> is on: "power", "fullscreen", or null.</summary>
    internal string? AutoPauseReason { get; private set; }

    internal TrayApp(EventWaitHandle showSignal)
    {
        _ = _anchor.Handle; // installs the WinForms sync context for BeginInvoke

        _pauseItem = new ToolStripMenuItem("Pause wallpaper", null, (_, _) => SetPaused(!State.Paused))
        {
            Checked = State.Paused,
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Open LiveWall", null, (_, _) => ShowGui()));
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => Quit()));

        _tray = new NotifyIcon
        {
            Icon = TrayIcon.Create(),
            Text = "LiveWall",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowGui(); };

        State.Autostart = Autostart.Enabled; // registry is the truth
        // rotation survives a restart: the saved deadline is the truth, a past one fires now
        _nextRotate = State.Rotate
            ? State.NextRotate ?? DateTime.UtcNow.AddMinutes(State.RotateMinutes)
            : DateTime.MaxValue;
        if (State.Current is { } saved && File.Exists(saved)) Apply(saved, quiet: true);

        _watch.Tick += (_, _) => Tick();
        _watch.Start();

        SystemEvents.SessionSwitch += OnSessionSwitch;
        new Thread(() =>
        {
            while (true)
            {
                showSignal.WaitOne();
                try { _anchor.BeginInvoke((Action)ShowGui); } catch { /* shutting down */ }
            }
        }) { IsBackground = true }.Start();
    }

    // ---- window ----

    internal void ShowGui()
    {
        if (_gui is null || _gui.IsDisposed) _gui = new MainWindow(this);
        _gui.Show();
        if (_gui.WindowState == FormWindowState.Minimized) _gui.WindowState = FormWindowState.Normal;
        _gui.Activate();
    }

    internal void HideGui()
    {
        if (_gui is { IsDisposed: false } gui)
        {
            // remember the size the user chose, not the size a maximized window happens to have
            State.WinMax = gui.WindowState == FormWindowState.Maximized;
            var size = State.WinMax ? gui.RestoreBounds.Size : gui.Size;
            if (size.Width > 0 && size.Height > 0) { State.WinW = size.Width; State.WinH = size.Height; }
            State.Save();
            gui.Hide();
        }
    }

    // ---- wallpaper ----

    internal void Apply(string path, bool quiet = false)
    {
        if (!File.Exists(path)) { Toast("That file is missing."); Push(); return; }

        State.Add(path);
        State.Current = path;
        State.Paused = false;
        _playOverride = IntPtr.Zero;
        _pauseItem.Checked = false;

        var wall = EnsureWall();
        if (wall is null) { Toast("The WebView2 runtime is missing — install it to show wallpapers."); return; }

        var height = Proxy.TargetHeight();
        wall.SetWallpaper(Proxy.Cached(path, height) ?? path); // a ready copy beats decoding 4K
        Optimize(path, height);
        ResetRotation();
        State.Save();
        if (!quiet) Toast("Wallpaper applied");
        Push();
    }

    internal int AddFiles(IEnumerable<string> paths)
    {
        var added = new List<string>();
        foreach (var p in paths)
        {
            string full;
            try { full = Path.GetFullPath(p); } catch { continue; }
            if (!Media.Supported(full) || !File.Exists(full)) continue;
            if (State.Add(full)) added.Add(full);
        }

        if (added.Count == 0) return 0;
        State.Save();
        if (State.Current is null) Apply(added[0]); // first wallpaper: put it up right away
        Push();
        return added.Count;
    }

    internal void Remove(string path)
    {
        State.Items.RemoveAll(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));
        if (string.Equals(State.Current, path, StringComparison.OrdinalIgnoreCase))
        {
            State.Current = null;
            _wall?.Clear();
            _wall?.Dispose();
            _wall = null;
        }
        State.Save();
        Push();
    }

    internal void SetPaused(bool paused)
    {
        State.Paused = paused;
        if (!paused)
        {
            _playOverride = _fullscreen;              // Play wins over the current fullscreen app
            _powerOverride = Native.PowerConstrained(); // ...and over the battery pause
        }
        _pauseItem.Checked = paused;
        if (_wall is { IsDisposed: false }) _wall.SetPaused(paused);
        State.Save();
        Push();
    }

    internal void SetTheme(string theme)
    {
        State.Theme = theme == "light" ? "light" : "dark";
        State.Save();
        Push();
    }

    internal void SetRotate(bool enabled, int minutes)
    {
        State.Rotate = enabled;
        State.RotateMinutes = Math.Clamp(minutes <= 0 ? 30 : minutes, 5, 24 * 60);
        ResetRotation();
        State.Save();
        Push();
    }

    internal void SetPauseFullscreen(bool enabled)
    {
        State.PauseFullscreen = enabled;
        State.Save();
        Push();
    }

    internal void SetPauseOnPower(bool enabled)
    {
        State.PauseOnPower = enabled;
        State.Save();
        Push();
    }

    internal void SetAutostart(bool enabled)
    {
        Autostart.Set(enabled);
        State.Autostart = Autostart.Enabled;
        State.Save();
        Push();
    }

    internal void Push() => _gui?.PushState();

    internal void Toast(string text) => _gui?.ShowToast(text);

    internal void Quit()
    {
        HideGui();
        _watch.Stop();
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        _tray.Visible = false;
        _tray.Dispose();
        _wall?.Dispose();
        if (_gui is { IsDisposed: false }) { _gui.SuppressHide(); _gui.Dispose(); }
        _anchor.Dispose();
        ExitThread();
    }

    // ---- internals ----

    /// <summary>Build the screen-sized copy in the background, then swap it in silently.</summary>
    private void Optimize(string path, int height)
    {
        if (!Media.IsVideo(path) || Proxy.Cached(path, height) is not null) return;
        if (_optimizing is not null)
        {
            // one encode at a time: a second ffmpeg would only fight the first for the CPU
            if (!string.Equals(_optimizing, path, StringComparison.OrdinalIgnoreCase)) _optimizeNext = (path, height);
            return;
        }
        _optimizing = path;

        _ = Task.Run(() => Proxy.Build(path, height)).ContinueWith(t =>
        {
            var copy = t.Status == TaskStatus.RanToCompletion ? t.Result : null;
            try
            {
                _anchor.BeginInvoke((Action)(() =>
                {
                    // reset on the UI thread: Optimize reads these from the tick loop, and a
                    // failed build must not latch the slot shut for the rest of the session
                    _optimizing = null;
                    var next = _optimizeNext;
                    _optimizeNext = null;
                    if (copy is not null && string.Equals(State.Current, path, StringComparison.OrdinalIgnoreCase))
                    {
                        _wall?.SetWallpaper(copy);
                        Toast($"Lighter copy ready — now playing at {height}p");
                    }
                    if (next is { } queued) Optimize(queued.path, queued.height);
                }));
            }
            catch { /* shutting down */ }
        });
    }

    private WallpaperWindow NewWall(string? path)
    {
        var wall = new WallpaperWindow();
        // the wallpaper window cannot talk to the GUI itself, and a failed WebView2
        // start used to be completely silent
        wall.Failed += message =>
        {
            try { _anchor.BeginInvoke((Action)(() => Toast(message))); } catch { }
        };
        wall.Start(path);
        return wall;
    }

    private WallpaperWindow? EnsureWall()
    {
        if (_wall is { IsDisposed: false } && Native.IsWindow(_wall.Handle)) return _wall;
        _wall?.Dispose();
        _wall = NewWall(null);
        return _wall;
    }

    private void Tick()
    {
        var wasAuto = AutoPaused;

        if (State.Current is { } current)
        {
            if (_wall is null || _wall.IsDisposed || !Native.IsWindow(_wall.Handle))
            {
                // Explorer restarted and took our window with it.
                _wall?.Dispose();
                // a deleted or moved file must leave the Windows wallpaper alone, not paint black
                _wall = File.Exists(current)
                    ? NewWall(Proxy.Cached(current, Proxy.TargetHeight()) ?? current)
                    : null;
            }
            else
            {
                _wall.EnsureAttached();
                _wall.FitToDesktop();
            }

            _fullscreen = _wall is null ? IntPtr.Zero : Native.CoveringWindow(_wall.Handle);
            if (_fullscreen != _playOverride) _playOverride = IntPtr.Zero; // new context
            var onPower = Native.PowerConstrained();
            if (!onPower) _powerOverride = false; // back on mains power: forget the override
            var covered = State.PauseFullscreen && _fullscreen != IntPtr.Zero && _fullscreen != _playOverride;
            var power = State.PauseOnPower && onPower && !_powerOverride;
            AutoPauseReason = power ? "power" : covered ? "fullscreen" : null;
            AutoPaused = AutoPauseReason is not null;
            _wall?.SetPaused(State.Paused || AutoPaused || _locked);
        }
        else
        {
            _fullscreen = IntPtr.Zero;
            AutoPaused = false;
            AutoPauseReason = null;
        }

        if (wasAuto != AutoPaused) Push(); // the window explains why the wallpaper is paused

        if (State.Rotate && State.Items.Count > 1 && DateTime.UtcNow >= _nextRotate) Rotate();
    }

    private void Rotate()
    {
        ResetRotation();
        var items = State.Items.Select(i => i.Path).Where(File.Exists).ToList();
        if (items.Count < 2) return;
        var index = items.FindIndex(p => string.Equals(p, State.Current, StringComparison.OrdinalIgnoreCase));
        Apply(items[(index + 1) % items.Count], quiet: true);
        Toast($"Now showing {Path.GetFileName(State.Current)}");
    }

    private void ResetRotation()
    {
        _nextRotate = State.Rotate ? DateTime.UtcNow.AddMinutes(State.RotateMinutes) : DateTime.MaxValue;
        State.NextRotate = State.Rotate ? _nextRotate : null; // the timer survives a restart
    }

    // Tick owns the pause state; this only records that nobody can see the desktop.
    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
            case SessionSwitchReason.RemoteDisconnect:
            case SessionSwitchReason.ConsoleDisconnect:
                _locked = true;
                break;
            case SessionSwitchReason.SessionUnlock:
            case SessionSwitchReason.RemoteConnect:
            case SessionSwitchReason.ConsoleConnect:
                _locked = false;
                break;
        }
    }
}

internal sealed class WallItem
{
    public string Path { get; set; } = "";
}

internal sealed class AppState
{
    public string? Current { get; set; }
    public bool Paused { get; set; }
    public string Theme { get; set; } = "dark";
    public bool Rotate { get; set; }
    public int RotateMinutes { get; set; } = 30;
    public bool Autostart { get; set; }
    public bool PauseFullscreen { get; set; } = true;
    public bool PauseOnPower { get; set; } = true;
    public DateTime? NextRotate { get; set; }
    public int WinW { get; set; }
    public int WinH { get; set; }
    public bool WinMax { get; set; }
    public List<WallItem> Items { get; set; } = new();

    /// <summary>v1 config was just <c>{ "Path": "..." }</c>.</summary>
    [JsonPropertyName("Path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyPath { get; set; }

    // settable so tests can point it at a scratch folder instead of the real config
    internal static string File { get; set; } = Path.Combine(Program.AppDir, "config.json");

    private static readonly JsonSerializerOptions Opt = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    internal static AppState Load()
    {
        AppState state;
        try { state = JsonSerializer.Deserialize<AppState>(System.IO.File.ReadAllText(File), Opt) ?? new AppState(); }
        catch
        {
            // missing or corrupt config means "nothing saved yet"; keep a corrupt copy so the next Save cannot destroy it
            state = new AppState();
            if (System.IO.File.Exists(File)) try { System.IO.File.Copy(File, File + ".corrupt", true); } catch { }
        }
        state.Normalize();
        return state;
    }

    internal void Normalize()
    {
        Items ??= new();
        Items.RemoveAll(i => string.IsNullOrWhiteSpace(i.Path));
        if (Items.Count == 0 && !string.IsNullOrWhiteSpace(LegacyPath)) Items.Add(new WallItem { Path = LegacyPath });
        if (string.IsNullOrWhiteSpace(Current)) Current = LegacyPath;
        LegacyPath = null;
        if (Theme is not ("dark" or "light")) Theme = "dark";
        RotateMinutes = Math.Clamp(RotateMinutes <= 0 ? 30 : RotateMinutes, 5, 24 * 60);
    }

    internal void Save()
    {
        try
        {
            // write-then-replace: a half-written config would read back as "no library"
            var temp = File + ".tmp";
            System.IO.File.WriteAllText(temp, JsonSerializer.Serialize(this, Opt));
            System.IO.File.Move(temp, File, overwrite: true);
        }
        catch { /* losing preferences is not worth crashing over */ }
    }

    internal bool Add(string path)
    {
        if (Items.Any(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase))) return false;
        Items.Insert(0, new WallItem { Path = path });
        return true;
    }
}

internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "LiveWall";

    private static string Command => $"\"{Environment.ProcessPath}\" --background";

    internal static bool Enabled
    {
        get
        {
            try { return Registry.CurrentUser.OpenSubKey(RunKey)?.GetValue(Name) as string == Command; }
            catch { return false; }
        }
    }

    internal static void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null) return;
            if (enabled) key.SetValue(Name, Command);
            else key.DeleteValue(Name, throwOnMissingValue: false);
        }
        catch { /* policy-locked registry: the toggle simply stays off */ }
    }
}

internal static class TrayIcon
{
    private static Icon? _cached;

    internal static Icon Create()
    {
        if (_cached is not null) return _cached;
        using var res = typeof(TrayIcon).Assembly.GetManifestResourceStream("icon.ico")
            ?? throw new InvalidOperationException("icon.ico is missing from the app resources.");
        _cached = new Icon(res, SystemInformation.SmallIconSize);
        return _cached;
    }
}
