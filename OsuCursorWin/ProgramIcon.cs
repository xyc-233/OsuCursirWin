using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OsuCursorWin;

internal static class ProgramIcon
{
    private const string ResourceName = "OsuCursorWin.Images.AppIcon.ico";

    internal static Icon CreateIcon()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Missing embedded resource: {ResourceName}");
        using var icon = new Icon(stream);
        return (Icon)icon.Clone();
    }

    internal static ImageSource CreateWindowIcon()
    {
        using var icon = CreateIcon();
        using var bitmap = icon.ToBitmap();
        var handle = bitmap.GetHbitmap();

        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(
                handle,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            DeleteObject(handle);
        }
    }

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}
