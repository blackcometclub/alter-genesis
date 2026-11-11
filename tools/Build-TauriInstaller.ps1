[CmdletBinding()]
param([string]$GoogleClientFile)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$appDirectory = Join-Path $repositoryRoot 'app'
$configPath = Join-Path $appDirectory 'src-tauri\tauri.conf.json'
$config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
$version = [string]$config.version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'A numeric x.y.z version is required for this Windows installer.' }
if (@($config.bundle.targets).Count -ne 1 -or @($config.bundle.targets)[0] -ne 'nsis') {
    throw 'This build is for NSIS only; review binary patching before adding another bundle format.'
}
$hostInfo = & rustc -vV
if ($LASTEXITCODE -ne 0 -or $hostInfo -notcontains 'host: x86_64-pc-windows-msvc') {
    throw 'Build this installer on the Windows x64 MSVC host.'
}

if (!$GoogleClientFile) { $GoogleClientFile = Join-Path $repositoryRoot 'artifacts\oauth-reminder-build\client.json' }
$resolvedClient = (Resolve-Path -LiteralPath $GoogleClientFile -ErrorAction Stop).Path
$clientJson = Get-Content -LiteralPath $resolvedClient -Raw | ConvertFrom-Json
if ($clientJson.web) { throw 'Use a Desktop OAuth client, not a Web client.' }
$desktopClient = if ($clientJson.installed) { $clientJson.installed } else { $clientJson }
if (!$desktopClient.client_id -or !$desktopClient.client_id.EndsWith('.apps.googleusercontent.com') -or !$desktopClient.client_secret) {
    throw 'Desktop Google client configuration is missing or invalid.'
}
# Validate without printing credentials. The client is embedded by the existing build.rs.
$clientJson = $null
$desktopClient = $null

$output = Join-Path $repositoryRoot ('artifacts\tauri-installer\' + $version + '\' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
if (Test-Path -LiteralPath $output) { throw 'This build output already exists; preserve it and run again.' }
New-Item -ItemType Directory -Path $output | Out-Null
$previousClient = $env:ALTER_GENESIS_GOOGLE_CLIENT_PATH
try {
    $env:ALTER_GENESIS_GOOGLE_CLIENT_PATH = $resolvedClient
    Push-Location $appDirectory
    try {
        & npm.cmd run check *> (Join-Path $output 'svelte-check.log')
        if ($LASTEXITCODE -ne 0) { throw 'Svelte check failed; see the build log.' }
        # Tauri CLI runs beforeBuildCommand, embeds the new frontend, builds Rust and bundles NSIS.
        # One installer format, no updater plugin: keep the packaged EXE identical to the recorded EXE.
        & npm.cmd run tauri -- build --bundles nsis --no-binary-patching *> (Join-Path $output 'tauri-build.log')
        if ($LASTEXITCODE -ne 0) { throw 'Tauri build failed; see the build log.' }
    } finally { Pop-Location }

    $executable = Join-Path $appDirectory 'src-tauri\target\release\alter-genesis.exe'
    $bundledSetup = Join-Path $appDirectory ("src-tauri\target\release\bundle\nsis\{0}_{1}_x64-setup.exe" -f $config.productName, $version)
    if (!(Test-Path -LiteralPath $bundledSetup -PathType Leaf)) { throw 'Expected NSIS installer was not created.' }
    $installer = Join-Path $output ("ALTER-GENESIS-Tauri-Setup-{0}-x64.exe" -f $version)
    Copy-Item -LiteralPath $bundledSetup -Destination $installer
    Copy-Item -LiteralPath $executable -Destination (Join-Path $output 'alter-genesis.exe')
    Copy-Item -LiteralPath (Join-Path $appDirectory 'src-tauri\target\release\nsis\x64\installer.nsi') -Destination (Join-Path $output 'generated-installer.nsi')

    $sourcePaths = @('app/src-tauri/tauri.conf.json', 'app/src-tauri/windows/installer-hooks.nsh', 'app/src-tauri/build.rs', 'app/src-tauri/Cargo.lock', 'app/package-lock.json', 'tools/Build-TauriInstaller.ps1', 'app/src-tauri/icons/app-icon.svg', 'app/src-tauri/icons/icon.ico', 'app/src-tauri/icons/32x32.png')
    $sources = foreach ($relative in $sourcePaths) {
        [ordered]@{ path=$relative; sha256=(Get-FileHash -LiteralPath (Join-Path $repositoryRoot $relative)).Hash }
    }
    $manifest = [ordered]@{
        product=$config.productName; version=$version; publisher=$config.bundle.publisher
        identifier=$config.identifier; architecture='x64'; installerMode='currentUser'
        defaultInstallDirectory='%LOCALAPPDATA%\ALTER GENESIS'
        dataDirectory='%LOCALAPPDATA%\com.automatonprotocol.altergenesis'
        googleConfigured=$true; signed=$false; binaryPatching=$false
        gitCommit=(& git -C $repositoryRoot rev-parse HEAD)
        gitStatus=@(& git -C $repositoryRoot -c core.quotePath=false status --short)
        sourceHashes=@($sources)
        executable='alter-genesis.exe'; executableBytes=(Get-Item -LiteralPath $executable).Length
        executableSha256=(Get-FileHash -LiteralPath $executable).Hash
        installer=(Split-Path $installer -Leaf); installerBytes=(Get-Item -LiteralPath $installer).Length
        installerSha256=(Get-FileHash -LiteralPath $installer).Hash
        createdAt=(Get-Date).ToUniversalTime().ToString('o')
    }
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'build-manifest.json') -Encoding utf8
    Write-Output $output
} finally { $env:ALTER_GENESIS_GOOGLE_CLIENT_PATH = $previousClient }
