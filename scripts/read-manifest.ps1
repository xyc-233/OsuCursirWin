param([Parameter(Mandatory = $true)][string]$ExePath)

$ErrorActionPreference = 'Stop'

if (-not ('OsuCursorManifest.NativeResource' -as [type]))
{
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace OsuCursorManifest
{
    public static class NativeResource
    {
        public static byte[] Read(string path)
        {
            IntPtr module = LoadLibraryEx(path, IntPtr.Zero, 0x00000022);
            if (module == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            try
            {
                IntPtr resource = FindResource(module, new IntPtr(1), new IntPtr(24));
                if (resource == IntPtr.Zero)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                uint size = SizeofResource(module, resource);
                if (size == 0)
                {
                    throw new InvalidOperationException("The executable manifest resource is empty.");
                }

                IntPtr loaded = LoadResource(module, resource);
                if (loaded == IntPtr.Zero)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                IntPtr data = LockResource(loaded);
                if (data == IntPtr.Zero)
                {
                    throw new InvalidOperationException("The executable manifest resource could not be read.");
                }

                byte[] bytes = new byte[checked((int)size)];
                Marshal.Copy(data, bytes, 0, bytes.Length);
                return bytes;
            }
            finally
            {
                FreeLibrary(module);
            }
        }

        [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);

        [DllImport("kernel32.dll", EntryPoint = "FindResourceW", SetLastError = true)]
        private static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint SizeofResource(IntPtr module, IntPtr resource);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LoadResource(IntPtr module, IntPtr resource);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LockResource(IntPtr resource);

        [DllImport("kernel32.dll")]
        private static extern bool FreeLibrary(IntPtr module);
    }
}
'@
}

$resolved = (Resolve-Path -LiteralPath $ExePath).Path
$bytes = [OsuCursorManifest.NativeResource]::Read($resolved)
$stream = [IO.MemoryStream]::new($bytes, $false)
$reader = [IO.StreamReader]::new($stream, [Text.Encoding]::UTF8, $true)
try
{
    $reader.ReadToEnd()
}
finally
{
    $reader.Dispose()
    $stream.Dispose()
}
