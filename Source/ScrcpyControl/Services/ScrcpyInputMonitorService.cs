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
    private bool _rightHoldEnabled;
    private int _rightHoldMilliseconds = 450;
    // Written from the hook thread, read from the hold-timer thread.
    private volatile bool _rightHoldPending;
    private volatile bool _rightHoldFired;
    private NativeMethods.POINT _rightHoldPoint;
    private int _rightHoldGeneration;

    public event EventHandler? ActivityDetected;
    public event EventHandler<ScrcpyClickEventArgs>? ScrcpyClicked;
    public event EventHandler? RightShortPressRequested;
    public event EventHandler? RightLongPressTriggered;
    public event EventHandler<string>? MonitorError;

    public bool IsRunning { get; private set; }

    public void Start(
        int processId,
        IntPtr scrcpyWindow,
        bool longPressRightClickEnabled = false,
        int longPressMilliseconds = 450)
    {
        Stop();

        _rightHoldEnabled = longPressRightClickEnabled;
        _rightHoldMilliseconds = Math.Clamp(longPressMilliseconds, 200, 2000);
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

        _rightHoldPending = false;
        _rightHoldFired = false;
        Interlocked.Increment(ref _rightHoldGeneration);

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
        CancelRightHold();
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
        if (nCode < 0)
        {
            return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        var data = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
        var message = wParam.ToInt32();
        var inside = IsScrcpyPoint(data.pt);
        var suppress = false;

        switch (message)
        {
            case NativeMethods.WM_LBUTTONDOWN:
                _leftDragging = inside;
                _leftDownPoint = data.pt;
                _leftMoved = false;
                if (inside) RaiseActivity();
                break;
            case NativeMethods.WM_RBUTTONDOWN:
                if (inside && _rightHoldEnabled)
                {
                    // Hold the event back: scrcpy must not see the BACK click
                    // until we know whether this is a short or a long press.
                    BeginRightHold(data.pt);
                    RaiseActivity();
                    suppress = true;
                    break;
                }

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
                if (_rightHoldPending)
                {
                    EndRightHold();
                    suppress = true;
                    break;
                }

                _rightDragging = false;
                break;
            case NativeMethods.WM_MBUTTONUP:
                _middleDragging = false;
                break;
            case NativeMethods.WM_XBUTTONUP:
                _xDragging = false;
                break;
            case NativeMethods.WM_MOUSEMOVE:
                if (_rightHoldPending)
                {
                    var holdDx = data.pt.X - _rightHoldPoint.X;
                    var holdDy = data.pt.Y - _rightHoldPoint.Y;
                    if (Math.Abs(holdDx) > 4 || Math.Abs(holdDy) > 4)
                    {
                        // Right-button drag: abandon the hold and treat it as a
                        // plain drag, so a held-still press never becomes BACK.
                        CancelRightHold();
                        _rightDragging = true;
                    }
                }

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

        return suppress
            ? (IntPtr)1
            : NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private void BeginRightHold(NativeMethods.POINT point)
    {
        _rightHoldPoint = point;
        _rightHoldPending = true;
        _rightHoldFired = false;

        // A plain timer thread instead of a Win32 timer: the hook thread only
        // pumps messages, and a delayed check here cannot be dropped.
        var generation = Interlocked.Increment(ref _rightHoldGeneration);
        var delay = _rightHoldMilliseconds;
        _ = Task.Run(async () =>
        {
            await Task.Delay(delay).ConfigureAwait(false);
            if (generation != Volatile.Read(ref _rightHoldGeneration))
            {
                return;
            }

            OnRightHoldTimer();
        });
    }

    private void EndRightHold()
    {
        Interlocked.Increment(ref _rightHoldGeneration);
        var fired = _rightHoldFired;
        _rightHoldPending = false;
        _rightHoldFired = false;

        if (fired)
        {
            return;
        }

        // Never touch SendInput from inside a low-level hook: the injected
        // event re-enters this callback, which is still on the stack, and the
        // deadlock makes Windows drop the hook after its 300 ms timeout.
        RightShortPressRequested?.Invoke(this, EventArgs.Empty);
    }

    private void CancelRightHold()
    {
        Interlocked.Increment(ref _rightHoldGeneration);
        _rightHoldPending = false;
        _rightHoldFired = false;
    }

    private void OnRightHoldTimer()
    {
        if (!_rightHoldPending || _rightHoldFired)
        {
            return;
        }

        // No foreground check here: the switch-app chord is posted straight to
        // the scrcpy window and does not need it to be the active window.
        _rightHoldFired = true;
        RightLongPressTriggered?.Invoke(this, EventArgs.Empty);
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
