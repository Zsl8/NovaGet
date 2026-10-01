<#
.SYNOPSIS
  Builds, tests and packages NovaGet into dist\NovaGet-Setup-<version>.exe.

.DESCRIPTION
  1. dotnet test (all tests must pass)
  2. dotnet publish NovaGet.App (self-contained, single-file, ReadyToRun) -> out\app
  3. dotnet publish NovaGet.NativeHost -> out\app
  4. Copy/zip the browser extension -> out\extension
  5. Download the pinned ffmpeg LGPL shared build (SHA-256 verified) -> out\app\ffmpeg
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

if (-not $SkipTests) {
    Invoke-Step '1. Run tests' {
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

if (-not $SkipFfmpeg) {
    Invoke-Step '5. Fetch pinned ffmpeg (LGPL shared build)' {
        $pin = Get-Content (Join-Path $root 'build/ffmpeg.json') -Raw | ConvertFrom-Json
        if (-not $pin.url -or -not $pin.sha256) {
            Write-Warning 'build/ffmpeg.json has no pinned url/sha256; ffmpeg is not bundled (stream merging will be unavailable).'
            return
        }
        $archive = Join-Path $cache ([IO.Path]::GetFileName(([Uri] $pin.url).AbsolutePath))
        if (-not (Test-Path $archive)) {
            Write-Host "    Downloading $($pin.url)"
            Invoke-WebRequest -Uri $pin.url -OutFile $archive -UseBasicParsing
        }
        $actual = (Get-FileHash $archive -Algorithm SHA256).Hash
        if ($actual -ne $pin.sha256.ToUpperInvariant()) {
            Remove-Item $archive -Force
            throw "ffmpeg archive hash mismatch. Expected $($pin.sha256), got $actual"
        }
        $extract = Join-Path $cache 'ffmpeg-extract'
        if (Test-Path $extract) { Remove-Item $extract -Recurse -Force }
        Expand-Archive $archive -DestinationPath $extract
        $bin = Get-ChildItem $extract -Recurse -Directory -Filter 'bin' | Select-Object -First 1
        if (-not $bin) { throw 'ffmpeg archive has no bin folder' }
        $ffmpegOut = Join-Path $appOut 'ffmpeg'
        New-Item -ItemType Directory -Path $ffmpegOut -Force | Out-Null
        Copy-Item (Join-Path $bin.FullName '*') $ffmpegOut
        Get-ChildItem $extract -Recurse -File | Where-Object { $_.Name -match '^(LICENSE|COPYING)' } |
            Select-Object -First 1 | ForEach-Object { Copy-Item $_.FullName (Join-Path $ffmpegOut 'LICENSE.txt') }
        Set-Content -Path (Join-Path $ffmpegOut 'VERSION.txt') -Value "$($pin.version)`n$($pin.url)`nSHA-256 $($pin.sha256)"
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
