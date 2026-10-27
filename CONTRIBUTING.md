# Contributing to LiveWall

Thanks for having a look. This is a small app; the bar is "does it work on a real desktop",
not process.

## Build and run

```powershell
cd LiveWall
dotnet build -c Release
.\bin\Release\net8.0-windows\LiveWall.exe
```

Rebuild only while LiveWall is not running: the running exe is locked, so `dotnet build` fails
with "Device or resource busy". Quit it first (tray → *Exit*), then build and start again.

`LIVEWALL_DEBUG=1` writes desktop-attach details to `%LOCALAPPDATA%\LiveWall\debug.log`.

`dotnet test` from the repo root runs the unit tests in `LiveWall.Tests`: config loading and the
legacy migration, the rotation clamp, the ffmpeg proxy cache naming, and file-kind classification.
They need no desktop, so they run on any machine.

## Where things live

| File | What it holds |
| --- | --- |
| `LiveWall/Program.cs` | entry point, tray app, state/config, autostart, rotation, watchdog |
| `LiveWall/MainWindow.cs` | the settings window (frameless WinForms shell + WebView2 bridge) |
| `LiveWall/WallpaperWindow.cs` | the wallpaper window, plus the `Native` Win32 interop that both windows use |
| `LiveWall/assets/ui.html` | the entire GUI — HTML/CSS/JS in one file, no build step |
| `LiveWall/assets/player.html` | what actually plays the wallpaper |
| `LiveWall.Tests/` | xunit tests for state, proxy naming and media classification |
| `installer/` | the setup app and its uninstall mode |

The C# side and `ui.html` talk over `PostWebMessageAsJson`; look at the `cmd` switch in
`MainWindow.cs` for the whole protocol.

## Pull requests

- Keep the front end dependency-free: no CDN, no bundler, no hosted fonts — `ui.html` and
  `player.html` ship inside the exe and must work offline.
- Match the surrounding code: file-scoped namespaces, `internal` for app types, comments only
  where the code cannot explain itself.
- Test on a real desktop: set a wallpaper, open a fullscreen app (should pause), restart
  Explorer from Task Manager (should re-attach), exit from the tray (should fully quit).
- If the change affects the UI, say what you looked at — a screenshot helps.

## Cutting a release

1. Bump the version in `installer/LiveWallSetup.csproj` (its `<Version>` feeds the uninstall
   entry via `Install.AppVersion`), in both `app.manifest` files and in the `brand-version` span
   in `LiveWall/assets/ui.html`.
2. `powershell -ExecutionPolicy Bypass -File build-installer.ps1`
3. Install `dist\LiveWallSetup.exe` on a clean-ish VM (or uninstall first) and check:
   install → wallpaper shows → uninstall leaves nothing behind.
4. Attach `dist\LiveWallSetup.exe` to a GitHub Release tagged `v<version>`.

Build output (`bin/`, `obj/`, `dist/`, `installer/payload/`) is git-ignored; never commit it.

## License

Contributions are accepted under the [Apache License 2.0](LICENSE).
