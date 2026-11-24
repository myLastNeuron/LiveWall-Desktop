using LiveWallSetup;

namespace LiveWall.Tests;

/// <summary>
/// The setup probe must match what the apphost will actually resolve: an exact 8.x
/// WindowsDesktop runtime. A newer major is not a substitute without a roll-forward policy.
/// </summary>
public sealed class DesktopRuntimeTests : IDisposable
{
    private readonly Temp _temp = new();

    public void Dispose() => _temp.Dispose();

    private string RootWith(params string[] versions)
    {
        var root = _temp.Dir();
        var framework = Path.Combine(root, "shared", DesktopRuntime.FrameworkName);
        Directory.CreateDirectory(framework);
        foreach (var version in versions)
            Directory.CreateDirectory(Path.Combine(framework, version));
        return root;
    }

    [Theory]
    [InlineData("8.0.0")]
    [InlineData("8.0.11")]
    [InlineData("8.1.4")]
    public void A_stable_8_x_runtime_satisfies_the_probe(string version) =>
        Assert.True(DesktopRuntime.IsSatisfied(new[] { RootWith(version) }));

    [Theory]
    [InlineData("6.0.36")]
    [InlineData("7.0.20")]
    [InlineData("9.0.0")]
    [InlineData("10.0.0")]
    public void A_different_major_does_not_satisfy_the_probe(string version) =>
        Assert.False(DesktopRuntime.IsSatisfied(new[] { RootWith(version) }));

    [Fact]
    public void A_preview_8_x_does_not_count_as_a_stable_install() =>
        Assert.False(DesktopRuntime.IsSatisfied(new[] { RootWith("8.0.0-preview.7.23375.6") }));

    [Fact]
    public void A_stable_8_x_beside_9_x_still_satisfies_the_probe() =>
        Assert.True(DesktopRuntime.IsSatisfied(new[] { RootWith("8.0.11", "9.0.0") }));

    [Fact]
    public void An_empty_framework_folder_does_not_satisfy_the_probe() =>
        Assert.False(DesktopRuntime.IsSatisfied(new[] { RootWith() }));

    [Fact]
    public void A_missing_dotnet_root_does_not_satisfy_the_probe() =>
        Assert.False(DesktopRuntime.IsSatisfied(new[] { Path.Combine(_temp.Dir(), "absent") }));

    [Fact]
    public void The_first_root_with_8_x_satisfies_even_when_another_root_lacks_it() =>
        Assert.True(DesktopRuntime.IsSatisfied(new[]
        {
            RootWith("9.0.0"),
            RootWith("8.0.11"),
        }));

    [Fact]
    public void Blank_roots_are_ignored() =>
        Assert.False(DesktopRuntime.IsSatisfied(new[] { "", "   " }));
}
