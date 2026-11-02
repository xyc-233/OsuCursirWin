param([string]$ExePath, [switch]$RequireUiAccess)

$ErrorActionPreference = "Stop"

$log = Join-Path $env:TEMP "OsuCursorWin.log"
$publishV2Exe = Join-Path $PSScriptRoot "..\publish-v2\OsuCursorWin.exe"
$publishExe = Join-Path $PSScriptRoot "..\publish\OsuCursorWin.exe"
$binExe = Join-Path $PSScriptRoot "..\OsuCursorWin\bin\Release\net8.0-windows\OsuCursorWin.exe"
$exe = if ($ExePath)
{
    $ExePath
}
else
{
    @($publishV2Exe, $publishExe, $binExe) |
        Where-Object { Test-Path -LiteralPath $_ } |
        Get-Item |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}
if (-not $exe)
{
    throw "No build found. Run scripts\build.ps1 first."
}
$exe = (Resolve-Path -LiteralPath $exe).Path
$arguments = @("--smoke")
if ($RequireUiAccess)
{
    $arguments += "--require-uiaccess"
}
$process = Start-Process -FilePath $exe -ArgumentList $arguments -WindowStyle Hidden -PassThru

if (-not $process.WaitForExit(10000))
{
    Stop-Process -Id $process.Id -Force
    throw "Smoke test timed out after 10 seconds."
}

if (Test-Path -LiteralPath $log)
{
    Get-Content -Tail 60 -LiteralPath $log
}
else
{
    throw "Smoke test did not write a log."
}

if ($process.ExitCode -ne 0)
{
    throw "Smoke test failed with exit code $($process.ExitCode)."
}

Write-Host "Smoke test passed."
