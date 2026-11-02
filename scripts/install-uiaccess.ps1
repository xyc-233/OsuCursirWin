param(
    [string]$ExePath = (Join-Path $PSScriptRoot "..\publish-uiaccess\OsuCursorWin.exe"),
    [string]$InstallDir
)

$ErrorActionPreference = "Stop"

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))
{
    Write-Error "This script must be run as administrator."
    exit 1
}

$sourceExe = (Resolve-Path -LiteralPath $ExePath).Path
[xml]$manifest = & (Join-Path $PSScriptRoot "read-manifest.ps1") -ExePath $sourceExe
$namespaces = [Xml.XmlNamespaceManager]::new($manifest.NameTable)
$namespaces.AddNamespace('asm1', 'urn:schemas-microsoft-com:asm.v1')
$namespaces.AddNamespace('asm3', 'urn:schemas-microsoft-com:asm.v3')
$executionLevel = $manifest.SelectSingleNode('/asm1:assembly/asm3:trustInfo/asm3:security/asm3:requestedPrivileges/asm3:requestedExecutionLevel', $namespaces)
if ($null -eq $executionLevel -or $executionLevel.GetAttribute('uiAccess') -ne 'true')
{
    throw "The executable does not have a valid UIAccess manifest. Build it with scripts\build.ps1 -UiAccess."
}

$nativeProgramFiles = (Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion').ProgramW6432Dir
if ([string]::IsNullOrWhiteSpace($nativeProgramFiles))
{
    $nativeProgramFiles = [Environment]::GetFolderPath('ProgramFiles')
}
if ([string]::IsNullOrWhiteSpace($InstallDir))
{
    $InstallDir = Join-Path $nativeProgramFiles "OsuCursorWin"
}
$InstallDir = [IO.Path]::GetFullPath($InstallDir)
$programFilesRoot = [IO.Path]::GetFullPath($nativeProgramFiles).TrimEnd('\') + '\'
if (-not $InstallDir.StartsWith($programFilesRoot, [StringComparison]::OrdinalIgnoreCase))
{
    throw "UIAccess must be installed under the native Program Files directory: $programFilesRoot."
}

$certSubject = "CN=Osu Cursor Local"
$cert = Get-ChildItem "Cert:\CurrentUser\My" |
    Where-Object {
        $_.Subject -eq $certSubject -and $_.HasPrivateKey -and
        $_.NotBefore -le (Get-Date) -and $_.NotAfter -gt (Get-Date) -and
        ($_.EnhancedKeyUsageList.ObjectId -contains "1.3.6.1.5.5.7.3.3")
    } |
    Select-Object -First 1

if (-not $cert)
{
    $cert = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $certSubject `
        -CertStoreLocation "Cert:\CurrentUser\My" `
        -KeyExportPolicy Exportable `
        -NotAfter (Get-Date).AddYears(10)
}

$certDir = Join-Path $PSScriptRoot "..\certs"
New-Item -ItemType Directory -Force -Path $certDir | Out-Null
$certPath = Join-Path $certDir "OsuCursorLocal.cer"
Export-Certificate -Cert $cert -FilePath $certPath -Force | Out-Null

$rootCert = Get-ChildItem "Cert:\LocalMachine\Root" | Where-Object Thumbprint -eq $cert.Thumbprint
if (-not $rootCert)
{
    Import-Certificate -FilePath $certPath -CertStoreLocation "Cert:\LocalMachine\Root" | Out-Null
}

$publisherCert = Get-ChildItem "Cert:\LocalMachine\TrustedPublisher" | Where-Object Thumbprint -eq $cert.Thumbprint
if (-not $publisherCert)
{
    Import-Certificate -FilePath $certPath -CertStoreLocation "Cert:\LocalMachine\TrustedPublisher" | Out-Null
}

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
$destExe = Join-Path $InstallDir "OsuCursorWin.exe"
& (Join-Path $PSScriptRoot "stop-running.ps1")
Copy-Item -LiteralPath $sourceExe -Destination $destExe -Force
Set-AuthenticodeSignature -FilePath $destExe -Certificate $cert -HashAlgorithm SHA256 | Out-Null

$signature = Get-AuthenticodeSignature -FilePath $destExe
if ($signature.Status -ne "Valid")
{
    throw "Signature verification failed: $($signature.Status)"
}

Write-Host "Installed: $destExe"
Write-Host "Signature status: $($signature.Status)"
Write-Host "Launch it from this secure location so UIAccess can overlay immersive shell windows."
