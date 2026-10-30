# Tolk Island 🏝️

A **Windows Dynamic Island** — a pure-black, top-flush overlay that lives at the top center of your
screen, with concave fillets flaring into the top edge and rounded bottom corners. Built as a native
.NET 8 WPF app.

**No fake data.** Everything on the island is real:

| State | Data source |
|---|---|
| Idle — clock + battery | `DateTime.Now`, `GetSystemPowerStatus` |
| Idle expanded — clock, date, battery | `DateTime.Now`, `GetSystemPowerStatus` |
| Now Playing (compact + player on demand) | Windows media sessions (SMTC) — Spotify, YouTube, browsers, any media app |
| Focus timer | In-island focus picker (15/25/50 min) backed by a real timer service |
| Notification | Real Windows toast notifications via `UserNotificationListener` (polled) |
| Charging | `GetSystemPowerStatus` (fires when the battery actually starts charging) |
| Volume | Windows Core Audio (`IAudioEndpointVolume`) change notifications + default-device watch |
| Calendar | Windows appointment store (`AppointmentManager`), with join links pulled from the event text |
| Downloads | `FileSystemWatcher` on the Downloads folder; growing `.crdownload`/`.part` files report real bytes and rate |
| Mirror (opt-in) | Front camera through WinRT `MediaCapture` / `MediaFrameReader`; preview only, never recorded |

## States

- **Idle** — small pill: time on the left, real battery percentage on the right
- **Idle hidden** — after 10 seconds without interaction the idle pill retracts to a thin top-edge tab; hovering the tab reveals the expanded panel again
- **Idle expanded** — hovering the idle pill opens the big clock, the date, real battery state and four destinations: Shelf, Notes, Calendar and Ask. Enabling Mirror adds a fifth
- **Now Playing compact** — when media starts, the island shows a small pill with album art and a live waveform. It never pops the full player open on its own
- **Now Playing player** — hover the compact pill to open the full player: album art, title/artist, real progress bar and play/pause/skip buttons that *actually control* the media session. Moving away returns it to the compact pill; pinned open from the hotkey or API, it stays put
- **Focus** — hover the idle clock and choose **Focus** to pick 15, 25 or 50 minutes. The session runs as a compact pill; hover it for pause/reset, and a completion card appears when it ends
- **Notification** — a real toast drops the island down for 4 seconds, then it retracts to whatever it was showing; existing toasts are never replayed
- **Charging** — a green battery HUD appears for 3 seconds when the charger starts charging the battery
- **Volume** — a real volume slider appears whenever the system volume or mute state changes
- **Calendar** — hover the clock and pick the calendar icon: the next appointment with a **Join** button for a real Teams / Zoom / Meet / Webex link
- **Ask** — a prompt box that answers through a local Ollama-compatible model or Groq (with your own API key), or copies the prompt when neither is set
- **Mirror** — an opt-in front-camera preview: the island springs open into a wide card, the shutter lifts to the live, horizontally flipped image, and the camera stops the moment the panel closes or the island hides
- **Live activity** — a short card for a download in progress or a finished download. It never cuts off a notification

## Behavior

- **Above everything** (`HWND_TOPMOST`), never takes focus, never appears in Alt+Tab or the taskbar
- **Click-through everywhere outside the black shape** — implemented via per-pixel `WM_NCHITTEST`,
  so the window is actually a fixed-size host and only the island itself is interactive
- **Hovers**: hover the idle pill → the home panel opens; hover the compact player → the *same*
  island slides open into the full player; move away → it slides back
- **Click to pin**: clicking the island background pins the current panel open. Clicking anywhere outside
  the black silhouette unpins it and recedes to Idle (or Idle hidden when idle-hide is on). A low-level
  mouse hook is installed only while pinned, so no global hook runs in normal use.
- **Global hotkey**: `Ctrl+Alt+I` opens and pins the island; pressing it again closes the pin.
- **Launch at startup**: optional per-user Run-key entry, controlled from *Settings → General*.
- **Quick pause**: the tray can pause every overlay and hide the island without stopping the services; unpause restores the current activity.
- **Media stays compact** — new tracks and videos update the pill in place; they never pop the
  full player open automatically
- **Focus stays compact** — starting a session does not leave the expanded notch on screen; it
  sits as a small ring + time pill and only opens when you hover it
- **Auto-hides during fullscreen apps** (games, F11 video) — with hysteresis so alt-tab flashes never make it flicker
- **One continuous motion**: transitions morph the shell from its current shape to the next one
  (~300 ms, spring easing for expand, smooth ease-out for collapse); content crossfades while the shape moves

## Quick actions and gestures

The expanded clock panel is one quiet row of destinations under the clock:

- **Focus** — the green pill starts a 15, 25 or 50 minute session
- **Dock** — file shelf, notes, the next calendar event, and Ask (prompt); Mirror joins only when enabled in Settings
- **Quick launch** — the first three paths from *Settings → Advanced → Quick launch* become letter buttons on the right
- **Shift + wheel** cycles activities: idle → compact media → compact focus while resting, and
  calendar → shelf → notes while a panel is pinned

## Mirror and camera privacy

Mirror is off by default and lives in **Settings → Activities → Island panels**. When enabled, a fifth
dock tile opens a 480 × 280 preview card:

- The front camera is preferred; if Windows exposes no panel metadata the first colour camera is used.
- The image is flipped like a mirror. Frames stay in memory and are painted straight into the island;
  nothing is encoded, saved, sent or added to the log.
- Capture starts only while the panel is visible and pinned. Closing it, hiding the island, pausing or
  switching the feature off releases the camera immediately.
- Permission is owned by Windows. If access is denied, the panel says so and links straight to
  **Settings → Privacy & security → Camera**; if no camera exists, it says that instead of showing a
  black rectangle.
- The **Live** chip and Windows' own camera indicator are the only status signals; the app never
  records silently.

## Monitors, DPI and the webcam area

- **Monitor mode** — primary, the monitor with the active window, one specific monitor, or **all monitors**
- **All monitors** creates one independent shell per screen. Exactly one shell is primary and owns the
  shared Windows services (SMTC, toast listener, local API — see
  [docs/LOCAL_API.md](docs/LOCAL_API.md)), so nothing is opened twice
- **Per-monitor DPI** — `PerMonitorV2` in the manifest, plus `GetDpiForMonitor` and `SetWindowPos` in
  physical pixels, so the clickable silhouette lines up after a scale change or a monitor swap
- **Webcam-area alignment** — `Top offset` in *Placement* drops the island below a laptop camera housing;
  `Floating pill` keeps it off the top edge entirely

## AI answers (local or Groq)

The Ask panel chooses its provider in **Settings → Advanced**:

- **This PC** — an Ollama-compatible endpoint such as `http://127.0.0.1:11434`. The address must be
  loopback or on your own network; public addresses are rejected.
- **Groq** — the hosted OpenAI-compatible API at `https://api.groq.com`. Toggle **Use Groq for the Ask
  panel**, paste a key from [console.groq.com/keys](https://console.groq.com/keys), and pick a model
  (default `llama-3.3-70b-versatile`). Groq takes priority over the local endpoint.

With neither configured, Enter copies the prompt to the clipboard. Groq prompts leave this PC and go to
Groq; local prompts never do. The key is encrypted with Windows DPAPI for your Windows account, so
`settings.json` holds only ciphertext; the key never appears in the local log or the local API. To stop using Groq, turn it off and remove the saved key in Settings.

## Accessibility

- Every control carries an `AutomationProperties.Name`; the panels expose readable row labels
- The global hotkey (`Ctrl+Alt+I` by default) pins the island **and** gives it keyboard focus; `Esc` closes
- **Reduced motion** removes spring overshoot and the waveform pulse while keeping the same states
- **High contrast** maps the palette onto Windows system colours
- Sound and haptics are separate opt-ins; both are off by default

## Getting started

Prerequisites:

- Windows 10 2004 (build 19041) or newer, or Windows 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (the app targets
  `net8.0-windows10.0.22621`)
- No other packages, workloads or admin rights are required

From any checkout location:

```powershell
dotnet run
```

That builds the Debug configuration and starts the island. Alternatively, launch the built exe:

```powershell
.\bin\Debug\net8.0-windows10.0.22621\TolkIsland.exe
```

### Build and verify

```powershell
dotnet build TolkIsland.csproj -c Release
dotnet run --project tests/TolkIsland.Regression -c Debug
```

The regression runner covers settings, notes, update checks, prompts, the local API and log
rotation, and exits non-zero when any case fails. UI-only checks (assistive technology, 200% scaling,
high contrast, multi-monitor) stay manual.

### Publish a single-file exe

```powershell
.\tools\publish.ps1
```

The script publishes Release `win-x64`, self-contained and single-file, into `dist\`. A manual
equivalent is:

```powershell
dotnet publish TolkIsland.csproj -c Release -r win-x64 --self-contained true -o dist
```

### Drive the island from a script

A loopback HTTP API runs at `http://127.0.0.1:5178/` while the app is open. Quick check:

```powershell
curl.exe http://127.0.0.1:5178/health
```

Full route reference, limits, error codes and the compatibility policy:
[docs/LOCAL_API.md](docs/LOCAL_API.md). The base URL and a **Check API** button live in
**Settings → Advanced → Developer**.

## Tray icon

Four commands, nothing else:

- **Show / hide island** → same as the `Ctrl+Alt+I` global hotkey
- **Pause island** → hide all overlays and the island until unpaused
- **Settings…** → open the settings window
- **Exit**

## Architecture notes

- `MainWindow` — fixed-size topmost host window; the `Shell` border inside it morphs between
  state sizes. Animating the shell instead of the window is what keeps transitions smooth
  (layered-window resizes were the source of the original lag).
- `WM_NCHITTEST` hook — returns `HTTRANSPARENT` outside the island shape, `HTCLIENT` inside.
  Pixel-perfect click-through with no ex-style toggling.
- `Services/MediaSessionService` — `GlobalSystemMediaTransportControlsSessionManager` (SMTC):
  current session, media properties, album-art thumbnail, real timeline, playback controls. The
  timeline is extrapolated while playing so the progress bar moves between app updates; refreshes
  from old sessions are discarded.
- `Services/NotificationListenerService` — `UserNotificationListener`, polled once a second (its
  `NotificationChanged` event throws for unpackaged desktop apps). Toast IDs are deduplicated and
  existing toasts are primed at startup; if Windows denies access, the state never triggers.
- `Services/AudioVolumeService` — Core Audio `IAudioEndpointVolume` with a change callback, so the
  volume HUD follows keyboard, mixer and system-UI changes alike. It also watches the default
  output and rebinds its callback when you switch headphones, DACs or monitors.
- `Services/StartupService` — opt-in per-user `Run` registry entry. It stores the current executable path (or the dotnet host plus entry assembly when launched through `dotnet run`) and removes only its own value.
- `Services/FocusTimerService` — Stopwatch-based focus timer with one-shot scheduled checks, so it
  finishes exactly on target and pause/resume cannot make the countdown drift.
- `Services/SystemStatusService` — battery, charging state, clock and formatting helpers.
- `Services/DownloadWatchService` — fires `FileArrived` for finished files and `TransferProgress` for
  growing temporary files, so the island can show real bytes and bytes/second.
- `Services/MirrorService` — shared front-camera reader. One owner count keeps a single capture alive
  while any shell is showing the mirror; frames are copied into a reusable buffer and painted into a
  `WriteableBitmap`, never written to disk.
- `Services/UpdateService` — opt-in release-manifest check. It compares versions and returns a download
  URL; it never downloads or installs anything on its own.
- `Services/PromptService` — answers the Ask panel through a loopback/private Ollama-compatible endpoint
  or Groq's fixed HTTPS endpoint. `Services/SecretProtection` wraps DPAPI so the Groq key is stored as
  ciphertext and can only be read by the signed-in Windows account.
- `App` — owns the shells. It builds one island per screen in *all monitors* mode, keeps a single primary
  shell for the shared services, and rebuilds the windows when the monitor mode changes
  (`ShutdownMode.OnExplicitShutdown`, so a rebuild is not an exit).

## What shipping still needs

These are deliberately **not** faked in the app:

| Topic | Status |
|---|---|
| Code signing | needs a real certificate and a build machine; until then SmartScreen warns |
| Microsoft Store | needs MSIX packaging and a Partner Center listing |
| Licence server / purchase | `LicenseService` verifies a key **format** locally; a signed receipt or server check replaces it |
| Auto-update | the manifest check is real, but installing the update is left to the vendor's installer |
| Crash reporting | unhandled exceptions go to a **local** `crash.log`; nothing is uploaded |
| Analytics | settings expose an opt-in flag; this build has no analytics endpoint |
| Trademark | the product name is your call — the code never uses "Dynamic Island" |
| Stocks / sports | need an API key and a provider decision, so they are not shipped as fake numbers |
| Live captions / translation | needs a local speech model; not in this build |


### A note on the camera
The mirror uses the Windows camera privacy setting, not a separate permission dialog. Windows shows
its camera-in-use indicator while the preview is live. If another app holds the camera exclusively,
the island reports "Camera unavailable" and offers a retry instead of pretending to show a feed.

### A note on Do Not Disturb
Windows exposes no public API for an unpackaged desktop app to switch system-wide Do Not Disturb.
**Quiet mode** in *Settings → General* silences the island's own notification cards; system DND and
Focus remain controlled by Windows.

### A note on notifications
Windows only grants toast-listener access per user consent. If no toasts ever show up, check
**Settings → System → Notifications** and that the app got permission (the prompt appears on first
run). Because the notification change event is not available to unpackaged desktop apps, the app
polls the listener; a new toast normally appears within about a second.
