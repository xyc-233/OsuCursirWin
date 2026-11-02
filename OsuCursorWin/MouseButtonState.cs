using System;

namespace OsuCursorWin;

[Flags]
internal enum CursorButtons
{
    None = 0,
    Left = 1,
    Right = 2,
    Middle = 4,
    X1 = 8,
    X2 = 16
}

internal sealed class MouseButtonState
{
    internal CursorButtons Buttons { get; private set; }
    internal bool IsPressed => Buttons != CursorButtons.None;

    internal bool SetButton(CursorButtons button, bool pressed)
    {
        var updated = pressed ? Buttons | button : Buttons & ~button;
        if (updated == Buttons)
        {
            return false;
        }

        Buttons = updated;
        return true;
    }

    internal void Reset(CursorButtons buttons = CursorButtons.None)
    {
        Buttons = buttons;
    }
}
