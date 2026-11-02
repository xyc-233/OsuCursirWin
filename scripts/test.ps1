$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "OsuCursorWin\OsuCursorWin.csproj"

dotnet build $project -c Release
if ($LASTEXITCODE -ne 0)
{
    throw "Build failed with exit code $LASTEXITCODE."
}

& (Join-Path $root "tests\ManifestRegression.ps1") -ExePath (Join-Path $root "OsuCursorWin\bin\Release\net8.0-windows\OsuCursorWin.exe")

foreach ($name in @("Regression", "AudioRegression", "CursorRegression", "AnimationRegression", "InputRegression", "ResizeSamplerRegression", "FramePacerRegression", "FrameClockRegression", "ResizeRegression", "UiRegression", "NativeOverlayRegression", "NativeUiRegression", "SettingsRegression"))
{
    $testProject = Join-Path $root "tests\$name\$name.csproj"
    dotnet run --project $testProject -c Release
    if ($LASTEXITCODE -ne 0)
    {
        throw "$name failed with exit code $LASTEXITCODE."
    }
}

& (Join-Path $PSScriptRoot "smoke.ps1") -ExePath (Join-Path $root "OsuCursorWin\bin\Release\net8.0-windows\OsuCursorWin.exe")
