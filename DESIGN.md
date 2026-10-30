# Tolk Island design contract

The source of truth for the island's visual language. Values here mirror the resource dictionaries in
`MainWindow.xaml`, `SettingsWindow.xaml` and `ApplyAppearance` in `MainWindow.xaml.cs`. If a number in
this file disagrees with the XAML, the XAML wins and this file is wrong — fix it in the same change.

## Principles

1. **The island is a shape, not a window.** A pure-black (dark) or near-white (light) silhouette,
   flush with the top edge, with concave 20 DIP ears and a 30 DIP bottom radius. Everything inside is
   content; there is no chrome, title bar or outer border.
2. **One continuous surface.** Every state is a size of the same shell. Panels morph the shell; they
   never open a second window.
3. **Real data or an honest blank.** A value is either measured or shown as `—` with a reason
   (`unavailable`, `no fix`, `off`). Decorative fake numbers are a defect.
4. **Quiet until it matters.** Resting states are compact. Expansions happen on hover, click or a real
   event, and collapse back to what was there before.
5. **Keyboard and pointer are equal.** Every interactive element is reachable by Tab, has a visible
   focus ring, and `Esc` leaves the current panel.

## Colour tokens

Island (`MainWindow.xaml`, dark values; light theme is applied at runtime):

| Token | Dark | Light | Use |
|---|---|---|---|
| Shell fill | `#FF000000` | `#FFF8F8FB` + 18% shadow | The island body |
| `PrimaryBrush` | `#FFFFFFFF` | `#FF0B0B0D` | Primary text and icons |
| `SecondaryBrush` | `#FF9C9CA3` | `#FF5A5A63` | Captions, hints, inactive chips |
| `ChipBrush` | `#FF2C2C2E` | `#FFE7E7EC` | Chip and input background |
| `TrackBrush` | `#FF3A3A3C` | `#FFD0D0D8` | Progress and meter tracks |
| `BlueBrush` (accent) | `#FF0A84FF` default, user-selectable | same | Active chip, primary action |
| `GreenBrush` | `#FF30D158` | `#FF146B30` | Charging, success, completed |
| `DangerBrush` | `#FFFF453A` | — | Errors inline in Notes |
| `AccentInkBrush` | `#FF0B0B0D` | `#FF0B0B0D` | Glyph on an accent fill |
| `HoverBrush` | `#14FFFFFF` | `#0F000000` | Hover wash |
| `PressedBrush` | `#24FFFFFF` | `#1C000000` | Pressed wash |
| `HomeTileBrush` | `#14FFFFFF` | `#0F000000` | Idle-home control surface |
| `AccentSurfaceBrush` | accent @ 14% | accent @ 9% | Toggled-on surface |
| `GreenSurfaceBrush` | `#FF30D158` @ 14% | `#FF146B30` @ 10% | Focus pill surface |
| `WarmBrush` | `#FFFFB340` | `#FF9A5B00` | Night-light glyph when on |
| `WarmSurfaceBrush` | `#FFFFB340` @ 14% | `#FF9A5B00` @ 10% | Night-light surface when on |
| `HiddenTabBrush` | white `#00`→`#B3`→`#00` | black `#00`→`#8C`→`#00` | 44 × 3 seam of the retracted island |
| `ShellBorderBrush` | transparent | `#4D000000` | Light-theme rim |
| `WaveBrush` | accent → `#FFFF7A45`, 0,1 → 1,0 | same | Media waveform |
| `ArtPlaceholder` | `#FF9B6BFF` → `#FFFF6FA5` → `#FFFFA45B` | same | Missing album art |

High contrast ignores the palette and maps to `SystemColors` (`WindowText`, `Control`, `Highlight`,
`HighlightText`, `GrayText`, `WindowBrush`); tinted surfaces become plain control fills and the shell
shadow is removed.

Skins (`Services/SpiderSkin.cs`) swap palette and decoration without touching layout. **Spider** uses
a deep navy shell (`#FF0A0F1E` → `#FF05070F`; ivory `#FFFFF9F7` → `#FFFFF4F0` in light), a scarlet
accent (`#FFE23636` dark, `#FFC62828` light), a scarlet→cobalt rim, a red→blue retracted seam, and a
generated web canopy (9 spokes, sagging rings) clipped to the shell silhouette. The web is white at
8% (black at 6% in light), drops to 55% opacity below 60 DIP, and is skipped in high contrast. The
user's saved accent returns when the skin is switched back to Classic.

The Spider skin adds a mascot, **Smol Spidy**: an original suit-built spider, not a round cartoon
bug. Its red mask-shaped body carries a fine black web lattice and large angular white lenses with
black rims; a red chest panel sits over a deep cobalt abdomen, and eight tapered limbs use red upper
segments, blue lower segments, rounded knee joints and a claw tip. It hangs on a two-tone silk
thread (a 2.2 DIP dark shade under a 0.8 DIP white core, so it reads on both dark and light
wallpapers) and leans 3.2° off the thread's axis so it feels alive rather than printed. It hangs from
the island's bottom edge, pivots from the thread's anchor, and is decorative — never hit-testable. It
is shown only in the ambient states (`Idle`,
`IdleExpanded`, `CompactMedia`, `ExpandedMedia`), never in high contrast, hidden states, panels,
focus or transient HUDs. Its only motion is a 1.5 s damped swing when it appears (skipped with
reduced motion); it then rests, so no animation clock keeps the idle island rendering. The host
window is 360 DIP tall to leave room for the mascot at the maximum 1.25 scale.

Settings (`SettingsWindow.xaml`, always its own dark chrome):

| Token | Value | Use |
|---|---|---|
| `PrimaryBrush` | `#FFF4F4F6` | Titles, values |
| `SecondaryBrush` | `#FFA2A2AB` | Hints |
| `TertiaryBrush` | `#FF8A8A93` | Section labels |
| `NavBrush` | `#FF9B9BA4` | Inactive navigation |
| `AccentBrush` | `#FF0A84FF` | Selection, primary button |
| `AccentBorderBrush` | `#660A84FF` | Selected card rim |
| `DangerBrush` | `#FFFF8A80` | Local AI error, destructive actions |
| `SuccessBrush` | `#FF7EE2A0` | API check success |
| `CardBrush` | `#0EFFFFFF` → `#07FFFFFF` gradient | Card surface |
| Scroll thumbs | `#73` / `#8C` / `#A6` white | Rest, hover, drag |

## Type

- **Display:** `Segoe UI Variable Display, Segoe UI` — clock (46), panel titles (22), player title (20),
  compact titles (17), section values (15).
- **Text:** `Segoe UI Variable Text, Segoe UI` — body 12–13, captions 11–12, chip labels 13.
- **Icons:** `Segoe MDL2 Assets` at 14–18. Private-use glyphs only (`\uE000`–`\uF8FF`).
- Numbers are tabular in time and progress positions; no letter-spacing tricks.

## Shape and spacing

| Property | Value |
|---|---|
| Ear notch radius | 20 DIP |
| Bottom corner radius | 30 DIP (user range 0–40) |
| Idle | 300 × 46 |
| Idle hidden tab | 160 × 10 shell with a 44 × 3 seam |
| Idle expanded | 500 × 172 |
| Compact media / focus | 300 × 50 |
| Player | 460 × 172 |
| Notification | 460 × 118 |
| HUDs (charging, volume, brightness) | 380–440 × 72 |
| Panels (shelf, notes, calendar, prompt) | 460 × 150–208 |
| Mirror | 480 × 280 |
| Panel padding | 22 DIP horizontal, 16–20 DIP vertical |
| Card padding | 12–14 DIP |
| Control gap | 6–10 DIP |
| Scale | 0.85–1.25 (user), default 1.0 |

The host window is fixed; only the shell inside it changes size and shape.

## Motion

| Token | Value | Use |
|---|---|---|
| Morph | 340 ms, `BackEase` amplitude 0.14 (grow) / 260 ms `CubicEase` ease-out (retreat) | Shell resize |
| Content crossfade | 180 ms, `CubicEase` ease-out | Panel content while the shell moves |
| Idle-home entrance | 280 / 270 / 240 ms slides (0 / 40 / 70 ms delays), 9 / 8 / 6 DIP rise | Clock row, header and dock arrive in reading order |
| Overlay entrance | 240 ms, 10 DIP rise | Notification, charging, volume, brightness, live activity |
| Button hover | 120 ms in, 180 ms out | Wash on `HoverLayer` |
| Button press | 80 ms wash in; scale to `PressScale` (0.94 circle, 0.96 chip, 0.97 text) | `PressLayer` + `PressScale` |
| Button release | 240 ms `BackEase` amplitude 0.20 back to 1.0 | The only overshoot allowed on press |
| Hover leave delay | 500 ms | Stay-open grace before collapsing |
| Waveform frame | 33 ms (30 fps) | Live media bars |
| Notification peek | 4 s default (configurable 2–30 s) | Notification card |
| Charging HUD | 3 s | Charging card |
| Volume / brightness | 1.6 s / 1.8 s | HUD |
| Focus complete | 4 s | Completion card |
| Live activity | 5 s default | Transfer card |
| Mirror reveal | Shutter fades 440 ms; frame settles from 4.5% at 680 ms; starting glyph pulses 850 ms | Camera preview opening |
| Mirror camera stop | Immediate on close, hide, pause or feature off | Privacy invariant |
| Idle retract | 10 s default (3–120 s) | Idle → hidden tab |
| Reduced motion | 1 ms morph, fade, entrance and leave delay | All of the above |

Motion only communicates a state change: expand/collapse, HUD entrance, live audio, and direct
pointer feedback. No looping decoration outside media, and reduced motion keeps every state while
removing overshoot, pulses and long fades. An animation clock is released in its `Completed` handler
whenever its result is the base value, so nothing keeps the render thread awake in a resting state.

## Components

| Component | Contract |
|---|---|
| Button motion | Every button template exposes `HoverLayer`, `PressLayer` and `PressScale`; `ButtonMotion` fades the washes and springs the scale, and releases every clock on leave/unload |
| `IconButton` | 36 × 36, radius 10, transparent, hover/pressed wash, focus ring, 40% opacity when disabled |
| `CircleButton` | 44 × 44 white circle, black glyph, hover `#FFE4E4E6`, pressed `#FFCFCFD4` |
| `SmallCircleButton` | 36 × 36, radius 18 |
| `ChipButton` | 36 high, `ChipBrush`, secondary text; used by player and panel chips |
| `HomeDockButton` | 52 × 43, radius 13, transparent with a hover wash; the four-tile home dock |
| `HomeTileButton` | 44 × 44, radius 12, `HomeTileBrush`, 18 glyph; quick-launch tiles |
| `HomePillButton` | 44-high pill, radius 22, green-tint surface, green glyph + primary label; the Focus entry |
| `IslandFocusRing` | 2 DIP outline, 3 DIP offset, radius 11 |
| `HomeTileFocusRing` / `HomePillFocusRing` | 2 DIP outline, 3 DIP offset, radius 15 / 25 (matches tile and pill geometry) |
| Idle clock | 17 SemiBold tabular numerals, left; battery percent 12.5 Medium + 21 × 11 outlined gauge, right |
| Idle home | Greeting + date/gauge header; 54 Light clock left, 100-wide Focus pill right; four-tile dock (Shelf, Notes, Calendar, Prompt), or five with opt-in Mirror |
| Timeline | 3-high rounded track, accent fill, 8 DIP knob that follows the pointer while seeking |
| Cards (panels, settings) | 12–14 padding, 14–18 radius, subtle translucent fill, hairline rim |
| Mirror card | 24 radius, full-bleed preview with top and bottom scrims; title, Live chip and close in white; status shutter uses the same card radius |
| Inline errors | `DangerBrush`, `LiveSetting.Assertive`, collapsed until needed; copy names the cause and the next step |
| Empty / stale values | `—` value with a short reason caption; never a fabricated number |
| Primary button | Accent fill, ink text, 8 radius, disabled at 40% |
| Danger button | Outlined `DangerBrush`, only for destructive confirmations; Cancel is the default focus |

## State matrix

| State | Trigger | Content | Exit |
|---|---|---|---|
| Idle | Launch, or any activity ends | Time, battery | Hover, click, hotkey, media, focus |
| Idle hidden | Idle timer expires | 10 DIP tab | Hover the tab |
| Idle expanded | Hover / click idle | Greeting, live clock, date and battery gauge; green Focus pill with quick-launch tiles; four-tile dock with Shelf/Notes/Calendar/Prompt | Leave, click outside, Esc |
| Compact media | Media playing | Art, title, live waveform | Hover → player; media stops |
| Player | Hover compact media, hotkey pin, API | Art, title, timeline, controls | Leave, pin off, Esc |
| Compact focus | Focus running | Ring, remaining time | Hover → timer |
| Focus picker | Focus entry | 15/25/50 minute choices | Choose, leave |
| Focus timer | Session running | Countdown, pause/reset | Completion, reset |
| Focus complete | Session ends | Completion card | 4 s timer |
| Notification | Real toast | App, title, body, reply when supported | 4 s timer, next queued card, dismiss |
| Charging | Charger starts | Battery HUD | 3 s |
| Volume / brightness | System change | Slider HUD | 1.6 s / 1.8 s |
| Shelf / Notes / Calendar / Prompt | Entry icon, API | Panel content | Esc, click outside, pin toggle |
| Mirror | Opt-in dock tile | Full-bleed mirrored front-camera preview, Live chip, close and permission states | Esc, close, click outside; camera stops immediately |
| Live activity | Transfer, finished download | Progress or status card | 5 s after it settles |

Panel precedence: a notification peek never destroys a pinned panel; it restores the pinned state when
it finishes. The next queued notification shows before the restore. Pausing suppresses notifications,
clears the queue and releases audio capture.

## Evidence status

- Baseline audit (2026-10-24): report, screenshots and measurements under
  `C:\Users\Nova\.gstack\projects\tolkisland\` — dark/light/high-contrast idle and expanded, media,
  settings at 100/125/150%.
- Interaction polish pass (2026-10-28): offscreen, fixed-sample renders of the idle pill, idle home,
  hidden tab and player in dark and light live in `demo/captures/preview-*.png`; the renderer
  (`tests/TolkIsland.Regression --render-island`) also asserts that every button template still has
  its motion parts.
- Idle home revision (2026-10-28): the home was recomposed into the four-row grid (header, clock +
  Focus, four-up toggles, eight-tile dock) and re-rendered in both themes. The renderer accepts an
  optional accent argument (`--render-island <dir> #FFFF6B6B`) so tinted toggle states can be checked
  against the user's actual accent without a live session; an optional skin argument (`spider`) renders
  the themed skin the same way.
- Smol Spidy (2026-10-28, redesigned 2026-10-29): `--render-screens <dir> "#FFE23636" spider`
  renders the mascot under the idle pill, idle home, player and compact media in both themes. The
  redesign replaced the round-eyed bug with the masked, web-latticed, jointed-limb suit spider above;
  the two themes were checked at 5× zoom for lens rim, sheen and thread contrast. The harness adds
  64 DIP below those crops only when the mascot is actually visible, so Classic captures are
  unchanged.
- Mirror prototype (2026-10-29): opt-in fifth dock tile and a 480 × 280 camera card, rendered
  offscreen with a synthetic frame; live camera behaviour still needs a real-device pass.
- Subtraction pass (2026-10-29): Clipboard, System, Screenshot, the four quick toggles, the widget
  dashboard, weather/accessory/call cards and the Phone Link claim were removed; Calendar was reduced
  to the next event and the tray to four commands. The idle home is now 500 × 172 with a four-tile
  dock, and every remaining screen was re-rendered offscreen in both themes.
- This batch (2026-10-28): tokens and components were re-checked against source; **new captures for the
  prompt Cancel state and the Notes inline error are pending** because they only appear with live data
  and must not be produced by automation that takes over the desktop.
- Manual gates still open: real screen reader, 200% scaling, high contrast on a live session, and
  multi-monitor DPI transitions.
