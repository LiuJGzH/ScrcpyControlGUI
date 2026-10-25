using System.Text.RegularExpressions;
using ScrcpyControl.Localization;

namespace ScrcpyControl.Models;

public enum ScrcpyOptionCategory
{
    Connection,
    Video,
    Audio,
    Window,
    Input,
    Power,
    Clipboard,
    Recording,
    Diagnostics,
    Experimental
}

public enum ScrcpyOptionKind
{
    Boolean,
    Text,
    Number,
    Enum,
    File,
    Folder,
    Color,
    KeyValue
}

public sealed class ScrcpyOptionDefinition
{
    public required string CliName { get; init; }
    public required string Label { get; init; }
    public required string Description { get; init; }
    public string ChineseDescription { get; init; } = string.Empty;
    public required ScrcpyOptionCategory Category { get; init; }
    public required ScrcpyOptionKind Kind { get; init; }
    public string? ArgumentName { get; init; }
    public bool OptionalValue { get; init; }
    public bool ActionOnly { get; init; }
    public IReadOnlyList<string> Choices { get; init; } = Array.Empty<string>();
    public string? DefaultHint { get; init; }
}

public sealed class ScrcpyOptionValue
{
    public required ScrcpyOptionDefinition Definition { get; init; }
    public bool IsEnabled { get; set; }
    public string Value { get; set; } = string.Empty;

    public bool IsModified =>
        IsEnabled || (Definition.Kind != ScrcpyOptionKind.Boolean && !string.IsNullOrEmpty(Value));

    public string ToArgument()
    {
        if (Definition.Kind == ScrcpyOptionKind.Boolean)
        {
            return Definition.CliName;
        }

        if (string.IsNullOrEmpty(Value))
        {
            return Definition.CliName;
        }

        return $"{Definition.CliName}={Value}";
    }
}

public sealed class SavedOptionState
{
    public bool Enabled { get; set; }
    public string Value { get; set; } = string.Empty;
}

public sealed class ScrcpyProfile
{
    public string Name { get; set; } = "默认配置";
    public Dictionary<string, SavedOptionState> Options { get; set; } = new(StringComparer.Ordinal);
    public string ExtraArguments { get; set; } = string.Empty;
}

public static partial class ScrcpyOptionCatalog
{
    private static readonly HashSet<string> ActionOnlyOptions = new(StringComparer.Ordinal)
    {
        "--help",
        "--version",
        "--list-apps",
        "--list-cameras",
        "--list-camera-sizes",
        "--list-displays",
        "--list-encoders"
    };

    private static readonly HashSet<string> ConnectionOptions = new(StringComparer.Ordinal)
    {
        "--serial", "--select-usb", "--select-tcpip", "--port", "--tcpip",
        "--tunnel-host", "--tunnel-port", "--force-adb-forward", "--kill-adb-on-close"
    };

    private static readonly HashSet<string> PowerOptions = new(StringComparer.Ordinal)
    {
        "--keep-active", "--stay-awake", "--turn-screen-off", "--screen-off-timeout",
        "--no-power-on", "--power-off-on-close", "--show-touches", "--start-app"
    };

    private static readonly HashSet<string> ClipboardOptions = new(StringComparer.Ordinal)
    {
        "--no-clipboard-autosync", "--legacy-paste"
    };

    private static readonly HashSet<string> RecordingOptions = new(StringComparer.Ordinal)
    {
        "--record", "--record-format", "--record-orientation", "--v4l2-sink",
        "--v4l2-buffer", "--time-limit"
    };

    private static readonly HashSet<string> DiagnosticsOptions = new(StringComparer.Ordinal)
    {
        "--verbosity", "--print-fps", "--no-cleanup", "--pause-on-exit",
        "--no-terminal-title", "--help", "--version", "--list-apps",
        "--list-cameras", "--list-camera-sizes", "--list-displays", "--list-encoders"
    };

    private static readonly HashSet<string> InputOptions = new(StringComparer.Ordinal)
    {
        "--no-control", "--keyboard", "--mouse", "--mouse-bind", "--gamepad",
        "--otg", "--no-key-repeat", "--prefer-text", "--raw-key-events", "--shortcut-mod",
        "--no-mouse-hover"
    };

    private static readonly HashSet<string> ExperimentalOptions = new(StringComparer.Ordinal)
    {
        "--new-display", "--no-vd-destroy-content", "--no-vd-system-decorations",
        "--flex-display", "--no-window-aspect-ratio-lock", "--ignore-video-encoder-constraints",
        "--min-size-alignment"
    };

    private static readonly Dictionary<string, string[]> EnumChoices = new(StringComparer.Ordinal)
    {
        ["--audio-codec"] = ["opus", "aac", "flac", "raw"],
        ["--audio-source"] =
        [
            "output", "playback", "mic", "mic-unprocessed", "mic-camcorder",
            "mic-voice-recognition", "mic-voice-communication", "voice-call",
            "voice-call-uplink", "voice-call-downlink", "voice-performance"
        ],
        ["--video-codec"] = ["h264", "h265", "av1", "vp8", "vp9"],
        ["--video-source"] = ["display", "camera"],
        ["--camera-facing"] = ["front", "back", "external"],
        ["--hwdec"] = ["auto", "disabled", "d3d11va"],
        ["--record-format"] = ["mp4", "mkv", "m4a", "mka", "opus", "aac", "flac", "wav"],
        ["--render-driver"] = ["direct3d", "opengl", "opengles2", "opengles", "software"],
        ["--render-fit"] = ["letterbox", "stretched", "unscaled"],
        ["--display-ime-policy"] = ["local", "fallback", "hide"],
        ["--gamepad"] = ["disabled", "uhid", "aoa"],
        ["--keyboard"] = ["disabled", "sdk", "uhid", "aoa"],
        ["--mouse"] = ["disabled", "sdk", "uhid", "aoa"],
        ["--verbosity"] = ["verbose", "debug", "info", "warn", "error"],
        ["--capture-orientation"] =
        [
            "0", "90", "180", "270", "flip0", "flip90", "flip180", "flip270",
            "@", "@0", "@90", "@180", "@270", "@flip0", "@flip90", "@flip180", "@flip270"
        ],
        ["--display-orientation"] = ["0", "90", "180", "270", "flip0", "flip90", "flip180", "flip270"],
        ["--orientation"] = ["0", "90", "180", "270", "flip0", "flip90", "flip180", "flip270"],
        ["--record-orientation"] = ["0", "90", "180", "270"]
    };

    private static readonly Dictionary<string, string> ChineseLabels = new(StringComparer.Ordinal)
    {
        ["--serial"] = "设备序列号",
        ["--select-usb"] = "仅使用 USB 设备",
        ["--select-tcpip"] = "仅使用无线设备",
        ["--port"] = "ADB 端口",
        ["--tcpip"] = "启用无线连接",
        ["--video-bit-rate"] = "视频码率",
        ["--max-size"] = "最大画面尺寸",
        ["--max-fps"] = "最大帧率",
        ["--video-codec"] = "视频编码",
        ["--video-encoder"] = "视频编码器",
        ["--video-codec-options"] = "视频编码参数",
        ["--video-source"] = "视频来源",
        ["--video-buffer"] = "视频缓冲",
        ["--no-video"] = "禁用视频",
        ["--no-audio"] = "禁用音频",
        ["--audio-codec"] = "音频编码",
        ["--audio-bit-rate"] = "音频码率",
        ["--audio-source"] = "音频来源",
        ["--audio-buffer"] = "音频缓冲",
        ["--audio-output-buffer"] = "音频输出缓冲",
        ["--always-on-top"] = "窗口始终置顶",
        ["--fullscreen"] = "全屏启动",
        ["--window-title"] = "窗口标题",
        ["--window-borderless"] = "无边框窗口",
        ["--window-width"] = "窗口宽度",
        ["--window-height"] = "窗口高度",
        ["--window-x"] = "窗口 X 坐标",
        ["--window-y"] = "窗口 Y 坐标",
        ["--background-color"] = "窗口背景色",
        ["--stay-awake"] = "保持设备唤醒",
        ["--keep-active"] = "模拟活动保持亮屏",
        ["--turn-screen-off"] = "启动时关闭手机屏幕",
        ["--screen-off-timeout"] = "屏幕超时秒数",
        ["--power-off-on-close"] = "退出时关闭屏幕",
        ["--no-power-on"] = "启动时不点亮屏幕",
        ["--show-touches"] = "显示触控点",
        ["--no-clipboard-autosync"] = "关闭剪贴板自动同步",
        ["--legacy-paste"] = "兼容旧式粘贴",
        ["--no-control"] = "只看不控",
        ["--keyboard"] = "键盘模式",
        ["--mouse"] = "鼠标模式",
        ["--mouse-bind"] = "鼠标按键映射",
        ["--gamepad"] = "手柄模式",
        ["--otg"] = "OTG 模式",
        ["--shortcut-mod"] = "快捷键修饰键",
        ["--record"] = "录制文件",
        ["--record-format"] = "录制格式",
        ["--record-orientation"] = "录制方向",
        ["--push-target"] = "拖放上传目录",
        ["--no-playback"] = "禁用电脑端播放",
        ["--no-window"] = "禁用 scrcpy 窗口",
        ["--new-display"] = "新建虚拟显示",
        ["--display-id"] = "显示编号",
        ["--display-orientation"] = "显示方向",
        ["--orientation"] = "方向",
        ["--crop"] = "画面裁剪",
        ["--angle"] = "旋转角度",
        ["--render-driver"] = "渲染驱动",
        ["--render-fit"] = "画面适配方式",
        ["--verbosity"] = "日志级别",
        ["--print-fps"] = "显示 FPS",
        ["--no-cleanup"] = "退出时保留设备清理",
        ["--help"] = "显示帮助",
        ["--version"] = "显示版本",
        ["--list-apps"] = "列出应用",
        ["--list-cameras"] = "列出摄像头",
        ["--list-camera-sizes"] = "列出摄像头尺寸",
        ["--list-displays"] = "列出显示设备",
        ["--list-encoders"] = "列出编码器"
    };

    [GeneratedRegex(@"^\s{4}(?:-[A-Za-z0-9],\s*)?(?<long>--[a-z0-9][a-z0-9-]*)(?<value>\[?=[^\r\n]+)?\s*$")]
    private static partial Regex OptionLineRegex();

    [GeneratedRegex(@"^\s{4}(?:-[A-Za-z0-9](?:,|\s|$)|--[a-z0-9])")]
    private static partial Regex AnyOptionLineRegex();

    public static IReadOnlyList<ScrcpyOptionDefinition> ParseHelp(string helpText)
    {
        var lines = helpText.Replace("\r\n", "\n").Split('\n');
        var result = new List<ScrcpyOptionDefinition>();

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var match = OptionLineRegex().Match(line);
            if (!match.Success)
            {
                continue;
            }

            var cli = match.Groups["long"].Value;
            var hasValue = match.Groups["value"].Success;
            var optionalValue = line.Contains("[=", StringComparison.Ordinal);
            var argumentName = hasValue
                ? match.Groups["value"].Value.TrimStart('[', '=')
                    .TrimEnd(']')
                    .Trim()
                : null;

            var descriptionLines = new List<string>();
            for (var j = i + 1; j < lines.Length; j++)
            {
                if (AnyOptionLineRegex().IsMatch(lines[j]) ||
                    lines[j].StartsWith("Shortcuts:", StringComparison.Ordinal))
                {
                    break;
                }

                var trimmed = lines[j].Trim();
                if (!string.IsNullOrEmpty(trimmed))
                {
                    descriptionLines.Add(trimmed);
                }
            }

            var description = string.Join(" ", descriptionLines);
            var defaultMatch = Regex.Match(
                description,
                @"Default is\s+(?<value>""[^""]+""|'[^']+'|[^.;]+)",
                RegexOptions.IgnoreCase);

            var kind = ResolveKind(cli, hasValue, optionalValue);
            var choices = EnumChoices.TryGetValue(cli, out var values)
                ? values
                : Array.Empty<string>();

            var displayName = BilingualScrcpyText.Names.TryGetValue(cli, out var translatedName)
                ? translatedName
                : ChineseLabels.TryGetValue(cli, out var label) ? label : cli;
            var chineseDescription = BilingualScrcpyText.Descriptions.TryGetValue(
                cli,
                out var translatedDescription)
                ? translatedDescription
                : string.Empty;

            result.Add(new ScrcpyOptionDefinition
            {
                CliName = cli,
                Label = displayName,
                Description = description,
                ChineseDescription = chineseDescription,
                Category = ResolveCategory(cli),
                Kind = kind,
                ArgumentName = argumentName,
                OptionalValue = optionalValue,
                ActionOnly = ActionOnlyOptions.Contains(cli),
                Choices = choices,
                DefaultHint = defaultMatch.Success
                    ? defaultMatch.Groups["value"].Value.Trim().TrimEnd('.')
                    : null
            });
        }

        return result
            .GroupBy(option => option.CliName, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
    }

    public static string CategoryLabel(ScrcpyOptionCategory category) => category switch
    {
        ScrcpyOptionCategory.Connection => "连接与设备",
        ScrcpyOptionCategory.Video => "视频",
        ScrcpyOptionCategory.Audio => "音频",
        ScrcpyOptionCategory.Window => "窗口与画面",
        ScrcpyOptionCategory.Input => "输入与控制",
        ScrcpyOptionCategory.Power => "电源与屏幕",
        ScrcpyOptionCategory.Clipboard => "剪贴板",
        ScrcpyOptionCategory.Recording => "录制与输出",
        ScrcpyOptionCategory.Diagnostics => "诊断与工具",
        ScrcpyOptionCategory.Experimental => "实验功能",
        _ => category.ToString()
    };

    private static ScrcpyOptionCategory ResolveCategory(string cli)
    {
        if (ConnectionOptions.Contains(cli)) return ScrcpyOptionCategory.Connection;
        if (PowerOptions.Contains(cli)) return ScrcpyOptionCategory.Power;
        if (ClipboardOptions.Contains(cli)) return ScrcpyOptionCategory.Clipboard;
        if (RecordingOptions.Contains(cli)) return ScrcpyOptionCategory.Recording;
        if (DiagnosticsOptions.Contains(cli)) return ScrcpyOptionCategory.Diagnostics;
        if (InputOptions.Contains(cli)) return ScrcpyOptionCategory.Input;
        if (ExperimentalOptions.Contains(cli)) return ScrcpyOptionCategory.Experimental;
        if (cli.StartsWith("--audio", StringComparison.Ordinal) || cli == "--no-audio")
            return ScrcpyOptionCategory.Audio;
        if (cli.StartsWith("--camera", StringComparison.Ordinal))
            return ScrcpyOptionCategory.Video;
        if (cli.StartsWith("--video", StringComparison.Ordinal) ||
            cli.Contains("encoder", StringComparison.Ordinal) ||
            cli is "--max-size" or "--max-fps" or "--crop" or "--angle" or
                "--ignore-video-encoder-constraints" or "--no-downsize-on-error")
            return ScrcpyOptionCategory.Video;
        if (cli.StartsWith("--window", StringComparison.Ordinal) ||
            cli is "--fullscreen" or "--always-on-top" or "--background-color" or
                "--display-id" or "--display-ime-policy" or "--no-mipmaps")
            return ScrcpyOptionCategory.Window;
        if (cli.Contains("display", StringComparison.Ordinal) ||
            cli.Contains("vd-", StringComparison.Ordinal) ||
            cli is "--flex-display")
            return ScrcpyOptionCategory.Experimental;
        return ScrcpyOptionCategory.Experimental;
    }

    private static ScrcpyOptionKind ResolveKind(string cli, bool hasValue, bool optionalValue)
    {
        if (!hasValue && !optionalValue)
        {
            return ScrcpyOptionKind.Boolean;
        }

        if (EnumChoices.ContainsKey(cli))
        {
            return ScrcpyOptionKind.Enum;
        }

        if (cli == "--background-color")
        {
            return ScrcpyOptionKind.Color;
        }

        if (cli == "--record")
        {
            return ScrcpyOptionKind.File;
        }

        if (cli.EndsWith("-codec-options", StringComparison.Ordinal))
        {
            return ScrcpyOptionKind.KeyValue;
        }

        var numericNames = new[]
        {
            "--angle", "--audio-bit-rate", "--audio-buffer", "--audio-output-buffer",
            "--video-bit-rate", "--camera-fps", "--camera-zoom", "--display-id",
            "--max-size", "--max-fps", "--min-size-alignment", "--port",
            "--screen-off-timeout", "--time-limit", "--tunnel-port", "--v4l2-buffer",
            "--video-buffer", "--window-height", "--window-width", "--window-x", "--window-y"
        };

        return numericNames.Contains(cli, StringComparer.Ordinal)
            ? ScrcpyOptionKind.Number
            : ScrcpyOptionKind.Text;
    }
}
