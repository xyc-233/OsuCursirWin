$ErrorActionPreference = "Stop"

Add-Type -MemberDefinition @'
[DllImport("user32.dll")]
public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

[DllImport("user32.dll")]
public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

[DllImport("user32.dll")]
public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
'@ -Name Native -Namespace OsuCursorStop

$processes = Get-Process OsuCursorWin -ErrorAction SilentlyContinue
if (-not $processes)
{
    Write-Host "No OsuCursorWin process is running."
    exit 0
}

foreach ($process in $processes)
{
    $targetPid = $process.Id
    $callback = [OsuCursorStop.Native+EnumWindowsProc]{
        param([IntPtr]$hWnd, [IntPtr]$lParam)
        [uint32]$winPid = 0
        [OsuCursorStop.Native]::GetWindowThreadProcessId($hWnd, [ref]$winPid) | Out-Null
        if ($winPid -eq $targetPid)
        {
            [OsuCursorStop.Native]::PostMessage($hWnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
        }
        return $true
    }
    [OsuCursorStop.Native]::EnumWindows($callback, [IntPtr]::Zero) | Out-Null
}

foreach ($process in $processes)
{
    if (-not $process.WaitForExit(5000))
    {
        throw "OsuCursorWin did not exit cleanly. Close it from the tray before installing."
    }
}
$remaining = Get-Process OsuCursorWin -ErrorAction SilentlyContinue
if ($remaining)
{
    throw "An OsuCursorWin process is still running."
}
else
{
    Write-Host "OsuCursorWin stopped."
}
