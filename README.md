# osu! Cursor for Windows

[English](README.en.md) | 中文



https://github.com/user-attachments/assets/4e4088e9-9a9e-4c97-8205-bad2f7645db5



注意：部分代码由AI生成

Windows 全局光标覆盖层：

- 使用跟随鼠标、点击穿透的原生透明窗口绘制光标
- 临时用透明系统光标隐藏 Windows 原生指针
- 支持拖动时旋转、按下时缩放/发光、悬停可点击元素时播放音效
- 退出后自动恢复原系统光标
- 音效支持


## 运行

双击exe程序，不需要安装。

或使用源码运行：

```powershell
dotnet run --project OsuCursorWin\OsuCursorWin.csproj
```

需要 Windows 10/11 和 .NET 8 Desktop Runtime。默认构建不请求管理员权限，可以直接运行。若需要覆盖开始菜单等 Shell 窗口，请使用下面的 UIAccess 构建和安装步骤。

首次启动会自动打开设置窗口；之后右键托盘图标选择“设置”可重新打开。修改会实时生效并保存到 `%APPDATA%\OsuCursorWin\settings.json`。最小化或关闭设置窗口只会隐藏到后台，不会退出程序。

设置界面使用 Windows 系统配色、标准分组框、复选框、滑块和按钮，支持键盘操作。关闭某项音效后，其音量控件会禁用并保留原值。启动时添加 `--settings` 可直接显示设置窗口。

“性能”分组支持动画帧率上限、渲染质量和静止时暂停连续绘制，修改后立即生效并自动保存。默认跟随当前显示器刷新率、使用高质量绘制并在动画结束后暂停连续绘制。降低帧率只限制旋转、缩放和发光动画，不延迟光标位置更新；“性能优先”使用较快的图像插值，可降低旋转时的绘制开销。帧率上限不会超过当前显示器刷新率。

“登录 Windows 时自动启动”会同步 Windows 启动项的启用状态。在任务管理器中禁用后，重新打开设置会显示未勾选；在程序中重新勾选即可恢复登录启动。

“跟随系统光标自动隐藏”默认开启。游戏或应用隐藏 Windows 光标时，覆盖层会自动隐藏并停止触发音效；系统光标恢复时重新显示，无需添加游戏名单。隐藏期间仍会检查系统状态，托盘手动关闭光标后不会自动重新启用。若游戏完全自行绘制指针，又没有隐藏 Windows 光标，则无法仅凭系统可见状态识别。

敲击音效使用内嵌的 `OsuCursorWin\cursor-tap.wav`，按下和抬起都会播放；悬停音效使用 `OsuCursorWin\default-hover.wav`，进入手形光标状态时播放，并使用 20ms 去抖。所有音频通过独立音频线程和 NAudio 低延迟 WaveOut 通道池播放，UI 线程只负责排队。

普通 `SetWindowPos` 只能改变桌面窗口带内的顺序，无法覆盖开始菜单等沉浸式 Shell 窗口；可选的 UIAccess 构建用于这些场景。

要启用 `uiAccess`，exe 必须位于 `C:\Program Files\` 等安全目录，并且使用本机受信任证书签名。仓库里提供了安装脚本：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -UiAccess
powershell -ExecutionPolicy Bypass -File scripts\install-uiaccess.ps1
```

请以管理员身份运行安装脚本。脚本会停止当前实例、复用有效的本地代码签名证书（不存在时创建并信任）、把 exe 复制到 `C:\Program Files\OsuCursorWin\` 并签名。之后启动 `C:\Program Files\OsuCursorWin\OsuCursorWin.exe`。

程序使用独立线程上的低层鼠标钩子接收移动和点击事件，合批后立即移动原生透明窗口，无需等待动画帧，也不经过 WPF 透明窗口的同步绘制。旋转、缩放和发光使用缓存图片与持久位图缓冲区，通过 Windows 分层窗口提交。独立高精度计时器按光标所在显示器的当前刷新率驱动动画，最多保留一次待执行回调，延迟时跳过过期帧，避免积压后补帧。移动后保留 120ms 的绘制活动，静止且动画结束后停止连续渲染。边缘命中检测由单个后台工作项执行，只保留最新请求，避免其他窗口响应慢时阻塞光标跟随。钩子安装失败时使用轮询。

左右键、中键和两个侧键分别跟踪；输入合批保留按键顺序、按下位置和快速往返拖动，持续高频输入也会给绘制留出执行机会。设置滑块实时生效，停止拖动 250ms 后合并写盘，关闭窗口时立即保存。覆盖层仍经过 Windows 桌面合成，显示延迟不能等同于原生硬件光标。

光标进入可调整大小的窗口边缘或角落时，会朝对应的八个方向平滑旋转。按住拖动调整大小时，保留原有的旋转效果，根据最初按下点与当前位置的连线改变朝向。松开后仍位于边缘则恢复对应方向，离开边缘则恢复原朝向。此行为不受音效开关影响；方向优先使用窗口命中测试，避免把窗口内的分隔线当作外边缘。

悬停在按钮、链接等可点击内容上时，保持光标原有朝向，并保留悬停发光和音效。

光标覆盖层会检测 Windows 任务栏悬浮预览窗口（`TaskListThumbnailWnd`），并把覆盖层临时提到预览窗口之上，避免光标被任务栏预览遮挡。

## 构建

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build.ps1
```

构建产物会输出到 `publish\OsuCursorWin.exe`。默认生成 `win-x64` 框架依赖单文件版本，需要安装 .NET 8 Desktop Runtime。

`-UiAccess` 构建输出到 `publish-uiaccess\OsuCursorWin.exe`，须通过安装脚本签名后运行。

如果开始菜单遮挡光标，请运行 `C:\Program Files\OsuCursorWin\OsuCursorWin.exe`，而不是 `publish` 下的便携版。设置窗口会显示是否已启用开始菜单覆盖。安装版可通过以下命令检查 Windows 是否实际授予 UIAccess，无需退出运行中的实例：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\smoke.ps1 -ExePath "C:\Program Files\OsuCursorWin\OsuCursorWin.exe" -RequireUiAccess
```

## 验证

```powershell
powershell -ExecutionPolicy Bypass -File scripts\test.ps1
```

回归验证覆盖多键输入、快速拖动、不同刷新率下的动画、原生透明窗口的绘制与隐藏、计时器停止与重启、WPF 视图、配置保存、音频转换与异步故障恢复、系统光标替换失败后的恢复，并检查实际构建产物的清单。启动冒烟测试使用独立的 `--smoke` 实例，会在 2.5 秒后退出，不替换系统光标、不安装鼠标钩子、不写入配置或开机启动项。

输入回归还验证钩子的启动、关闭、合批和绘制调度；后台边缘检测回归覆盖慢查询、旧结果丢弃和退出清理。可运行 `dotnet run --project tests\LatencyProbe -c Release -- --rotate --input-hz 8000` 测量高频合成输入经程序队列到窗口位置提交的耗时，以及实际原生绘制回调的间隔和耗时；它不移动系统鼠标，结果不代表物理显示延迟。`tests\PerformanceProbe` 提供独立透明 WPF 窗口的帧率对照。

## 代码结构

`CursorModel` 管理输入和动画状态，`CursorDimensions` 统一尺寸计算，`NativeCursorOverlay` 负责原生窗口和绘制缓冲区，`NativeFrameClock` 负责动画调度。`MainWindow` 连接鼠标事件、窗口位置、托盘和设置，`CursorVisual` 保留 WPF 对照测试。动画模型不依赖 WPF 或 Win32，可独立验证。

音频由 `PcmSample` 处理采样数据，`AudioVoice` 管理单路播放生命周期，`TapSoundPlayer` 管理有界队列和后台线程。关闭最多等待音频线程 250ms；驱动延迟返回时继续在后台安全清理。

## 如果光标没有恢复

正常情况下从托盘退出会自动恢复。如果程序被任务管理器强制结束，系统光标可能仍处于透明状态，可以运行：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\restore-cursor.ps1
```

## 说明

- 仅适用于 Windows 10/11。
- 独占全屏的游戏可能不会显示覆盖层，建议在无边框或窗口化模式下使用。
- 光标图片来自原 `assets/cursor.png` 和 `assets/cursor-additive.png`。
- 悬停音效需要应用支持，效果并不好建议开启”窗口拉伸时播放“选项以改为光标在窗口可拉伸时播放。

## 参考与感谢

- 参考 [solstice23/osu-cursor](https://github.com/solstice23/osu-cursor) 的网页自定义光标实现。
- 光标图片来自 [ppy/osu-resources](https://github.com/ppy/osu-resources)。

## 预计可能出现的问题

- 任务栏预览应用窗口时光标被覆盖导致光标消失。
- 有光标的游戏可能会出现同时存在两个光标。
- ~~未知情况下光标旋转时卡顿，释放时正常。~~
- 在文件资源管理器中光标快速移动时悬停提示会出现撕裂。
