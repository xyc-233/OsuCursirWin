using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using OsuCursorWin;

internal static class Program
{
    private static int _passed;

    [STAThread]
    private static int Main()
    {
        try
        {
            Run("A native overlay starts hidden with click-through topmost styles", HiddenWindowStyles);
            Run("Rendering submits dimensions and position without showing the window", HiddenRender);
            Run("The DIB contains transparent premultiplied pixels around the sprite", PremultipliedPixels);
            Run("Rotation uses the configured hotspot instead of the sprite center", RotationHotspot);
            Run("Press scaling preserves the hotspot", ScaleHotspot);
            Run("Glow opacity changes the same pixels without accumulating old frames", GlowOpacity);
            Run("DPI scales sprite pixels, hotspot and native window bounds together", DpiScaling);
            Run("Image and surface caches survive movement and rebuild for size changes", CacheLifetime);
            Run("Live quality changes preserve cached resources, hotspot and transparency", QualitySwitching);
            Run("Visibility changes do not activate the overlay", VisibilityWithoutActivation);
            Run("Disposal destroys the window and releases GDI objects", ResourceLifetime);
            Console.WriteLine($"Passed {_passed} native overlay checks without modifying the system cursor.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void HiddenWindowStyles()
    {
        using var overlay = new NativeCursorOverlay();
        Assert(overlay.Handle != IntPtr.Zero && IsWindow(overlay.Handle), "Overlay did not create a native window.");
        Assert(!overlay.IsVisible, "A newly created overlay is visible.");
        var styles = NativeMethods.GetWindowExtendedStyle(overlay.Handle);
        const long required = 0x00080000 | 0x00000008 | 0x00000080 | 0x00000020 | 0x08000000;
        Assert((styles & required) == required, "Overlay styles are incomplete.");
        Assert((NativeMethods.GetWindowStyle(overlay.Handle) & 0x80000000L) != 0, "Overlay is not a popup.");
    }

    private static void HiddenRender()
    {
        using var overlay = new NativeCursorOverlay();
        overlay.Render(new CursorModel(), new CursorDimensions(30), 1, 1, 120, 180);
        AssertBounds(overlay, 120, 180, 160, 160);
        Assert(!overlay.IsVisible, "Rendering changed hidden visibility.");
        overlay.Render(new CursorModel(), new CursorDimensions(30), 1, 1, -40, 90);
        AssertBounds(overlay, -40, 90, 160, 160);
    }

    private static void PremultipliedPixels()
    {
        using var overlay = new NativeCursorOverlay();
        overlay.Render(new CursorModel(), new CursorDimensions(30), 1, 1, 0, 0);
        var pixels = ReadPixels(overlay);
        Assert(pixels.AlphaSum > 0, "Rendered sprite contains no visible pixels.");
        Assert(pixels.AlphaAt(0, 0) == 0 && pixels.AlphaAt(159, 0) == 0
            && pixels.AlphaAt(0, 159) == 0 && pixels.AlphaAt(159, 159) == 0, "Transparent margins are opaque.");
        for (var i = 0; i < pixels.Bytes.Length; i += 4)
        {
            var alpha = pixels.Bytes[i + 3];
            Assert(pixels.Bytes[i] <= alpha && pixels.Bytes[i + 1] <= alpha && pixels.Bytes[i + 2] <= alpha,
                "A DIB pixel is not premultiplied.");
        }
    }

    private static void RotationHotspot()
    {
        using var overlay = new NativeCursorOverlay();
        var dimensions = new CursorDimensions(30);
        var model = new CursorModel();
        overlay.Render(model, dimensions, 1, 1, 0, 0);
        var original = ReadPixels(overlay).Centroid;
        SetModelValue(model, "Angle", 90);
        overlay.Render(model, dimensions, 1, 1, 0, 0);
        var rotated = ReadPixels(overlay).Centroid;
        Near(dimensions.Margin - (original.Y - dimensions.Margin), rotated.X, 1.25);
        Near(dimensions.Margin + (original.X - dimensions.Margin), rotated.Y, 1.25);
    }

    private static void ScaleHotspot()
    {
        using var overlay = new NativeCursorOverlay();
        var dimensions = new CursorDimensions(30);
        var model = new CursorModel();
        overlay.Render(model, dimensions, 1, 1, 0, 0);
        var original = ReadPixels(overlay);
        SetModelValue(model, "Scale", 0.9);
        overlay.Render(model, dimensions, 1, 1, 0, 0);
        var scaled = ReadPixels(overlay);
        Near(dimensions.Margin + (original.Centroid.X - dimensions.Margin) * 0.9, scaled.Centroid.X, 1.25);
        Near(dimensions.Margin + (original.Centroid.Y - dimensions.Margin) * 0.9, scaled.Centroid.Y, 1.25);
        Assert(scaled.AlphaSum < original.AlphaSum, "Press scaling did not shrink the visible sprite.");
    }

    private static void GlowOpacity()
    {
        using var overlay = new NativeCursorOverlay();
        var model = new CursorModel();
        var dimensions = new CursorDimensions(30);
        overlay.Render(model, dimensions, 1, 1, 0, 0);
        var plain = ReadPixels(overlay).Bytes;
        SetModelValue(model, "Opacity", 1);
        overlay.Render(model, dimensions, 1, 1, 0, 0);
        var glow = ReadPixels(overlay).Bytes;
        Assert(!plain.SequenceEqual(glow), "The glow layer did not change the sprite pixels.");
        SetModelValue(model, "Opacity", 0.5);
        overlay.Render(model, dimensions, 1, 1, 0, 0);
        var halfGlow = ReadPixels(overlay).Bytes;
        Assert(!halfGlow.SequenceEqual(glow) && !halfGlow.SequenceEqual(plain), "Glow opacity was not applied.");
        SetModelValue(model, "Opacity", 0);
        overlay.Render(model, dimensions, 1, 1, 0, 0);
        Assert(plain.SequenceEqual(ReadPixels(overlay).Bytes), "A new frame retained pixels from the previous glow.");
    }

    private static void DpiScaling()
    {
        using var overlay = new NativeCursorOverlay();
        var dimensions = new CursorDimensions(30);
        var model = new CursorModel();
        overlay.Render(model, dimensions, 1, 1, 0, 0);
        var original = ReadPixels(overlay).Centroid;
        overlay.Render(model, dimensions, 1.5, 2, 300, 400);
        AssertBounds(overlay, 300, 400, 240, 320);
        var scaled = ReadPixels(overlay).Centroid;
        Near(original.X * 1.5, scaled.X, 1.5);
        Near(original.Y * 2, scaled.Y, 1.5);
        SetModelValue(model, "Angle", 90);
        overlay.Render(model, dimensions, 1.5, 2, 300, 400);
        var rotated = ReadPixels(overlay).Centroid;
        Near((dimensions.Margin - (original.Y - dimensions.Margin)) * 1.5, rotated.X, 1.75);
        Near((dimensions.Margin + (original.X - dimensions.Margin)) * 2, rotated.Y, 1.75);
    }

    private static void CacheLifetime()
    {
        using var overlay = new NativeCursorOverlay();
        var model = new CursorModel();
        overlay.Render(model, new CursorDimensions(30), 1, 1, 0, 0);
        var surface = GetField(overlay, "_surface");
        var sprite = GetField(overlay, "_scaledBase");
        SetModelValue(model, "Angle", 40);
        overlay.Render(model, new CursorDimensions(30), 1, 1, 90, 100);
        Assert(ReferenceEquals(surface, GetField(overlay, "_surface"))
            && ReferenceEquals(sprite, GetField(overlay, "_scaledBase")), "Movement or rotation rebuilt static drawing resources.");
        overlay.Render(model, new CursorDimensions(64), 1.5, 1.5, 90, 100);
        Assert(!ReferenceEquals(surface, GetField(overlay, "_surface"))
            && !ReferenceEquals(sprite, GetField(overlay, "_scaledBase")), "Size and DPI changes did not rebuild the caches.");
        AssertBounds(overlay, 90, 100, 512, 512);
    }

    private static void VisibilityWithoutActivation()
    {
        using var overlay = new NativeCursorOverlay();
        overlay.Render(new CursorModel(), new CursorDimensions(30), 1, 1, 40, 40);
        var foreground = GetForegroundWindow();
        overlay.SetVisible(true);
        Assert(overlay.IsVisible, "Show did not make the overlay visible.");
        Assert(GetForegroundWindow() == foreground, "Showing the overlay changed foreground activation.");
        overlay.SetVisible(false);
        Assert(!overlay.IsVisible, "Hide did not hide the overlay.");
    }

    private static void QualitySwitching()
    {
        using var overlay = new NativeCursorOverlay();
        var model = new CursorModel();
        var dimensions = new CursorDimensions(30);
        SetModelValue(model, "Angle", 37);
        SetModelValue(model, "Scale", 0.9);
        SetModelValue(model, "Opacity", 0.65);
        overlay.Render(model, dimensions, 1.5, 1.5, 150, 250);
        var qualityPixels = ReadPixels(overlay);
        var surface = GetField(overlay, "_surface");
        var sprite = GetField(overlay, "_scaledBase");
        using var qualityTransform = ((Graphics)GetField(surface, "_graphics")).Transform;
        overlay.SetHighQualityRendering(false);
        overlay.Render(model, dimensions, 1.5, 1.5, 150, 250);
        var performancePixels = ReadPixels(overlay);
        using var performanceTransform = ((Graphics)GetField(surface, "_graphics")).Transform;
        Assert(ReferenceEquals(surface, GetField(overlay, "_surface"))
            && ReferenceEquals(sprite, GetField(overlay, "_scaledBase")), "Changing quality rebuilt cached drawing resources.");
        Assert(qualityTransform.Elements.SequenceEqual(performanceTransform.Elements),
            "Changing rendering quality moved the hotspot transform.");
        Assert(!qualityPixels.Bytes.SequenceEqual(performancePixels.Bytes), "The rendering quality option did not affect rotated pixels.");
        Near(qualityPixels.Centroid.X, performancePixels.Centroid.X, 1.0);
        Near(qualityPixels.Centroid.Y, performancePixels.Centroid.Y, 1.0);
        for (var i = 0; i < performancePixels.Bytes.Length; i += 4)
        {
            var alpha = performancePixels.Bytes[i + 3];
            Assert(performancePixels.Bytes[i] <= alpha && performancePixels.Bytes[i + 1] <= alpha
                && performancePixels.Bytes[i + 2] <= alpha, "Performance rendering produced a non-premultiplied pixel.");
        }
        Assert(performancePixels.AlphaAt(0, 0) == 0 && performancePixels.AlphaAt(239, 239) == 0,
            "Performance rendering changed transparent margins.");
        AssertBounds(overlay, 150, 250, 240, 240);
        overlay.SetHighQualityRendering(true);
        overlay.Render(model, dimensions, 1.5, 1.5, 150, 250);
        Assert(qualityPixels.Bytes.SequenceEqual(ReadPixels(overlay).Bytes), "Returning to high quality changed its original output.");
        overlay.SetHighQualityRendering(false);
        overlay.Render(model, new CursorDimensions(48), 1.5, 1.5, 150, 250);
        var graphics = (Graphics)GetField(GetField(overlay, "_surface"), "_graphics");
        Assert(graphics.InterpolationMode == InterpolationMode.Bilinear
            && graphics.CompositingQuality == CompositingQuality.HighSpeed,
            "Resizing lost the selected rendering quality.");
    }

    private static void ResourceLifetime()
    {
        using (var warmup = new NativeCursorOverlay())
        {
            warmup.Render(new CursorModel(), new CursorDimensions(30), 1, 1, 0, 0);
        }
        var process = Process.GetCurrentProcess();
        var gdiBefore = GetGuiResources(process.Handle, 0);
        var userBefore = GetGuiResources(process.Handle, 1);
        for (var i = 0; i < 20; i++)
        {
            var overlay = new NativeCursorOverlay();
            var handle = overlay.Handle;
            overlay.Render(new CursorModel(), new CursorDimensions(30), 1, 1, 0, 0);
            overlay.Render(new CursorModel(), new CursorDimensions(64), 1.5, 1.5, 10, 10);
            overlay.Dispose();
            overlay.Dispose();
            Assert(!IsWindow(handle) && overlay.Handle == IntPtr.Zero && !overlay.IsVisible,
                "Disposal did not destroy the overlay window.");
            try
            {
                overlay.SetVisible(true);
                throw new InvalidOperationException("A disposed overlay accepted a visibility change.");
            }
            catch (ObjectDisposedException)
            {
            }
        }
        Assert(GetGuiResources(process.Handle, 0) <= gdiBefore, "Repeated rendering and disposal leaked GDI handles.");
        Assert(GetGuiResources(process.Handle, 1) <= userBefore, "Repeated disposal leaked window handles.");
    }

    private static PixelData ReadPixels(NativeCursorOverlay overlay)
    {
        var surface = GetField(overlay, "_surface");
        var bitmap = (Bitmap)GetField(surface, "_bitmap");
        var area = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var locked = bitmap.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var bytes = new byte[bitmap.Width * bitmap.Height * 4];
            for (var y = 0; y < bitmap.Height; y++)
            {
                Marshal.Copy(locked.Scan0 + y * locked.Stride, bytes, y * bitmap.Width * 4, bitmap.Width * 4);
            }
            return new PixelData(bitmap.Width, bitmap.Height, bytes);
        }
        finally
        {
            bitmap.UnlockBits(locked);
        }
    }

    private static object GetField(object value, string name) =>
        value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value)!;

    private static void SetModelValue(CursorModel model, string property, double value) =>
        typeof(CursorModel).GetProperty(property, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(model, value);

    private static void AssertBounds(NativeCursorOverlay overlay, int x, int y, int width, int height)
    {
        Assert(NativeMethods.GetWindowRect(overlay.Handle, out var bounds), "Unable to read native window bounds.");
        Assert(bounds.Left == x && bounds.Top == y && bounds.Right - bounds.Left == width
            && bounds.Bottom - bounds.Top == height, "Native window bounds do not match the submitted pixels and position.");
    }

    private static void Near(double expected, double actual, double tolerance)
    {
        Assert(Math.Abs(expected - actual) <= tolerance, $"Expected {expected}, got {actual} with tolerance {tolerance}.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Run(string name, Action test)
    {
        test();
        _passed++;
        Console.WriteLine($"PASS {name}");
    }

    private sealed class PixelData(int width, int height, byte[] bytes)
    {
        internal byte[] Bytes => bytes;
        internal byte AlphaAt(int x, int y) => bytes[(y * width + x) * 4 + 3];
        internal long AlphaSum => bytes.Where((_, index) => index % 4 == 3).Sum(value => (long)value);
        internal (double X, double Y) Centroid
        {
            get
            {
                double mass = 0, xSum = 0, ySum = 0;
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        var alpha = AlphaAt(x, y);
                        mass += alpha;
                        xSum += (x + 0.5) * alpha;
                        ySum += (y + 0.5) * alpha;
                    }
                }
                return (xSum / mass, ySum / mass);
            }
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr process, uint flags);
}
