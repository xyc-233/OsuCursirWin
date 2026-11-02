using System;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace OsuCursorWin;

internal static class UiAccessStatus
{
    internal static bool IsEnabled { get; } = CheckCurrentProcess();

    private static bool CheckCurrentProcess()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            if (GetTokenInformation(identity.AccessToken, 26, out var enabled, sizeof(int), out _))
            {
                return enabled != 0;
            }

            Program.Log($"Failed to query UIAccess: {Marshal.GetLastWin32Error()}");
        }
        catch (Exception exception)
        {
            Program.Log($"Failed to query UIAccess: {exception}");
        }

        return false;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(
        SafeAccessTokenHandle tokenHandle,
        int tokenInformationClass,
        out int tokenInformation,
        int tokenInformationLength,
        out int returnLength);
}
