# LiveWall

## What it is

A lightweight Windows live-wallpaper app. It renders a video, GIF, or HTML page behind the
desktop icons by reparenting a WebView2 window under Explorer's `WorkerW`, restarts itself
after Explorer crashes, and auto-pauses when a fullscreen app is in front.

## Who and where

Someone at their own Windows desktop, often at night or in a dim room, who wants the desktop
to feel alive without a manager, an account, or a subscription. They open the app rarely:
to add a wallpaper, to switch, or to turn auto-rotation on. Everything else runs unattended
from the tray.

## The job

- Add a wallpaper and see it on the desktop within a second, with no wizard.
- Keep a library of what they have added, so switching later costs one click.
- Understand what is currently on the desktop and whether it is paused.
- Control costs: pause, rotate, start with Windows.

## Product truth (settled)

- Local files only. No catalog, no downloads, no accounts, no telemetry.
- Tray-resident. Closing the window keeps it running; Quit lives in settings.
- Wallpaper window is separate and never steals focus.
- Existing behavior is preserved: fullscreen auto-pause, Explorer-restart recovery, the same
  accepted file types (MP4, WebM, MKV, MOV, M4V, GIF, HTML/HTM), the same config location
  (`%LOCALAPPDATA%\LiveWall`).

## Constraints

- Offline app UI. No hosted fonts, no CDN, no runtime downloads. The GUI ships in the exe.
- Heavy work stays off the UI thread; the GUI must feel instant on a 6-year-old laptop.
- The GUI is a WinForms shell hosting the same WebView2 dependency for its interface.

## Assumptions (labeled, from the brief)

- Windows 10/11 desktop, single primary monitor for the wallpaper.
- English UI.
- Dark is the default theme; light is a first-class peer, not an afterthought.
