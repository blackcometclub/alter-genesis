[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$GoogleClientFile,
    [string]$InnoCompiler
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot 'src\AlterGenesis.NotificationProbe\AlterGenesis.NotificationProbe.csproj'
$productPropsPath = Join-Path $repositoryRoot 'eng\Product.props'
$installerScriptPath = Join-Path $repositoryRoot 'packaging\installer\AlterGenesis.iss'
$productOutputRoot = Join-Path $repositoryRoot 'artifacts\productization'
$publishDirectory = Join-Path $productOutputRoot 'publish'
$installerDirectory = Join-Path $productOutputRoot 'installer'
$manifestPath = Join-Path $productOutputRoot 'build-manifest.json'

function Assert-ProductOutputPath([string]$Path) {
    $expectedRoot = [IO.Path]::GetFullPath($productOutputRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $candidate = [IO.Path]::GetFullPath($Path)
    if (-not $candidate.StartsWith($expectedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to change a path outside the productization output: $candidate"
    }
}

function Find-InnoCompiler([string]$ExplicitPath) {
    if ($ExplicitPath) {
        if (-not (Test-Path -LiteralPath $ExplicitPath -PathType Leaf)) {
            throw "Inno Setup compiler was not found: $ExplicitPath"
        }
        return (Resolve-Path -LiteralPath $ExplicitPath).Path
    }

    $knownPaths = @(
        'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
        'C:\Program Files\Inno Setup 6\ISCC.exe',
        (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)) 'AppData\Local\Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'Programs\Inno Setup 6\ISCC.exe')
    )
    foreach ($knownPath in $knownPaths) {
        if (Test-Path -LiteralPath $knownPath -PathType Leaf) { return $knownPath }
    }

    $command = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    throw 'Inno Setup 6 is required to build the normal installer. Install it, then run this script again.'
}

[xml]$productProps = Get-Content -Raw -LiteralPath $productPropsPath
$productGroup = $productProps.Project.PropertyGroup | Select-Object -First 1
$productName = [string]$productGroup.AlterGenesisProductName
$productVersion = [string]$productGroup.AlterGenesisVersion
$executableName = [string]$productGroup.AlterGenesisExecutableName + '.exe'

if (-not $GoogleClientFile) {
    $GoogleClientFile = Join-Path $repositoryRoot 'artifacts\oauth-reminder-build\client.json'
}
if (-not (Test-Path -LiteralPath $GoogleClientFile -PathType Leaf)) {
    throw 'The Google desktop OAuth client JSON is required. Pass its path with -GoogleClientFile.'
}
$resolvedGoogleClient = (Resolve-Path -LiteralPath $GoogleClientFile).Path

foreach ($directory in @($publishDirectory, $installerDirectory)) {
    Assert-ProductOutputPath $directory
    if (Test-Path -LiteralPath $directory) {
        Remove-Item -LiteralPath $directory -Recurse -Force
    }
    New-Item -ItemType Directory -Path $directory | Out-Null
}

& dotnet publish $projectPath `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained true `
    --output $publishDirectory `
    "-p:GoogleClientFile=$resolvedGoogleClient" `
    '-p:PublishSingleFile=false' `
    '-p:DebugType=None' `
    '-p:DebugSymbols=false' `
    '-p:RestoreIgnoreFailedSources=true'
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

$publishedExecutable = Join-Path $publishDirectory $executableName
if (-not (Test-Path -LiteralPath $publishedExecutable -PathType Leaf)) {
    throw "The published executable was not found: $publishedExecutable"
}

$isccPath = Find-InnoCompiler $InnoCompiler
& $isccPath "/DMyAppVersion=$productVersion" "/DSourceDir=$publishDirectory" "/DOutputDir=$installerDirectory" $installerScriptPath
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed with exit code $LASTEXITCODE." }

$installer = Get-ChildItem -LiteralPath $installerDirectory -Filter '*.exe' -File | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $installer) { throw 'The installer executable was not created.' }

$buildManifest = [ordered]@{
    product = $productName
    version = $productVersion
    runtime = $Runtime
    executable = $publishedExecutable
    installer = $installer.FullName
    installerSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $installer.FullName).Hash
    createdAt = (Get-Date).ToUniversalTime().ToString('o')
}
$buildManifest | ConvertTo-Json | Set-Content -LiteralPath $manifestPath -Encoding utf8

$buildManifest
