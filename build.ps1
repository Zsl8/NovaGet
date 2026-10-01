<#
.SYNOPSIS
  Builds, tests and packages NovaGet into dist\NovaGet-Setup-<version>.exe.

.DESCRIPTION
  0. Fetch the ffmpeg LGPL shared build (build/ffmpeg.json; SHA-256 verified) into build\cache
  1. dotnet test (all tests must pass; NOVAGET_FFMPEG lets the stream-merge tests use the fetched ffmpeg)
  2. dotnet publish NovaGet.App (self-contained, single-file, ReadyToRun) -> out\app
  3. dotnet publish NovaGet.NativeHost -> out\app
  4. Copy/zip the browser extension -> out\extension
  5. Bundle ffmpeg.exe and its DLLs (no ffprobe/ffplay) with its license -> out\app\ffmpeg
  6. Copy runtime assets (sounds, lang, docs) -> out\app
  7. iscc /DAppVersion=<version> installer\NovaGet.iss -> dist\
  8. Print the installer's SHA-256

  Code signing: set CERT_PFX (path to a .pfx) and CERT_PASS to sign the installer and uninstaller.

.EXAMPLE
  ./build.ps1
  ./build.ps1 -SkipTests -Runtime win-arm64
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string] $Configuration = 'Release',

    [ValidateSet('win-x64', 'win-arm64')]
    [string] $Runtime = 'win-x64',

    [switch] $SkipTests,
    [switch] $SkipFfmpeg,
    [switch] $SkipInstaller
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$root = $PSScriptRoot
$out = Join-Path $root 'out'
$appOut = Join-Path $out 'app'
$extensionOut = Join-Path $out 'extension'
$dist = Join-Path $root 'dist'
$cache = Join-Path $root 'build/cache'

function Invoke-Step([string] $Name, [scriptblock] $Action) {
    Write-Host ""
    Write-Host "==> $Name" -ForegroundColor Cyan
    & $Action
}

function Invoke-Native([string] $Exe, [string[]] $Arguments, [switch] $HideArguments) {
    if ($HideArguments) {
        Write-Host "    $Exe (arguments hidden)" -ForegroundColor DarkGray
    }
    else {
        Write-Host "    $Exe $($Arguments -join ' ')" -ForegroundColor DarkGray
    }
    & $Exe @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Exe failed with exit code $LASTEXITCODE"
    }
}

function Get-ProductVersion {
    [xml] $props = Get-Content (Join-Path $root 'Directory.Build.props') -Raw
    $node = $props.SelectSingleNode('/Project/PropertyGroup/Version')
    if (-not $node -or -not $node.InnerText.Trim()) { throw 'Version not found in Directory.Build.props' }
    return $node.InnerText.Trim()
}

function Find-Iscc {
    $onPath = Get-Command 'iscc' -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    ) | Where-Object { $_ -and (Test-Path $_) }
    if ($candidates) { return @($candidates)[0] }
    throw 'ISCC.exe (Inno Setup 6) not found. Install it with: choco install innosetup -y'
}

$version = Get-ProductVersion
Write-Host "NovaGet $version ($Configuration, $Runtime)" -ForegroundColor Green

Invoke-Step 'Clean output folders' {
    foreach ($dir in @($out, $dist)) {
        if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
        New-Item -ItemType Directory -Path $dir | Out-Null
    }
    New-Item -ItemType Directory -Path $appOut, $extensionOut, $cache -Force | Out-Null
}

# ffmpeg (LGPL shared build, an external process for HLS/DASH merging). With a pinned sha256 the archive must match
# it. Without one, the archive is checked against the publisher's checksums file and its hash is printed so it can
# be pinned; release (v*) builds refuse to run unpinned. Returns the folder holding ffmpeg.exe, or $null.
function Get-Ffmpeg {
    $pin = Get-Content (Join-Path $root 'build/ffmpeg.json') -Raw | ConvertFrom-Json
    $isRelease = "$env:GITHUB_REF" -like 'refs/tags/v*'
    $pinned = [bool] $pin.sha256
    if (-not $pin.url) {
        if ($isRelease) { throw 'Release builds bundle ffmpeg: set url and sha256 in build/ffmpeg.json.' }
        Write-Warning 'build/ffmpeg.json has no url; ffmpeg is not bundled (streams are saved without merging).'
        return $null
    }
    if ($isRelease -and -not $pinned) { throw 'Release builds need a pinned ffmpeg: set sha256 in build/ffmpeg.json.' }

    $archive = Join-Path $cache ([IO.Path]::GetFileName(([Uri] $pin.url).AbsolutePath))
    try {
        if (-not $pinned -or -not (Test-Path $archive)) {
            Write-Host "    Downloading $($pin.url)"
            Invoke-WebRequest -Uri $pin.url -OutFile $archive -UseBasicParsing
        }
        if ($pinned) {
            $expected = $pin.sha256.ToUpperInvariant()
        }
        else {
            if (-not $pin.checksumsUrl) { throw 'neither sha256 nor checksumsUrl is set' }
            $sums = (Invoke-WebRequest -Uri $pin.checksumsUrl -UseBasicParsing).Content
            if ($sums -is [byte[]]) { $sums = [Text.Encoding]::UTF8.GetString($sums) }
            $name = [IO.Path]::GetFileName($archive)
            $line = ($sums -split "`n") | Where-Object { $_.Trim() -match "^([0-9a-fA-F]{64})\s+\*?$([regex]::Escape($name))$" } | Select-Object -First 1
            if (-not $line) { throw "$name is not listed in $($pin.checksumsUrl)" }
            $expected = $line.Trim().Substring(0, 64).ToUpperInvariant()
        }
        $actual = (Get-FileHash $archive -Algorithm SHA256).Hash
        if ($actual -ne $expected) {
            Remove-Item $archive -Force
            throw "ffmpeg archive hash mismatch. Expected $expected, got $actual"
        }
        if (-not $pinned) {
            Write-Warning "ffmpeg is not pinned. To pin this archive, set sha256 = $actual in build/ffmpeg.json."
        }
        Write-Host "    ffmpeg archive SHA-256: $actual"
    }
    catch {
        if ($pinned) { throw }
        Write-Warning "ffmpeg could not be fetched ($($_.Exception.Message)); streams are saved without merging."
        return $null
    }

    $extract = Join-Path $cache 'ffmpeg-extract'
    if (Test-Path $extract) { Remove-Item $extract -Recurse -Force }
    Expand-Archive $archive -DestinationPath $extract
    $exe = Get-ChildItem $extract -Recurse -File -Filter 'ffmpeg.exe' | Select-Object -First 1
    if (-not $exe) { throw 'The ffmpeg archive has no ffmpeg.exe' }
    return $exe.Directory.FullName
}

$ffmpegBin = $null
if (-not $SkipFfmpeg) {
    Invoke-Step '0. Fetch ffmpeg (LGPL shared build)' {
        $script:ffmpegBin = Get-Ffmpeg
    }
}

if (-not $SkipTests) {
    Invoke-Step '1. Run tests' {
        if ($ffmpegBin) {
            $env:NOVAGET_FFMPEG = Join-Path $ffmpegBin 'ffmpeg.exe'
            Write-Host "    NOVAGET_FFMPEG=$env:NOVAGET_FFMPEG"
        }
        Invoke-Native 'dotnet' @('test', (Join-Path $root 'NovaGet.sln'), '-c', $Configuration, '--logger', 'console;verbosity=normal')
    }
}

$publishArgs = @(
    '-c', $Configuration,
    '-r', $Runtime,
    '--self-contained', 'true',
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:PublishReadyToRun=true',
    '-o', $appOut
)

Invoke-Step '2. Publish NovaGet.App' {
    Invoke-Native 'dotnet' (@('publish', (Join-Path $root 'src/NovaGet.App/NovaGet.App.csproj')) + $publishArgs)
}

Invoke-Step '3. Publish NovaGet.NativeHost' {
    Invoke-Native 'dotnet' (@('publish', (Join-Path $root 'src/NovaGet.NativeHost/NovaGet.NativeHost.csproj')) + $publishArgs)
}

Invoke-Step '4. Package browser extension' {
    # Each flavor = the shared sources (browser-extension/src) + its own manifest, stamped with the app version.
    $shared = Join-Path $root 'browser-extension/src'
    foreach ($flavor in @('chromium', 'firefox')) {
        $manifestPath = Join-Path $root "browser-extension/$flavor/manifest.json"
        $target = Join-Path $extensionOut $flavor
        New-Item -ItemType Directory -Path $target -Force | Out-Null
        Copy-Item (Join-Path $shared '*') $target -Recurse -Force
        $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
        $manifest.version = $version
        $manifest | ConvertTo-Json -Depth 10 | Set-Content -Path (Join-Path $target 'manifest.json') -Encoding utf8NoBOM
        Compress-Archive -Path (Join-Path $target '*') -DestinationPath (Join-Path $extensionOut "novaget-$flavor.zip") -Force
    }
}

if ($ffmpegBin) {
    Invoke-Step '5. Bundle ffmpeg' {
        # The app runs only ffmpeg.exe; ffprobe/ffplay and the static libraries stay out of the installer.
        $ffmpegOut = Join-Path $appOut 'ffmpeg'
        New-Item -ItemType Directory -Path $ffmpegOut -Force | Out-Null
        Copy-Item (Join-Path $ffmpegBin 'ffmpeg.exe') $ffmpegOut
        Get-ChildItem $ffmpegBin -File -Filter '*.dll' | ForEach-Object { Copy-Item $_.FullName $ffmpegOut }
        $package = Split-Path $ffmpegBin -Parent
        Get-ChildItem $package -File | Where-Object { $_.Name -match '^(LICENSE|COPYING)' } |
            Select-Object -First 1 | ForEach-Object { Copy-Item $_.FullName (Join-Path $ffmpegOut 'LICENSE.txt') }
        $pin = Get-Content (Join-Path $root 'build/ffmpeg.json') -Raw | ConvertFrom-Json
        $archive = Join-Path $cache ([IO.Path]::GetFileName(([Uri] $pin.url).AbsolutePath))
        $hash = (Get-FileHash $archive -Algorithm SHA256).Hash
        Set-Content -Path (Join-Path $ffmpegOut 'VERSION.txt') -Value "$($pin.version)`n$($pin.url)`nSHA-256 $hash"
        $size = (Get-ChildItem $ffmpegOut -File | Measure-Object Length -Sum).Sum / 1MB
        Write-Host ("    ffmpeg bundled: {0:N1} MB" -f $size)
    }
}

Invoke-Step '6. Copy runtime assets' {
    foreach ($asset in @('sounds', 'lang')) {
        $source = Join-Path $root "assets/$asset"
        if (Test-Path $source) {
            $files = Get-ChildItem $source -File -Recurse | Where-Object { $_.Name -ne '.gitkeep' -and $_.Extension -ne '.md' }
            if ($files) {
                New-Item -ItemType Directory -Path (Join-Path $appOut $asset) -Force | Out-Null
                $files | ForEach-Object { Copy-Item $_.FullName (Join-Path $appOut $asset) }
            }
        }
    }
    New-Item -ItemType Directory -Path (Join-Path $appOut 'docs') -Force | Out-Null
    Copy-Item (Join-Path $root 'docs/install-extension.html') (Join-Path $appOut 'docs')
    Copy-Item (Join-Path $root 'docs/command-line.md') (Join-Path $appOut 'docs') -ErrorAction SilentlyContinue
}

if (-not $SkipInstaller) {
    Invoke-Step '7. Build installer (Inno Setup)' {
        $iscc = Find-Iscc
        $isccArgs = @("/DAppVersion=$version", '/Qp')
        $signing = $false
        if ($env:CERT_PFX -and (Test-Path $env:CERT_PFX)) {
            $signing = $true
            Write-Host '    Code signing enabled'
            $sign = 'signtool sign /fd sha256 /tr http://timestamp.digicert.com /td sha256 /f $q' + $env:CERT_PFX + '$q /p $q' + $env:CERT_PASS + '$q $f'
            $isccArgs += @('/DSignEnabled', "/Ssigntool=$sign")
        }
        else {
            Write-Host '    CERT_PFX not set: installer is not signed'
        }
        $isccArgs += (Join-Path $root 'installer/NovaGet.iss')
        Invoke-Native $iscc $isccArgs -HideArguments:$signing
    }

    Invoke-Step '8. Installer checksum' {
        $setup = Join-Path $dist "NovaGet-Setup-$version.exe"
        if (-not (Test-Path $setup)) { throw "Installer not found: $setup" }
        $hash = (Get-FileHash $setup -Algorithm SHA256).Hash
        Set-Content -Path "$setup.sha256" -Value "$hash  $([IO.Path]::GetFileName($setup))"
        Write-Host "    $([IO.Path]::GetFileName($setup))"
        Write-Host "    SHA-256: $hash" -ForegroundColor Green
    }
}

Write-Host ""
Write-Host 'Build complete.' -ForegroundColor Green
