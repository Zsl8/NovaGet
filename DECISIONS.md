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
