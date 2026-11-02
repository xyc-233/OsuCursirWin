$ErrorActionPreference = "Stop"

Add-Type -Namespace OsuCursorRestore -Name Api -MemberDefinition @'
[DllImport("user32.dll", SetLastError = true)]
public static extern bool SystemParametersInfo(uint uiAction, uint uiParam, System.IntPtr pvParam, uint fWinIni);
'@

if (-not [OsuCursorRestore.Api]::SystemParametersInfo(0x0057, 0, [System.IntPtr]::Zero, 0))
{
    throw "Failed to restore system cursors: $([Runtime.InteropServices.Marshal]::GetLastWin32Error())"
}

Write-Host "System cursors restored."
