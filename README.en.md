# osu! Cursor for Windows

[中文](README.md) | English

An osu!-style global cursor for 64-bit Windows 10/11.

https://github.com/user-attachments/assets/d3142455-bdbc-49af-a330-afb258b63654

## Features

- Rotation while dragging, scale and glow on click, and optional click and hover sounds.
- Direction changes when resizing windows.
- Automatic hiding with the system cursor and optional login startup.
- Adjustable cursor size, animation frame rate, and rendering quality.

## Install and use

Run `OsuCursorWin-Setup-version-x64.exe` and approve the administrator prompt. The installer includes the .NET runtime and configures Start menu coverage. Normal launches do not require administrator privileges.

The portable `OsuCursorWin.exe` requires .NET 8 Desktop Runtime and does not provide Start menu coverage.

Right-click the tray icon to open settings or exit. Settings save automatically. Run a newer installer to upgrade.

## Build

Requires .NET 8 SDK and, for the installer, [Inno Setup 6](https://jrsoftware.org/isdl.php).

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build.ps1
powershell -ExecutionPolicy Bypass -File scripts\build-installer.ps1
```

Portable output is in `publish`; installer output is in `artifacts`. Run `scripts\test.ps1` for regression checks.

## Credits

Inspired by [solstice23/osu-cursor](https://github.com/solstice23/osu-cursor), with cursor assets from [ppy/osu-resources](https://github.com/ppy/osu-resources). Some code was developed with AI assistance.
