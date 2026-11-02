using System;
using System.IO;
using Microsoft.Win32;

namespace OsuCursorWin;

internal static class AutoStartManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovalKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "OsuCursorWin";

    internal static bool Apply(bool enabled) =>
        Apply(enabled, Registry.CurrentUser, Environment.ProcessPath, Environment.GetCommandLineArgs()[0]);

    internal static bool Apply(bool enabled, RegistryKey root, string? processPath, string? assemblyPath)
    {
        try
        {
            var command = enabled
                ? BuildStartupCommand(processPath, assemblyPath)
                : null;
            if (enabled && command is null)
            {
                return false;
            }

            using var key = enabled
                ? root.CreateSubKey(RunKeyPath, writable: true)
                : root.OpenSubKey(RunKeyPath, writable: true);
            using var approval = enabled
                ? root.CreateSubKey(ApprovalKeyPath, writable: true)
                : root.OpenSubKey(ApprovalKeyPath, writable: true);
            var oldCommand = key?.GetValue(ValueName);
            var oldCommandKind = oldCommand is null ? RegistryValueKind.String : key!.GetValueKind(ValueName);
            var oldApproval = approval?.GetValue(ValueName);
            var oldApprovalKind = oldApproval is null ? RegistryValueKind.Binary : approval!.GetValueKind(ValueName);
            try
            {
                if (enabled)
                {
                    key!.SetValue(ValueName, command!, RegistryValueKind.String);
                    var state = new byte[12];
                    state[0] = 2;
                    approval!.SetValue(ValueName, state, RegistryValueKind.Binary);
                }
                else
                {
                    key?.DeleteValue(ValueName, false);
                    approval?.DeleteValue(ValueName, false);
                }
            }
            catch
            {
                RestoreValue(key, oldCommand, oldCommandKind);
                RestoreValue(approval, oldApproval, oldApprovalKind);
                throw;
            }

            return GetEnabled(root) == enabled;
        }
        catch (Exception ex)
        {
            Program.Log($"Failed to update auto-start: {ex}");
            return false;
        }
    }

    internal static bool? GetEnabled() => GetEnabled(Registry.CurrentUser);

    internal static bool? GetEnabled(RegistryKey root)
    {
        try
        {
            using var key = root.OpenSubKey(RunKeyPath);
            if (string.IsNullOrWhiteSpace(key?.GetValue(ValueName) as string))
            {
                return false;
            }

            using var approval = root.OpenSubKey(ApprovalKeyPath);
            var state = approval?.GetValue(ValueName) as byte[];
            return state is null || state.Length < sizeof(uint)
                || (BitConverter.ToUInt32(state, 0) & 1) == 0;
        }
        catch (Exception exception)
        {
            Program.Log($"Failed to read auto-start: {exception}");
            return null;
        }
    }

    private static void RestoreValue(RegistryKey? key, object? value, RegistryValueKind kind)
    {
        try
        {
            if (value is null)
            {
                key?.DeleteValue(ValueName, false);
            }
            else
            {
                key?.SetValue(ValueName, value, kind);
            }
        }
        catch (Exception exception)
        {
            Program.Log($"Failed to restore auto-start value: {exception}");
        }
    }

    internal static string? BuildStartupCommand(string? processPath, string? assemblyPath)
    {
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return null;
        }

        if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(assemblyPath)
                ? null
                : $"\"{processPath}\" \"{Path.GetFullPath(assemblyPath)}\"";
        }

        return $"\"{processPath}\"";
    }
}
