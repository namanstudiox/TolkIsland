# Changelog

Notable changes to Tolk Island. The app follows semantic versioning; the local API has its own contract version.

## Unreleased

### Added

- Text scan panel using the local Windows OCR engine. The island hides while the screen is captured, and the panel offers copy, re-scan, no-text and missing-language states.
- Mirror panel, off by default. Enabling it adds a fifth dock tile and opens a 480 x 280 front-camera preview. Capture stops when the panel closes, the island hides, the app pauses, the session locks or the feature is turned off.
- Local API v2 with a token on every route except `/health`, rejected `Origin` headers, a strict `Host` check, an 8 KB body limit and a concurrency limit of eight.
- Notification queue for up to three toast bursts and restoration of a pinned panel after a peek.
- Spider skin and mascot, with a generated web overlay and a one-shot swing animation.
- Idle home redesign with a Focus pill, quick-launch tiles and four dock entries, plus Mirror when enabled.
- Hover, press and release feedback for island buttons.
- Offscreen renderer and regression harness.

### Changed

- Settings writes are atomic. A failed write leaves memory and disk on the previous values.
- Calendar join links are validated by host.
- Notes restore their previous in-memory state when a save fails.
- The audio visualizer uses reference-counted ownership and stops when no shell needs it.
- Only one Settings window can be open.
- Disabled calendar and download features stop their timers and watchers.
- One heartbeat drives the clock, battery, idle retract and fullscreen check. The media timeline tick only runs while the player is open.
- Shell motion uses 340 ms to grow, 260 ms to retreat, 180 ms content fades and a 500 ms hover grace period.
- Album art is decoded once per track and shared between the compact and expanded views.

### Removed

- Clipboard history panel, capture listener and related P/Invoke.
- System monitor panel and its CPU, memory and network samplers.
- Screenshot dock shortcut. Use `Win+Shift+S`.
- Wi-Fi, Bluetooth, Quiet and Night-light chips. Quiet mode remains a setting.
- Widget dashboard, weather, accessory battery, call detection, Phone Link and the third-party manifest SDK.
- Tray commands beyond show/hide, pause, settings and exit.
- Ask panel, its local and Groq providers, the AI cards in Settings and the related API keys and tests.
- `system` and `clipboard` from the local API `/show` panels.

### Fixed

- New media could show the previous track because some players raise the SMTC event before their metadata is ready. The media service now re-reads after each change, polls while a session exists and refreshes artwork for six seconds after a track change.
- A failed settings write is reported without losing the previous values.
- Repositioning the island no longer shows it again after idle hide, pause or fullscreen.
- The shelf rejects UNC and device paths.
- List panels keep their scroll state, and high contrast recolors the waveform.
- Thumbnail streams are disposed. Update manifests have an 8 KB cap, and two-part versions parse.
- `--settings` no longer holds the single-instance mutex.
- A paused island stops the audio capture session.

## 1.0.0 - 2026-10-24

- Top-center click-through island with hover, click-to-pin and a global hotkey.
- Media sessions, toast notifications, battery, charging, volume, calendar, downloads, focus timer, file shelf, notes, prompt box and live activity cards.
- Settings with live preview, appearance, placement, motion, accessibility and monitor modes.
- Tray menu, optional startup entry, update check and local crash log.
- Loopback API and widget manifest SDK.
