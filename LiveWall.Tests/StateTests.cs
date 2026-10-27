using LiveWall;

namespace LiveWall.Tests;

/// <summary>
/// The config file is the only thing between a user and a lost library, and Load/Save have
/// already destroyed it once (BUGS-FIXED #4). These run against a scratch file, never the real one.
/// </summary>
public sealed class StateTests : IDisposable
{
    private readonly Temp _temp = new();
    private readonly string _realConfigPath = AppState.File;

    public void Dispose()
    {
        AppState.File = _realConfigPath;
        _temp.Dispose();
    }

    private string UseScratchConfig()
    {
        AppState.File = Path.Combine(_temp.Dir(), "config.json");
        return AppState.File;
    }

    [Fact]
    public void Load_migrates_a_v1_config_that_only_has_a_path()
    {
        var config = UseScratchConfig();
        File.WriteAllText(config, """{"Path":"D:\\media\\one.mp4"}""");

        var state = AppState.Load();

        Assert.Equal(@"D:\media\one.mp4", Assert.Single(state.Items).Path);
        Assert.Equal(@"D:\media\one.mp4", state.Current);
        Assert.Null(state.LegacyPath); // migrated away, so the next Save cannot write it back
    }

    [Theory]
    [InlineData(0, 30)]       // never set: the documented default
    [InlineData(-5, 30)]      // nonsense: the default
    [InlineData(1, 5)]        // below the 5 minute floor
    [InlineData(1441, 1440)]  // above the 24 hour ceiling
    [InlineData(45, 45)]      // a sane value is left alone
    public void Normalize_clamps_the_rotation_interval(int saved, int expected)
    {
        var state = new AppState { RotateMinutes = saved };

        state.Normalize();

        Assert.Equal(expected, state.RotateMinutes);
    }

    [Fact]
    public void Save_then_Load_keeps_the_library_and_the_settings()
    {
        UseScratchConfig();
        var saved = new AppState
        {
            Theme = "light",
            Rotate = true,
            RotateMinutes = 120,
            Current = @"C:\wall\b.mp4",
            Autostart = true,
            PauseFullscreen = false,
            PauseOnPower = false,
            NextRotate = new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc),
        };
        saved.Items.Add(new WallItem { Path = @"C:\wall\b.mp4" });
        saved.Items.Add(new WallItem { Path = @"C:\wall\a.gif" });
        saved.Save();

        var loaded = AppState.Load();

        Assert.Equal(saved.Items.Select(i => i.Path), loaded.Items.Select(i => i.Path));
        Assert.Equal("light", loaded.Theme);
        Assert.Equal(120, loaded.RotateMinutes);
        Assert.True(loaded.Rotate && loaded.Autostart);
        Assert.False(loaded.PauseFullscreen || loaded.PauseOnPower);
        Assert.Equal(saved.Current, loaded.Current);
        Assert.Equal(saved.NextRotate, loaded.NextRotate);
    }

    [Fact]
    public void Save_replaces_the_file_instead_of_leaving_a_partial_one()
    {
        var config = UseScratchConfig();
        var state = new AppState { Theme = "light" };
        state.Items.Add(new WallItem { Path = @"C:\wall\a.mp4" });

        state.Save();

        Assert.Equal(new[] { config }, Directory.GetFiles(Path.GetDirectoryName(config)!));
    }

    [Fact]
    public void Load_keeps_a_copy_when_the_config_is_corrupt()
    {
        var config = UseScratchConfig();
        const string garbage = "{ this is not json";
        File.WriteAllText(config, garbage);

        var state = AppState.Load();

        Assert.Empty(state.Items);
        Assert.Equal("dark", state.Theme); // defaults, not a crash
        Assert.Equal(garbage, File.ReadAllText(config + ".corrupt"));
    }

    [Fact]
    public void Load_treats_an_empty_file_as_nothing_saved()
    {
        var config = UseScratchConfig();
        File.WriteAllText(config, "");

        var state = AppState.Load();

        Assert.Empty(state.Items);
        Assert.Null(state.Current);
        Assert.False(state.Rotate);
    }

    [Fact]
    public void Load_treats_a_missing_file_as_nothing_saved_and_writes_no_corrupt_copy()
    {
        var config = UseScratchConfig();

        var state = AppState.Load();

        Assert.Empty(state.Items);
        Assert.False(File.Exists(config + ".corrupt"));
    }

    [Fact]
    public void Normalize_drops_blank_paths_and_falls_back_to_the_dark_theme()
    {
        var state = new AppState { Theme = "neon" };
        state.Items.Add(new WallItem { Path = "   " });
        state.Items.Add(new WallItem { Path = @"C:\wall\a.mp4" });

        state.Normalize();

        Assert.Equal("dark", state.Theme);
        Assert.Equal(@"C:\wall\a.mp4", Assert.Single(state.Items).Path);
    }

    [Fact]
    public void Add_ignores_a_duplicate_in_any_case_and_puts_new_items_first()
    {
        var state = new AppState();

        Assert.True(state.Add(@"C:\Wall\One.mp4"));
        Assert.False(state.Add(@"c:\wall\ONE.mp4"));
        Assert.True(state.Add(@"C:\Wall\Two.mp4"));

        Assert.Equal(new[] { @"C:\Wall\Two.mp4", @"C:\Wall\One.mp4" }, state.Items.Select(i => i.Path));
    }
}
