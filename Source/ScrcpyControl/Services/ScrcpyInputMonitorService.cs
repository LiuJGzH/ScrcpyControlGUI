using System.ComponentModel;
using System.Runtime.InteropServices;
using ScrcpyControl.Infrastructure;
using ScrcpyControl.Models;

namespace ScrcpyControl.Services;

public sealed class ScrcpyInputMonitorService : IDisposable
{
    private readonly object _sync = new();
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _keyboardHook;
    private IntPtr _mouseHook;
    private NativeMethods.LowLevelKeyboardProc? _keyboardProc;
    private NativeMethods.LowLevelMouseProc? _mouseProc;
    private IntPtr _targetWindow;
    private uint _targetProcessId;
    private bool _leftDragging;
    private bool _rightDragging;
    private bool _middleDragging;
    private bool _xDragging;
    private NativeMethods.POINT _leftDownPoint;
    private bool _leftMoved;

    public event EventHandler? ActivityDetected;
    public event EventHandler<ScrcpyClickEventArgs>? ScrcpyClicked;
    public event EventHandler<string>? MonitorError;

    public bool IsRunning { get; private set; }

    public void Start(int processId, IntPtr scrcpyWindow)
    {
        Stop();

        _targetProcessId = (uint)processId;
        _targetWindow = scrcpyWindow;
        _thread = new Thread(HookThreadMain)
        {
            IsBackground = true,
            Name = "Scrcpy input monitor"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public void UpdateTargetWindow(IntPtr window)
    {
        lock (_sync)
        {
            _targetWindow = window;
        }
    }

    public void Stop()
    {
        var thread = _thread;
        if (thread is null)
        {
            IsRunning = false;
            return;
        }

        if (_threadId != 0)
        {
            NativeMethods.PostThreadMessage(_threadId, NativeMethods.WM_QUIT, UIntPtr.Zero, IntPtr.Zero);
        }

        if (thread.Join(TimeSpan.FromSeconds(2)) is false)
        {
            // The hook thread is a background thread and will not keep the process alive.
        }

        _thread = null;
        _threadId = 0;
        IsRunning = false;
    }

    public void Dispose()
    {
        Stop();
    }

    private void HookThreadMain()
    {
        _threadId = NativeMethods.GetCurrentThreadId();
        _keyboardProc = KeyboardHookCallback;
        _mouseProc = MouseHookCallback;
        var module = NativeMethods.GetModuleHandle(null);

        _keyboardHook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_KEYBOARD_LL,
            _keyboardProc,
            module,
            0);
        _mouseHook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_MOUSE_LL,
            _mouseProc,
            module,
            0);

        if (_keyboardHook == IntPtr.Zero || _mouseHook == IntPtr.Zero)
        {
            var error = new Win32Exception(Marshal.GetLastWin32Error());
            MonitorError?.Invoke(this, $"键鼠监控初始化失败：{error.Message}");
            UninstallHooks();
            IsRunning = false;
            return;
        }

        IsRunning = true;
        while (NativeMethods.GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            NativeMethods.TranslateMessage(ref message);
            NativeMethods.DispatchMessage(ref message);
        }

        UninstallHooks();
        IsRunning = false;
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 &&
            (wParam == (IntPtr)NativeMethods.WM_KEYDOWN ||
             wParam == (IntPtr)NativeMethods.WM_SYSKEYDOWN))
        {
            if (IsScrcpyForeground())
            {
                RaiseActivity();
            }
        }

        return NativeMethods.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var data = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
            var message = wParam.ToInt32();
            var inside = IsScrcpyPoint(data.pt);

            switch (message)
            {
                case NativeMethods.WM_LBUTTONDOWN:
                    _leftDragging = inside;
                    _leftDownPoint = data.pt;
                    _leftMoved = false;
                    if (inside) RaiseActivity();
                    break;
                case NativeMethods.WM_RBUTTONDOWN:
                    _rightDragging = inside;
                    if (inside) RaiseActivity();
                    break;
                case NativeMethods.WM_MBUTTONDOWN:
                    _middleDragging = inside;
                    if (inside) RaiseActivity();
                    break;
                case NativeMethods.WM_XBUTTONDOWN:
                    _xDragging = inside;
                    if (inside) RaiseActivity();
                    break;
                case NativeMethods.WM_LBUTTONUP:
                    if (_leftDragging &&
                        !_leftMoved &&
                        inside)
                    {
                        ScrcpyClicked?.Invoke(
                            this,
                            new ScrcpyClickEventArgs(data.pt.X, data.pt.Y));
                    }
                    _leftDragging = false;
                    break;
                case NativeMethods.WM_RBUTTONUP:
                    _rightDragging = false;
                    break;
                case NativeMethods.WM_MBUTTONUP:
                    _middleDragging = false;
                    break;
                case NativeMethods.WM_XBUTTONUP:
                    _xDragging = false;
                    break;
                case NativeMethods.WM_MOUSEMOVE:
                    if (_leftDragging)
                    {
                        var dx = data.pt.X - _leftDownPoint.X;
                        var dy = data.pt.Y - _leftDownPoint.Y;
                        if (Math.Abs(dx) > 4 || Math.Abs(dy) > 4)
                        {
                            _leftMoved = true;
                        }
                    }

                    if ((_leftDragging || _rightDragging || _middleDragging || _xDragging) &&
                        inside)
                    {
                        RaiseActivity();
                    }
                    break;
                case NativeMethods.WM_MOUSEWHEEL:
                case NativeMethods.WM_MOUSEHWHEEL:
                    if (inside) RaiseActivity();
                    break;
            }
        }

        return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private bool IsScrcpyForeground()
    {
        IntPtr target;
        uint targetPid;
        lock (_sync)
        {
            target = _targetWindow;
            targetPid = _targetProcessId;
        }

        if (targetPid == 0 || !NativeMethods.IsWindow(target))
        {
            return false;
        }

        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(foreground, out var foregroundPid);
        return foregroundPid == targetPid;
    }

    private bool IsScrcpyPoint(NativeMethods.POINT point)
    {
        IntPtr target;
        lock (_sync)
        {
            target = _targetWindow;
        }

        if (target == IntPtr.Zero ||
            !NativeMethods.IsWindow(target) ||
            !NativeMethods.IsWindowVisible(target) ||
            !NativeMethods.GetWindowRect(target, out var rect) ||
            !rect.Contains(point))
        {
            return false;
        }

        var windowAtPoint = NativeMethods.WindowFromPoint(point);
        if (windowAtPoint == IntPtr.Zero)
        {
            return false;
        }

        var root = NativeMethods.GetAncestor(windowAtPoint, NativeMethods.GA_ROOT);
        return root == target || windowAtPoint == target;
    }

    private void RaiseActivity()
    {
        ActivityDetected?.Invoke(this, EventArgs.Empty);
    }

    private void UninstallHooks()
    {
        if (_keyboardHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }

        if (_mouseHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
    }
}
