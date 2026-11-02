# osu! Cursor for Windows

[English](README.en.md) | 中文

为 Windows 10/11 64 位系统添加 osu! 风格的全局光标。

https://github.com/user-attachments/assets/4e4088e9-9a9e-4c97-8205-bad2f7645db5

## 功能

- 拖动旋转、点击缩放与发光，可选点击和悬停音效。
- 调整窗口大小时自动切换光标方向。
- 跟随系统光标自动隐藏，支持开机自启。
- 可调光标大小、动画帧率和渲染质量。

## 安装与使用

运行 `OsuCursorWin-Setup-版本号-x64.exe`，确认管理员授权即可安装。安装包内置 .NET 运行时，并自动配置开始菜单覆盖权限；日常启动无需管理员授权。

便携版直接运行 `OsuCursorWin.exe`，需要 .NET 8 Desktop Runtime，不支持开始菜单覆盖。

右键托盘图标打开设置或退出程序，设置修改后自动保存。更新时运行新版安装包即可。

## 构建

需要 .NET 8 SDK；构建安装包还需 [Inno Setup 6](https://jrsoftware.org/isdl.php)。

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build.ps1
powershell -ExecutionPolicy Bypass -File scripts\build-installer.ps1
```

便携版输出到 `publish`，安装包输出到 `artifacts`。运行 `scripts\test.ps1` 可执行回归测试。

## 致谢

参考 [solstice23/osu-cursor](https://github.com/solstice23/osu-cursor)，光标素材来自 [ppy/osu-resources](https://github.com/ppy/osu-resources)。部分代码由 AI 辅助完成。
