# LiveWall — design system

The GUI is a frame for the user's footage, never the star. Monochrome by contract: every
pixel of colour on screen belongs to a wallpaper, not to the interface.

## Mode

Operate. Familiar Windows patterns, precision over expression, no choreography. The test is
whether a Windows user can find "add", "apply", and "pause" without reading anything twice.

## Position

Modes are: title bar (40px) / content (scrolls) / status bar (30px). Content padding 20–22px,
22px between hero and library. Window default 1040×700, min 820×560.

- **Title bar** — 18px brand mark, `LiveWall` 12.5/600, version 11/ink-3. Right: theme toggle,
  settings, then the three window buttons (44×40). The bar is the drag surface.
- **Hero** — the selected or current wallpaper, `clamp(210px, 34vh, 320px)`, radius 18,
  hairline border, dark scrim under the copy. File name 21/600, meta 12 with tabular numerals.
  Actions bottom-right. First run replaces it with a centered welcome: 26/600 title, one
  sentence, format hint, primary button, dashed border, max 760px wide.
- **Library** — heading 14/600 with a count, search 32px, `Add wallpaper`; grid
  `repeat(auto-fill, minmax(196px, 1fr))`, 12px gaps. Cards: 16:9 poster, 13/500 name,
  12 meta, radius 14.
- **Status bar** — one line of truth (`7 wallpapers · rotating every 1h`) plus shortcut hints.
- **Settings** — a 320px popover under the gear, never a modal: appearance, auto-rotate,
  start with Windows, pause, pause over fullscreen apps, quit.

## Color

Restrained. Two neutrals layers plus one inverted primary. Tokens live in `assets/ui.html`.

| token | dark | light |
| --- | --- | --- |
| bg | `#0a0a0b` | `#fafafa` |
| panel / panel-2 / panel-3 | `#111113` / `#17171a` / `#222227` | `#ffffff` / `#f4f4f5` / `#e8e8eb` |
| line / line-2 | `rgba(255,255,255,.07)` / `.14` | `rgba(9,9,11,.09)` / `.18` |
| ink / ink-2 / ink-3 | `#f4f4f6` / `#a6a6ae` / `#8a8a94` | `#111114` / `#54545d` / `#6d6d77` |
| primary / on primary | `#f4f4f6` / `#0a0a0b` | `#111114` / `#ffffff` |
| media bed | `#060608` | `#e6e6ea` |

Contrast: ink 15:1, ink-2 6.4:1, ink-3 4.7:1 on both grounds. The one stateful exception is
the "already applied" primary over live footage, which goes white-on-glass so it stays legible
against any video.

## Type

`Segoe UI Variable Text` / `Segoe UI Variable Display`, system fallback — no hosted fonts (the
app is offline). Ramp: 26 welcome · 21 hero · 14 section · 13 body and card names · 12 meta,
labels, help, status · 11 chips and micro-labels. Tabular numerals for sizes and counts.
Headings track −0.017 to −0.025em. No text below 11px.

## Components

- **Button** — 34px, radius 9, 12.5/500. `primary` (inverted), default (hairline), `glass`
  (white 10% — only over media), `small` (28px), `link`. Every control has hover, active,
  disabled; focus is a 2px ring at 2px offset.
- **Search** — 32px field inside a hairline container; focus lifts the container border.
- **Card** — poster, name, meta, and a full-bleed transparent hit button for keyboard focus.
  On hover: border brightens, `Apply` and remove appear, hover playback swaps the poster for
  the live video. `On desktop` chip in the corner of the current wallpaper.
- **Segments and switches** — settings only. 26px segments on a recessed track, 34×20 switches.
- **Toast** — inverted ink pill, centered above the status bar, 2.4s.
- **Poster loading** — a static recessed block, never a shimmer.

## Motion

The brief asked for almost none. 120ms ease (`cubic-bezier(.2,0,0,1)`) on colour and border,
160ms poster reveal, 220ms video fade in the wallpaper player. No entrance sequences, no
infinite animation, no parallax. `prefers-reduced-motion` removes all of it.

## States that must exist

Hover, focus-visible, selected, on-desktop, paused, paused by a fullscreen app (its own badge and
a status-bar note), missing file, empty library, empty search, loading poster, disabled apply,
unsupported file (toast), WebView2 runtime missing (dialog).

## Native frame

Frameless WinForms hosting WebView2. DWM rounded corners (square when maximized), DWM border
colour from the theme, `CS_DROPSHADOW`, 8px resize hit-test (DPI-scaled) in `WndProc`, maximize constrained
to the monitor work area, title-bar drag through `WM_NCLBUTTONDOWN`. Theme changes update the
form background, the WebView2 default background, and the DWM border together so no white
flash ever appears.

## Wallpaper surface

`assets/player.html` is deliberately bare: black ground, `object-fit: cover`, muted looping
video, 220ms fade-in on `loadeddata`, `vw_pause` / `vw_play` for the app. HTML wallpapers render
themselves, unframed.

## Shortcuts

`Ctrl+O` add · `Space` pause/play · `Enter` apply selected · `1–9` apply the nth wallpaper ·
`Esc` close the popover.
