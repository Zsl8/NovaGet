# NovaGet QA checklist

Manual pass before a release (section 23), on a clean Windows 11 VM and a Windows 10 1809+ VM, x64.
Automated coverage is noted in *italics*: CI runs the engine, data, IPC, packaging and WPF smoke tests, renders the
main windows in the dark theme and in Arabic, and installs, upgrades and uninstalls the installer
(`installer/smoke-test.ps1`). Tick a box only after doing the step by hand.

Record: Windows version, display scale, browser versions, installer SHA-256, date, tester.

## 1. Install

- [ ] `NovaGet-Setup-x.y.z.exe` runs without an admin prompt (per-user) and installs to `%LOCALAPPDATA%\Programs\NovaGet`. *(smoke test)*
- [ ] "Install for all users" from the first page asks for elevation and installs to `Program Files\NovaGet`.
- [ ] Wizard pages: Welcome → License (MIT) → folder → Tasks (desktop shortcut ✓, startup ✓, browsers ✓, clipboard ☐) → Ready → Install → Finish (Launch NovaGet ✓, Open browser extension setup page ✓).
- [ ] Finish starts NovaGet (tray icon present) and opens `docs\install-extension.html`.
- [ ] SmartScreen shows the publisher when the build is signed.
- [ ] Re-running Setup while NovaGet runs offers to close it, closes it, and upgrades in place; downloads and settings are kept. *(smoke test, silent)*
- [ ] Sign out and in: NovaGet starts in the tray when "Launch at startup" was ticked.

## 2. Main window

- [ ] Every **Tasks** item: Add new download (Ctrl+N), Add batch download, Add batch download from clipboard, Export (EF2/text), Import (EF2/text), Run site grabber, Grabber projects, Exit.
- [ ] Every **File** item on a selected download: Download now, Stop, Remove, Redownload, Move up/down in queue, Open, Open with, Open folder, Move/Rename, Properties.
- [ ] Every **Downloads** item: Pause all, Stop all, Delete all completed, Find (Ctrl+F) / Find next (F3), Scheduler, Start queue ▸, Stop queue ▸, Speed limiter ▸ (on, off, settings), Options.
- [ ] Every **View** item: Hide categories, Toolbar ▸ (large, small, hide labels, Customize…, Toolbar theme ▸ Default/Monochrome), Arrange files ▸ (each key, ascending/descending), Show/hide columns…, Language ▸ (English/العربية, restart note), Show drop target, Show tray speed graph.
- [ ] Every **Help** item: Contents (F1), FAQ, Command line switches, Check for updates, About (version matches the installer).
- [ ] Every toolbar button, including the ▾ parts of Delete, Start Queue and Stop Queue; disabled states follow the selection.
- [ ] The list context menu: every item, including Refresh download address and Add to queue ▸.
- [ ] Category tree: All/Unfinished/Finished/categories/Grabber projects/queues filter the list; drag downloads onto a category or queue.
- [ ] Columns: resize, reorder, sort by each, hide/show; the layout survives a restart.
- [ ] 10,000 downloads scroll smoothly. *(MainWindowSmokeTests)*

## 3. Dialogs

- [ ] Add URL: clipboard address prefilled, login fields, invalid address message.
- [ ] Download File Info: category changes the folder, "Remember this path", Download later ▸ queue, Start download.
- [ ] Progress dialog: Download status / Speed limiter / Options on completion tabs, segment bar, connection table, Pause/Resume/Cancel, Show/Hide details.
- [ ] Download complete: Open / Open with / Open folder / Close, "Don't show again".
- [ ] Properties: every field, address swap, checksum Verify (match and mismatch), "Ignore certificate errors" warning.
- [ ] Move/Rename, Duplicate download (each choice), Batch, Download all links, Choose video quality, Find, Speed limiter, Tell a friend, About.
- [ ] Every dialog: Tab order follows the layout, Esc cancels, Enter accepts, Alt+underlined letter works. *(AccessibilityTests: names)*

## 4. Options (all 10 tabs)

- [ ] General, File Types, Save To, Downloads, Connection, Proxy/Socks, Site Logins, Dial Up/VPN, Sounds, Advanced: change one setting on each, OK, reopen, and see it kept; Cancel discards.
- [ ] Color theme: Light (classic), Dark, Use Windows setting — switching applies at once; title bars follow the Windows app mode.
- [ ] Export settings / Import settings / Reset all; Proxy "Test".
- [ ] Sounds: each ▶ plays.

## 5. Queues and scheduler

- [ ] Scheduler window: new queue, delete, files tab reorder, daily and one-time schedules, start/stop times, retries, after-finish actions (open file, hang up, exit, turn off with countdown), wake the computer.
- [ ] Synchronization queue: changed files are downloaded again.

## 6. Tray

- [ ] Every tray menu item: Open NovaGet, Add new download, Speed limiter ▸, Pause all, Resume all, Start/Stop queue ▸, Show drop target, Monitor clipboard, Exit.
- [ ] Tooltip shows active downloads and speed; animated/colored icon while downloading; speed graph when enabled.
- [ ] Notifications: download complete (Open / Open folder buttons only open after the click), failed, checksum mismatch, queue started/finished.

## 7. Browser integration (Chrome, Edge, Firefox)

- [ ] Load the extension as described in `docs\install-extension.html`; its popup shows "Connected".
- [ ] Clicking a `.zip` link: the browser download is cancelled and File Info appears with the referrer and cookies.
- [ ] Alt+click is not captured; Alt+Ctrl+click forces capture; excluded sites/addresses are not captured.
- [ ] Context menus: Download with NovaGet, Download all links with NovaGet, Download video with NovaGet.
- [ ] Video panel on a page with `<video src=mp4>` and on an HLS page; a DRM page shows "Protected content". *(Playwright e2e)*
- [ ] Clipboard monitor and drop target accept links.

## 8. Video and streams

- [ ] HLS and DASH: quality list, audio track choice, merge with ffmpeg; the file plays.
- [ ] DRM-protected stream: "This stream is protected and cannot be downloaded."

## 9. Site grabber

- [ ] Each template, login via browser, offline browsing copy opens locally, Run again offers only new/changed files.

## 10. Language, display, accessibility

- [ ] Arabic: every window right-to-left, no English left in menus/dialogs, addresses and paths left-to-right, sizes read "4 كيلوبايت", Alt shortcuts work. *(ThemeTests renders and Arabic coverage test)*
- [ ] 150% and 200% scaling, and moving the window between monitors with different scaling: crisp icons, nothing clipped.
- [ ] High contrast: the classic theme follows the high-contrast colors.
- [ ] Narrator reads every input's label and every toolbar button.

## 11. Reliability (section 19)

- [ ] 1 Gbps LAN, 16 connections: ≥ 90% of line speed, CPU < 10% on 4 cores.
- [ ] Kill NovaGet in Task Manager mid-download, restart: resumes, SHA-256 matches the source. *(engine tests)*
- [ ] 200 queued downloads, 10 at once, 2 hours: stable, memory growth < 50 MB.

## 12. Uninstall

- [ ] Uninstall from Settings → Apps while NovaGet runs: it offers to close it.
- [ ] "Remove your download list and settings too?" — No keeps `%APPDATA%\NovaGet`; Yes removes it and `%LOCALAPPDATA%\NovaGet`.
- [ ] Nothing else is left: program folder, shortcuts, Run values, native messaging keys, `novaget://`, scheduled wake tasks. *(smoke test)*
