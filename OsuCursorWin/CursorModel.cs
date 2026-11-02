using System;

namespace OsuCursorWin;

internal readonly record struct CursorPosition(int X, int Y);

internal readonly record struct ButtonChanges(CursorButtons Pressed, CursorButtons Released)
{
    internal bool Any => Pressed != CursorButtons.None || Released != CursorButtons.None;
}

internal sealed class CursorModel
{
    internal const double SpriteAngle = 24.3;
    private CursorPosition _downStart;
    private double _angleVelocity;
    private double _scaleVelocity;
    private double _opacityVelocity;
    private double _elasticStartAngle;
    private double _elasticDuration;
    private double _elasticElapsed;
    private ResizeDirection _resizeDirection;

    internal CursorPosition Position { get; private set; }
    internal CursorButtons Buttons { get; private set; }
    internal bool IsPressed => Buttons != CursorButtons.None;
    internal bool IsDragging { get; private set; }
    internal bool IsReturning { get; private set; }
    internal bool IsHovering { get; set; }
    internal ResizeDirection ResizeDirection
    {
        get => _resizeDirection;
        set
        {
            if (_resizeDirection == value)
            {
                return;
            }

            _resizeDirection = value;
            if (IsPressed)
            {
                return;
            }

            if (value != ResizeDirection.None)
            {
                IsReturning = false;
                _elasticElapsed = 0.0;
            }

            _angleVelocity = 0.0;
        }
    }
    internal double Angle { get; private set; }
    internal double Scale { get; private set; } = 1.0;
    internal double Opacity { get; private set; }

    internal void MoveTo(int x, int y, double dragThreshold)
    {
        Position = new CursorPosition(x, y);
        if (!IsPressed || IsDragging)
        {
            return;
        }

        var dx = (double)x - _downStart.X;
        var dy = (double)y - _downStart.Y;
        if (dx * dx + dy * dy > dragThreshold * dragThreshold)
        {
            IsDragging = true;
        }
    }

    internal ButtonChanges SetButton(CursorButtons button, bool pressed) =>
        SetButtons(pressed ? Buttons | button : Buttons & ~button);

    internal ButtonChanges SetButtons(CursorButtons buttons)
    {
        var changes = new ButtonChanges(buttons & ~Buttons, Buttons & ~buttons);
        var wasPressed = IsPressed;
        Buttons = buttons;

        if (!wasPressed && IsPressed)
        {
            _downStart = Position;
            IsDragging = false;
            IsReturning = false;
            _angleVelocity = 0.0;
        }
        else if (wasPressed && !IsPressed)
        {
            if (_resizeDirection == ResizeDirection.None && IsDragging && Math.Abs(Angle) >= 0.5)
            {
                _elasticStartAngle = Angle;
                _elasticDuration = 0.6 * (1.0 + Math.Abs(Angle / 720.0));
                _elasticElapsed = 0.0;
                IsReturning = true;
                _angleVelocity = 0.0;
            }
            IsDragging = false;
        }

        return changes;
    }

    internal bool Advance(double elapsedSeconds)
    {
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds <= 0.0)
        {
            return false;
        }

        var remaining = Math.Min(elapsedSeconds, 0.1);
        while (remaining > 0.0)
        {
            var step = Math.Min(remaining, 1.0 / 120.0);
            Update(step);
            remaining -= step;
        }

        return Settle();
    }

    internal void Reset()
    {
        Buttons = CursorButtons.None;
        IsDragging = false;
        IsReturning = false;
        IsHovering = false;
        _resizeDirection = ResizeDirection.None;
        Angle = 0.0;
        Scale = 1.0;
        Opacity = 0.0;
        _angleVelocity = 0.0;
        _scaleVelocity = 0.0;
        _opacityVelocity = 0.0;
        _elasticElapsed = 0.0;
    }

    private double TargetAngle => IsPressed ? (IsDragging ? CalculateDragAngle() : 0.0) : _resizeDirection switch
    {
        ResizeDirection.Top => SpriteAngle + 180.0,
        ResizeDirection.TopRight => SpriteAngle + 225.0,
        ResizeDirection.Right => SpriteAngle + 270.0,
        ResizeDirection.BottomRight => SpriteAngle + 315.0,
        ResizeDirection.Bottom => SpriteAngle,
        ResizeDirection.BottomLeft => SpriteAngle + 45.0,
        ResizeDirection.Left => SpriteAngle + 90.0,
        ResizeDirection.TopLeft => SpriteAngle + 135.0,
        _ => 0.0
    };

    private void Update(double dt)
    {
        var targetScale = IsPressed ? 0.9 : 1.0;
        var targetOpacity = IsPressed || IsHovering ? 1.0 : 0.0;

        if (IsPressed)
        {
            Angle += NormalizeAngle(TargetAngle - Angle) * Math.Min(dt * 8.0, 1.0);
            _angleVelocity = 0.0;
        }
        else if (IsReturning)
        {
            _elasticElapsed += dt;
            var t = Math.Min(1.0, _elasticElapsed / _elasticDuration);
            Angle = _elasticStartAngle * (1.0 - ElasticOut(t));
            targetOpacity = 0.0;
            if (t >= 1.0)
            {
                Angle = 0.0;
                IsReturning = false;
                _angleVelocity = 0.0;
            }
        }
        else
        {
            _angleVelocity += (240.0 * NormalizeAngle(TargetAngle - Angle) - 20.0 * _angleVelocity) * dt;
            Angle += _angleVelocity * dt;
        }

        _scaleVelocity += (240.0 * (targetScale - Scale) - 20.0 * _scaleVelocity) * dt;
        Scale = Math.Clamp(Scale + _scaleVelocity * dt, 0.8, 1.1);
        _opacityVelocity += (160.0 * (targetOpacity - Opacity) - 18.0 * _opacityVelocity) * dt;
        Opacity = Math.Clamp(Opacity + _opacityVelocity * dt, 0.0, 1.0);
    }

    private bool Settle()
    {
        var targetScale = IsPressed ? 0.9 : 1.0;
        var targetOpacity = IsPressed || IsHovering ? 1.0 : 0.0;
        if (IsReturning
            || Math.Abs(NormalizeAngle(TargetAngle - Angle)) > 0.02
            || Math.Abs(_angleVelocity) > 0.02
            || Math.Abs(targetScale - Scale) > 0.0001
            || Math.Abs(_scaleVelocity) > 0.001
            || Math.Abs(targetOpacity - Opacity) > 0.0001
            || Math.Abs(_opacityVelocity) > 0.001)
        {
            return false;
        }

        Angle = TargetAngle;
        Scale = targetScale;
        Opacity = targetOpacity;
        _angleVelocity = 0.0;
        _scaleVelocity = 0.0;
        _opacityVelocity = 0.0;
        return true;
    }

    private double CalculateDragAngle()
    {
        var dx = (double)Position.X - _downStart.X;
        var dy = (double)Position.Y - _downStart.Y;
        return Math.Atan2(-dx, dy) * 180.0 / Math.PI + SpriteAngle;
    }

    private static double NormalizeAngle(double degrees)
    {
        degrees %= 360.0;
        return degrees > 180.0 ? degrees - 360.0 : degrees < -180.0 ? degrees + 360.0 : degrees;
    }

    private static double ElasticOut(double t) =>
        Math.Pow(2.0, -10.0 * t) * Math.Sin((0.5 * t - 0.075) * 20.943951023931955)
        + 1.0 - 0.0004882812499999998 * t;
}
