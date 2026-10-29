param()
$ErrorActionPreference = 'Stop'
# Developer-only preparation from the preserved, encrypted OAuth experiment. No picker or clipboard.
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$sourceDirectory = Join-Path $repoRoot 'artifacts/snooze-manual/google'
$buildDirectory = Join-Path $repoRoot 'artifacts/oauth-reminder-build'
$liveDirectory = Join-Path $repoRoot 'artifacts/oauth-reminder-live/google'
git -C $repoRoot check-ignore --quiet 'artifacts/oauth-reminder-build/client.json'
if ($LASTEXITCODE -ne 0) { throw 'The client build input must be excluded from Git before preparation.' }
function Get-StoreFilename([string] $typeName, [string] $key) {
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($typeName + ':' + $key)
    return [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($bytes)) + '.dpapi'
}
$clientName = Get-StoreFilename 'Google.Apis.Auth.OAuth2.ClientSecrets' 'client'
$tokenName = Get-StoreFilename 'Google.Apis.Auth.OAuth2.Responses.TokenResponse' 'calendar-user'
$catalogName = Get-StoreFilename 'AlterGenesis.NotificationProbe.Google.GoogleCalendarSnapshot' 'catalog'
foreach ($name in @($clientName, $tokenName, $catalogName)) {
    if (-not (Test-Path -LiteralPath (Join-Path $sourceDirectory $name))) { throw 'Preserved OAuth experiment is incomplete.' }
}
New-Item -ItemType Directory -Path $buildDirectory, $liveDirectory -Force | Out-Null
Add-Type -AssemblyName System.Security.Cryptography.ProtectedData
$plain = $null
try {
    $cipher = [System.IO.File]::ReadAllBytes((Join-Path $sourceDirectory $clientName))
    $entropy = [System.Text.Encoding]::UTF8.GetBytes('TINMOON.AlterGenesis.Google.v1')
    $plain = [System.Security.Cryptography.ProtectedData]::Unprotect($cipher, $entropy, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
    # This is the desktop public-client configuration only, never a user token.
    [System.IO.File]::WriteAllBytes((Join-Path $buildDirectory 'client.json'), $plain)
    foreach ($name in @($clientName, $tokenName, $catalogName)) {
        $target = Join-Path $liveDirectory $name
        if (-not (Test-Path -LiteralPath $target)) { Copy-Item -LiteralPath (Join-Path $sourceDirectory $name) -Destination $target }
    }
}
finally { if ($null -ne $plain) { [Array]::Clear($plain, 0, $plain.Length) } }
Write-Output 'Prepared: desktop client build input and isolated encrypted store; no credentials printed.'
