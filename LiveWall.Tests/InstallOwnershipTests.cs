using LiveWallSetup;

namespace LiveWall.Tests;

public sealed class InstallOwnershipTests : IDisposable
{
    private readonly Temp _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void NormalizePath_rejects_a_drive_root()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => InstallOwnership.NormalizePath(@"C:\"));

        Assert.Contains("below a drive root", ex.Message);
    }

    [Fact]
    public void NormalizePath_rejects_the_windows_folder()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        var ex = Assert.Throws<InvalidOperationException>(() => InstallOwnership.NormalizePath(windows));

        Assert.Contains("Windows system folders", ex.Message);
    }

    [Fact]
    public void NormalizePath_rejects_traversal_before_it_can_be_resolved()
    {
        var safe = Path.Combine(_temp.Dir(), "safe");

        var ex = Assert.Throws<InvalidOperationException>(
            () => InstallOwnership.NormalizePath(Path.Combine(safe, "..", "Windows")));

        Assert.Contains("parent-directory", ex.Message);
    }

    [Fact]
    public void ValidateInstallTarget_allows_an_empty_folder()
    {
        var dir = _temp.Dir();

        Assert.Equal(dir.TrimEnd('\\'), InstallOwnership.ValidateInstallTarget(dir));
    }

    [Fact]
    public void ValidateInstallTarget_refuses_a_non_empty_folder_without_a_marker()
    {
        var dir = _temp.Dir();
        File.WriteAllText(Path.Combine(dir, "personal.txt"), "keep me");

        var ex = Assert.Throws<InvalidOperationException>(() => InstallOwnership.ValidateInstallTarget(dir));

        Assert.Contains("not a LiveWall install", ex.Message);
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(dir, "personal.txt")));
    }

    [Fact]
    public void Marker_allows_an_upgrade_and_records_the_exact_owned_files()
    {
        var dir = _temp.Dir();
        Directory.CreateDirectory(Path.Combine(dir, "assets"));
        File.WriteAllText(Path.Combine(dir, "LiveWall.exe"), "app");
        File.WriteAllText(Path.Combine(dir, "assets", "ui.html"), "ui");

        InstallOwnership.WriteMarker(dir, "0.9.0", new[] { "LiveWall.exe", @"assets\ui.html" }, null);
        var marker = InstallOwnership.ReadMarker(dir);

        Assert.NotNull(marker);
        Assert.Equal(InstallOwnership.ProductId, marker.ProductId);
        Assert.Equal(dir.TrimEnd('\\'), marker.InstallPath);
        Assert.Contains("LiveWall.exe", marker.Files);
        Assert.Contains(@"assets\ui.html", marker.Files);
        Assert.Contains("assets", marker.Directories);
        Assert.Equal(dir.TrimEnd('\\'), InstallOwnership.ValidateInstallTarget(dir));
    }

    [Fact]
    public void DeleteOwnedFiles_keeps_unlisted_sentinels_and_the_folder()
    {
        var dir = _temp.Dir();
        Directory.CreateDirectory(Path.Combine(dir, "assets"));
        File.WriteAllText(Path.Combine(dir, "LiveWall.exe"), "app");
        File.WriteAllText(Path.Combine(dir, "assets", "ui.html"), "ui");
        File.WriteAllText(Path.Combine(dir, "keep-me.txt"), "sentinel");
        File.WriteAllText(Path.Combine(dir, "assets", "personal.txt"), "nested sentinel");
        InstallOwnership.WriteMarker(dir, "1.0.0", new[] { "LiveWall.exe", @"assets\ui.html" }, null);

        var marker = InstallOwnership.ReadMarker(dir);
        Assert.NotNull(marker);
        Assert.True(InstallOwnership.DeleteOwnedFiles(dir, marker));

        Assert.False(File.Exists(Path.Combine(dir, "LiveWall.exe")));
        Assert.False(File.Exists(Path.Combine(dir, "assets", "ui.html")));
        Assert.False(File.Exists(InstallOwnership.MarkerPath(dir)));
        Assert.Equal("sentinel", File.ReadAllText(Path.Combine(dir, "keep-me.txt")));
        Assert.Equal("nested sentinel", File.ReadAllText(Path.Combine(dir, "assets", "personal.txt")));
        Assert.True(Directory.Exists(dir));
    }

    [Fact]
    public void ReadMarker_rejects_a_file_path_that_escapes_the_install_folder()
    {
        var dir = _temp.Dir();
        var marker = $$"""
            {
              "productId": "{{InstallOwnership.ProductId}}",
              "installPath": "{{dir.Replace("\\", "\\\\")}}",
              "version": "1.0.0",
              "files": ["..\\outside.txt"],
              "directories": []
            }
            """;
        File.WriteAllText(InstallOwnership.MarkerPath(dir), marker);

        Assert.Null(InstallOwnership.ReadMarker(dir));
    }
}
