using System;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Image = System.Windows.Controls.Image;
using Point = System.Windows.Point;

namespace OsuCursorWin;

internal sealed class CursorVisual : Canvas
{
    private readonly Grid _rotationLayer = new();
    private readonly Grid _scaleLayer = new();
    private readonly RotateTransform _rotation = new();
    private readonly ScaleTransform _scale = new(1.0, 1.0);
    private readonly Image _image = LoadImage("OsuCursorWin.Images.cursor.png");
    private readonly Image _glow = LoadImage("OsuCursorWin.Images.cursorAdditive.png");

    internal CursorVisual(CursorDimensions dimensions)
    {
        IsHitTestVisible = false;
        _rotationLayer.RenderTransformOrigin = new Point(0, 0);
        _rotationLayer.RenderTransform = _rotation;
        _scaleLayer.RenderTransformOrigin = new Point(0, 0);
        _scaleLayer.RenderTransform = _scale;
        _glow.Opacity = 0.0;
        _scaleLayer.Children.Add(_image);
        _scaleLayer.Children.Add(_glow);
        _rotationLayer.Children.Add(_scaleLayer);
        Children.Add(_rotationLayer);
        Resize(dimensions);
    }

    internal void Resize(CursorDimensions dimensions)
    {
        _rotationLayer.Width = _scaleLayer.Width = _image.Width = _glow.Width = dimensions.Width;
        _rotationLayer.Height = _scaleLayer.Height = _image.Height = _glow.Height = dimensions.Height;
        SetLeft(_rotationLayer, dimensions.Margin);
        SetTop(_rotationLayer, dimensions.Margin);
    }

    internal void Render(CursorModel model)
    {
        _rotation.Angle = model.Angle;
        _scale.ScaleX = _scale.ScaleY = model.Scale;
        _glow.Opacity = model.Opacity;
    }

    private static Image LoadImage(string resourceName)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing embedded resource: {resourceName}");
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        var image = new Image { Source = bitmap, Stretch = Stretch.Fill, IsHitTestVisible = false };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.Fant);
        return image;
    }
}
