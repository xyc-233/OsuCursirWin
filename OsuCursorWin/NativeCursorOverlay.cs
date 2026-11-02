using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;

namespace OsuCursorWin;

internal sealed class NativeCursorOverlay : IDisposable
{
    private const uint OverlayStyles = 0x00080000 | 0x00000008 | 0x00000080 | 0x00000020 | 0x08000000;
    private Bitmap? _baseImage;
    private Bitmap? _glowImage;
    private Bitmap? _scaledBase;
    private Bitmap? _scaledGlow;
    private DrawingSurface? _surface;
    private readonly ImageAttributes _glowAttributes = new();
    private readonly ColorMatrix _glowMatrix = new();
    private readonly PointF[] _glowPoints = new PointF[3];
    private double _lastGlowOpacity = double.NaN;
    private IntPtr _window;
    private bool _highQualityRendering = true;
    private bool _disposed;

    internal NativeCursorOverlay()
    {
        try
        {
            _baseImage = LoadImage("OsuCursorWin.Images.cursor.png");
            _glowImage = LoadImage("OsuCursorWin.Images.cursorAdditive.png");
            _window = CreateWindowEx(OverlayStyles, "STATIC", "OsuCursorOverlay", 0x80000000,
                0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (_window == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal IntPtr Handle => _window;
    internal bool IsVisible => _window != IntPtr.Zero && IsWindowVisible(_window);

    internal void SetVisible(bool visible)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ShowWindow(_window, visible ? 4 : 0);
    }

    internal void SetHighQualityRendering(bool enabled)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_highQualityRendering == enabled)
        {
            return;
        }

        _highQualityRendering = enabled;
        if (_surface is not null)
        {
            ConfigureGraphics(_surface.Graphics, enabled);
        }
    }

    internal void Render(CursorModel model, CursorDimensions dimensions, double dpiX, double dpiY, int windowX, int windowY)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(model);
        if (!double.IsFinite(dpiX) || !double.IsFinite(dpiY) || dpiX <= 0.0 || dpiY <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(dpiX));
        }

        var width = checked((int)Math.Ceiling(dimensions.WindowSize * dpiX));
        var height = checked((int)Math.Ceiling(dimensions.WindowSize * dpiY));
        var spriteWidth = checked((int)Math.Ceiling(dimensions.Width * dpiX));
        var spriteHeight = checked((int)Math.Ceiling(dimensions.Height * dpiY));
        EnsureImages(spriteWidth, spriteHeight);
        if (_surface is null || _surface.Width != width || _surface.Height != height)
        {
            var replacement = new DrawingSurface(width, height, _highQualityRendering);
            _surface?.Dispose();
            _surface = replacement;
        }

        var graphics = _surface.Graphics;
        graphics.ResetTransform();
        graphics.Clear(Color.Transparent);
        graphics.TranslateTransform((float)(dimensions.Margin * dpiX), (float)(dimensions.Margin * dpiY));
        graphics.ScaleTransform((float)dpiX, (float)dpiY);
        graphics.RotateTransform((float)model.Angle);
        graphics.ScaleTransform((float)model.Scale, (float)model.Scale);
        var destination = new RectangleF(0.0f, 0.0f, (float)dimensions.Width, (float)dimensions.Height);
        graphics.DrawImage(_scaledBase!, destination, new RectangleF(0, 0, spriteWidth, spriteHeight), GraphicsUnit.Pixel);
        if (model.Opacity > 0.0)
        {
            if (_lastGlowOpacity != model.Opacity)
            {
                _glowMatrix.Matrix33 = (float)model.Opacity;
                _glowAttributes.SetColorMatrix(_glowMatrix);
                _lastGlowOpacity = model.Opacity;
            }

            _glowPoints[1] = new PointF(destination.Width, 0);
            _glowPoints[2] = new PointF(0, destination.Height);
            graphics.DrawImage(_scaledGlow!, _glowPoints, new RectangleF(0, 0, spriteWidth, spriteHeight), GraphicsUnit.Pixel, _glowAttributes);
        }

        graphics.Flush(FlushIntention.Sync);
        var location = new NativePoint(windowX, windowY);
        var size = new NativeSize(width, height);
        var source = new NativePoint(0, 0);
        var blend = new BlendFunction { SourceConstantAlpha = 255, AlphaFormat = 1 };
        if (!UpdateLayeredWindow(_window, IntPtr.Zero, ref location, ref size, _surface.DeviceContext,
            ref source, 0, ref blend, 2))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_window != IntPtr.Zero)
        {
            DestroyWindow(_window);
            _window = IntPtr.Zero;
        }
        _surface?.Dispose();
        _surface = null;
        _scaledBase?.Dispose();
        _scaledGlow?.Dispose();
        _baseImage?.Dispose();
        _glowImage?.Dispose();
        _glowAttributes.Dispose();
    }

    private void EnsureImages(int width, int height)
    {
        if (_scaledBase?.Width == width && _scaledBase.Height == height)
        {
            return;
        }

        var baseImage = ScaleImage(_baseImage!, width, height);
        Bitmap glowImage;
        try
        {
            glowImage = ScaleImage(_glowImage!, width, height);
        }
        catch
        {
            baseImage.Dispose();
            throw;
        }

        _scaledBase?.Dispose();
        _scaledGlow?.Dispose();
        _scaledBase = baseImage;
        _scaledGlow = glowImage;
    }

    private static Bitmap LoadImage(string resourceName)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing embedded resource: {resourceName}");
        using var original = new Bitmap(stream);
        return ScaleImage(original, original.Width, original.Height);
    }

    private static Bitmap ScaleImage(Bitmap image, int width, int height)
    {
        var scaled = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        try
        {
            using var graphics = Graphics.FromImage(scaled);
            ConfigureGraphics(graphics);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            using var attributes = new ImageAttributes();
            attributes.SetWrapMode(WrapMode.TileFlipXY);
            graphics.DrawImage(image, new Rectangle(0, 0, width, height), 0, 0, image.Width, image.Height,
                GraphicsUnit.Pixel, attributes);
            return scaled;
        }
        catch
        {
            scaled.Dispose();
            throw;
        }
    }

    private static void ConfigureGraphics(Graphics graphics, bool highQuality = true)
    {
        graphics.CompositingMode = CompositingMode.SourceOver;
        graphics.CompositingQuality = highQuality ? CompositingQuality.HighQuality : CompositingQuality.HighSpeed;
        graphics.InterpolationMode = highQuality ? InterpolationMode.HighQualityBicubic : InterpolationMode.Bilinear;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.SmoothingMode = highQuality ? SmoothingMode.HighQuality : SmoothingMode.None;
        graphics.PageUnit = GraphicsUnit.Pixel;
    }

    private sealed class DrawingSurface : IDisposable
    {
        private IntPtr _bitmapHandle;
        private IntPtr _previousBitmap;
        private Bitmap? _bitmap;
        private Graphics? _graphics;

        internal int Width { get; }
        internal int Height { get; }
        internal IntPtr DeviceContext { get; private set; }
        internal Graphics Graphics => _graphics!;

        internal DrawingSurface(int width, int height, bool highQuality)
        {
            Width = width;
            Height = height;
            try
            {
                DeviceContext = CreateCompatibleDC(IntPtr.Zero);
                if (DeviceContext == IntPtr.Zero)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                var info = new BitmapInfo
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfo>(),
                    Width = width,
                    Height = -height,
                    Planes = 1,
                    BitCount = 32
                };
                _bitmapHandle = CreateDIBSection(DeviceContext, ref info, 0, out var bits, IntPtr.Zero, 0);
                if (_bitmapHandle == IntPtr.Zero || bits == IntPtr.Zero)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                _previousBitmap = SelectObject(DeviceContext, _bitmapHandle);
                if (_previousBitmap == IntPtr.Zero || _previousBitmap == new IntPtr(-1))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                _bitmap = new Bitmap(width, height, checked(width * 4), PixelFormat.Format32bppPArgb, bits);
                _graphics = Graphics.FromImage(_bitmap);
                ConfigureGraphics(_graphics, highQuality);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            _graphics?.Dispose();
            _graphics = null;
            _bitmap?.Dispose();
            _bitmap = null;
            if (DeviceContext != IntPtr.Zero && _previousBitmap != IntPtr.Zero && _previousBitmap != new IntPtr(-1))
            {
                SelectObject(DeviceContext, _previousBitmap);
                _previousBitmap = IntPtr.Zero;
            }
            if (_bitmapHandle != IntPtr.Zero)
            {
                DeleteObject(_bitmapHandle);
                _bitmapHandle = IntPtr.Zero;
            }
            if (DeviceContext != IntPtr.Zero)
            {
                DeleteDC(DeviceContext);
                DeviceContext = IntPtr.Zero;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativePoint(int x, int y)
    {
        internal readonly int X = x;
        internal readonly int Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeSize(int width, int height)
    {
        internal readonly int Width = width;
        internal readonly int Height = height;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BlendFunction
    {
        internal byte BlendOp;
        internal byte BlendFlags;
        internal byte SourceConstantAlpha;
        internal byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        internal uint Size;
        internal int Width;
        internal int Height;
        internal ushort Planes;
        internal ushort BitCount;
        internal uint Compression;
        internal uint ImageSize;
        internal int XPelsPerMeter;
        internal int YPelsPerMeter;
        internal uint ColorsUsed;
        internal uint ColorsImportant;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint extendedStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateLayeredWindow(IntPtr window, IntPtr destinationDc, ref NativePoint destination,
        ref NativeSize size, IntPtr sourceDc, ref NativePoint source, uint colorKey, ref BlendFunction blend, uint flags);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr deviceContext);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateDIBSection(IntPtr deviceContext, ref BitmapInfo info, uint usage,
        out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr SelectObject(IntPtr deviceContext, IntPtr value);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr value);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr deviceContext);
}
