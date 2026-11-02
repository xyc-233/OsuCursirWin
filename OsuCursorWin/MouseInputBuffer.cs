using System;
using System.Collections.Generic;

namespace OsuCursorWin;

internal readonly record struct MouseInputEvent(uint Message, int X, int Y, uint MouseData);
internal readonly record struct MouseInputSnapshot(uint Buttons, int X, int Y);

internal sealed class MouseInputBuffer
{
    internal const int MaximumMovesPerSegment = 256;
    private readonly List<MouseInputEvent> _events = new();
    private int _moveStart = -1;
    private int _farthestIndex;
    private double _farthestDistance;
    private uint _pressedButtons;
    private int _originX;
    private int _originY;
    private int _latestX;
    private int _latestY;

    internal int Count => _events.Count;

    internal void Add(MouseInputEvent input)
    {
        _latestX = input.X;
        _latestY = input.Y;
        if (input.Message != 0x0200)
        {
            TrackButton(input);
            _moveStart = -1;
            _events.Add(input);
            return;
        }

        if (_moveStart < 0)
        {
            _moveStart = _events.Count;
            _farthestIndex = _moveStart;
            _farthestDistance = -1.0;
            if (_pressedButtons == 0)
            {
                _originX = input.X;
                _originY = input.Y;
            }
        }

        var dx = (double)input.X - _originX;
        var dy = (double)input.Y - _originY;
        var distance = dx * dx + dy * dy;
        if (distance > _farthestDistance)
        {
            _farthestDistance = distance;
            _farthestIndex = _events.Count;
        }

        _events.Add(input);
        if (_events.Count - _moveStart > MaximumMovesPerSegment)
        {
            CompactMoves();
        }
    }

    internal MouseInputEvent[] Drain()
    {
        var events = _events.ToArray();
        Clear();
        return events;
    }

    internal void Clear()
    {
        _events.Clear();
        _moveStart = -1;
    }

    internal void InitializeButtons(uint buttons, int x, int y)
    {
        _pressedButtons = buttons;
        _originX = _latestX = x;
        _originY = _latestY = y;
    }

    internal MouseInputSnapshot ResetTracking()
    {
        var snapshot = new MouseInputSnapshot(_pressedButtons, _latestX, _latestY);
        Clear();
        _originX = snapshot.X;
        _originY = snapshot.Y;
        return snapshot;
    }

    private void TrackButton(MouseInputEvent input)
    {
        var button = input.Message switch
        {
            0x0201 or 0x0202 => 1u,
            0x0204 or 0x0205 => 2u,
            0x0207 or 0x0208 => 4u,
            0x020B or 0x020C => input.MouseData >> 16 == 1 ? 8u : 16u,
            _ => 0u
        };
        if (button == 0)
        {
            return;
        }

        if (input.Message is 0x0201 or 0x0204 or 0x0207 or 0x020B)
        {
            if (_pressedButtons == 0)
            {
                _originX = input.X;
                _originY = input.Y;
            }

            _pressedButtons |= button;
        }
        else
        {
            _pressedButtons &= ~button;
        }
    }

    private void CompactMoves()
    {
        var first = _events[_moveStart];
        var farthest = _events[_farthestIndex];
        var last = _events[^1];
        var farthestIsFirst = _farthestIndex == _moveStart;
        var farthestIsLast = _farthestIndex == _events.Count - 1;
        _events.RemoveRange(_moveStart, _events.Count - _moveStart);
        _events.Add(first);
        if (!farthestIsFirst && !farthestIsLast)
        {
            _events.Add(farthest);
        }

        _events.Add(last);
        _farthestIndex = farthestIsFirst ? _moveStart : _events.Count - (farthestIsLast ? 1 : 2);
    }
}
