# ScrcpyControlGUI

面向 Windows 的 scrcpy 图形控制台。它把命令行参数、设备管理、文件传输、中文输入、手机按键、截图和运行日志集中到一个桌面界面中，同时保留 scrcpy 的独立投屏窗口。

当前版本：`0.0.1`

> 本项目不是 scrcpy 官方项目。镜像与设备控制能力来自 [Genymobile/scrcpy](https://github.com/Genymobile/scrcpy)。

## 项目背景

scrcpy 具有低延迟、高质量和跨平台等优点，但大量功能需要通过命令行参数配置。对于需要频繁切换参数、传输文件、输入中文或执行手机按键操作的用户，完全依赖终端并不方便。

ScrcpyControlGUI 的目标是在不修改 scrcpy 核心协议的前提下，提供一个适合 Windows 桌面环境的控制层：

- 保留 scrcpy 独立窗口和原始性能。
- 将常用操作放到清晰的图形界面中。
- 提供 scrcpy 命令行没有直接覆盖的工作流，例如中文候选词输入、手机截图到 Windows 剪贴板和文件管理。
- 继续允许用户使用完整的 scrcpy 参数。

## 实现原理

项目使用 .NET 8 WPF 开发，通过以下方式与 scrcpy、ADB 和 Windows 交互：

1. 从 `scrcpy.exe --help` 动态解析参数定义，并生成分类表单。
2. 使用同目录中的 `adb.exe` 检测、授权和查询 Android 设备。
3. 启动独立的 `scrcpy.exe` 子进程，隐藏控制台窗口并重定向日志。
4. 使用 Win32 低级键鼠钩子判断用户是否正在操作 scrcpy 窗口。
5. 通过无边框、透明、无任务栏图标的 WPF 输入宿主接收 Windows 输入法文本。
6. 使用 Windows 剪贴板和 scrcpy 粘贴快捷键将中文发送到手机。
7. 使用 ADB Shell 和文件传输命令执行手机按键、截图和文件管理。
8. 使用 Android `screencap`、MediaStore 媒体扫描和 Windows 图像剪贴板实现两种截图模式。

## 功能

### 设备与投屏

- 自动检测 ADB 设备，每 2 秒刷新状态。
- 顶部设备选择器与连接状态集中显示。
- 支持启动、停止 scrcpy。
- 隐藏 scrcpy 控制台窗口，保留独立投屏窗口。
- 支持显示设备和运行状态。

### 参数设置

- 动态读取 scrcpy 5.0.1 的 109 个参数。
- 中文名称、中文说明和 scrcpy 原始英文说明同时显示。
- 按连接、视频、音频、窗口、输入、电源、剪贴板、录制、诊断等类型分类。
- 支持参数搜索、分类筛选和只显示已修改项。
- 支持数字、开关、下拉、颜色、文件、组合键等参数控件。
- 支持配置加载、配置保存、恢复默认和原始参数追加。
- 自动检查常见互斥参数和不合法数值。

### 手机控制

控制台提供一行常用手机按键：

- 返回
- 主屏幕
- 最近任务
- 菜单
- 音量加减
- 电源键
- 熄灭 / 点亮屏幕
- 系统截屏
- 截图到电脑剪贴板

系统截屏保存在手机：

```text
/sdcard/Pictures/Screenshots/
```

截图到电脑会通过 ADB 拉取 PNG，并写入 Windows 剪贴板，可以直接粘贴到聊天工具、绘图软件或文档中。

### 中文输入

- 点击手机输入框时自动识别 Android 文本焦点。
- 输入宿主透明且几乎不可见，不额外创建常驻悬浮窗。
- 支持 Windows 中文输入法和候选词。
- 输入法组合期间不会提交拼音或未选择的候选词。
- 自动发送默认开启，默认防抖时间为 100 ms，可在设置中调整。
- 支持按 Enter 立即发送。
- 发送后保留输入宿主，可连续输入多段文字。
- 新输入不会被发送到 scrcpy 窗口，避免文字顺序错乱。
- 支持 Backspace、Delete、Ctrl+A、Ctrl+C、Ctrl+X、Ctrl+V、Ctrl+Z、Ctrl+Y。
- 本地输入宿主为空时，常用编辑快捷键会转发给手机。
- 最新发送的文字保留在 Windows 剪贴板中。

### 文件管理

- 浏览 Android 目录和文件。
- 支持多选文件。
- 支持上传、下载、拖放上传。
- 显示传输进度和状态。
- 支持中文和空格文件名。
- 支持复制所选名称、完整手机路径和传输任务信息。

### 智能亮度

- 只统计发生在 scrcpy 窗口内的键盘、点击、滚轮和拖动。
- 不把其他 Windows 程序的操作误判为 scrcpy 操作。
- 空闲一段时间后降低手机系统亮度，但不锁屏。
- 下一次有效 scrcpy 操作时恢复原亮度和亮度模式。
- 支持配置空闲时间和目标亮度。

### 运行和界面

- 中文和英文参数说明同时展示。
- 日志实时显示并可保存。
- 设备、参数、文件、日志和设置页面使用统一深色主题。
- 提高标题、说明文字、表格表头和选中行对比度。
- 适配窗口最小尺寸，避免主要按钮和文字重叠。

## 主要优化点

- 使用 `CreateNoWindow` 启动 scrcpy，消除额外命令行窗口。
- 通过窗口消息向 scrcpy 发送粘贴快捷键，不抢占输入宿主焦点。
- 使用 WPF `TextInputStart`、`TextInputUpdate` 和 `TextInput` 事件识别 TSF 输入法组合状态。
- 保留 Win32 IMM 状态检测，兼容不同 Windows 输入法。
- 剪贴板使用持久化 `DataObject` 并回读校验，避免旧剪贴板对象恢复导致失效。
- 设备自动刷新只在设备列表或状态变化时更新界面，避免定时闪烁。
- 输入发送使用串行队列，避免连续文本粘贴顺序错乱。
- 配置目录不可写时自动回退到 `%LOCALAPPDATA%\ScrcpyControl`。

## 系统要求

- Windows 10 / Windows 11 x64
- Android 5.0 或更高版本
- scrcpy 5.0.1 Windows x64 运行环境
- USB 数据线
- 手机已开启开发者选项和 USB 调试

本项目的单文件版本自带 .NET 8 运行时，目标电脑不需要额外安装 .NET。

## 使用方法

### 方式一：使用 Release 便携包

1. 从 [Releases](https://github.com/LiuJGzH/ScrcpyControlGUI/releases/tag/v0.0.1) 下载 Windows x64 便携包。
2. 解压完整目录，不要只在压缩包内运行。
3. 在手机中开启 USB 调试。
4. 使用 USB 数据线连接手机和电脑。
5. 手机弹出 USB 调试授权时，勾选“一律允许使用这台计算机”。
6. 双击 `ScrcpyControl.exe`。
7. 等待设备状态显示“设备已连接”。
8. 点击“启动 scrcpy”。

### 方式二：使用单独 EXE

Release 中也提供单独的 `ScrcpyControl.exe`。该 EXE 仍需与 **scrcpy 5.0.1 Windows x64** 运行文件放在同一目录。

下载 scrcpy：

<https://github.com/Genymobile/scrcpy/releases/tag/v5.0.1>

目录至少应包含：

```text
ScrcpyControl.exe
scrcpy.exe
adb.exe
scrcpy-server
SDL3.dll
avcodec-63.dll
avformat-63.dll
avutil-61.dll
swresample-7.dll
libusb-1.0.dll
```

### 开启 USB 调试

不同 Android 系统的入口可能略有差异。常见步骤如下：

1. 打开“设置 → 关于手机 / 我的设备”。
2. 连续点击系统版本约 7 次，开启开发者模式。
3. 返回“设置 → 系统 / 更多设置 → 开发者选项”。
4. 开启“USB 调试”。
5. 连接 USB 后允许电脑的调试授权。

## 从源码构建

### 环境

- .NET 8 SDK
- Windows 10 / Windows 11 x64

### 构建

```powershell
dotnet build .\Source\ScrcpyControl\ScrcpyControl.csproj -c Release
```

### 发布单文件

```powershell
dotnet publish .\Source\ScrcpyControl\ScrcpyControl.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -o .\publish
```

发布结果中的 `ScrcpyControl.exe` 不要求目标电脑安装 .NET。

### 运行集成测试

测试需要可用的 scrcpy 目录和已授权的 ADB 设备：

```powershell
$env:SCRCPY_DIR = "C:\path\to\scrcpy-win64-v5.0.1"
dotnet run --project .\Source\ScrcpyControl.Tests\ScrcpyControl.Tests.csproj -c Release
```

## 目录结构

```text
ScrcpyControlGUI/
├─ README.md
├─ LICENSE
├─ .gitignore
├─ .gitattributes
└─ Source/
   ├─ ScrcpyControl/
   │  ├─ App.xaml
   │  ├─ MainWindow.xaml
   │  ├─ InputSinkWindow.xaml
   │  ├─ Infrastructure/
   │  ├─ Localization/
   │  ├─ Models/
   │  ├─ Services/
   │  └─ Themes/
   └─ ScrcpyControl.Tests/
      ├─ Program.cs
      └─ ScrcpyControl.Tests.csproj
```

## Release 资产

`v0.0.1` 提供：

- `ScrcpyControl.exe`：自包含控制台可执行文件。
- `ScrcpyControl-0.0.1-win64.zip`：包含 scrcpy 5.0.1 运行环境和控制台的便携包。

便携包中的 scrcpy 二进制遵循其原始 Apache-2.0 许可证。scrcpy 许可证会随便携包一起分发。

## 已知限制

- 仅支持 Windows 10/11 x64。
- 项目针对 scrcpy 5.0.1 编写；其他 scrcpy 版本可能出现参数或行为差异。
- 单文件 EXE 不是独立投屏程序，仍需同目录中的 scrcpy 运行文件。
- 不提供 root 功能。
- 不提供 APK 安装、文件删除和文件重命名。
- 文件访问受 Android 权限和目录权限限制。
- 智能亮度只统计通过 scrcpy 发生的键鼠操作，不统计直接触摸手机物理屏幕。
- 首次运行未签名 EXE 时，Windows SmartScreen 可能显示“未知发布者”。

## 安全说明

USB 调试允许电脑执行高权限设备操作。请只在自己控制的电脑上授权，并在使用后关闭不再需要的 USB 调试连接。

## 许可证

项目源代码使用 [MIT License](LICENSE)。

scrcpy、ADB 和相关运行文件遵循其各自原始许可证。
