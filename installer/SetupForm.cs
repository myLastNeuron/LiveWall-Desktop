using System.Diagnostics;

namespace LiveWallSetup;

internal sealed class SetupForm : Form
{
    private readonly TextBox _dir = new() { Location = new Point(16, 120), Size = new Size(378, 23) };
    private readonly Button _browse = new() { Text = "Browse...", Location = new Point(400, 119), Size = new Size(84, 25) };
    private readonly CheckBox _desktop = new() { Text = "Create a desktop shortcut", Checked = true, Location = new Point(16, 154), AutoSize = true };
    private readonly CheckBox _launch = new() { Text = "Start LiveWall when setup finishes", Checked = true, Location = new Point(16, 176), AutoSize = true };
    private readonly ProgressBar _bar = new() { Location = new Point(16, 206), Size = new Size(468, 16), Maximum = 100, Visible = false };
    private readonly Label _status = new() { Location = new Point(16, 230), Size = new Size(468, 18), Text = "Ready to install." };
    private readonly Button _go = new() { Text = "Install", Location = new Point(296, 266), Size = new Size(90, 26) };
    private readonly Button _close = new() { Text = "Close", Location = new Point(394, 266), Size = new Size(90, 26) };

    private bool _busy;
    private bool _installed;

    internal SetupForm()
    {
        SuspendLayout();

        Text = "LiveWall Setup";
        Font = new Font("Segoe UI", 9F);
        ClientSize = new Size(502, 308);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowIcon = true;
        using (var icon = typeof(SetupForm).Assembly.GetManifestResourceStream("icon.ico"))
            if (icon is not null) Icon = new Icon(icon);

        var title = new Label
        {
            Text = "Install LiveWall",
            Font = new Font("Segoe UI", 12F, FontStyle.Bold),
            Location = new Point(16, 14),
            AutoSize = true,
        };
        var desc = new Label
        {
            Text = "An animated video wallpaper for your desktop.\r\n" +
                   "Anything it needs that is missing from this PC is downloaded automatically.",
            Location = new Point(16, 46),
            Size = new Size(470, 44),
        };
        var dirLabel = new Label { Text = "Install location:", Location = new Point(16, 100), AutoSize = true };

        _dir.Text = Install.DefaultDir;
        _browse.Click += Browse;
        _go.Click += Go;
        _close.Click += (_, _) => Close();

        Controls.AddRange(new Control[] { title, desc, dirLabel, _dir, _browse, _desktop, _launch, _bar, _status, _go, _close });
        ResumeLayout(false);
    }

    private void Browse(object? sender, EventArgs e)
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Choose where to install LiveWall.",
            UseDescriptionForTitle = true,
            SelectedPath = _dir.Text,
        };
        if (dlg.ShowDialog(this) == DialogResult.OK) _dir.Text = dlg.SelectedPath;
    }

    private async void Go(object? sender, EventArgs e)
    {
        if (_installed) { Close(); return; }
        if (_busy) return;

        var dir = _dir.Text.Trim().TrimEnd('\\', '/');
        try
        {
            dir = Path.GetFullPath(dir);
            if (dir == Path.GetPathRoot(dir)?.TrimEnd('\\'))
                throw new InvalidOperationException("Pick a folder below a drive root, e.g. D:\\LiveWall.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "LiveWall Setup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _busy = true;
        SetEnabled(false);
        _bar.Value = 0;
        _bar.Visible = true;

        try
        {
            await Install.RunAsync(dir, _desktop.Checked, (pct, text) =>
            {
                if (IsDisposed) return;
                BeginInvoke(() =>
                {
                    _bar.Value = Math.Clamp(pct, 0, 100);
                    _status.Text = text;
                });
            });

            _installed = true;
            _bar.Value = 100;
            _status.Text = $"LiveWall is installed to {dir}.";
            _go.Visible = false;
            _close.Text = "Finish";
            _close.Enabled = true;

            if (_launch.Checked) Launch(dir);
        }
        catch (Exception ex)
        {
            _status.Text = "Installation failed.";
            MessageBox.Show(this, ex.Message, "LiveWall Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _busy = false;
            if (!_installed) SetEnabled(true);
        }
    }

    private void SetEnabled(bool on)
    {
        _dir.Enabled = on;
        _browse.Enabled = on;
        _desktop.Enabled = on;
        _launch.Enabled = on;
        _go.Enabled = on && !_installed;
        _close.Enabled = on;
    }

    /// <summary>Explorer launches the app de-elevated-safe and lets it take focus normally.</summary>
    private static void Launch(string dir)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", '"' + Path.Combine(dir, Install.AppName + ".exe") + '"')
            { UseShellExecute = false });
        }
        catch { /* the app can still be started from the shortcut */ }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_busy) e.Cancel = true; // installing: let it finish
        base.OnFormClosing(e);
    }
}
