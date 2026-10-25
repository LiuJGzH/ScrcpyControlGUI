using System.Diagnostics;
using System.Runtime.InteropServices;
using ScrcpyControl.Infrastructure;

namespace ScrcpyControl.Services;

public sealed class ScrcpyProcessService : IDisposable
{
    private readonly PathService _paths;
    private Process? _process;
    private IntPtr _windowHandle;

    public ScrcpyProcessService(PathService paths)
    {
        _paths = paths;
    }

    public event EventHandler<string>? LogReceived;
    public event EventHandler<int>? ProcessExited;
    public event EventHandler? StateChanged;

    public bool IsRunning => _process is { HasExited: false };
    public int? ProcessId => IsRunning ? _process!.Id : null;
    public IntPtr WindowHandle => _windowHandle;

    public async Task StartAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        if (IsRunning)
        {
            throw new InvalidOperationException("scrcpy 已经在运行");
        }

        var startInfo = ProcessRunner.CreateStartInfo(
            _paths.ScrcpyExe,
            arguments,
            _paths.BaseDirectory);
        startInfo.Environment["ADB"] = _paths.AdbExe;

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };
        process.OutputDataReceived += OnOutputDataReceived;
        process.ErrorDataReceived += OnErrorDataReceived;
        process.Exited += OnProcessExited;

        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("无法启动 scrcpy.exe");
        }

        _process = process;
        _windowHandle = IntPtr.Zero;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        StateChanged?.Invoke(this, EventArgs.Empty);
        LogReceived?.Invoke(this, $"已启动 scrcpy：{string.Join(" ", arguments)}");

        for (var attempt = 0; attempt < 60 && !cancellationToken.IsCancellationRequested; attempt++)
        {
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            process.Refresh();
            if (process.HasExited)
            {
                break;
            }

            if (process.MainWindowHandle != IntPtr.Zero)
            {
                _windowHandle = process.MainWindowHandle;
                LogReceived?.Invoke(this, $"scrcpy 窗口已就绪，PID={process.Id}");
                break;
            }
        }
    }

    public async Task StopAsync()
    {
        var process = _process;
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.CloseMainWindow();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                try
                {
                    await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    ProcessRunner.TryKill(process);
                }
            }
        }
        finally
        {
            _windowHandle = IntPtr.Zero;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task<ProcessResult> RunActionAsync(
        IReadOnlyList<string> arguments,
        string? serial,
        CancellationToken cancellationToken = default)
    {
        var args = new List<string>();
        if (!string.IsNullOrWhiteSpace(serial))
        {
            args.Add($"--serial={serial}");
        }
        args.AddRange(arguments);

        var result = await ProcessRunner.RunAsync(
            _paths.ScrcpyExe,
            args,
            _paths.BaseDirectory,
            cancellationToken).ConfigureAwait(false);

        var output = result.CombinedOutput;
        if (!string.IsNullOrWhiteSpace(output))
        {
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                LogReceived?.Invoke(this, line.TrimEnd('\r'));
            }
        }

        return result;
    }

    public async Task SendScreenShortcutAsync(
        bool turnScreenOn,
        string shortcutModifier,
        CancellationToken cancellationToken = default)
    {
        await SendShortcutAsync(
            NativeMethods.VK_O,
            shift: turnScreenOn,
            shortcutModifier,
            cancellationToken).ConfigureAwait(false);
        LogReceived?.Invoke(this, turnScreenOn ? "已发送亮屏快捷键" : "已发送息屏快捷键");
    }

    public async Task SendPasteShortcutAsync(
        string shortcutModifier,
        CancellationToken cancellationToken = default)
    {
        if (!IsRunning)
        {
            throw new InvalidOperationException("scrcpy 尚未运行");
        }

        if (_windowHandle == IntPtr.Zero && _process is not null)
        {
            for (var attempt = 0; attempt < 30; attempt++)
            {
                _process.Refresh();
                if (_process.MainWindowHandle != IntPtr.Zero)
                {
                    _windowHandle = _process.MainWindowHandle;
                    break;
                }
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }

        if (_windowHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException("找不到 scrcpy 投屏窗口");
        }

        var modifier = ResolveModifier(shortcutModifier);
        var queued =
            NativeMethods.PostMessage(
                _windowHandle,
                NativeMethods.WM_KEYDOWN,
                (UIntPtr)modifier,
                IntPtr.Zero) &
            NativeMethods.PostMessage(
                _windowHandle,
                NativeMethods.WM_KEYDOWN,
                (UIntPtr)NativeMethods.VK_V,
                IntPtr.Zero) &
            NativeMethods.PostMessage(
                _windowHandle,
                NativeMethods.WM_KEYUP,
                (UIntPtr)NativeMethods.VK_V,
                IntPtr.Zero) &
            NativeMethods.PostMessage(
                _windowHandle,
                NativeMethods.WM_KEYUP,
                (UIntPtr)modifier,
                IntPtr.Zero);

        if (!queued)
        {
            throw new InvalidOperationException("无法向 scrcpy 窗口发送粘贴快捷键");
        }

        await Task.Delay(10, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendShortcutAsync(
        ushort virtualKey,
        bool shift,
        string shortcutModifier,
        CancellationToken cancellationToken)
    {
        if (!IsRunning)
        {
            throw new InvalidOperationException("scrcpy 尚未运行");
        }

        if (_windowHandle == IntPtr.Zero && _process is not null)
        {
            for (var attempt = 0; attempt < 30; attempt++)
            {
                _process.Refresh();
                if (_process.MainWindowHandle != IntPtr.Zero)
                {
                    _windowHandle = _process.MainWindowHandle;
                    break;
                }
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }

        if (_windowHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException("找不到 scrcpy 投屏窗口");
        }

        ActivateWindow();
        await Task.Delay(100, cancellationToken).ConfigureAwait(false);

        var modifier = ResolveModifier(shortcutModifier);
        var events = new List<NativeMethods.INPUT>
        {
            CreateKey(modifier, keyUp: false)
        };

        if (shift)
        {
            events.Add(CreateKey(NativeMethods.VK_SHIFT, keyUp: false));
        }

        events.Add(CreateKey(virtualKey, keyUp: false));
        events.Add(CreateKey(virtualKey, keyUp: true));

        if (shift)
        {
            events.Add(CreateKey(NativeMethods.VK_SHIFT, keyUp: true));
        }

        events.Add(CreateKey(modifier, keyUp: true));
        var inputArray = events.ToArray();
        var sent = NativeMethods.SendInput(
            (uint)inputArray.Length,
            inputArray,
            Marshal.SizeOf<NativeMethods.INPUT>());

        if (sent != inputArray.Length)
        {
            throw new InvalidOperationException("无法向 scrcpy 窗口发送息屏快捷键");
        }

    }

    public void Dispose()
    {
        var process = _process;
        if (process is not null)
        {
            process.OutputDataReceived -= OnOutputDataReceived;
            process.ErrorDataReceived -= OnErrorDataReceived;
            process.Exited -= OnProcessExited;
            ProcessRunner.TryKill(process);
            process.Dispose();
        }

        _process = null;
        _windowHandle = IntPtr.Zero;
    }

    private void OnOutputDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.Data))
        {
            LogReceived?.Invoke(this, e.Data);
        }
    }

    private void OnErrorDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.Data))
        {
            LogReceived?.Invoke(this, e.Data);
        }
    }

    private void ActivateWindow()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == _windowHandle)
        {
            return;
        }

        var foregroundThread = foreground != IntPtr.Zero
            ? NativeMethods.GetWindowThreadProcessId(foreground, out _)
            : 0;
        var currentThread = NativeMethods.GetCurrentThreadId();
        var attached = foregroundThread != 0 &&
                       foregroundThread != currentThread &&
                       NativeMethods.AttachThreadInput(foregroundThread, currentThread, true);

        NativeMethods.SetForegroundWindow(_windowHandle);
        NativeMethods.SetFocus(_windowHandle);

        if (attached)
        {
            NativeMethods.AttachThreadInput(foregroundThread, currentThread, false);
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        var exitCode = -1;
        try
        {
            exitCode = _process?.ExitCode ?? -1;
        }
        catch
        {
            // The process object may already be disposed during application shutdown.
        }

        _windowHandle = IntPtr.Zero;
        StateChanged?.Invoke(this, EventArgs.Empty);
        ProcessExited?.Invoke(this, exitCode);
    }

    private static ushort ResolveModifier(string configured)
    {
        var token = configured
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault()?
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault()
            ?.ToLowerInvariant();

        return token switch
        {
            "lctrl" => NativeMethods.VK_LCONTROL,
            "rctrl" => NativeMethods.VK_RCONTROL,
            "lalt" => NativeMethods.VK_LMENU,
            "ralt" => NativeMethods.VK_RMENU,
            "lsuper" => NativeMethods.VK_LWIN,
            "rsuper" => NativeMethods.VK_RWIN,
            _ => NativeMethods.VK_LMENU
        };
    }

    private static NativeMethods.INPUT CreateKey(ushort virtualKey, bool keyUp) =>
        new()
        {
            type = NativeMethods.INPUT_KEYBOARD,
            U = new NativeMethods.InputUnion
            {
                ki = new NativeMethods.KEYBDINPUT
                {
                    wVk = virtualKey,
                    dwFlags = keyUp ? NativeMethods.KEYEVENTF_KEYUP : 0
                }
            }
        };
}
