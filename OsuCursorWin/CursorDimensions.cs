using System;

namespace OsuCursorWin;

internal readonly record struct CursorDimensions
{
    internal const double DefaultWidth = 30.0;
    internal const double MinWidth = 16.0;
    internal const double MaxWidth = 64.0;

    internal CursorDimensions(double width)
    {
        Width = NormalizeWidth(width);
    }

    internal double Width { get; }
    internal double Height => Width * (42.5 / DefaultWidth);
    internal double WindowSize => Width * (160.0 / DefaultWidth);
    internal double Margin => Width * (64.0 / DefaultWidth);

    internal static double NormalizeWidth(double width) =>
        double.IsFinite(width) ? Math.Clamp(width, MinWidth, MaxWidth) : DefaultWidth;
}
