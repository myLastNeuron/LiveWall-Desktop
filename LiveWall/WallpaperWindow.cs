using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace LiveWall;

/// <summary>
/// A borderless window reparented under Explorer's "WorkerW" — the window that sits
/// behind the desktop icons — rendering the wallpaper with WebView2.
/// </summary>
internal sealed class WallpaperWindow : Form
{
    private readonly WebView2 _web;
    private string? _path;
    private string? _mediaDir;
    private bool _paused;
    private IntPtr _host;
    private bool _hostIsFallback; // parked on Progman before Explorer had a WorkerW ready
    private DateTime _retryAt;    // back-off so a failing attach does not shout at Explorer 1x/s

    internal WallpaperWindow()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black;

        _web = new WebView2 { Dock = DockStyle.Fill, TabStop = false };
        Controls.Add(_web);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_NOACTIVATE; // desktop must never lose focus to us
            return cp;
        }
    }

    /// <summary>Show the window and drop it behind the desktop icons.</summary>
    internal void Start(string? path)
    {
        Show();               // creates the HWND; no paint is processed before we reparent
        // AttachToDesktop hides us again if the desktop is not ready yet (logon start).
        AttachToDesktop();
        if (path is not null) SetWallpaper(path);
    }

    /// <summary>
    /// Release the WebView2 explicitly. When Explorer restarts it destroys our host and the
    /// old browser instance would otherwise keep its processes alive for the rest of the session.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing) _web.Dispose();
        base.Dispose(disposing);
    }

    internal event Action<string>? Failed;

    internal void SetWallpaper(string path)
    {
        _path = path;
        _ = ApplyAsync().ContinueWith(
            t => Failed?.Invoke("The wallpaper could not start — " + t.Exception?.GetBaseException().Message),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    internal void Clear()
    {
        _path = null;
        _web.CoreWebView2?.Navigate("about:blank");
    }

    internal void SetPaused(bool pause)
    {
        if (pause == _paused) return;
        if (_web.CoreWebView2 is null) return; // not ready yet — retry next tick, don't latch
        _paused = pause;
        ApplyPauseState();
    }

    private void ApplyPauseState()
    {
        if (_web.CoreWebView2 is null) return;
        // window.vw_* only exist on the video player page; guard for html wallpapers
        _ = _web.CoreWebView2.ExecuteScriptAsync(
            _paused ? "(window.vw_pause||function(){})()" : "(window.vw_play||function(){})()");
    }

    /// <summary>
    /// Re-attach if Explorer restarted and blew our host window away, if the parent never took,
    /// or if we started at logon before Explorer had a WorkerW to park us in.
    /// </summary>
    internal void EnsureAttached()
    {
        if (!IsHandleCreated) return;
        var lost = !Native.IsWindow(_host) || Native.GetParent(Handle) != _host;
        // Only while the wallpaper may still be covering the icons, never as a 1 Hz loop.
        var upgrade = _hostIsFallback && FindWorkerW() != IntPtr.Zero;
        if (lost || upgrade)
        {
            if (DateTime.UtcNow >= _retryAt) AttachToDesktop();
        }
        // Explorer rebuilding the desktop can leave us stacked above the icon view; without this
        // the icons stay buried until restart, so re-stack here. Cheap: a few handles, once a second.
        else if (!IconsVisible()) Place();
    }

    /// <summary>Keep the wallpaper covering the primary desktop after a resolution or DPI change.</summary>
    internal void FitToDesktop()
    {
        if (!IsHandleCreated || _host == IntPtr.Zero) return;
        var b = Screen.PrimaryScreen!.Bounds;
        if (Left == 0 && Top == 0 && Width == b.Width && Height == b.Height) return;
        Native.SetWindowPos(Handle, IntPtr.Zero, 0, 0, b.Width, b.Height,
            Native.SWP_NOACTIVATE | Native.SWP_NOZORDER);
    }

    private async Task ApplyAsync()
    {
        if (_path is null) return;
        await EnsureWebAsync();
        var core = _web.CoreWebView2;
        var full = Path.GetFullPath(_path);
        var file = Path.GetFileName(full);

        var dir = Path.GetDirectoryName(full)!;
        var folderChanged = !string.Equals(_mediaDir, dir, StringComparison.OrdinalIgnoreCase);
        core.SetVirtualHostNameToFolderMapping(
            "media.local", dir, CoreWebView2HostResourceAccessKind.Allow);
        _mediaDir = dir; // only once the mapping took: a folder that failed (unplugged drive) must retry

        if (file.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
            file.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))
        {
            core.Navigate("https://media.local/" + Uri.EscapeDataString(file));
            return;
        }

        // A new folder mapping only reaches the next document, so a wallpaper from another
        // folder reloads the player instead of quietly failing to load its video.
        if (core.Source?.Contains("player.html") == true && !folderChanged) SendSource();
        else core.Navigate("https://app.local/player.html"); // NavigationCompleted sends the source
    }

    private Task? _webInit; // one start even when two applies race during startup

    private async Task EnsureWebAsync()
    {
        if (_web.CoreWebView2 is not null) return;
        _webInit ??= InitWebAsync();
        try { await _webInit; }
        catch { _webInit = null; throw; } // a failed start is retried by the next apply
    }

    private async Task InitWebAsync()
    {
        await _web.EnsureCoreWebView2Async(await Web.Shared); // same browser process as the GUI

        var core = _web.CoreWebView2
            ?? throw new InvalidOperationException("WebView2 runtime is missing (install the Edge WebView2 Runtime).");
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsSwipeNavigationEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.SetVirtualHostNameToFolderMapping(
            "app.local", Path.Combine(AppContext.BaseDirectory, "assets"),
            CoreWebView2HostResourceAccessKind.Allow);

        core.NavigationCompleted += (_, e) => { if (e.IsSuccess) SendSource(); };
        core.NewWindowRequested += (_, e) => e.Handled = true;
    }

    private void SendSource()
    {
        if (_path is null || _web.CoreWebView2 is null) return;
        if (Path.GetExtension(_path).ToLowerInvariant() is ".html" or ".htm") return;
        var url = "https://media.local/" + Uri.EscapeDataString(Path.GetFileName(_path));
        _ = _web.CoreWebView2.ExecuteScriptAsync($"setSource({System.Text.Json.JsonSerializer.Serialize(url)})");
        ApplyPauseState(); // a reloaded page always starts playing
    }

    /// <summary>The WorkerW that sits behind the icon view, or Zero before Explorer has one.</summary>
    private static IntPtr FindWorkerW()
    {
        var workerw = IntPtr.Zero;
        Native.EnumWindows((top, _) =>
        {
            if (Native.FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
                workerw = Native.FindWindowEx(IntPtr.Zero, top, "WorkerW", null);
            return true;
        }, IntPtr.Zero);
        return workerw;
    }

    private void AttachToDesktop()
    {
        var progman = Native.FindWindow("Progman", null);
        if (progman == IntPtr.Zero) { Detach(); return; } // launched at logon, desktop not up yet

        // Ask Explorer to create the WorkerW that lives behind the icon view.
        // Windows 11 needs the message twice.
        Native.SendMessageTimeout(progman, 0x052C, IntPtr.Zero, IntPtr.Zero, 0, 1000, out _);
        Native.SendMessageTimeout(progman, 0x052C, IntPtr.Zero, IntPtr.Zero, 0, 1000, out _);

        var workerw = FindWorkerW();

        // Fallback to Progman itself: still renders behind the icons on most builds.
        _host = workerw != IntPtr.Zero ? workerw : progman;
        _hostIsFallback = workerw == IntPtr.Zero;
        Log($"progman=0x{progman:X} workerw=0x{workerw:X} host=0x{_host:X}");

        // A borderless Form is WS_POPUP; without WS_CHILD SetParent only sets the
        // window's owner and it stays a top-level window over the desktop.
        var style = Native.GetWindowLong(Handle, Native.GWL_STYLE);
        style &= ~(Native.WS_POPUP | Native.WS_CAPTION | Native.WS_THICKFRAME);
        style |= Native.WS_CHILD;
        Native.SetWindowLong(Handle, Native.GWL_STYLE, style);

        Native.SetParent(Handle, _host);
        Log($"parent now=0x{Native.GetParent(Handle):X} (expected 0x{_host:X})");
        if (Native.GetParent(Handle) != _host) { Detach(); return; } // a reparent we cannot verify must not sit over the icons

        Place();
        // Attach retries happen while hidden, so a half-attached wallpaper can never cover the icons.
        Native.ShowWindow(Handle, Native.SW_SHOWNOACTIVATE);
    }

    /// <summary>
    /// Size to the desktop and stack under the icon view. The result is checked: if Windows
    /// refused the placement the window drops to the bottom of its stack, which is always below
    /// the icons.
    /// </summary>
    private void Place()
    {
        var b = Screen.PrimaryScreen!.Bounds;
        var icons = IconView();
        // Directly under the icon view when we share its parent: above Progman's wallpaper paint
        // and anything else parked in the desktop stack, but below the icons. Inside a WorkerW
        // there is no DefView to anchor to, so drop to the bottom.
        var anchor = icons != IntPtr.Zero && Native.GetParent(icons) == _host ? icons : Native.HWND_BOTTOM;
        Native.SetWindowPos(Handle, anchor, 0, 0, b.Width, b.Height,
            Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);
        if (!IconsVisible())
            Native.SetWindowPos(Handle, Native.HWND_BOTTOM, 0, 0, b.Width, b.Height, Native.SWP_NOACTIVATE);
    }

    /// <summary>
    /// True unless our window is drawn over the desktop icons. The icon view sits in Progman, in a
    /// WorkerW inside Progman, or in a top-level WorkerW depending on the build, so the two z-order
    /// chains are compared where they meet instead of assuming one shared parent.
    /// </summary>
    private bool IconsVisible()
    {
        var icons = IconView();
        if (icons == IntPtr.Zero) return true; // no icon view to cover

        var ours = Chain(Handle);   // [us, parent, ..., top]
        var theirs = Chain(icons);
        for (var i = 0; i < ours.Length; i++)
            for (var j = 0; j < theirs.Length; j++)
            {
                if (ours[i] != theirs[j]) continue;
                // The chains meet at a window that contains both of us:
                if (i == 0 || j == 0) return true;             // one contains the other, so it draws under it
                return Rank(theirs[j - 1]) < Rank(ours[i - 1]); // rank 0 is topmost
            }

        return true; // separate top-level stacks: we cannot be covering them
    }

    /// <summary>The desktop icon list (SHELLDLL_DefView), or Zero before Explorer has one.</summary>
    private static IntPtr IconView()
    {
        var progman = Native.FindWindow("Progman", null);
        foreach (var parent in new[] { progman, Native.FindWindowEx(progman, IntPtr.Zero, "WorkerW", null) })
        {
            if (parent == IntPtr.Zero) continue;
            var def = Native.FindWindowEx(parent, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (def != IntPtr.Zero) return def;
        }

        // Some builds keep the icon view in a top-level WorkerW instead.
        var found = IntPtr.Zero;
        Native.EnumWindows((top, _) =>
        {
            found = Native.FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null);
            return found == IntPtr.Zero;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>A window and its parents, nearest first.</summary>
    private static IntPtr[] Chain(IntPtr h)
    {
        var chain = new List<IntPtr>();
        for (var w = h; w != IntPtr.Zero; w = Native.GetParent(w)) chain.Add(w);
        return chain.ToArray();
    }

    /// <summary>Z-order rank among the siblings: 0 is the topmost window.</summary>
    private static int Rank(IntPtr child)
    {
        var rank = 0;
        for (var w = Native.GetWindow(Native.GetParent(child), Native.GW_CHILD);
             w != IntPtr.Zero && w != child;
             w = Native.GetWindow(w, Native.GW_HWNDNEXT)) rank++;
        return rank;
    }

    /// <summary>Hide and drop the host so the next tick retries a clean attach.</summary>
    private void Detach()
    {
        _host = IntPtr.Zero;
        _hostIsFallback = false;
        _retryAt = DateTime.UtcNow.AddSeconds(5);
        Native.ShowWindow(Handle, Native.SW_HIDE);
    }

    private static void Log(string msg)
    {
        if (Environment.GetEnvironmentVariable("LIVEWALL_DEBUG") is null) return;
        try { File.AppendAllText(Path.Combine(Program.AppDir, "debug.log"), $"{DateTime.Now:HH:mm:ss} {msg}\n"); } catch { }
    }
}

internal static class Native
{
    internal const int GWL_STYLE = -16;
    internal const int WS_CAPTION = 0x00C00000;
    internal const int WS_THICKFRAME = 0x00040000;
    internal const int WS_POPUP = unchecked((int)0x80000000);
    internal const int WS_CHILD = 0x40000000;
    internal const int WS_EX_NOACTIVATE = 0x08000000;
    internal static readonly IntPtr HWND_BOTTOM = 1;
    internal const int SW_HIDE = 0;
    internal const int SW_SHOWNOACTIVATE = 4;
    internal const uint GW_HWNDNEXT = 2;
    internal const uint GW_CHILD = 5;

    [DllImport("user32.dll")]
    internal static extern bool ShowWindow(IntPtr hWnd, int cmd);
    internal const uint SWP_NOACTIVATE = 0x0010;
    internal const uint SWP_FRAMECHANGED = 0x0020;
    internal const uint SWP_NOZORDER = 0x0004;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    internal delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr FindWindow(string? cls, string? title);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);

    [DllImport("user32.dll")]
    internal static extern bool EnumWindows(EnumProc cb, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll")]
    internal static extern IntPtr SetParent(IntPtr child, IntPtr parent);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);

    [DllImport("user32.dll")]
    internal static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    internal static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll")]
    internal static extern int SetWindowLong(IntPtr hWnd, int index, int value);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    internal static extern IntPtr LoadCursor(IntPtr instance, int id);

    [DllImport("user32.dll")]
    internal static extern IntPtr SetCursor(IntPtr cursor);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern bool ReleaseCapture();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

    [DllImport("user32.dll")]
    internal static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [StructLayout(LayoutKind.Sequential)]
    internal struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;   // 1 while Windows is in battery-saver mode
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    /// <summary>Running on battery, or Windows is in battery-saver mode.</summary>
    internal static bool PowerConstrained()
    {
        if (!GetSystemPowerStatus(out var status)) return false;
        return status.ACLineStatus == 0 || status.SystemStatusFlag == 1;
    }

    /// <summary>The window in front that covers the desktop, or Zero.</summary>
    internal static IntPtr CoveringWindow(IntPtr ignore)
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == ignore) return IntPtr.Zero;

        // Our own GUI maximised over the desktop is not a fullscreen app; counting it would
        // pause the wallpaper the instant the user opens the window and fight the Play button.
        GetWindowThreadProcessId(fg, out var pid);
        if (pid == (uint)Environment.ProcessId) return IntPtr.Zero;

        var cls = new StringBuilder(64);
        GetClassName(fg, cls, cls.Capacity);
        if (cls.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return IntPtr.Zero;

        if (!GetWindowRect(fg, out var r)) return IntPtr.Zero;

        // Only a window on the wallpaper's own monitor can cover it; a maximised app on a
        // second screen must leave the primary desktop playing.
        var monitor = MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST);
        if (ignore != IntPtr.Zero && monitor != MonitorFromWindow(ignore, MONITOR_DEFAULTTONEAREST)) return IntPtr.Zero;

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return IntPtr.Zero;

        // The work area, not the monitor: a maximised window covers the desktop the user can
        // see, and that is what the wallpaper must yield to. A small window leaves it playing.
        var m = info.rcWork;
        return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom
            ? fg
            : IntPtr.Zero;
    }
}
