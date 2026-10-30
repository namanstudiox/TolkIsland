# Tolk Island local API

A loopback-only HTTP surface for scripts and tools. It can post a live activity, open a panel, start a
focus session and drive playback — the same actions the island itself performs.

- **Base URL:** `http://127.0.0.1:5178/`
- **Binding:** `127.0.0.1` only. Nothing on your network can reach it.
- **Format:** JSON in, JSON out. Every response is `application/json; charset=utf-8`.
- **Availability:** it runs in the primary island shell. If the port is taken (URL ACL or another
  process), the rest of the island still works and the API is simply absent. The exact URL is shown
  in **Settings → Advanced → Developer**.

## Authentication

Every route except `GET /health` requires the per-install access token in the `X-Tolk-Token` header.
The token is generated on first start and saved to `%LOCALAPPDATA%\TolkIsland\api-token.txt`; the
same value is shown in **Settings → Advanced → Developer** with a copy button. Delete that file to
rotate the token on the next launch.

The listener also refuses requests that carry an `Origin` header (browsers attach it to cross-site
calls) and requests whose `Host` header is not `127.0.0.1:<port>`, so a web page or a DNS-rebinding
trick cannot reach the routes. `/health` stays open for uptime checks.

## Quickstart (about two minutes)

1. Start Tolk Island (`dotnet run`, or the published exe). The island appears at the top of the screen.
2. In a new PowerShell window, check liveness (no token needed):

```powershell
curl.exe http://127.0.0.1:5178/health
```

```json
{"ok":true,"app":"TolkIsland","version":"1.0.0","apiVersion":2,"uptimeSeconds":4,"paused":false}
```

3. Read the access token and make the island react:

```powershell
$token = Get-Content "$env:LOCALAPPDATA\TolkIsland\api-token.txt"
curl.exe -X POST http://127.0.0.1:5178/activity `
  -H "Content-Type: application/json" `
  -H "X-Tolk-Token: $token" `
  -d "{\"title\":\"Build finished\",\"body\":\"tolk-island · 0 errors\",\"seconds\":6}"
```

```json
{"ok":true}
```

The island drops down for six seconds with the card, then returns to what it was showing. If the
island is paused, the call returns `409 paused` instead.

## Limits

| Limit | Value |
|---|---|
| Concurrent requests | 8 in flight; the ninth gets `429 busy` immediately |
| Request body | 8 KB (declared `Content-Length` or chunked) |
| Auth | `X-Tolk-Token` header on every route except `GET /health` |
| Route timeout | none — handlers return quickly or are gated |
| `activity` card | 1–60 seconds, default 5 |
| `focus` session | 1–180 minutes, default 25 |
| Text fields | title 120 chars, body 600 chars, panel/action 32 chars |

Bodies must be a JSON **object**; a missing body is treated as `{}`.

## Pause and feature gates

Pausing the island (`tray → Pause island`) refuses every command with `409 paused`. Disabling the
feature behind a command refuses it with `403 disabled`. Policy is uniform across commands:

| Command | Feature switch in Settings |
|---|---|
| `POST /activity` | Notifications |
| `POST /focus` | Focus |
| `POST /media` | Media |
| `POST /shelf` | File shelf |
| `POST /show` with `shelf` / `media` / `focus` | that feature |
| `POST /show` with `notes`, `idle` | always allowed |

`GET /health` needs no token; `/status` and every command require it. Both are answered even when
paused (the token check still applies to `/status`).

## Routes

All routes except `GET /health` require the `X-Tolk-Token` header shown above.

### `GET /health`

Liveness plus the contract version. Use it as the smoke check.

```json
{"ok":true,"app":"TolkIsland","version":"1.0.0","apiVersion":2,"uptimeSeconds":12,"paused":false}
```

### `GET /status`

The same payload with additional detail and the machine's local time.

```json
{
  "app": "TolkIsland",
  "version": "1.0.0",
  "apiVersion": 2,
  "uptimeSeconds": 12,
  "paused": false,
  "time": "2026-10-28T09:14:03.5120000+01:00"
}
```

### `POST /activity`

Shows a live-activity card. Title, body, or both.

```json
{"title": "Render complete", "body": "final.mp4 · 48 MB", "seconds": 6}
```

| Key | Type | Notes |
|---|---|---|
| `title` | string | up to 120 chars; defaults to `Local activity` when only `body` is sent |
| `body` | string | up to 600 chars |
| `seconds` | int | 1–60, default 5 |

`202 {"ok":true}` on acceptance. `400 title_required` when both fields are empty.
`409 paused` / `403 disabled` per the gates above.

### `POST /focus`

Starts a countdown, or a stopwatch when `"stopwatch": true`.

```json
{"minutes": 25}
```

```json
{"stopwatch": true}
```

| Key | Type | Notes |
|---|---|---|
| `minutes` | int | 1–180, default 25; ignored for a stopwatch |
| `stopwatch` | bool | counts up instead of down |

`202 {"ok":true}`. Gated by **Focus**.

### `POST /media`

Controls the current media session.

```json
{"action": "playpause"}
```

| Key | Type | Notes |
|---|---|---|
| `action` | string | `playpause`, `next`, `previous` or `seek` |
| `position` | int | seconds, 0–86400; only used by `seek` |

`202 {"ok":true}`. `400 unknown_action` for anything else. Gated by **Media**.

### `POST /show`

Opens a panel, exactly as if you clicked it.

```json
{"panel": "notes"}
```

Allowed panels: `shelf`, `notes`, `media`, `idle`, `focus`.
`400 unknown_panel` otherwise. `202 {"ok":true}`.

### `POST /shelf`

Drops a path onto the file shelf. The island does not read the file; it only holds the path.

```json
{"path": "C:\\Users\\you\\Desktop\\report.pdf"}
```

| Key | Type | Notes |
|---|---|---|
| `path` | string | required, up to 1024 chars |

`400 path_required` when empty. `202 {"ok":true}`. Gated by **File shelf**.

## Errors

Every failure is one envelope:

```json
{"ok": false, "code": "unknown_panel", "message": "Panel must be shelf, notes, media, idle or focus."}
```

`message` is prose for humans. Branch on `code` and the status, never on the message.

| Status | `code` | Meaning |
|---|---|---|
| 400 | `bad_json` | body is not a JSON object |
| 400 | `title_required` | activity with no title and no body |
| 400 | `path_required` | shelf without a path |
| 400 | `unknown_action` | media action not in the list |
| 400 | `unknown_panel` | show panel not in the list |
| 401 | `unauthorized` | missing or wrong token, or a request carrying `Origin` |
| 403 | `disabled` | that feature is off in Settings |
| 404 | `not_found` | no route matches the method and path |
| 409 | `paused` | the island is paused |
| 413 | `body_too_large` | body larger than 8 KB |
| 429 | `busy` | eight requests are already in flight |
| 500 | `internal` | unexpected failure; details go to the local log |

## Request log

While the API runs, each handled request appends one line to the local diagnostics log:

```
[2026-10-28 09:14:03Z] api POST /activity -> 202
```

The log (`%LOCALAPPDATA%\TolkIsland\crash.log`) rotates at 1 MB with three files. Bodies and query
strings are never logged. Request logging can be disabled by setting
`DiagnosticsService.RequestLoggingEnabled = false` in code.

## Compatibility policy (API v2)

- `apiVersion` is the contract version and is independent of the app version. It is reported by
  `GET /health` and `GET /status`.
- **v2 added the `X-Tolk-Token` requirement**, the `Origin`/`Host` checks and the `401 unauthorized`
  response. A v1 client that called routes without a token must read the token before upgrading.
- **Within a version, changes are additive-only.** New routes, new optional request keys and new
  response fields can appear in any release. A client that ignores unknown fields keeps working.
- Removing a route or key, renaming one, changing a status code or changing the meaning of an existing
  field requires a new `apiVersion`. A v2 client can assert `apiVersion == 2` and refuse to guess.
- Unknown *request* fields are ignored, so forward-compatible clients can send more.
- `/status.version` is the app version and may change on every release; do not use it for feature
  detection.

## Time to hello world

The documented path to a first successful call:

1. Start the app — island visible. *(~20 s)*
2. `curl.exe http://127.0.0.1:5178/health` → `ok: true`. *(~20 s)*
3. `POST /activity` with a title and the `X-Tolk-Token` header. *(~30 s)*
4. Watch the island show the card. *(~5 s)*
5. Stop the timer. **Target: under 2 minutes.**

If step 2 fails: check that the app is running, that no other process owns port 5178, and look at the
last `api` lines in `crash.log`.

## Verifying it yourself

```powershell
# contract and policy regression, no UI automation
dotnet run --project tests/TolkIsland.Regression -c Debug
```
