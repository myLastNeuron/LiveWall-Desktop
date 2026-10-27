using LiveWall;

namespace LiveWall.Tests;

/// <summary>The cache name decides whether a stale 4K transcode is reused, so it must be stable.</summary>
public sealed class ProxyNameTests : IDisposable
{
    private readonly Temp _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void NameFor_is_deterministic_and_embeds_the_size_and_the_height()
    {
        var source = _temp.File("holiday clip.mp4", 1234);

        var first = Proxy.NameFor(source, 1080);
        var second = Proxy.NameFor(source, 1080);

        Assert.NotNull(first);
        Assert.Equal(first, second);
        Assert.Equal(Path.Combine(Program.AppDir, "proxy"), Path.GetDirectoryName(first));
        Assert.Equal("holiday clip-1234-1080p.mp4", Path.GetFileName(first));
    }

    [Fact]
    public void NameFor_differs_per_target_height()
    {
        var source = _temp.File("clip.mp4", 1000);

        Assert.NotEqual(Proxy.NameFor(source, 1080), Proxy.NameFor(source, 720));
    }

    [Fact]
    public void NameFor_changes_when_the_file_is_re_recorded_at_the_same_path()
    {
        var source = _temp.File("clip.mp4", 1000);
        var before = Proxy.NameFor(source, 1080);

        System.IO.File.WriteAllBytes(source, new byte[2000]);

        Assert.NotEqual(before, Proxy.NameFor(source, 1080));
    }

    [Fact]
    public void NameFor_keeps_unicode_names_useful()
    {
        var source = _temp.File("море-海.mp4", 10);

        Assert.Contains("море-海", Proxy.NameFor(source, 1080));
    }

    [Fact]
    public void NameFor_returns_null_when_the_source_is_gone()
    {
        var missing = Path.Combine(_temp.Dir(), "gone.mp4");

        Assert.Null(Proxy.NameFor(missing, 1080));
    }
}

public sealed class MediaTests
{
    [Theory]
    [InlineData("a.mp4", "video")]
    [InlineData("a.MKV", "video")]
    [InlineData("a.webm", "video")]
    [InlineData("a.mov", "video")]
    [InlineData("a.m4v", "video")]
    [InlineData("a.gif", "gif")]
    [InlineData("a.GIF", "gif")]
    [InlineData("a.html", "page")]
    [InlineData("a.HTM", "page")]
    public void Kind_classifies_the_supported_extensions(string name, string expected)
    {
        Assert.Equal(expected, Media.Kind(name));
    }

    [Theory]
    [InlineData("a.mp4", true)]
    [InlineData("a.HTM", true)]
    [InlineData("a.txt", false)]
    [InlineData("a", false)]
    public void Supported_gates_what_may_enter_the_library(string name, bool expected)
    {
        Assert.Equal(expected, Media.Supported(name));
    }

    [Fact]
    public void IsVideo_is_true_only_for_video_kinds()
    {
        Assert.True(Media.IsVideo("clip.mp4"));
        Assert.False(Media.IsVideo("loop.gif"));
        Assert.False(Media.IsVideo("page.html"));
    }
}
