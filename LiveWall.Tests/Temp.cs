namespace LiveWall.Tests;

/// <summary>Scratch folders under %TEMP%, deleted when the owning test class is disposed.</summary>
internal sealed class Temp : IDisposable
{
    private readonly List<string> _dirs = new();

    internal string Dir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "livewall-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        return dir;
    }

    internal string File(string name, int bytes)
    {
        var path = Path.Combine(Dir(), name);
        System.IO.File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    public void Dispose()
    {
        foreach (var dir in _dirs)
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}
