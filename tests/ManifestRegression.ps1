param([string]$ExePath, [switch]$UiAccess)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

function Assert-Manifest
{
    param([xml]$Manifest, [bool]$ExpectedUiAccess, [string]$Label)

    $namespaces = [Xml.XmlNamespaceManager]::new($Manifest.NameTable)
    $namespaces.AddNamespace('asm', 'urn:schemas-microsoft-com:asm.v1')
    $namespaces.AddNamespace('security', 'urn:schemas-microsoft-com:asm.v3')
    $execution = $Manifest.SelectSingleNode(
        '/asm:assembly/security:trustInfo/security:security/security:requestedPrivileges/security:requestedExecutionLevel',
        $namespaces)
    if ($null -eq $execution)
    {
        throw "$Label does not declare execution privileges in the Windows asm.v3 namespace."
    }

    $declarations = $Manifest.SelectNodes('descendant::*[local-name()="requestedExecutionLevel"]')
    if ($declarations.Count -ne 1)
    {
        throw "$Label must contain exactly one execution privilege declaration."
    }

    if ($execution.GetAttribute('level') -ne 'asInvoker')
    {
        throw "$Label must use asInvoker."
    }

    $expected = $ExpectedUiAccess.ToString().ToLowerInvariant()
    if ($execution.GetAttribute('uiAccess') -cne $expected)
    {
        throw "$Label must declare uiAccess=$expected."
    }

    Write-Host "PASS $Label uses asm.v3 and uiAccess=$expected."
}

[xml]$standard = Get-Content -LiteralPath (Join-Path $root 'OsuCursorWin\app.manifest') -Raw
[xml]$accessible = Get-Content -LiteralPath (Join-Path $root 'OsuCursorWin\app.uiaccess.manifest') -Raw
Assert-Manifest -Manifest $standard -ExpectedUiAccess $false -Label 'Standard source manifest'
Assert-Manifest -Manifest $accessible -ExpectedUiAccess $true -Label 'UIAccess source manifest'

if ($ExePath)
{
    [xml]$embedded = & (Join-Path $root 'scripts\read-manifest.ps1') -ExePath $ExePath
    Assert-Manifest -Manifest $embedded -ExpectedUiAccess $UiAccess.IsPresent -Label 'Executable RT_MANIFEST resource'
}
