# Tolk Island design notes

These values follow the resource dictionaries in `MainWindow.xaml`, `SettingsWindow.xaml` and `ApplyAppearance` in `MainWindow.xaml.cs`. If this file and the XAML disagree, the XAML is correct.

## Principles

1. The island is one fixed host window. Only the black shell inside it changes size.
2. Values come from Windows APIs or are shown as unavailable. There are no sample numbers.
3. Resting states stay small. Panels open on hover, click, a hotkey or a real event, then close back to the resting state.
4. Every control works with the keyboard, has a visible focus ring and closes on `Esc`.
5. Transient overlays never destroy a pinned panel. The pinned state is restored afterwards.

## Colour tokens

Island (`MainWindow.xaml`, dark values; light values are applied at runtime):

| Token | Dark | Light | Use |
|---|---|---|---|
| Shell fill | `#FF000000` | `#FFF8F8FB` + 18% shadow | Island body |
| PrimaryBrush | `#FFFFFFFF` | `#FF0B0B0D` | Text and icons |
| SecondaryBrush | `#FF9C9CA3` | `#FF5A5A63` | Captions, hints, inactive controls |
| ChipBrush | `#FF2C2C2E` | `#FFE7E7EC` | Chips and inputs |
| TrackBrush | `#FF3A3A3C` | `#FFD0D0D8` | Progress tracks |
| BlueBrush | `#FF0A84FF` or the saved accent | same | Accent and primary actions |
| GreenBrush | `#FF30D158` | `#FF146B30` | Charging, focus, success |
| DangerBrush | `#FFFF453A` | same | Inline errors |
| AccentInkBrush | `#FF0B0B0D` | `#FF0B0B0D` | Glyph on an accent fill |
| HoverBrush | `#14FFFFFF` | `#0F000000` | Hover wash |
| PressedBrush | `#24FFFFFF` | `#1C000000` | Pressed wash |
| HomeTileBrush | `#14FFFFFF` | `#0F000000` | Home tiles and empty-state icons |
| AccentSurfaceBrush | accent at 14% | accent at 9% | Active home controls |
| GreenSurfaceBrush | green at 14% | green at 10% | Focus pill |
| WarmBrush | `#FFFFB340` | `#FF9A5B00` | Charging accent |
| WarmSurfaceBrush | warm at 14% | warm at 10% | Charging surface |
| HiddenTabBrush | white gradient | black gradient | Retracted seam |
| ShellBorderBrush | transparent | `#4D000000` | Light-theme rim |
| WaveBrush | accent to `#FFFF7A45` | same | Media waveform |
| ArtPlaceholder | purple to pink to orange | same | Missing album art |

High contrast maps these tokens to `SystemColors` and removes the shell shadow.

Skins in `Services/SpiderSkin.cs` change the palette and add decoration. The Spider skin uses a dark navy shell, a scarlet accent and a generated web drawn inside the shell outline. It also adds a spider mascot below the island in ambient states. High contrast always uses the system palette.

Settings use their own dark palette:

| Token | Value | Use |
|---|---|---|
| PrimaryBrush | `#FFF4F4F6` | Titles and values |
| SecondaryBrush | `#FFA2A2AB` | Hints |
| TertiaryBrush | `#FF8A8A93` | Section labels |
| NavBrush | `#FF9B9BA4` | Inactive navigation |
| AccentBrush | `#FF0A84FF` | Selection and primary action |
| AccentBorderBrush | `#660A84FF` | Selected card border |
| DangerBrush | `#FFFF8A80` | Errors |
| SuccessBrush | `#FF7EE2A0` | Successful API check |
| CardBrush | `#0EFFFFFF` to `#07FFFFFF` | Card surface |

## Type

- Display: `Segoe UI Variable Display, Segoe UI` at 46 for the home clock, 22 for panel titles, 20 for the player title and 17 for compact titles.
- Text: `Segoe UI Variable Text, Segoe UI` at 12 to 13 for body copy and 11 to 12 for captions.
- Icons: `Segoe MDL2 Assets` at 14 to 18. Only private-use code points are used.
- Times and positions use tabular numerals.

## Shape and spacing

| Property | Value |
|---|---|
| Host window | 630 x 360 DIP, fixed |
| Ear radius | 20 DIP |
| Bottom radius | 30 DIP, adjustable 0 to 40 |
| Idle | 300 x 46 |
| Idle hidden | 160 x 10 shell with a 44 x 3 seam |
| Idle expanded | 500 x 172 |
| Compact media and focus | 300 x 50 |
| Player | 460 x 172 |
| Notification | 460 x 118 |
| HUDs | 380 to 440 x 72 |
| Panels | 460 x 150 to 260 |
| Mirror | 480 x 280 |
| Text scan | 460 x 260 |
| Panel padding | 22 horizontal, 16 to 20 vertical |
| Card padding | 12 to 14 |
| Scale | 0.85 to 1.25, default 1.0 |

## Motion

| Token | Value | Use |
|---|---|---|
| Shell grow | 340 ms, BackEase amplitude 0.14 | Expansion |
| Shell retreat | 260 ms, CubicEase ease-out | Collapse |
| Content crossfade | 180 ms | Panel change |
| Idle home entrance | 280 / 270 / 240 ms slides at 0 / 40 / 70 ms | Clock, header, dock |
| Overlay entrance | 240 ms, 10 DIP rise | Notifications and HUDs |
| Button hover | 120 ms in, 180 ms out | Hover wash |
| Button press | 80 ms wash, scale to 0.94 to 0.97 | Press feedback |
| Button release | 240 ms, BackEase amplitude 0.20 | Return to rest |
| Hover leave delay | 500 ms | Close grace period |
| Notification | 4 s default, configurable 2 to 30 s | Notification card |
| Charging | 3 s | Charging card |
| Volume and brightness | 1.6 s and 1.8 s | HUDs |
| Focus complete | 4 s | Completion card |
| Live activity | 5 s default | Download card |
| Idle retract | 10 s default, configurable 3 to 120 s | Idle to hidden tab |
| Mirror reveal | 440 ms shutter fade, 680 ms image settle, 850 ms starting pulse | Camera preview |
| Mirror stop | Immediate | Close, hide, pause, session lock or feature off |

Animations release their clocks when they finish. Reduced motion keeps every state and removes overshoot, pulses and long fades.

## Components

| Component | Contract |
|---|---|
| Button motion | Templates expose `HoverLayer`, `PressLayer` and `PressScale`; `ButtonMotion` owns the clocks |
| IconButton | 36 x 36, radius 10 |
| CircleButton | 44 x 44, white circle with a black glyph |
| SmallCircleButton | 36 x 36, radius 18 |
| ChipButton | 36 high, ChipBrush |
| HomeDockButton | 52 x 43, transparent with a hover wash |
| HomeTileButton | 44 x 44, radius 12 |
| HomePillButton | 44 high, radius 22, green surface |
| Focus rings | 2 DIP outline, 3 DIP offset |
| Idle home | Clock, date and battery header; Focus pill; four dock items (Shelf, Notes, Calendar, OCR), or five with Mirror |
| Timeline | 3 high track, accent fill, 8 DIP seek knob |
| Mirror card | 24 radius, full-bleed preview, top and bottom scrims |
| Empty values | Dash or a short reason, never a placeholder number |
| Primary button | Accent fill, ink text, 8 radius |
| Danger button | Outlined danger colour, Cancel focused by default |

## State matrix

| State | Trigger | Content | Exit |
|---|---|---|---|
| Idle | Launch or activity end | Time and battery | Hover, click, hotkey, media, focus |
| Idle hidden | Idle timer | 10 DIP tab | Hover |
| Idle expanded | Hover or click | Clock, date, battery, Focus, dock | Leave, click outside, Esc |
| Compact media | Media playing | Art and waveform | Hover, media stops |
| Player | Hover compact media, hotkey pin, API | Art, title, timeline, controls | Leave, unpin, Esc |
| Compact focus | Focus running | Ring and time | Hover |
| Focus picker | Focus entry | 15, 25 or 50 minutes | Choose, leave |
| Focus timer | Session running | Countdown, pause, reset | Completion or reset |
| Focus complete | Session ends | Completion card | 4 s |
| Notification | Real toast | App, title, body, reply when available | Timer, next card, dismiss |
| Charging | Charger starts | Battery HUD | 3 s |
| Volume or brightness | System change | Slider HUD | 1.6 s or 1.8 s |
| Shelf, Notes, Calendar, OCR | Dock or API | Panel content | Esc, click outside, unpin |
| Mirror | Opt-in dock tile | Mirrored camera preview, live state, permission states | Esc, close, click outside; camera stops |
| Text scan | OCR dock tile or scan button | Screen text with a copy action; language and error states | Esc, click outside, unpin |
| Live activity | Transfer or finished download | Progress or status | 5 s after it settles |

A notification peek does not destroy a pinned panel. When the peek ends, the pinned panel returns. Pausing clears the notification queue and releases audio capture.

## Evidence

- Baseline audit (2026-10-24): screenshots and measurements under `C:\Users\Nova\.gstack\projects\tolkisland\`.
- Offscreen renders (2026-10-30): dark and light captures for every state in `demo/screens`, generated by `tests/TolkIsland.Regression --render-screens`.
- Mirror (2026-10-30): panel states rendered with a synthetic frame. Live camera behaviour still needs a test on a machine with a camera.
- Manual checks still open: screen reader pass, high contrast on a live session, 200% scaling and multi-monitor DPI transitions.
