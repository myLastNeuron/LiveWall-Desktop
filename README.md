# LiveWall

A live wallpaper app for Windows. It draws a video, a GIF or a whole HTML page behind your
desktop icons, so the icons keep working and the wallpaper keeps playing underneath them. It
uses the same WebView2 engine Edge ships with and lives in the tray. No account, no telemetry,
and the app itself never touches the network. The only downloads happen in the setup, and only
for the runtimes a PC is missing.

## What it does

- Plays mp4, webm, mkv, mov, m4v, gif and html files. Anything local.
- Keeps a library, so switching is one click and searching it is a text box away.
- Rotates on a timer if you want it to, from every 5 minutes to every 24 hours. The deadline
  survives a restart.
- Steps aside. It pauses while a fullscreen app is in front and while you are on battery. Both
  are toggles, and the Play button overrides them.
- Can start with Windows.
- Puts itself back when Explorer restarts or the desktop gets rebuilt.
- Has a dark and a light theme. Closing the window keeps the wallpaper running, and Exit lives
  in the tray menu.

## Install

Grab `LiveWallSetup.exe` from the Releases page and run it. One window: pick a folder, press
Install.

A few things the setup takes care of:

- Installs per user (default `%LOCALAPPDATA%\Programs\LiveWall`), so there is no admin prompt.
- Downloads only what the PC is missing: the .NET 8 Desktop Runtime (it asks for admin once)
  and the WebView2 Runtime. If they are already there, it skips them.
- Adds a Start Menu shortcut, an optional desktop shortcut, and an entry in Windows
  *Installed apps*. `Uninstall.exe` ends up in the install folder.

The build is not code-signed, so SmartScreen may warn about an unrecognised app. Choose
*More info*, then *Run anyway*. If you would rather avoid that, build from source and skip the
installer.

Unattended install: `LiveWallSetup.exe --silent "D:\Apps\LiveWall" [--desktop]`, with the log
in `%TEMP%\livewall-setup.log`.

## Build from source

You need the [.NET 8 SDK](https://dotnet.microsoft.com/download) on Windows 10 or 11 x64.

```powershell
cd LiveWall
dotnet build -c Release
.\bin\Release\net8.0-windows\LiveWall.exe
```

## Build the installer

```powershell
powershell -ExecutionPolicy Bypass -File build-installer.ps1
```

That produces `dist\LiveWallSetup.exe`, around 60 MB. The app is embedded in it, and the setup
itself is self-contained so it runs on a PC with no .NET installed.

## Using it

- The tray icon opens LiveWall, pauses the wallpaper, or exits.
- Add wallpapers from the window, or drop a file anywhere on it.
- With `ffmpeg` on your PATH, a 4K video is re-encoded once to screen size and cached, which is
  much lighter on the GPU.
- Settings cover the rotation interval, starting with Windows, pausing behind other apps, and
  pausing on battery.
- The library, the last wallpaper and the settings live in `%LOCALAPPDATA%\LiveWall\config.json`.
- Set `LIVEWALL_DEBUG=1` to log desktop-attach details to `%LOCALAPPDATA%\LiveWall\debug.log`.

## How it works

1. `WallpaperWindow` is a borderless `Form` that hosts a `WebView2`.
2. It posts `0x052C` to `Progman`, which makes Explorer create the `WorkerW` behind the icons.
   On builds that never create one, the window is parented to `Progman` instead.
3. The window is given `WS_CHILD`, reparented to that host, and stacked directly beneath
   `SHELLDLL_DefView`. That puts it above the desktop background and below the icons.
4. `assets/player.html` loads your file through two virtual hosts: `app.local` for the player
   itself, and `media.local` for the folder your file sits in. Video loops muted.
5. Once a second it looks at the foreground window. If something fills the whole screen,
   playback pauses until that window goes away. The same tick notices a dead Explorer and
   re-attaches.
6. The window you interact with is a borderless WinForms shell hosting `assets/ui.html`, which
   talks to C# over `PostWebMessageAsJson`. No local server, no framework, and no build step
   for the front end.

## Known limits

- The wallpaper covers the primary monitor only, scaled with `object-fit: cover`.
- Audio is muted.
- Windows x64 only.

## Project layout

```
LiveWall/            the app (WinForms + WebView2, C#, assets/ui.html + player.html)
LiveWall.Tests/      unit tests for state, proxy naming and media classification (dotnet test)
installer/           the setup app; embeds the published app as payload.zip
build-installer.ps1  one shot: publish the app, zip it, build dist\LiveWallSetup.exe
tools/               dev scripts (benchmarks, window capture, page checks)
```

## Contributing

Bug reports and PRs are welcome. Have a look at [CONTRIBUTING.md](CONTRIBUTING.md) first.

## License

[Apache License 2.0](LICENSE).
