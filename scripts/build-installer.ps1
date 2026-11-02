param(
    [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')]
    [string]$Version = '1.1.0',
    [string]$InnoSetupCompiler
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$project = Join-Path $root 'OsuCursorWin\OsuCursorWin.csproj'
$definition = Join-Path $root 'installer\OsuCursorWin.iss'
$artifacts = Join-Path $root 'artifacts'

$compiler = $null
if (-not [string]::IsNullOrWhiteSpace($InnoSetupCompiler))
{
    if (-not (Test-Path -LiteralPath $InnoSetupCompiler -PathType Leaf))
    {
        throw "Inno Setup compiler was not found: $InnoSetupCompiler"
    }
    $compiler = (Resolve-Path -LiteralPath $InnoSetupCompiler).Path
}
else
{
    $candidates = @(
        $env:INNOSETUP_COMPILER
        (Join-Path $root '.tools\inno-setup\ISCC.exe')
    )
    foreach ($programFilesDirectory in @(${env:ProgramFiles(x86)}, $env:ProgramW6432, $env:ProgramFiles))
    {
        if (-not [string]::IsNullOrWhiteSpace($programFilesDirectory))
        {
            $candidates += Join-Path $programFilesDirectory 'Inno Setup 6\ISCC.exe'
        }
    }
    foreach ($candidate in $candidates)
    {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and (Test-Path -LiteralPath $candidate -PathType Leaf))
        {
            $compiler = (Resolve-Path -LiteralPath $candidate).Path
            break
        }
    }
    if ($null -eq $compiler)
    {
        $command = Get-Command ISCC.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -ne $command)
        {
            $compiler = $command.Source
        }
    }
}

if ($null -eq $compiler)
{
    throw 'Inno Setup 6 is required. Install it from https://jrsoftware.org/isdl.php, add ISCC.exe to PATH, or pass -InnoSetupCompiler with its full path.'
}
if (-not (Test-Path -LiteralPath $definition -PathType Leaf))
{
    throw "Installer definition was not found: $definition"
}

$payload = Join-Path $artifacts ('installer-payload\' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $payload -Force | Out-Null
$publishArguments = @(
    'publish'
    $project
    '-c'
    'Release'
    '-r'
    'win-x64'
    '--self-contained'
    'true'
    '-p:PublishSingleFile=true'
    '-p:IncludeNativeLibrariesForSelfExtract=true'
    '-p:PublishTrimmed=false'
    '-p:DebugType=None'
    '-p:DebugSymbols=false'
    '-p:UseUiAccess=true'
    "-p:Version=$Version"
    '-o'
    $payload
)
& dotnet @publishArguments
if ($LASTEXITCODE -ne 0)
{
    throw "Self-contained application publish failed with exit code $LASTEXITCODE."
}
if (-not (Test-Path -LiteralPath (Join-Path $payload 'OsuCursorWin.exe') -PathType Leaf))
{
    throw 'Publishing did not produce OsuCursorWin.exe.'
}

& (Join-Path $root 'tests\InstallerRegression.ps1') -PayloadPath (Join-Path $payload 'OsuCursorWin.exe')

& $compiler "/DAppVersion=$Version" "/DPayloadDir=$payload" "/DOutputDir=$artifacts" $definition
if ($LASTEXITCODE -ne 0)
{
    throw "Inno Setup compilation failed with exit code $LASTEXITCODE."
}

$installer = Join-Path $artifacts "OsuCursorWin-Setup-$Version-x64.exe"
if (-not (Test-Path -LiteralPath $installer -PathType Leaf))
{
    throw "Inno Setup did not produce the expected installer: $installer"
}
Write-Host "Built installer: $installer"
