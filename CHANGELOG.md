# Changelog

All notable changes to Tolk Island are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project uses semantic versioning
for the app while the local API carries its own contract version.

## [Unreleased]

### Added

- **Mirror** (off by default): a fifth dock tile, shown only when enabled in Settings → Activities,
  springs the island into a 480 × 280 camera card. The front camera is preferred, the preview is
  mirrored like a real mirror, and a shutter lifts into the live image. Capture starts only while the
  panel is visible and pinned, and stops immediately on close, hide, pause or feature off. Denied
  access links to Windows camera privacy settings; no frame is ever recorded or written.

- Loopback API: typed error envelope (`ok`, `code`, `message`) on every failure, a bounded request body
  (8 KB), a concurrency cap (8 in flight), pause and per-feature gates, `/health` and `/status` with
  `apiVersion` and uptime, and one request line per call in the local log.
- Prompt panel: a **Cancel** button and `Esc` cancellation, a generation guard so a late answer can
  never overwrite a newer request, and a bounded response size.
- Ask panel: optional **Groq** provider (fixed `api.groq.com` endpoint, configurable model) with the
  API key stored as Windows DPAPI ciphertext. Settings can add, replace and remove the key; failures
  distinguish a rejected key, an unknown model and a rate limit. The local endpoint stays
  loopback/private-only.
- Notification cards: up to three bursts queue instead of overwriting each other, and a peek no longer
  discards a pinned panel.
- `docs/LOCAL_API.md` and compatibility policies for API v1/v2.
- Local API authentication (v2): every route except `/health` requires the `X-Tolk-Token` header;
  requests with an `Origin` header are rejected, and the `Host` must be `127.0.0.1`. The token is
  generated once, stored in `%LOCALAPPDATA%\TolkIsland\api-token.txt`, and can be copied from
  Settings → Advanced.
- A dependency-free regression harness (`tests/TolkIsland.Regression`) with a non-zero exit code.
- Skins: a **Spider** option in Appearance paints the island in a scarlet/cobalt suit palette with a
  generated web canopy clipped to the shell, a two-tone rim and a themed retracted seam. Classic stays
  the default, high contrast always wins, and the saved accent returns with Classic.
- **Smol Spidy**, the Spider skin mascot: an original suit-built spider — a web-lattice red mask with
  angular lens eyes, a red-over-cobalt body, and eight tapered, jointed scarlet-and-blue limbs — hanging
  from the island's bottom edge on a two-tone silk thread in the ambient states, with a one-shot
  damped swing on appearance (skipped with reduced motion). It is decorative and click-through, and
  stays hidden in high contrast, hidden states, panels, focus and transient HUDs. The host window
  grew to 360 DIP so the mascot fits at the maximum 1.25 scale.
- Design contract in `DESIGN.md`; this changelog and a local API time-to-hello-world checklist.
- Idle home redesign: greeting and date/battery header, a 46 pt clock with a green Focus pill and
  quick-launch tiles and a four-tile dock (Shelf/Notes/Calendar/Prompt), with a three-step entrance
  that follows reading order.
- Pointer feedback on every island button: hover wash, pressed wash and a spring press scale, with the
  playback, shuffle and repeat controls repainting before the media session round trip.
- `--render-island` offscreen renderer for fixed-sample dark and light previews of the idle pill, idle
  home, hidden tab and player, with an optional accent hex for tint verification; it also fails if a
  button template loses its motion parts. `--render-promo` renders curated 2× stills (player,
  notifications, focus, compact media) with fictional sample content for the promo kit.

### Changed

- Idle home controls share one surface system: 44 pt tile and pill buttons, tinted on-states with a
  coloured glyph and bright label instead of solid accent fills (a coral accent no longer reads as an
  alarm), focus rings matched to each shape, and a drawn battery gauge replacing the `E850` glyph that
  rendered as `=`.
- Settings reports a failed disk write honestly while still applying the change in memory, and exposes
  behaviour (expand mode, sounds, haptics), local AI endpoint/model, monitor mode **Every monitor**, and
  a Developer card with the API URL.
- Calendar join links are validated by host, not by substring.
- Notes roll back in-memory changes when the file cannot be written.
- The audio visualizer is reference-counted, never blocks the UI when stopping, and only runs in the
  primary shell while media is actually playing, the compact waveform is on screen, and the island is
  not paused or hidden.
- Unreadable settings are quarantined as `settings.json.bad` and recovered from the last good
  `settings.json.bak` instead of silently reverting to defaults and overwriting the file.
- Only one Settings window exists at a time; opening Settings again re-activates it. The prompt
  panel's privacy label follows the configured provider (Groq or local).
- Reset now says exactly what it erases (preferences, Pro unlock, licence, Groq key, startup entry)
  and reports when parts of the reset fail.
- Disabled calendar and download features stop their timers and watchers.
- One 1 s heartbeat now drives the clock, battery, idle retract and fullscreen probe; the 500 ms
  timeline tick exists only while the player is open, downloads are polled only while a transfer is
  growing, and idle-home text is written only when it changes.
- Motion retuned for a shorter, tighter feel: 340 ms spring grow and 260 ms retreat on the shell,
  180 ms content fades, 500 ms hover grace.
- Album art is decoded once per track at 256 px, shared by the compact and expanded slots, and the
  thumbnail stream is cached per track instead of on every playback event.

### Removed

- Clipboard history: the dock entry, panel, capture listener and its P/Invoke surface are gone.
- System monitor: the dock entry, panel and its CPU, memory, network and privacy samplers are gone.
- Screenshot: the dock shortcut is gone; use `Win+Shift+S`.
- Wi-Fi, Bluetooth, Quiet and Night-light chips: the idle home is no longer a second Quick Settings.
  Quiet mode remains a Settings preference and still silences island cards.
- Widget dashboard: the panel, the weather, accessory-battery and call cards, the Phone Link "Linked"
  claim and the third-party JSON manifest SDK are gone. Calendar keeps only the next event with its
  join link.
- Tray menu: reduced from twelve commands to Show/hide, Pause, Settings and Exit.
- Local API `POST /show` no longer accepts `system` or `clipboard`; widget manifest docs and the
  `--validate-widgets` CLI were removed.

### Fixed

- A track or video change could keep showing the previous item: some players raise the SMTC change
  event before they publish the new metadata. The media service now re-reads shortly after every
  change, polls gently while a session exists, re-reads fresh artwork during the first seconds of a
  new track, and only redraws the island when the snapshot actually changed.
- Clicking the prompt answer area or resizing could leave stale content; state transitions now cancel
  or clean up the prompt request.
- Repositioning the island no longer re-shows it after idle hide, pause or fullscreen.
- The shelf rejects UNC and device paths, so a stored remote path can no longer freeze the UI or leak
  NTLM credentials.
- List panels no longer jump to the previous scroll position on refresh, the scroll fade clears at
  the end of the list, and high contrast recolors the waveform bars.
- Album-art thumbnail streams are disposed, update manifests are read with an 8 KB cap, and
  two-part versions parse.
- A launch with `--settings` no longer holds the single-instance mutex, and the full island fits at
  125% UI scale.
- A paused island no longer keeps the audio capture session alive.
- The `demo/audiotest` sweep now exits non-zero when the synthetic tone is not detected.

## [1.0.0] — 2026-10-24

### Added

- Top-center Windows island with click-through hit testing, hover, click-to-pin and a global hotkey.
- Real data sources: media sessions (SMTC), Windows toast notifications, battery and charging, volume,
  calendar, downloads, microphone-backed call detection, weather, accessory batteries and Phone Link.
- Focus timer, file shelf, clipboard history, quick notes, widgets, local prompt box and live activity
  cards.
- Settings window with live island preview, placement, theme, motion, accessibility and monitor modes,
  including **All monitors** with a single primary shell that owns shared services.
- Tray menu, optional start-with-Windows entry, opt-in update manifest check, and a local crash log.
- Widget manifest SDK (`docs/PLUGIN_SDK.md`) and the loopback local API.
