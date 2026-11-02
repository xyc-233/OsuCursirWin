param(
    [Parameter(Mandatory = $true)][ValidateSet('Prepare', 'Rollback', 'Uninstall')][string]$Mode,
    [string]$ExePath,
    [Parameter(Mandatory = $true)][string]$StatePath,
    [Parameter(Mandatory = $true)][string]$InstallDir,
    [string]$LogPath
)

$ErrorActionPreference = 'Stop'

function Write-InstallLog([string]$Message)
{
    if ([string]::IsNullOrWhiteSpace($LogPath)) { return }
    try
    {
        [IO.File]::AppendAllText([IO.Path]::GetFullPath($LogPath), "$(Get-Date -Format o) $Message$([Environment]::NewLine)")
    }
    catch { }
}

function Save-State($State)
{
    $temporary = $StatePath + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    $backup = $temporary + '.bak'
    try
    {
        $directory = [IO.Path]::GetDirectoryName($StatePath)
        if (-not [IO.Directory]::Exists($directory)) { [IO.Directory]::CreateDirectory($directory) | Out-Null }
        [IO.File]::WriteAllText($temporary, ($State | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
        if ([IO.File]::Exists($StatePath))
        {
            [IO.File]::Replace($temporary, $StatePath, $backup)
        }
        else
        {
            [IO.File]::Move($temporary, $StatePath)
        }
    }
    finally
    {
        if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
        if ([IO.File]::Exists($backup)) { [IO.File]::Delete($backup) }
    }
}

function Read-State([string]$Path)
{
    if (-not [IO.File]::Exists($Path)) { return $null }
    $state = [IO.File]::ReadAllText($Path) | ConvertFrom-Json
    if ($state.SchemaVersion -ne 1 -or $state.InstallDir -ine $InstallDir -or $state.Subject -cne $script:CertificateSubject)
    {
        throw 'The UIAccess receipt does not match this installation.'
    }
    foreach ($name in @('OwnsCertificate', 'OwnsRoot', 'OwnsPublisher', 'CreatedCertificate', 'AddedRoot', 'AddedPublisher'))
    {
        if ($state.$name -isnot [bool]) { throw "The UIAccess receipt contains an invalid $name value." }
    }
    if ($state.Thumbprint -notmatch '^[A-Fa-f0-9]{40}$' -and
        (-not [string]::IsNullOrEmpty($state.Thumbprint) -or $state.OwnsCertificate -or $state.OwnsRoot -or
            $state.OwnsPublisher -or $state.CreatedCertificate -or $state.AddedRoot -or $state.AddedPublisher))
    {
        throw 'The UIAccess receipt contains an invalid certificate thumbprint.'
    }
    return $state
}

function Find-Certificate([string]$Store, [string]$Thumbprint)
{
    if ($Thumbprint -notmatch '^[A-Fa-f0-9]{40}$') { return $null }
    return Get-Item -LiteralPath "Cert:\LocalMachine\$Store\$Thumbprint" -ErrorAction SilentlyContinue
}

function Test-SigningCertificate($Certificate)
{
    return ($null -ne $Certificate -and $Certificate.Subject -ceq $script:CertificateSubject -and
        $Certificate.HasPrivateKey -and $Certificate.NotBefore -le (Get-Date) -and
        $Certificate.NotAfter -gt (Get-Date) -and
        ($Certificate.EnhancedKeyUsageList.ObjectId -contains '1.3.6.1.5.5.7.3.3'))
}

function Add-Trust($Certificate, [string]$StoreName)
{
    $existing = Find-Certificate $StoreName $Certificate.Thumbprint
    if ($null -ne $existing)
    {
        if ($existing.Subject -cne $script:CertificateSubject) { throw 'A certificate subject does not match its receipt.' }
        return $false
    }
    $store = [Security.Cryptography.X509Certificates.X509Store]::new($StoreName, 'LocalMachine')
    $publicCertificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new([byte[]]$Certificate.RawData)
    try
    {
        $store.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
        $store.Add($publicCertificate)
    }
    finally
    {
        $store.Close()
        $publicCertificate.Dispose()
    }
    return $true
}

function Remove-ExactCertificate([string]$Store, [string]$Thumbprint)
{
    $certificate = Find-Certificate $Store $Thumbprint
    if ($null -eq $certificate) { return }
    if ($certificate.Subject -cne $script:CertificateSubject) { throw 'Refusing to remove a certificate from another installation.' }
    $path = "Cert:\LocalMachine\$Store\$Thumbprint"
    if ($Store -eq 'My')
    {
        Remove-Item -LiteralPath $path -DeleteKey -Force
    }
    else
    {
        Remove-Item -LiteralPath $path -Force
    }
}

function Undo-Attempt($State)
{
    $errors = [Collections.Generic.List[string]]::new()
    foreach ($entry in @(@('AddedPublisher', 'TrustedPublisher'), @('AddedRoot', 'Root'), @('CreatedCertificate', 'My')))
    {
        $flag = $entry[0]
        if ($State.$flag)
        {
            try
            {
                Remove-ExactCertificate $entry[1] $State.Thumbprint
                $State.$flag = $false
            }
            catch { $errors.Add($_.Exception.Message) }
        }
    }
    try { Save-State $State } catch { $errors.Add($_.Exception.Message) }
    if ($errors.Count -gt 0) { throw ($errors -join [Environment]::NewLine) }
}

function Remove-StartupEntries
{
    $users = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::Users, [Microsoft.Win32.RegistryView]::Default)
    $expected = '"' + (Join-Path $InstallDir 'OsuCursorWin.exe') + '"'
    try
    {
        foreach ($sid in $users.GetSubKeyNames())
        {
            if ($sid -notmatch '^S-1-(?:5-21|12-1)-\d+(?:-\d+)*$') { continue }
            $run = $null
            $approval = $null
            try
            {
                $run = $users.OpenSubKey("$sid\Software\Microsoft\Windows\CurrentVersion\Run", $true)
                if ($null -eq $run -or $run.GetValue('OsuCursorWin') -isnot [string] -or
                    $run.GetValue('OsuCursorWin') -ine $expected) { continue }
                $run.DeleteValue('OsuCursorWin', $false)
                $approval = $users.OpenSubKey("$sid\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", $true)
                if ($null -ne $approval) { $approval.DeleteValue('OsuCursorWin', $false) }
            }
            catch { Write-InstallLog "Could not remove the startup entry for ${sid}: $($_.Exception.Message)" }
            finally
            {
                if ($null -ne $approval) { $approval.Dispose() }
                if ($null -ne $run) { $run.Dispose() }
            }
        }
    }
    finally { $users.Dispose() }
}

try
{
    if ($InstallDir -notmatch '^[A-Za-z]:[\\/]') { throw 'An absolute installation directory is required.' }
    $InstallDir = [IO.Path]::GetFullPath($InstallDir).TrimEnd('\')
    $StatePath = [IO.Path]::GetFullPath($StatePath)
    $registry = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)
    $versionKey = $null
    try
    {
        $versionKey = $registry.OpenSubKey('SOFTWARE\Microsoft\Windows\CurrentVersion')
        $nativeDirectory = [string]$versionKey.GetValue('ProgramW6432Dir')
        if ([string]::IsNullOrWhiteSpace($nativeDirectory)) { $nativeDirectory = [string]$versionKey.GetValue('ProgramFilesDir') }
        $programFiles = [IO.Path]::GetFullPath($nativeDirectory).TrimEnd('\') + '\'
    }
    finally
    {
        if ($null -ne $versionKey) { $versionKey.Dispose() }
        $registry.Dispose()
    }
    if (-not $InstallDir.StartsWith($programFiles, [StringComparison]::OrdinalIgnoreCase))
    {
        throw 'UIAccess must be installed beneath the native Program Files directory.'
    }
    $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))
    {
        throw 'UIAccess configuration requires administrator privileges.'
    }
    $hash = [Security.Cryptography.SHA256]::Create()
    try
    {
        $digest = $hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($InstallDir.ToLowerInvariant()))
        $suffix = ([BitConverter]::ToString($digest).Replace('-', '').ToLowerInvariant()).Substring(0, 16)
        $script:CertificateSubject = 'CN=Osu Cursor ' + $suffix
    }
    finally { $hash.Dispose() }

    if ($Mode -eq 'Prepare')
    {
        if ([string]::IsNullOrWhiteSpace($ExePath)) { throw 'The staged executable path is required.' }
        $ExePath = (Resolve-Path -LiteralPath $ExePath).Path
        [xml]$manifest = & (Join-Path $PSScriptRoot 'read-manifest.ps1') -ExePath $ExePath
        $namespaces = [Xml.XmlNamespaceManager]::new($manifest.NameTable)
        $namespaces.AddNamespace('asm1', 'urn:schemas-microsoft-com:asm.v1')
        $namespaces.AddNamespace('asm3', 'urn:schemas-microsoft-com:asm.v3')
        $level = $manifest.SelectSingleNode('/asm1:assembly/asm3:trustInfo/asm3:security/asm3:requestedPrivileges/asm3:requestedExecutionLevel', $namespaces)
        if ($null -eq $level -or $level.GetAttribute('uiAccess') -ne 'true' -or $level.GetAttribute('level') -ne 'asInvoker')
        {
            throw 'The staged executable must request UIAccess with asInvoker privileges.'
        }
        $previous = $null
        try { $previous = Read-State (Join-Path $InstallDir 'uiaccess.json') }
        catch { Write-InstallLog "Ignoring invalid previous receipt: $($_.Exception.Message)" }
        $certificate = $null
        if ($null -ne $previous)
        {
            $candidate = Find-Certificate 'My' $previous.Thumbprint
            if (Test-SigningCertificate $candidate) { $certificate = $candidate }
        }
        if ($null -eq $certificate)
        {
            $certificate = Get-ChildItem -LiteralPath 'Cert:\LocalMachine\My' |
                Where-Object { Test-SigningCertificate $_ } |
                Sort-Object NotAfter -Descending | Select-Object -First 1
        }
        $state = [pscustomobject]@{
            SchemaVersion = 1
            Thumbprint = if ($null -ne $certificate) { $certificate.Thumbprint } else { '' }
            Subject = $script:CertificateSubject
            InstallDir = $InstallDir
            OwnsCertificate = $false
            OwnsRoot = $false
            OwnsPublisher = $false
            CreatedCertificate = $false
            AddedRoot = $false
            AddedPublisher = $false
        }
        if ($null -ne $previous -and $null -ne $certificate -and $previous.Thumbprint -ieq $certificate.Thumbprint)
        {
            $state.OwnsCertificate = $previous.OwnsCertificate
            $state.OwnsRoot = $previous.OwnsRoot
            $state.OwnsPublisher = $previous.OwnsPublisher
        }
        Save-State $state
        try
        {
            if ($null -eq $certificate)
            {
                $certificate = New-SelfSignedCertificate -Type CodeSigningCert -Subject $script:CertificateSubject `
                    -FriendlyName 'osu! Cursor UIAccess' -CertStoreLocation 'Cert:\LocalMachine\My' `
                    -KeyExportPolicy NonExportable -KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256 `
                    -NotAfter (Get-Date).AddYears(10)
                $state.Thumbprint = $certificate.Thumbprint
                $state.CreatedCertificate = $true
                $state.OwnsCertificate = $true
                Save-State $state
            }
            $state.AddedRoot = Add-Trust $certificate 'Root'
            if ($state.AddedRoot) { $state.OwnsRoot = $true }
            Save-State $state
            $state.AddedPublisher = Add-Trust $certificate 'TrustedPublisher'
            if ($state.AddedPublisher) { $state.OwnsPublisher = $true }
            Save-State $state
            Set-AuthenticodeSignature -FilePath $ExePath -Certificate $certificate -HashAlgorithm SHA256 | Out-Null
            $signature = Get-AuthenticodeSignature -FilePath $ExePath
            if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Thumbprint -ine $state.Thumbprint)
            {
                throw "The staged executable signature is not valid: $($signature.Status)."
            }
            Write-InstallLog "Prepared UIAccess executable with certificate $($state.Thumbprint)."
        }
        catch
        {
            $failure = $_
            Write-InstallLog "Prepare failed: $($failure | Out-String)"
            try { Undo-Attempt $state }
            catch { Write-InstallLog "Prepare rollback failed: $($_ | Out-String)" }
            throw $failure
        }
    }
    elseif ($Mode -eq 'Rollback')
    {
        $state = Read-State $StatePath
        if ($null -ne $state)
        {
            Undo-Attempt $state
            Write-InstallLog 'Rolled back this installation attempt.'
        }
    }
    else
    {
        Remove-StartupEntries
        $state = Read-State $StatePath
        if ($null -ne $state)
        {
            $errors = [Collections.Generic.List[string]]::new()
            foreach ($entry in @(@('OwnsPublisher', 'TrustedPublisher'), @('OwnsRoot', 'Root'), @('OwnsCertificate', 'My')))
            {
                if ($state.($entry[0]))
                {
                    try { Remove-ExactCertificate $entry[1] $state.Thumbprint }
                    catch { $errors.Add($_.Exception.Message) }
                }
            }
            if ($errors.Count -gt 0) { throw ($errors -join [Environment]::NewLine) }
        }
        Write-InstallLog 'Removed owned UIAccess certificates and matching startup entries.'
    }
}
catch
{
    Write-InstallLog "$Mode failed: $($_ | Out-String)"
    throw
}
