<#
.SYNOPSIS
  Installs NovaGet silently for the current user, checks the installation and the running app, upgrades it in
  place, uninstalls it and checks that only the user's data is left (section 24). Run by build.ps1 -SmokeTest.

.NOTES
  Changes the current user's profile (installs, then removes NovaGet); meant for CI machines and test VMs.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Setup
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$appDir = Join-Path $env:LOCALAPPDATA 'Programs\NovaGet'
$dataDir = Join-Path $env:APPDATA 'NovaGet'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{8C1B5E7A-3F7D-4C8E-9C2B-6E1F0A4D2B77}_is1'
$hostName = 'com.novaget.nativehost'
$nativeHostKeys = @(
    "HKCU:\Software\Google\Chrome\NativeMessagingHosts\$hostName",
    "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\$hostName",
    "HKCU:\Software\Chromium\NativeMessagingHosts\$hostName",
    "HKCU:\Software\Mozilla\NativeMessagingHosts\$hostName"
)
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$startMenuLink = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\NovaGet.lnk'
$desktopLink = Join-Path ([Environment]::GetFolderPath('Desktop')) 'NovaGet.lnk'
$failures = [System.Collections.Generic.List[string]]::new()

function Check([bool] $condition, [string] $what) {
    if ($condition) {
        Write-Host "    ok   $what" -ForegroundColor DarkGreen
    }
    else {
        Write-Host "    FAIL $what" -ForegroundColor Red
        $failures.Add($what)
    }
}

function Wait-Until([scriptblock] $condition, [int] $seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        if (& $condition) { return $true }
        Start-Sleep -Milliseconds 250
    }
    return [bool] (& $condition)
}

function Install([string] $what) {
    $log = Join-Path $env:RUNNER_TEMP "novaget-setup-$what.log"
    if (-not $env:RUNNER_TEMP) { $log = Join-Path $env:TEMP "novaget-setup-$what.log" }
    $process = Start-Process -FilePath $Setup -PassThru -Wait -ArgumentList @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER',
        '/TASKS="desktopicon,startup,browsers,clipboard"', "/LOG=`"$log`"")
    Check ($process.ExitCode -eq 0) "$what exits with 0 (got $($process.ExitCode); log $log)"
}

function Get-RegistryDefault([string] $key) {
    $item = Get-Item $key -ErrorAction SilentlyContinue
    if ($item) { return $item.GetValue('') }
    return $null
}

function Get-RunValue {
    $item = Get-ItemProperty $runKey -ErrorAction SilentlyContinue
    if ($item -and ($item.PSObject.Properties.Name -contains 'NovaGet')) { return $item.NovaGet }
    return $null
}

function App-Running { [bool] (Get-Process -Name 'NovaGet' -ErrorAction SilentlyContinue) }

function Pipe-Ready { Test-Path '\\.\pipe\NovaGet.Main' }

# Sends one native message through NovaGet.NativeHost.exe, like a browser does, and returns the reply.
function Invoke-NativeHost([string] $json) {
    $start = [System.Diagnostics.ProcessStartInfo]::new((Join-Path $appDir 'NovaGet.NativeHost.exe'), 'chrome-extension://smoke-test/')
    $start.UseShellExecute = $false
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = [System.Diagnostics.Process]::Start($start)
    try {
        $body = [Text.Encoding]::UTF8.GetBytes($json)
        $stdin = $process.StandardInput.BaseStream
        $stdin.Write([BitConverter]::GetBytes([int] $body.Length), 0, 4)
        $stdin.Write($body, 0, $body.Length)
        $stdin.Flush()
        $stdout = $process.StandardOutput.BaseStream
        $header = [byte[]]::new(4)
        $read = 0
        $task = $stdout.ReadAsync($header, 0, 4)
        if (-not $task.Wait(30000)) { return $null }
        $read = $task.Result
        while ($read -lt 4) { $read += $stdout.Read($header, $read, 4 - $read) }
        $length = [BitConverter]::ToInt32($header, 0)
        $reply = [byte[]]::new($length)
        $read = 0
        while ($read -lt $length) { $read += $stdout.Read($reply, $read, $length - $read) }
        return [Text.Encoding]::UTF8.GetString($reply)
    }
    finally {
        $process.StandardInput.Close()
        if (-not $process.WaitForExit(10000)) { $process.Kill() }
    }
}

Write-Host "    Setup: $Setup"
if (App-Running) { throw 'NovaGet is already running on this machine; stop it before the smoke test.' }

# ---------------------------------------------------------------- install
Install 'install'
foreach ($file in @('NovaGet.exe', 'NovaGet.NativeHost.exe', 'ffmpeg\ffmpeg.exe', 'THIRD_PARTY_NOTICES.txt', 'LICENSE.txt',
                    'docs\install-extension.html', 'docs\command-line.html', 'extension\chromium\manifest.json',
                    'extension\firefox\manifest.json', 'native-host\chrome.json', 'native-host\firefox.json',
                    'install-defaults.json', 'unins000.exe')) {
    Check (Test-Path (Join-Path $appDir $file)) "installed $file"
}
Check (-not (Get-ChildItem $appDir -Recurse -Filter '*.pdb' -ErrorAction SilentlyContinue)) 'no .pdb files are installed'

$chrome = Get-Content (Join-Path $appDir 'native-host\chrome.json') -Raw | ConvertFrom-Json
Check ($chrome.name -eq $hostName) 'Chrome host manifest has the host name'
Check ($chrome.path -eq (Join-Path $appDir 'NovaGet.NativeHost.exe')) 'Chrome host manifest points to the installed host'
Check (@($chrome.allowed_origins).Count -eq 1 -and $chrome.allowed_origins[0] -match '^chrome-extension://[a-p]{32}/$') 'Chrome host manifest allows only the NovaGet extension'
$firefox = Get-Content (Join-Path $appDir 'native-host\firefox.json') -Raw | ConvertFrom-Json
Check ($firefox.path -eq (Join-Path $appDir 'NovaGet.NativeHost.exe')) 'Firefox host manifest points to the installed host'
Check (@($firefox.allowed_extensions).Count -eq 1) 'Firefox host manifest allows only the NovaGet extension'

foreach ($key in $nativeHostKeys) {
    $value = Get-RegistryDefault $key
    Check ($value -and (Test-Path $value)) "native messaging key $key"
}
$run = Get-RunValue
Check ($run -eq "`"$(Join-Path $appDir 'NovaGet.exe')`" /tray") 'Run key starts NovaGet in the tray'
Check ("$(Get-RegistryDefault 'HKCU:\Software\Classes\novaget\shell\open\command')" -match '/d "%1"$') 'novaget:// links open NovaGet /d'
$uninstall = Get-ItemProperty $uninstallKey -ErrorAction SilentlyContinue
Check ($null -ne $uninstall) 'uninstall entry (per user, no elevation)'
Check ($uninstall -and $uninstall.DisplayIcon -match 'NovaGet\.exe' -and $uninstall.Publisher) 'uninstall entry has icon and publisher'
Check (Test-Path $startMenuLink) 'Start menu shortcut'
Check (Test-Path $desktopLink) 'desktop shortcut'
$defaults = Get-Content (Join-Path $appDir 'install-defaults.json') -Raw | ConvertFrom-Json
Check ($defaults.monitorClipboard -eq $true -and $defaults.browserIntegration -eq $true) 'install-defaults.json records the tasks'

# ---------------------------------------------------------------- the app and the native host
Start-Process -FilePath (Join-Path $appDir 'NovaGet.exe') -ArgumentList '/tray'
Check (Wait-Until { Pipe-Ready } 60) 'the app starts in the tray and opens its pipe'
$reply = Invoke-NativeHost '{"type":"ping"}'
Write-Host "    native host reply: $reply"
Check ($reply -and ($reply | ConvertFrom-Json).ok -eq $true -and ($reply | ConvertFrom-Json).payload.version) 'the native host relays a browser message to the app'
$settings = Join-Path $dataDir 'settings.json'
$database = Join-Path $dataDir 'novaget.db'
Check (Wait-Until { Test-Path $database } 30) 'the app created its database'

# ---------------------------------------------------------------- upgrade in place (closes the running app)
Install 'upgrade'
Check (-not (App-Running)) 'the upgrade closed the running app'
Check (Test-Path $database) 'the upgrade kept the database'
Check (Test-Path (Join-Path $appDir 'NovaGet.exe')) 'the upgrade left a working installation'

# The app again, so the uninstaller has to close it too.
Start-Process -FilePath (Join-Path $appDir 'NovaGet.exe') -ArgumentList '/tray'
Check (Wait-Until { Pipe-Ready } 60) 'the upgraded app starts'

# ---------------------------------------------------------------- uninstall
$uninstaller = Join-Path $appDir 'unins000.exe'
$process = Start-Process -FilePath $uninstaller -PassThru -Wait -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
Check ($process.ExitCode -eq 0) "uninstall exits with 0 (got $($process.ExitCode))"
# The uninstaller runs from a temporary copy; wait for it to finish removing files.
Check (Wait-Until { -not (Test-Path $appDir) } 90) 'the install folder is removed'
Check (-not (App-Running)) 'the uninstaller closed the running app'
foreach ($key in $nativeHostKeys) {
    Check (-not (Test-Path $key)) "removed $key"
}
Check (-not (Get-RunValue)) 'removed the Run value'
Check (-not (Test-Path 'HKCU:\Software\Classes\novaget')) 'removed the novaget:// protocol'
Check (-not (Test-Path $uninstallKey)) 'removed the uninstall entry'
Check (-not (Test-Path $startMenuLink)) 'removed the Start menu shortcut'
Check (-not (Test-Path $desktopLink)) 'removed the desktop shortcut'
Check (Test-Path $database) 'kept the user''s data (silent uninstall answers No)'

if ($failures.Count -gt 0) {
    throw "Installer smoke test: $($failures.Count) check(s) failed: $($failures -join '; ')"
}
Write-Host '    Installer smoke test passed.' -ForegroundColor Green
