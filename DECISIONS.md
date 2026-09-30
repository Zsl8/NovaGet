# Design decisions

Choices the specification left open, or where it had to be interpreted. Newest entries are appended per milestone.

## Milestone 1 — Skeleton

| # | Decision | Why |
|---|---|---|
| D1 | `EnableWindowsTargeting=true` in `Directory.Build.props`. | Lets the WPF app compile on Linux/macOS dev boxes and agents. The product still targets Windows only. |
| D2 | No separate `NovaGet.Cli` project. Command-line parsing lives in `NovaGet.Core.CommandLine` and `NovaGet.exe` handles the switches. | The spec marks the CLI project optional; one parser in Core is testable and shared with the pipe forwarding. |
| D3 | Dapper on top of Microsoft.Data.Sqlite. | Removes hand-written column mapping for ~30-column rows. Apache-2.0. Not trimmed, so reflection is fine. |
| D4 | Dates are stored as ISO-8601 UTC text and read back with `DateTimeKind.Utc`. | Readable in any SQLite tool; no time-zone ambiguity. |
| D5 | Category `defaultSaveDir = NULL` means "the built-in default" (`Downloads\<Category>`, `Downloads` for General). | Keeps the database valid when the profile folder moves and in portable mode. |
| D6 | Built-in categories and queues can't be renamed or deleted (enforced in the repositories). | The tree, Options and scheduler refer to them by identity; the spec forbids deleting them. |
| D7 | Deleting a user category moves its downloads to General; deleting a user queue removes its downloads from the queue. | Never lose list entries by deleting a container. |
| D8 | The pipe uses the same 32-bit little-endian length prefix + UTF-8 JSON framing as native messaging. | The native host relays messages without re-encoding; one framing implementation and test suite. |
| D9 | Pipe security uses `PipeOptions.CurrentUserOnly`. | On Windows this sets a current-user-only ACL on the server and verifies the server owner on the client (spec §22). |
| D10 | The single-instance mutex is the session-local `NovaGet.SingleInstance` (same name as the installer's `AppMutex`). The pipe name `NovaGet.Main` is fixed by the spec. | Per-user app. On a multi-session server a second user's instance can't create the pipe; it logs the error and keeps working without forwarding. |
| D11 | A second instance calls `AllowSetForegroundWindow(ASFW_ANY)` before forwarding. | Lets the running instance bring its window to the front when the user starts NovaGet again. |
| D12 | Settings JSON uses camelCase and string enums; unknown or missing values fall back to defaults and are clamped by `SettingsNormalizer`. A corrupt `settings.json` is renamed to `settings.json.corrupt` and the backup is used. | Hand-editable, forward-compatible, and the good backup is never rotated over by a bad file. |
| D13 | "Force capture" key defaults to **Ctrl+Alt** (spec says "Alt+Click"). | The spec's default conflicts with the "Prevent capture" key (Alt). |
| D14 | Speed limiter "Apply to scheduler queues only" defaults to **on**. | Shown checked in the spec (§11). |
| D15 | Double-clicking a completed download opens **Properties** by default. | Classic download-manager behavior; never runs a downloaded file without an explicit Open. |
| D16 | The app targets `net8.0-windows10.0.17763.0`. | 17763 is Windows 10 1809, the minimum OS, and this TFM enables toast notifications with action buttons (§16). |
| D17 | Non-Windows builds use `DevOnlySecretProtector` (base64, clearly named). | Only so the data layer and engine can be tested cross-platform. Windows always uses DPAPI (CurrentUser). |
| D18 | Added `/exit` and `/cleanup` switches. | The installer closes a running instance with `/exit`; the uninstaller runs `/cleanup` to remove wake tasks and run-time registrations. `/cleanup` never shows UI. |
| D19 | The installer writes `{app}\install-defaults.json` (startup, browser integration, clipboard monitor task choices). | The wizard can't edit per-user JSON in `%APPDATA%`; the app applies these as first-run defaults. |
| D20 | Installer UI is English only. | Inno Setup's Arabic translation is not an official one; the app itself ships Arabic. |
| D21 | ffmpeg is pinned in `build/ffmpeg.json` (url + SHA-256), downloaded and verified by `build.ps1`. An empty pin skips bundling with a warning. | Reproducible builds. The pin is filled in with the stream milestone. |
| D22 | The Chromium extension ID is fixed by a public `key` (`browser-extension/extension-ids.json`); the private key was not kept. A test checks the ID derivation and that the installer uses the same IDs. | Unpacked installs get a stable ID, so `allowed_origins` in the native host manifest always matches. Store publishing is out of scope. |
| D23 | Embedded PDBs for all assemblies. | Line numbers in logged stack traces without loose `.pdb` files in the install folder. |
| D24 | Closing the main window hides it to the tray (configurable); `Alt+F4` and Tasks → Exit quit. | Spec §6.1. |

## Milestone 2 — Engine v1

| # | Decision | Why |
|---|---|---|
| D25 | Probe: if HEAD succeeds with a length but no `Accept-Ranges: bytes`, a `GET Range: bytes=0-0` still runs to confirm resume support. | Many servers honor ranges without advertising them; a 206 is the reliable signal. The spec's HEAD-then-GET order is otherwise kept. |
| D26 | Requests are forced to HTTP/1.1. | Each segment must be its own TCP connection; HTTP/2 would multiplex every "connection" onto one socket. |
| D27 | One temp file handle with positional writes (`RandomAccess.WriteAsync`) instead of one `FileStream` per segment. The file is marked sparse on NTFS and sized up front. | Same result as per-segment streams at offsets (no merge step) with one handle. Sparse allocation avoids Windows zero-filling the gap when a far segment writes first. |
| D28 | Checkpoints snapshot the *written* offsets, flush the file to disk, then save the snapshot. | Anything written after the snapshot is simply fetched again after a crash or power loss, so a resumed file can't contain holes. |
| D29 | The retry limit counts consecutive failures without progress; any received data resets it. Backoff is 3 s, 6 s, 12 s … capped at 30 s, and the Options "Retry delay" default is 3 s. | A long download over a flaky link may see many drops and should still finish. §4.5's 3 s base wins over the "5" shown in §9.5. |
| D30 | Cookie and Authorization headers are not sent when a redirect goes to a different host. | They belong to the original site; forwarding them to a CDN or third party leaks credentials. |
| D31 | Requests send `Accept-Encoding: identity` and never decompress. | Byte ranges must refer to the stored file, not a compressed transfer encoding. |
| D32 | If the destination file exists at completion, the download is saved as `name (2).ext`, `name (3).ext` … | Never overwrite silently; the "Duplicate download" option governs adding duplicate links, not this. |
| D33 | "Not enough disk space" leaves the download **Paused** (with the message), not in Error. | §4.4 says "show the message and pause"; Resume works once space is freed. |
| D34 | The engine only probes on the first start of a download that was never probed. On resume, `If-Range` (strong ETag, else Last-Modified) plus a size check detect a changed file. A different size before any data was kept simply updates the size. | Avoids a redundant request on every resume while still never mixing two versions of a file. |
| D35 | A resumed range request answered with 200: "file changed" if validators were sent, otherwise "resume not supported". Downloads from servers without range support restart from byte 0 on every retry. | Matches §4.5 and never appends a full body to a partial file. |
| D36 | "Restart" clears progress and validators and starts again from the *original* address. | A changed file may have a new name, size and redirect target. |
| D37 | Temp files are named `<TempDir>\<id>\<name>.ngpart`, with the name part capped at 120 characters. | Keeps temp paths well under legacy path limits for long server names. |

## Milestone 3 — Engine v2

| # | Decision | Why |
|---|---|---|
| D38 | A segment is split only if both halves would be at least `MinSegmentSize` (64 KB), i.e. at least 128 KB remain. | One reading of "do not split below 64 KB remaining" that never creates tiny segments. |
| D39 | A new connection is opened when the previous newest one receives its first bytes, only if the download has resume support, a known size and splittable work. | "Open more connections one at a time" (§4.3), and no idle connections. |
| D40 | A free connection takes an unowned segment first (e.g. one left by a closed connection or restored from a checkpoint), and only then splits the largest active one. | Nothing is left behind, and resumed downloads with many saved segments continue all of them. |
| D41 | On 429/503/connection refused while other connections are running, that connection closes, its segment returns to the pool, and the cap for this download and the host (for the session) becomes the number still open. With only one connection, the error is retried normally. | §4.3. The pool guarantees the unfinished range is picked up by a surviving connection. |
| D42 | A range request answered with 200: if the response's ETag/Last-Modified differ from ours (or are missing after an If-Range), the file changed → "restart?" prompt. If they match, the server ignores ranges → resume capability becomes "No" and the download restarts automatically with one connection from byte 0. | Both paths produce a correct file; the prompt only appears when the content really changed. |
| D43 | Speed limiting: debt-based token buckets (per download and global), burst allowance of 0.1 s. While a limit is active, each read is capped at about 0.1 s worth of the rate. | Accurate long-run rate across any number of connections, without an initial burst of one buffer per connection. |
| D44 | "Apply to scheduler queues only" is decided per download by how it was started (`Start(id, startedByQueue)`). Changing the global limit applies immediately to running downloads. | Matches §11 and the tray presets. |
| D45 | Per-download connection count: download override → Options → Connection exception (exact host, then `*.domain` wildcard) → default; then capped by the session's learned host limit; always 1 without resume support or known size. | §4.3 ordering. |
