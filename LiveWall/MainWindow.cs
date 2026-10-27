using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace LiveWall;

/// <summary>
/// The app window: a frameless dark/light WinForms shell that hosts the GUI page.
/// All state lives in <see cref="TrayApp"/>; this class is only presentation and events.
/// </summary>
internal sealed class MainWindow : Form
{
    private const int WM_NCHITTEST = 0x0084;
    private const int WM_GETMINMAXINFO = 0x0024;
    private const int WM_SETCURSOR = 0x0020;
    private const int HTCAPTION = 2;
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int CS_DROPSHADOW = 0x00020000;

    private const int Grip = 8;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;

    private readonly TrayApp _app;
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly Dictionary<string, string> _mediaHosts = new(StringComparer.OrdinalIgnoreCase);
    private bool _ready;
    private bool _suppressHide;
    private bool _reloadPending;
    private bool _lastMaximized;
    private string? _pendingToast;

    internal MainWindow(TrayApp app)
    {
        _app = app;

        Text = "LiveWall";
        Icon = TrayIcon.Create(); // alt-tab / taskbar; same embedded brand mark as the tray
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(820, 560);
        var work = Screen.PrimaryScreen!.WorkingArea.Size;
        var width = Math.Min(app.State.WinW > 0 ? app.State.WinW : 1040, work.Width);
        var height = Math.Min(app.State.WinH > 0 ? app.State.WinH : 700, work.Height);
        Size = new Size(Math.Max(width, MinimumSize.Width), Math.Max(height, MinimumSize.Height));
        Padding = new Padding(Grip); // the frame band the page must not cover
        if (app.State.WinMax) WindowState = FormWindowState.Maximized;
        BackColor = ThemeColor();
        AutoScaleMode = AutoScaleMode.Dpi;
        DoubleBuffered = true;
        Controls.Add(_web);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= CS_DROPSHADOW;
            return cp;
        }
    }

    internal void PushState()
    {
        if (!_ready || !Visible || _web.CoreWebView2 is null) return; // hidden page is suspended

        var json = JsonSerializer.Serialize(new { type = "state", state = BuildState() });

        // WebView2 only applies new virtual-host mappings to the next document, so a page
        // that just learned about a new folder has to reload before it can preview it.
        if (_reloadPending)
        {
            _reloadPending = false;
            _ready = false;
            _web.CoreWebView2.Reload();
            return;
        }

        DebugLog("push " + json);
        _web.CoreWebView2.PostWebMessageAsJson(json);
    }

    internal void ShowToast(string text)
    {
        if (_web.CoreWebView2 is null || !Visible) return; // hidden page cannot show it
        if (!_ready) { _pendingToast = text; return; } // page is reloading; show it after
        _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "toast", text }));
    }

    internal void SuppressHide() => _suppressHide = true;

    // ---- lifecycle ----

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyFrame();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        _web.DefaultBackgroundColor = ThemeColor();
        _ = InitWebAsync();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        var maximized = WindowState == FormWindowState.Maximized;
        if (maximized == _lastMaximized) return;
        _lastMaximized = maximized;
        ApplyFrame(); // corner radius and border follow the window state
        if (!_ready || _web.CoreWebView2 is null) return;
        SendWindowState();
    }

    private void SendWindowState() =>
        _web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(
            new { type = "window", maximized = WindowState == FormWindowState.Maximized }));

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (_web.CoreWebView2 is null) return;
        if (Visible)
        {
            _web.CoreWebView2.Resume();
            PushState(); // the page was suspended, so give it fresh state
        }
        else _ = _web.CoreWebView2.TrySuspendAsync(); // hidden window costs nothing
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_suppressHide && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true; // the tray keeps us alive; Quit lives in settings
            _app.HideGui();
            return;
        }
        base.OnFormClosing(e);
    }

    // ---- web ----

    private async Task InitWebAsync()
    {
        try
        {
            await _web.EnsureCoreWebView2Async(await Web.Shared); // shared with the wallpaper window
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "LiveWall needs the Microsoft Edge WebView2 Runtime.\n\n" + ex.Message,
                "LiveWall", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var core = _web.CoreWebView2;
        if (core is null) return;

        var s = core.Settings;
        s.AreDevToolsEnabled = false;
        s.AreDefaultContextMenusEnabled = false;
        s.IsStatusBarEnabled = false;
        s.IsSwipeNavigationEnabled = false;
        s.AreBrowserAcceleratorKeysEnabled = false;
        s.IsZoomControlEnabled = false;
        s.IsPinchZoomEnabled = false;
        s.IsGeneralAutofillEnabled = false;
        s.IsPasswordAutosaveEnabled = false;
        s.IsBuiltInErrorPageEnabled = false;

        core.WebMessageReceived += OnWebMessage;
        core.NavigationStarting += OnNavigationStarting;
        core.SetVirtualHostNameToFolderMapping("app.local",
            Path.Combine(AppContext.BaseDirectory, "assets"), CoreWebView2HostResourceAccessKind.Allow);

        // Map every known folder before the page loads; later mappings need the reload in PushState.
        foreach (var item in _app.State.Items) MapMedia(item.Path);

        core.Navigate("https://app.local/ui.html");
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        // Dropping a file on the window makes WebView2 navigate to it — take the file instead.
        if (!e.Uri.StartsWith("file:///", StringComparison.OrdinalIgnoreCase)) return;
        e.Cancel = true;
        try { Import(new[] { new Uri(e.Uri).LocalPath }); } catch { /* not a local path */ }
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string? cmd = null;
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            // a missing or non-bool property has always meant "off"
            bool Flag(string name) =>
                root.TryGetProperty(name, out var f) && f.ValueKind == JsonValueKind.True;
            cmd = root.TryGetProperty("cmd", out var c) ? c.GetString() : null;
            var value = root.TryGetProperty("value", out var v) ? v.GetString() : null;
            var path = root.TryGetProperty("path", out var p) ? p.GetString() : null;

            switch (cmd)
            {
                case "ready":
                    _ready = true;
                    PushState();
                    // the window may already have been maximized when the page loaded
                    SendWindowState();
                    if (_pendingToast is { } pending) { _pendingToast = null; ShowToast(pending); }
                    break;
                case "log":
                    DebugLog("js " + value);
                    break;
                case "pick":
                    PickFiles();
                    break;
                case "apply":
                    if (path is not null) _app.Apply(path);
                    break;
                case "remove":
                    if (path is not null) _app.Remove(path);
                    break;
                case "pause":
                    // the page sends the state it wants: a blind toggle cannot express
                    // "resume although the watchdog is holding me paused"
                    _app.SetPaused(root.TryGetProperty("paused", out var pz)
                        ? pz.ValueKind == JsonValueKind.True
                        : !_app.State.Paused);
                    break;
                case "theme":
                    if (value is not null) { _app.SetTheme(value); ApplyFrame(); }
                    break;
                case "rotate":
                    _app.SetRotate(
                        Flag("enabled"),
                        root.TryGetProperty("minutes", out var rm) && rm.TryGetInt32(out var m) ? m : 30);
                    break;
                case "autostart":
                    _app.SetAutostart(Flag("enabled"));
                    break;
                case "fullscreenPause":
                    _app.SetPauseFullscreen(Flag("enabled"));
                    break;
                case "powerPause":
                    _app.SetPauseOnPower(Flag("enabled"));
                    break;
                case "reveal":
                    if (path is not null) Reveal(path);
                    break;
                case "minimize":
                    WindowState = FormWindowState.Minimized;
                    break;
                case "maximize":
                    WindowState = WindowState == FormWindowState.Maximized
                        ? FormWindowState.Normal
                        : FormWindowState.Maximized;
                    break;
                case "close":
                    _app.HideGui();
                    break;
                case "drag":
                    Native.ReleaseCapture();
                    Native.SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
                    break;
                case "quit":
                    // never tear down the WebView2 from inside its own message callback
                    BeginInvoke((Action)_app.Quit);
                    break;
            }
        }
        catch
        {
            // a malformed message is a bug in the page, never a crash in the shell
            ShowToast("Something went wrong there.");
        }
    }

    private void PickFiles()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Add wallpapers",
            Filter = Media.DialogFilter,
            Multiselect = true,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        Import(dlg.FileNames);
    }

    private void Import(IEnumerable<string> paths)
    {
        var list = paths.ToArray();
        var added = _app.AddFiles(list);
        if (_app.State.Current is not null && added > 0)
            ShowToast(added == 1 ? "Added to your library" : $"Added {added} wallpapers");
        else if (added == 0)
            ShowToast("Those files are already in your library or not supported.");
        PushState();
    }

    private static void DebugLog(string message)
    {
        if (Environment.GetEnvironmentVariable("LIVEWALL_DEBUG") is null) return;
        try
        {
            File.AppendAllText(Path.Combine(Program.AppDir, "gui.log"), $"{DateTime.Now:HH:mm:ss} {message}\n");
        }
        catch { }
    }

    private static void Reveal(string path)
    {
        try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\""); }
        catch { /* nothing to reveal */ }
    }

    // ---- state ----

    private object BuildState()
    {
        var current = _app.State.Current;
        var items = _app.State.Items.Select(item =>
        {
            var exists = File.Exists(item.Path);
            long? size = exists ? new FileInfo(item.Path).Length : null;
            return new
            {
                path = item.Path,
                name = Path.GetFileName(item.Path),
                kind = Media.Kind(item.Path),
                exists,
                size,
                preview = exists ? MapMedia(item.Path) : null,
                isCurrent = string.Equals(current, item.Path, StringComparison.OrdinalIgnoreCase),
            };
        }).ToArray();

        return new
        {
            theme = _app.State.Theme,
            paused = _app.State.Paused,
            autoPaused = _app.AutoPaused,
            autoPauseReason = _app.AutoPauseReason,
            pauseFullscreen = _app.State.PauseFullscreen,
            pauseOnPower = _app.State.PauseOnPower,
            currentPath = current,
            rotate = new { enabled = _app.State.Rotate, minutes = _app.State.RotateMinutes },
            autostart = _app.State.Autostart,
            items,
        };
    }

    /// <summary>Exposes one folder per virtual host so the page can preview local media.</summary>
    private string? MapMedia(string path)
    {
        if (_web.CoreWebView2 is null) return null;
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir is null) return null;

        if (!_mediaHosts.TryGetValue(dir, out var host))
        {
            if (_mediaHosts.Count >= 40) return null; // shortcut: 40+ folders is not a wallpaper library
            host = $"media{_mediaHosts.Count}.local";
            try { _web.CoreWebView2.SetVirtualHostNameToFolderMapping(host, dir, CoreWebView2HostResourceAccessKind.Allow); }
            catch { return null; }
            _mediaHosts[dir] = host;
            if (_ready) _reloadPending = true;
        }

        return $"https://{host}/{Uri.EscapeDataString(Path.GetFileName(path))}";
    }

    // ---- native frame ----

    private Color ThemeColor() =>
        _app.State.Theme == "light" ? Color.FromArgb(250, 250, 250) : Color.FromArgb(10, 10, 11);

    private void ApplyFrame()
    {
        BackColor = ThemeColor();
        _web.DefaultBackgroundColor = BackColor;
        if (!IsHandleCreated) return;

        var rounded = WindowState == FormWindowState.Maximized ? 1 : 2;
        Native.DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref rounded, sizeof(int));

        var border = _app.State.Theme == "light"
            ? ColorTranslator.ToWin32(Color.FromArgb(228, 228, 231))
            : ColorTranslator.ToWin32(Color.FromArgb(38, 38, 43));
        Native.DwmSetWindowAttribute(Handle, DWMWA_BORDER_COLOR, ref border, sizeof(int));
    }

    protected override void WndProc(ref Message m)
    {

        // A frameless window gets no automatic resize cursor, so set it from the hit test.
        if (m.Msg == WM_SETCURSOR && WindowState == FormWindowState.Normal)
        {
            var cursorId = (int)((long)m.LParam & 0xFFFF) switch
            {
                10 or 11 => 32644, // IDC_SIZEWE
                12 or 15 => 32645, // IDC_SIZENS
                13 or 17 => 32642, // IDC_SIZENWSE
                14 or 16 => 32643, // IDC_SIZENESW
                _ => 0,
            };
            if (cursorId != 0)
            {
                Native.SetCursor(Native.LoadCursor(IntPtr.Zero, cursorId));
                m.Result = 1;
                return;
            }
        }

        if (m.Msg == WM_NCHITTEST && WindowState == FormWindowState.Normal)
        {
            var screen = new Point(unchecked((short)(long)m.LParam), unchecked((short)((long)m.LParam >> 16)));
            var grip = (int)(Grip * DeviceDpi / 96.0) + 1;
            var b = Bounds;

            var left = screen.X >= b.Left - 1 && screen.X < b.Left + grip;
            var right = screen.X <= b.Right && screen.X > b.Right - grip;
            var top = screen.Y >= b.Top - 1 && screen.Y < b.Top + grip;
            var bottom = screen.Y <= b.Bottom && screen.Y > b.Bottom - grip;

            int hit = (left, right, top, bottom) switch
            {
                (true, _, true, _) => 13,   // HTTOPLEFT
                (_, true, true, _) => 14,   // HTTOPRIGHT
                (true, _, _, true) => 16,   // HTBOTTOMLEFT
                (_, true, _, true) => 17,   // HTBOTTOMRIGHT
                (true, _, _, _) => 10,      // HTLEFT
                (_, true, _, _) => 11,      // HTRIGHT
                (_, _, true, _) => 12,      // HTTOP
                (_, _, _, true) => 15,      // HTBOTTOM
                _ => 0,
            };
            if (hit != 0) { m.Result = hit; return; }
        }

        if (m.Msg == WM_GETMINMAXINFO)
        {
            var info = Marshal.PtrToStructure<MINMAXINFO>(m.LParam);
            var screen = Screen.FromHandle(Handle);
            var work = screen.WorkingArea;
            var bounds = screen.Bounds;
            info.ptMaxPosition = new POINT(work.Left - bounds.Left, work.Top - bounds.Top);
            info.ptMaxSize = new POINT(work.Width, work.Height);
            info.ptMinTrackSize = new POINT(MinimumSize.Width, MinimumSize.Height);
            Marshal.StructureToPtr(info, m.LParam, false);
            return;
        }

        base.WndProc(ref m);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT(int x, int y)
    {
        public int X = x;
        public int Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }
}
