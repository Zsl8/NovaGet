# NovaGet command line

NovaGet accepts the same switches as other popular Windows download managers, so existing scripts keep working.

```
NovaGet.exe [/d URL] [/s] [/p local_path] [/f local_file_name] [/q] [/h] [/n] [/a] [/tray]
            [/startqueue "name"] [/stopqueue "name"]
```

| Switch | Meaning |
|---|---|
| `/d URL` | Download the address. `http`, `https`, `ftp`, `ftps` and `novaget://` links are accepted. |
| `/p local_path` | Folder to save the file in. |
| `/f local_file_name` | File name to save as. |
| `/n` | Silent: no questions and no dialogs. |
| `/q` | Exit NovaGet after this download finishes successfully. |
| `/h` | Hang up the dial-up/VPN connection after a successful download. |
| `/a` | Add the download to the main queue without starting it. |
| `/s` | Start the main download queue. |
| `/tray` | Start hidden in the notification area. |
| `/startqueue "name"` | Start the named queue. |
| `/stopqueue "name"` | Stop the named queue. |
| `/exit` | Ask a running NovaGet to exit (used by the installer). |
| `/cleanup` | Remove scheduled wake tasks and other run-time registrations, then exit (used by the uninstaller). |

Switches are case-insensitive and may start with `/`, `-` or `--`.

If NovaGet is already running, the new process forwards its arguments to the running instance over the
`\\.\pipe\NovaGet.Main` named pipe and exits.

## Examples

```bat
NovaGet.exe /d "https://example.com/file.zip" /p "D:\Downloads" /f "renamed.zip" /n
NovaGet.exe /d "https://example.com/big.iso" /a
NovaGet.exe /s
NovaGet.exe /startqueue "Night downloads"
```
