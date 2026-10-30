using System.Globalization;
using System.Text.RegularExpressions;
using ScrcpyControl.Models;

namespace ScrcpyControl.Services;

public sealed class AdbService
{
    private readonly PathService _paths;
    private string? _metricsCacheSerial;
    private DisplayMetrics? _metricsCache;

    public AdbService(PathService paths)
    {
        _paths = paths;
    }

    public async Task<IReadOnlyList<DeviceInfo>> GetDevicesAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await ProcessRunner.RunAsync(
            _paths.AdbExe,
            ["devices", "-l"],
            _paths.BaseDirectory,
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(result.CombinedOutput)
                    ? "ADB 无法读取设备列表"
                    : result.CombinedOutput.Trim());
        }

        var devices = new List<DeviceInfo>();
        foreach (var rawLine in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith('*'))
            {
                continue;
            }

            var firstSpace = line.IndexOf(' ');
            if (firstSpace <= 0)
            {
                continue;
            }

            var serial = line[..firstSpace];
            var remainder = line[(firstSpace + 1)..].Trim();
            var stateEnd = remainder.IndexOf(' ');
            var state = stateEnd < 0 ? remainder : remainder[..stateEnd];
            var properties = stateEnd < 0 ? string.Empty : remainder[(stateEnd + 1)..];
            var values = ParseKeyValues(properties);

            devices.Add(new DeviceInfo
            {
                Serial = serial,
                State = state,
                Model = values.GetValueOrDefault("model", string.Empty),
                Product = values.GetValueOrDefault("product", string.Empty),
                Device = values.GetValueOrDefault("device", string.Empty),
                TransportId = values.GetValueOrDefault("transport_id", string.Empty)
            });
        }

        return devices;
    }

    public async Task<string> RunShellAsync(
        string serial,
        string command,
        CancellationToken cancellationToken = default)
    {
        var result = await ProcessRunner.RunAsync(
            _paths.AdbExe,
            ["-s", serial, "shell", command],
            _paths.BaseDirectory,
            cancellationToken).ConfigureAwait(false);

        var output = result.StandardOutput.TrimEnd('\r', '\n');
        if (result.ExitCode != 0)
        {
            var error = result.StandardError.Trim();
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(error)
                    ? $"ADB 命令失败：{command}"
                    : error);
        }

        return output;
    }

    public async Task<string> GetPropertyAsync(
        string serial,
        string property,
        CancellationToken cancellationToken = default)
    {
        return (await RunShellAsync(serial, $"getprop {QuotePosix(property)}", cancellationToken)
            .ConfigureAwait(false)).Trim();
    }

    public async Task<(int Brightness, int Mode)> GetBrightnessAsync(
        string serial,
        CancellationToken cancellationToken = default)
    {
        var brightnessText = await RunShellAsync(
            serial,
            "settings get system screen_brightness",
            cancellationToken).ConfigureAwait(false);
        var modeText = await RunShellAsync(
            serial,
            "settings get system screen_brightness_mode",
            cancellationToken).ConfigureAwait(false);

        if (!int.TryParse(brightnessText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var brightness) ||
            !int.TryParse(modeText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mode))
        {
            throw new InvalidOperationException("无法读取手机亮度设置");
        }

        return (brightness, mode);
    }

    public async Task SetBrightnessAsync(
        string serial,
        int brightness,
        int mode,
        CancellationToken cancellationToken = default)
    {
        brightness = Math.Clamp(brightness, 1, 255);

        // Mode first, value second. Writing the mode switches the system into
        // auto-brightness, which discards a brightness value written before it,
        // so the restore would silently fall back to the dimmed level.
        await RunShellAsync(
            serial,
            $"settings put system screen_brightness_mode {Math.Clamp(mode, 0, 1)}",
            cancellationToken).ConfigureAwait(false);
        await RunShellAsync(
            serial,
            $"settings put system screen_brightness {brightness}",
            cancellationToken).ConfigureAwait(false);
    }

    public sealed record DisplayMetrics(int Width, int Height, int Density);

    /// <summary>
    /// Resolves the on-screen position of the send button that sits at the
    /// right end of the input bar, then taps it. WeChat marks its editor with
    /// IME_FLAG_NO_ENTER_ACTION, so a physical ENTER can never submit a message
    /// and tapping the button is the only way to send without an IME.
    /// </summary>
    public async Task<(bool Tapped, int X, int Y)> TapSendButtonAsync(
        string serial,
        CancellationToken cancellationToken = default)
    {
        var metrics = await GetDisplayMetricsAsync(serial, cancellationToken).ConfigureAwait(false);
        var imeTop = await GetImeTopAsync(serial, cancellationToken).ConfigureAwait(false);

        if (imeTop <= 0)
        {
            // Without a visible keyboard the input bar cannot be located, and a
            // blind tap lands on whatever sits at the bottom edge of the screen.
            return (false, 0, 0);
        }

        var dp = metrics.Density / 160d;

        // Tap the IME's own action key at the bottom-right of the keyboard
        // instead of the app's send button. The app button moves whenever the
        // input bar grows (quoted message, @ hint, multi-line), and its two
        // layouts have no overlapping hit area; the keyboard key is bound to the
        // IME layout only. Calibrated by touch capture on 1116x2480:
        // keyboard send key = (1017, 2228).
        var x = metrics.Width - (int)Math.Round(33 * dp);
        var y = metrics.Height - (int)Math.Round(84 * dp);

        await RunShellAsync(serial, $"input tap {x} {y}", cancellationToken)
            .ConfigureAwait(false);

        return (true, x, y);
    }

    public async Task<DisplayMetrics> GetDisplayMetricsAsync(
        string serial,
        CancellationToken cancellationToken = default)
    {
        // Screen size and density never change during a session, and each
        // adb round trip costs an extra process launch on the send path.
        if (_metricsCache is not null &&
            string.Equals(_metricsCacheSerial, serial, StringComparison.Ordinal))
        {
            return _metricsCache;
        }

        var sizeText = await RunShellAsync(serial, "wm size", cancellationToken)
            .ConfigureAwait(false);
        var densityText = await RunShellAsync(serial, "wm density", cancellationToken)
            .ConfigureAwait(false);

        var sizeMatch = Regex.Match(sizeText, @"(\d+)x(\d+)");
        if (!sizeMatch.Success)
        {
            throw new InvalidOperationException("无法读取屏幕分辨率");
        }

        var width = int.Parse(
            sizeMatch.Groups[1].Value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture);
        var height = int.Parse(
            sizeMatch.Groups[2].Value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture);

        var densityMatch = Regex.Match(densityText, @"(\d+)");
        var density = densityMatch.Success
            ? int.Parse(densityMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture)
            : 160;

        var metrics = new DisplayMetrics(width, height, density);
        _metricsCache = metrics;
        _metricsCacheSerial = serial;
        return metrics;
    }

    /// <summary>
    /// Returns the top edge of the soft keyboard in absolute screen pixels, or 0
    /// when it is not shown. Calibrated against real touches on a 1116x2480
    /// device: touchableRegion.top is already in screen coordinates (1511 maps
    /// to an input bar centre of 1432, i.e. 1511 - 56dp/2).
    /// </summary>
    public async Task<int> GetImeTopAsync(
        string serial,
        CancellationToken cancellationToken = default)
    {
        var windows = await RunShellAsync(serial, "dumpsys window windows", cancellationToken)
            .ConfigureAwait(false);

        var block = Regex.Match(
            windows,
            @"Window\{[0-9a-f]+ u0 InputMethod\}:([\s\S]{0,2000})");
        if (!block.Success)
        {
            return 0;
        }

        var body = block.Groups[1].Value;

        // mViewVisibility 0x8 (GONE) still leaves the last touchable region in
        // place, so visibility has to be checked before trusting the geometry.
        var vis = Regex.Match(body, @"mViewVisibility=0x([0-9a-fA-F]+)");
        if (!vis.Success ||
            !int.TryParse(vis.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var visibility) ||
            visibility != 0)
        {
            return 0;
        }

        // Note the space: this block prints "touchable region=SkRegion(...)",
        // while the visible-window list prints "touchableRegion=...".
        var region = Regex.Match(
            body,
            @"touchable region=SkRegion\(\((-?\d+),\s*(-?\d+),\s*(-?\d+),\s*(-?\d+)\)\)");
        if (!region.Success ||
            !int.TryParse(region.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var top) ||
            top <= 0 ||
            top >= 6000)
        {
            return 0;
        }

        return top;
    }

    public async Task<bool> IsTextInputFocusedAsync(
        string serial,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var dump = await RunShellAsync(
                serial,
                "dumpsys input_method",
                cancellationToken).ConfigureAwait(false);

            if (ContainsActiveTextInput(dump))
            {
                return true;
            }

            if (attempt < 3)
            {
                await Task.Delay(120, cancellationToken).ConfigureAwait(false);
            }
        }

        return false;
    }

    public async Task<(bool Focused, string Package)> GetTextInputFocusAsync(
        string serial,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var dump = await RunShellAsync(
                serial,
                "dumpsys input_method",
                cancellationToken).ConfigureAwait(false);

            if (ContainsActiveTextInput(dump))
            {
                return (true, ExtractEditorPackage(dump) ?? string.Empty);
            }

            if (attempt < 3)
            {
                await Task.Delay(120, cancellationToken).ConfigureAwait(false);
            }
        }

        return (false, string.Empty);
    }

    /// <summary>
    /// Reads the package that owns the active editor from an input_method dump.
    /// Some apps ignore the injected KEYCODE_PASTE, so the caller needs to know
    /// which app is focused to pick a paste strategy.
    /// </summary>
    public static string? ExtractEditorPackage(string dump)
    {
        if (string.IsNullOrWhiteSpace(dump))
        {
            return null;
        }

        var lines = dump.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var insideEditor = false;
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.StartsWith("mCurrentEditorInfo", StringComparison.Ordinal))
            {
                insideEditor = true;
                continue;
            }

            if (!insideEditor)
            {
                continue;
            }

            if (line.StartsWith("packageName=", StringComparison.Ordinal))
            {
                var value = line["packageName=".Length..].Trim();
                var separator = value.IndexOf(' ');
                return separator > 0 ? value[..separator] : value;
            }

            if (line.StartsWith("mServedView", StringComparison.Ordinal) ||
                line.StartsWith("mCurMethodId", StringComparison.Ordinal))
            {
                break;
            }
        }

        return null;
    }

    public static bool ContainsActiveTextInput(string dump)
    {
        if (string.IsNullOrWhiteSpace(dump))
        {
            return false;
        }

        // Android versions and OEM builds use either ":" or "=" after these
        // fields. A non-null editor is the reliable signal that a text field
        // owns the active input connection.
        return HasActiveDumpValue(dump, "mCurrentEditorInfo");
    }

    private static bool HasActiveDumpValue(string dump, string fieldName)
    {
        var lines = dump.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n');

        for (var index = 0; index < lines.Length; index++)
        {
            var trimmed = lines[index].TrimStart();
            if (!trimmed.StartsWith(fieldName, StringComparison.Ordinal))
            {
                continue;
            }

            var markerIndex = fieldName.Length;
            if (markerIndex >= trimmed.Length ||
                (trimmed[markerIndex] != ':' && trimmed[markerIndex] != '='))
            {
                continue;
            }

            var inlineValue = trimmed[(markerIndex + 1)..].Trim();
            if (inlineValue.Length > 0 &&
                !inlineValue.Equals("null", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (trimmed[markerIndex] != ':' || inlineValue.Length > 0)
            {
                continue;
            }

            // On Android 17 the field name is followed by an indented object:
            // inputType=..., packageName=..., and so on.
            for (var childIndex = index + 1; childIndex < lines.Length; childIndex++)
            {
                var child = lines[childIndex].Trim();
                if (child.Length == 0)
                {
                    continue;
                }

                if (child.StartsWith("inputType=", StringComparison.Ordinal) ||
                    child.StartsWith("packageName=", StringComparison.Ordinal) ||
                    child.StartsWith("actionLabel=", StringComparison.Ordinal))
                {
                    return true;
                }

                break;
            }
        }

        return false;
    }

    public async Task<IReadOnlyList<RemoteEntry>> ListDirectoryAsync(
        string serial,
        string remotePath,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeDirectory(remotePath);
        var command = $"ls -la --color=never -- {QuotePosix(normalized)}";
        var output = await RunShellAsync(serial, command, cancellationToken).ConfigureAwait(false);
        var entries = new List<RemoteEntry>();

        foreach (var rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith("total ", StringComparison.Ordinal))
            {
                continue;
            }

            var match = Regex.Match(
                line,
                @"^(?<type>[dl-])(?<perm>[rwxstST-]{9})\s+\S+\s+(?<owner>\S+)\s+(?<group>\S+)\s+(?<size>\d+)\s+(?<date>\d{4}-\d{2}-\d{2})\s+(?<time>\d{2}:\d{2})\s+(?<name>.+)$");
            if (!match.Success)
            {
                continue;
            }

            var name = match.Groups["name"].Value;
            if (name is "." or "..")
            {
                continue;
            }

            var isSymlink = match.Groups["type"].Value == "l";
            var linkIndex = isSymlink ? name.IndexOf(" -> ", StringComparison.Ordinal) : -1;
            if (linkIndex >= 0)
            {
                name = name[..linkIndex];
            }

            if (long.TryParse(
                    match.Groups["size"].Value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var size) is false)
            {
                size = 0;
            }

            entries.Add(new RemoteEntry
            {
                Name = name,
                FullPath = CombineRemote(normalized, name),
                IsDirectory = match.Groups["type"].Value == "d",
                IsSymlink = isSymlink,
                Permissions = match.Groups["type"].Value + match.Groups["perm"].Value,
                Owner = match.Groups["owner"].Value,
                Group = match.Groups["group"].Value,
                Size = size,
                Modified = $"{match.Groups["date"].Value} {match.Groups["time"].Value}"
            });
        }

        return entries
            .OrderByDescending(entry => entry.IsDirectory)
            .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public async Task PushAsync(
        string serial,
        string localPath,
        string remoteDirectory,
        Action<double> progress,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeDirectory(remoteDirectory);
        var finalPath = CombineRemote(normalized, Path.GetFileName(localPath));
        var temporaryRemotePath = CombineRemote(
            normalized,
            $".scrcpy-upload-{Guid.NewGuid():N}.tmp");

        try
        {
            await RunTransferAsync(
                ["-s", serial, "push", localPath, temporaryRemotePath],
                value => progress(Math.Min(value * 0.95, 95)),
                cancellationToken).ConfigureAwait(false);

            await RunShellAsync(
                serial,
                $"mv -f {QuotePosix(temporaryRemotePath)} {QuotePosix(finalPath)}",
                cancellationToken).ConfigureAwait(false);
            progress(100);
        }
        catch
        {
            try
            {
                await RunShellAsync(
                    serial,
                    $"rm -f {QuotePosix(temporaryRemotePath)}",
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // The temporary file may not exist if the upload never started.
            }
            throw;
        }
    }

    public async Task PullAsync(
        string serial,
        string remotePath,
        string localDirectory,
        Action<double> progress,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(localDirectory);
        if (remotePath.EndsWith('/'))
        {
            throw new InvalidOperationException("请进入文件夹后选择具体文件下载。");
        }

        var fileName = Path.GetFileName(remotePath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = "download.bin";
        }

        var finalPath = Path.Combine(localDirectory, fileName);
        var temporaryPath = Path.Combine(
            localDirectory,
            $".scrcpy-download-{Guid.NewGuid():N}.tmp");
        var requiresAsciiSource = remotePath.Any(character => character > 127);
        var remoteTemporaryDirectory = "/sdcard/.scrcpy_control_tmp/";
        var remoteTemporaryPath = CombineRemote(
            remoteTemporaryDirectory,
            $".scrcpy-pull-{Guid.NewGuid():N}.tmp");
        var pullSource = remotePath;

        try
        {
            if (requiresAsciiSource)
            {
                await RunShellAsync(
                    serial,
                    $"mkdir -p {QuotePosix(remoteTemporaryDirectory)}",
                    cancellationToken).ConfigureAwait(false);
                await RunShellAsync(
                    serial,
                    $"cp -f {QuotePosix(remotePath)} {QuotePosix(remoteTemporaryPath)}",
                    cancellationToken).ConfigureAwait(false);
                pullSource = remoteTemporaryPath;
            }

            await RunTransferAsync(
                ["-s", serial, "pull", pullSource, temporaryPath],
                value => progress(Math.Min(value * 0.98, 98)),
                cancellationToken).ConfigureAwait(false);

            File.Move(temporaryPath, finalPath, overwrite: true);
            progress(100);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch
            {
                // Ignore temporary file cleanup failures.
            }

            if (requiresAsciiSource)
            {
                try
                {
                    await RunShellAsync(
                        serial,
                        $"rm -f {QuotePosix(remoteTemporaryPath)}",
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // The remote temporary copy may not exist if copying failed.
                }
            }
        }
    }

    public static string NormalizeDirectory(string path)
    {
        var normalized = string.IsNullOrWhiteSpace(path) ? "/sdcard/" : path.Trim();
        if (!normalized.StartsWith('/'))
        {
            normalized = "/" + normalized;
        }

        return normalized.EndsWith('/') ? normalized : normalized + "/";
    }

    public static string ParentDirectory(string path)
    {
        var normalized = NormalizeDirectory(path).TrimEnd('/');
        var index = normalized.LastIndexOf('/');
        return index <= 0 ? "/" : normalized[..index] + "/";
    }

    public static string CombineRemote(string directory, string name) =>
        NormalizeDirectory(directory) + name;

    public static string QuotePosix(string value) =>
        "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    private async Task RunTransferAsync(
        IReadOnlyList<string> arguments,
        Action<double> progress,
        CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunStreamingAsync(
            _paths.AdbExe,
            arguments,
            _paths.BaseDirectory,
            chunk => UpdateProgress(chunk, progress),
            chunk => UpdateProgress(chunk, progress),
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(result.CombinedOutput)
                    ? "文件传输失败"
                    : result.CombinedOutput.Trim());
        }

        progress(100);
    }

    private static void UpdateProgress(string chunk, Action<double> progress)
    {
        foreach (Match match in Regex.Matches(chunk, @"(?<percent>\d{1,3})%"))
        {
            if (double.TryParse(
                    match.Groups["percent"].Value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var percent))
            {
                progress(Math.Clamp(percent, 0, 100));
            }
        }
    }

    private static Dictionary<string, string> ParseKeyValues(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = token.IndexOf(':');
            if (separator <= 0 || separator == token.Length - 1)
            {
                continue;
            }

            values[token[..separator]] = token[(separator + 1)..];
        }

        return values;
    }
}
