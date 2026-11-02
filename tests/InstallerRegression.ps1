param(
    [Parameter(Mandatory = $true)][string]$PayloadPath,
    [switch]$Integration
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$helper = Join-Path $root 'installer\configure-uiaccess.ps1'
$reader = Join-Path $root 'scripts\read-manifest.ps1'
$payload = (Resolve-Path -LiteralPath $PayloadPath).Path
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('OsuCursorInstallerTest-' + [Guid]::NewGuid().ToString('N'))

function Assert-True([bool]$Condition, [string]$Message)
{
    if (-not $Condition) { throw $Message }
    Write-Host "PASS $Message"
}

function Invoke-Helper([string]$Mode, [string]$StatePath)
{
    & (Join-Path $temporary 'configure-uiaccess.ps1') -Mode $Mode -ExePath (Join-Path $temporary 'OsuCursorWin.exe') `
        -StatePath $StatePath -InstallDir $script:installation -LogPath (Join-Path $temporary 'integration.log')
    if (-not $?) { throw "Installer helper failed in mode $Mode." }
}

function Assert-CertificateState([string]$Thumbprint, [bool]$Expected)
{
    foreach ($store in @('My', 'Root', 'TrustedPublisher'))
    {
        Assert-True ((Test-Path -LiteralPath "Cert:\LocalMachine\$store\$Thumbprint") -eq $Expected) `
            "Certificate presence in $store matches $Expected"
    }
}

$tokens = $null
$errors = $null
[Management.Automation.Language.Parser]::ParseFile($helper, [ref]$tokens, [ref]$errors) | Out-Null
Assert-True ($errors.Count -eq 0) 'The installer helper parses without errors'
& (Join-Path $PSScriptRoot 'ManifestRegression.ps1') -ExePath $payload -UiAccess

if (-not $Integration) { return }
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))
{
    throw 'The isolated certificate integration test requires administrator privileges.'
}

$key = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)
$version = $key.OpenSubKey('SOFTWARE\Microsoft\Windows\CurrentVersion')
try
{
    $programFiles = [string]$version.GetValue('ProgramW6432Dir')
    if ([string]::IsNullOrWhiteSpace($programFiles)) { $programFiles = [string]$version.GetValue('ProgramFilesDir') }
}
finally { $version.Dispose(); $key.Dispose() }
$installation = Join-Path $programFiles ('OsuCursorWin-Test-' + [Guid]::NewGuid().ToString('N'))
$statePath = Join-Path $temporary 'state.json'
$upgradeState = Join-Path $temporary 'upgrade.json'
$installed = $false

try
{
    New-Item -ItemType Directory -Path $temporary -Force | Out-Null
    Copy-Item -LiteralPath $payload -Destination (Join-Path $temporary 'OsuCursorWin.exe')
    Copy-Item -LiteralPath $helper, $reader -Destination $temporary
    Invoke-Helper 'Prepare' $statePath
    $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    Assert-True ($state.CreatedCertificate -and $state.AddedRoot -and $state.AddedPublisher) 'A fresh install owns only its new certificate entries'
    Assert-CertificateState $state.Thumbprint $true
    $signature = Get-AuthenticodeSignature -FilePath (Join-Path $temporary 'OsuCursorWin.exe')
    Assert-True ($signature.Status -eq 'Valid' -and $signature.SignerCertificate.Thumbprint -eq $state.Thumbprint) 'The staged executable has a valid local signature'
    Invoke-Helper 'Rollback' $statePath
    Assert-CertificateState $state.Thumbprint $false

    Invoke-Helper 'Prepare' $statePath
    $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    New-Item -ItemType Directory -Path $installation | Out-Null
    Copy-Item -LiteralPath $statePath -Destination (Join-Path $installation 'uiaccess.json')
    Invoke-Helper 'Prepare' $upgradeState
    $upgrade = Get-Content -LiteralPath $upgradeState -Raw | ConvertFrom-Json
    Assert-True ($upgrade.Thumbprint -eq $state.Thumbprint -and -not $upgrade.CreatedCertificate -and
        -not $upgrade.AddedRoot -and -not $upgrade.AddedPublisher) 'An upgrade reuses the existing trusted certificate'
    Assert-True ($upgrade.OwnsCertificate -and $upgrade.OwnsRoot -and $upgrade.OwnsPublisher) 'An upgrade preserves certificate ownership for uninstall'
    Invoke-Helper 'Rollback' $upgradeState
    Assert-CertificateState $state.Thumbprint $true
    Invoke-Helper 'Uninstall' $upgradeState
    Assert-CertificateState $state.Thumbprint $false
    $installed = $true
}
finally
{
    if (-not $installed -and (Test-Path -LiteralPath $statePath))
    {
        try { Invoke-Helper 'Uninstall' $statePath }
        catch { Write-Warning $_ }
    }
    foreach ($cleanupPath in @($temporary, $installation))
    {
        if (-not (Test-Path -LiteralPath $cleanupPath)) { continue }
        $resolved = [IO.Path]::GetFullPath($cleanupPath)
        $expectedParent = if ($cleanupPath -eq $temporary) { [IO.Path]::GetTempPath() } else { $programFiles }
        $prefix = [IO.Path]::GetFullPath($expectedParent).TrimEnd('\') + '\'
        if (-not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Test cleanup escaped its expected directory.' }
        if ([IO.Path]::GetFileName($resolved) -notmatch '^OsuCursor(?:InstallerTest-|Win-Test-)[a-f0-9]{32}$') { throw 'Test cleanup directory does not match its generated name.' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}

Write-Host 'Installer certificate, rollback and upgrade integration checks passed.'
