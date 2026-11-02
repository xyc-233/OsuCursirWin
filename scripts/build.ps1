param([switch]$UiAccess)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "OsuCursorWin\OsuCursorWin.csproj"
$outputName = if ($UiAccess) { "publish-uiaccess" } else { "publish" }
$output = Join-Path $root $outputName
$outputV2 = Join-Path $root "$outputName-v2"

$publishExe = Join-Path $output "OsuCursorWin.exe"
$locked = $false
if (Test-Path -LiteralPath $publishExe)
{
    try
    {
        $stream = [System.IO.File]::Open(
            $publishExe,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::ReadWrite,
            [System.IO.FileShare]::None)
        $stream.Dispose()
    }
    catch
    {
        $locked = $true
    }
}

if ($locked)
{
    Write-Host "$publishExe is in use; building to $outputV2 instead."
    $output = $outputV2
}

dotnet publish $project -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -p:UseUiAccess=$($UiAccess.IsPresent.ToString().ToLowerInvariant()) `
    -o $output

if ($LASTEXITCODE -ne 0)
{
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

Write-Host "Built: $output\OsuCursorWin.exe"
