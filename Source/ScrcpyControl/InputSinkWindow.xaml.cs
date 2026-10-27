using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using ScrcpyControl.Infrastructure;

namespace ScrcpyControl;

public partial class InputSinkWindow : Window
{
    private readonly DispatcherTimer _timeoutTimer;
    private readonly DispatcherTimer _autoSubmitTimer;
    private readonly DispatcherTimer _imePollTimer;
    private readonly bool _autoSubmitEnabled;
    private readonly int _autoSubmitDebounceMilliseconds;
    private readonly bool _enterSendsMessage;
    private bool _cancelled;
    private bool _isSubmitting;
    private bool _waitingForImeEnterCommit;
    private bool _imeWasComposing;
    private bool _isTextCompositionActive;
    private bool _suppressTextChanged;
    private IntPtr _scrcpyWindow;

    public InputSinkWindow(
        bool autoSubmitEnabled = true,
        int autoSubmitDebounceMilliseconds = 100,
        bool enterSendsMessage = true)
    {
        _autoSubmitEnabled = autoSubmitEnabled;
        _enterSendsMessage = enterSendsMessage;
        _autoSubmitDebounceMilliseconds = Math.Clamp(
            autoSubmitDebounceMilliseconds,
            50,
            5000);
        InitializeComponent();
        _timeoutTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(30)
        };
        _timeoutTimer.Tick += (_, _) =>
        {
            _timeoutTimer.Stop();
            Cancel();
        };

        _autoSubmitTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(_autoSubmitDebounceMilliseconds)
        };
        _autoSubmitTimer.Tick += (_, _) =>
        {
            _autoSubmitTimer.Stop();
            if (_autoSubmitEnabled &&
                !_cancelled &&
                !_isSubmitting &&
                !string.IsNullOrWhiteSpace(InputBox.Text))
            {
                if (IsImeComposing())
                {
                    _imeWasComposing = true;
                }
                else
                {
                    Commit();
                }
            }
        };

        _imePollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(30)
        };
        _imePollTimer.Tick += (_, _) => PollImeState();

        InputBox.AddHandler(
            TextCompositionManager.TextInputStartEvent,
            new TextCompositionEventHandler(InputBox_TextCompositionStart),
            handledEventsToo: true);
        InputBox.AddHandler(
            TextCompositionManager.TextInputUpdateEvent,
            new TextCompositionEventHandler(InputBox_TextCompositionUpdate),
            handledEventsToo: true);
        InputBox.AddHandler(
            TextCompositionManager.TextInputEvent,
            new TextCompositionEventHandler(InputBox_TextCompositionCompleted),
            handledEventsToo: true);
    }

    public event EventHandler<string>? TextSubmitted;
    public event EventHandler? SendMessageRequested;
    public event EventHandler? NewlineRequested;
    public event EventHandler? Cancelled;

    public void ShowAtScreenPoint(int screenX, int screenY, IntPtr scrcpyWindow)
    {
        var dpi = NativeMethods.GetDpiForWindow(scrcpyWindow);
        var scale = dpi > 0 ? dpi / 96d : 1d;
        var left = screenX / scale - 2;
        var top = screenY / scale - 2;
        var minLeft = SystemParameters.VirtualScreenLeft + 1;
        var minTop = SystemParameters.VirtualScreenTop + 1;
        var maxLeft = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 5;
        var maxTop = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 5;

        Left = Math.Clamp(left, minLeft, Math.Max(minLeft, maxLeft));
        Top = Math.Clamp(top, minTop, Math.Max(minTop, maxTop));
        _suppressTextChanged = true;
        InputBox.Clear();
        _suppressTextChanged = false;
        _cancelled = false;
        _isSubmitting = false;
        _waitingForImeEnterCommit = false;
        _imeWasComposing = false;
        _isTextCompositionActive = false;
        _scrcpyWindow = scrcpyWindow;
        _autoSubmitTimer.Stop();
        _imePollTimer.Stop();
        if (_autoSubmitEnabled)
        {
            _imePollTimer.Start();
        }

        Show();
        ActivateInputWindow();
        _timeoutTimer.Start();
    }

    public void Cancel()
    {
        if (_cancelled)
        {
            return;
        }

        _cancelled = true;
        _timeoutTimer.Stop();
        _autoSubmitTimer.Stop();
        _imePollTimer.Stop();
        Cancelled?.Invoke(this, EventArgs.Empty);

        try
        {
            Close();
        }
        catch
        {
            // Already closed, or closed from a shutdown path.
        }
    }

    public bool IsSubmissionInProgress => _isSubmitting;

    public IntPtr InputWindowHandle => new WindowInteropHelper(this).Handle;

    public void CompleteSubmission()
    {
        _isSubmitting = false;
        if (!_cancelled && IsVisible)
        {
            _timeoutTimer.Start();
            if (_autoSubmitEnabled)
            {
                ScheduleAutoSubmit();
            }
        }
    }

    public bool RestoreFocusForContinuation()
    {
        if (_cancelled || _isSubmitting || !IsVisible)
        {
            return false;
        }

        ActivateInputWindow();
        _timeoutTimer.Start();
        return true;
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var style = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GWL_EXSTYLE).ToInt64();
        NativeMethods.SetWindowLongPtr(
            handle,
            NativeMethods.GWL_EXSTYLE,
            new IntPtr(style | NativeMethods.WS_EX_TOOLWINDOW));
    }

    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Held keys auto-repeat every ~30 ms. Without this guard a long press
        // on Enter queued dozens of taps that kept firing after the key was
        // released.
        if (e.IsRepeat && e.Key is Key.Enter or Key.Back or Key.Delete)
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Cancel();
            return;
        }

        if (e.Key == Key.Enter &&
            (Keyboard.Modifiers & ModifierKeys.Shift) == 0 &&
            IsImeComposing())
        {
            _waitingForImeEnterCommit = true;
            return;
        }

        if (IsImeComposing())
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(InputBox.Text) &&
            e.Key is Key.Left or Key.Up or Key.Right or Key.Down)
        {
            // Nothing pending locally: let the arrow keys move the caret inside
            // the phone's text field instead of a meaningless local caret.
            e.Handled = true;
            _autoSubmitTimer.Stop();
            SendKeyToDevice(e.Key switch
            {
                Key.Left => NativeMethods.VK_LEFT,
                Key.Up => NativeMethods.VK_UP,
                Key.Right => NativeMethods.VK_RIGHT,
                _ => NativeMethods.VK_DOWN
            });
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            switch (e.Key)
            {
                case Key.A:
                    e.Handled = true;
                    _autoSubmitTimer.Stop();
                    if (string.IsNullOrEmpty(InputBox.Text))
                    {
                        SendControlShortcutToDevice(NativeMethods.VK_A);
                    }
                    else
                    {
                        InputBox.SelectAll();
                    }
                    return;

                case Key.C:
                    e.Handled = true;
                    _autoSubmitTimer.Stop();
                    if (TryCopyInputText())
                    {
                        return;
                    }
                    SendControlShortcutToDevice(NativeMethods.VK_C);
                    return;

                case Key.X:
                    e.Handled = true;
                    if (TryCutInputText())
                    {
                        return;
                    }
                    SendControlShortcutToDevice(NativeMethods.VK_X);
                    return;

                case Key.V:
                    e.Handled = true;
                    if (TryPasteIntoInput())
                    {
                        return;
                    }
                    SendControlShortcutToDevice(NativeMethods.VK_V);
                    return;

                case Key.Z:
                    e.Handled = true;
                    InputBox.Undo();
                    return;

                case Key.Y:
                    e.Handled = true;
                    InputBox.Redo();
                    return;
            }
        }

        if (e.Key is Key.Back or Key.Delete)
        {
            var before = InputBox.Text;
            var command = e.Key == Key.Back
                ? EditingCommands.Backspace
                : EditingCommands.Delete;
            if (command.CanExecute(null, InputBox))
            {
                command.Execute(null, InputBox);
            }

            if (!string.Equals(before, InputBox.Text, StringComparison.Ordinal))
            {
                e.Handled = true;
                return;
            }

            if (InputBox.Text.Length == 0)
            {
                SendKeyToDevice(
                    e.Key == Key.Back
                        ? NativeMethods.VK_BACK
                        : NativeMethods.VK_DELETE);
                e.Handled = true;
            }
            return;
        }

        if (e.Key == Key.Enter &&
            (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            if (string.IsNullOrWhiteSpace(InputBox.Text))
            {
                // Empty host: Enter and Ctrl+Enter map to send / newline, and
                // the setting decides which one is which.
                var withCtrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
                if (_enterSendsMessage != withCtrl)
                {
                    SendMessageRequested?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    NewlineRequested?.Invoke(this, EventArgs.Empty);
                }

                return;
            }

            Commit();
        }
    }

    private bool TryCopyInputText()
    {
        var text = InputBox.SelectionLength > 0
            ? InputBox.SelectedText
            : InputBox.Text;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        try
        {
            Clipboard.SetDataObject(
                new DataObject(DataFormats.UnicodeText, text),
                copy: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool TryCutInputText()
    {
        if (string.IsNullOrEmpty(InputBox.Text))
        {
            return false;
        }

        if (InputBox.SelectionLength == 0)
        {
            InputBox.SelectAll();
        }

        if (!TryCopyInputText())
        {
            return false;
        }

        InputBox.SelectedText = string.Empty;
        return true;
    }

    private bool TryPasteIntoInput()
    {
        try
        {
            if (!Clipboard.ContainsText())
            {
                return false;
            }

            var text = Clipboard.GetText(TextDataFormat.UnicodeText);
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            var insertAt = InputBox.SelectionStart;
            InputBox.SelectedText = text;
            InputBox.CaretIndex = insertAt + text.Length;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void SendKeyToDevice(ushort virtualKey)
    {
        if (_scrcpyWindow == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.PostMessage(
            _scrcpyWindow,
            NativeMethods.WM_KEYDOWN,
            (UIntPtr)virtualKey,
            IntPtr.Zero);
        NativeMethods.PostMessage(
            _scrcpyWindow,
            NativeMethods.WM_KEYUP,
            (UIntPtr)virtualKey,
            IntPtr.Zero);
    }

    private void SendControlShortcutToDevice(ushort virtualKey)
    {
        if (_scrcpyWindow == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.PostMessage(
            _scrcpyWindow,
            NativeMethods.WM_KEYDOWN,
            (UIntPtr)NativeMethods.VK_LCONTROL,
            IntPtr.Zero);
        SendKeyToDevice(virtualKey);
        NativeMethods.PostMessage(
            _scrcpyWindow,
            NativeMethods.WM_KEYUP,
            (UIntPtr)NativeMethods.VK_LCONTROL,
            IntPtr.Zero);
    }

    private void InputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressTextChanged || _cancelled || _isSubmitting)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(InputBox.Text))
        {
            _autoSubmitTimer.Stop();
            return;
        }

        if (!_autoSubmitEnabled)
        {
            return;
        }

        if (IsImeComposing())
        {
            _imeWasComposing = true;
            _autoSubmitTimer.Stop();
            return;
        }

        _imeWasComposing = false;
        ScheduleAutoSubmit();
    }

    private void InputBox_TextCompositionStart(object sender, TextCompositionEventArgs e)
    {
        _isTextCompositionActive = true;
        _imeWasComposing = true;
        _autoSubmitTimer.Stop();
    }

    private void InputBox_TextCompositionUpdate(object sender, TextCompositionEventArgs e)
    {
        _isTextCompositionActive = true;
        _imeWasComposing = true;
        _autoSubmitTimer.Stop();
    }

    private void InputBox_TextCompositionCompleted(object sender, TextCompositionEventArgs e)
    {
        _isTextCompositionActive = false;
        _imeWasComposing = false;
        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            () =>
            {
                if (!_cancelled &&
                    !_isSubmitting &&
                    !string.IsNullOrWhiteSpace(InputBox.Text) &&
                    !IsImeComposing())
                {
                    ScheduleAutoSubmit();
                }
            });
    }

    private void PollImeState()
    {
        if (!_autoSubmitEnabled ||
            _cancelled ||
            _isSubmitting ||
            string.IsNullOrWhiteSpace(InputBox.Text))
        {
            _autoSubmitTimer.Stop();
            return;
        }

        if (IsImeComposing())
        {
            _imeWasComposing = true;
            _autoSubmitTimer.Stop();
            return;
        }

        if (_imeWasComposing)
        {
            _imeWasComposing = false;
            ScheduleAutoSubmit();
        }
    }

    private void ScheduleAutoSubmit()
    {
        if (!_autoSubmitEnabled ||
            _cancelled ||
            _isSubmitting ||
            string.IsNullOrWhiteSpace(InputBox.Text))
        {
            _autoSubmitTimer.Stop();
            return;
        }

        _autoSubmitTimer.Stop();
        _autoSubmitTimer.Start();
    }

    private void InputBox_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !_waitingForImeEnterCommit)
        {
            return;
        }

        if (IsImeComposing())
        {
            return;
        }

        _waitingForImeEnterCommit = false;
        e.Handled = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, Commit);
    }

    private bool IsImeComposing()
    {
        if (_isTextCompositionActive)
        {
            return true;
        }

        var window = new WindowInteropHelper(this).Handle;
        var context = NativeMethods.ImmGetContext(window);
        if (context == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            return NativeMethods.ImmGetCompositionString(
                context,
                NativeMethods.GCS_COMPSTR,
                null,
                0) > 0;
        }
        finally
        {
            NativeMethods.ImmReleaseContext(window, context);
        }
    }

    private void Commit()
    {
        if (_cancelled || _isSubmitting)
        {
            return;
        }

        var text = InputBox.Text.TrimEnd('\r', '\n');
        if (text.Length == 0)
        {
            return;
        }

        _isSubmitting = true;
        _waitingForImeEnterCommit = false;
        _imeWasComposing = false;
        _isTextCompositionActive = false;
        _timeoutTimer.Stop();
        _autoSubmitTimer.Stop();
        _suppressTextChanged = true;
        InputBox.Clear();
        _suppressTextChanged = false;
        Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            () =>
            {
                if (!_cancelled && IsVisible)
                {
                    InputBox.Focus();
                    Keyboard.Focus(InputBox);
                }
            });
        TextSubmitted?.Invoke(this, text);
    }

    private void ActivateInputWindow()
    {
        if (_cancelled || !IsVisible)
        {
            return;
        }

        var inputWindow = new WindowInteropHelper(this).Handle;
        var foreground = NativeMethods.GetForegroundWindow();
        var foregroundThread = foreground != IntPtr.Zero
            ? NativeMethods.GetWindowThreadProcessId(foreground, out _)
            : 0;
        var currentThread = NativeMethods.GetCurrentThreadId();
        var attached = foregroundThread != 0 &&
                       foregroundThread != currentThread &&
                       NativeMethods.AttachThreadInput(foregroundThread, currentThread, true);
        NativeMethods.SetForegroundWindow(inputWindow);
        NativeMethods.SetFocus(inputWindow);
        if (attached)
        {
            NativeMethods.AttachThreadInput(foregroundThread, currentThread, false);
        }

        Activate();
        Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            () =>
            {
                if (!_cancelled && IsVisible)
                {
                    InputBox.Focus();
                    Keyboard.Focus(InputBox);
                    InputBox.CaretIndex = InputBox.Text.Length;
                }
            });
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _timeoutTimer.Stop();
        _autoSubmitTimer.Stop();
        _imePollTimer.Stop();
    }
}
