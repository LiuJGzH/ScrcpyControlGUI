using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using ScrcpyControl.Infrastructure;
using ScrcpyControl.Models;
using ScrcpyControl.Services;

namespace ScrcpyControl;

public partial class MainWindow : Window
{
    private readonly PathService _paths;
    private readonly SettingsService _settings;
    private readonly AdbService _adb;
    private readonly ScrcpyProcessService _scrcpy;
    private readonly ScrcpyInputMonitorService _inputMonitor;
    private readonly IdleBrightnessService _idleBrightness;
    private readonly ObservableCollection<DeviceInfo> _devices = new();
    private readonly ObservableCollection<RemoteEntry> _fileEntries = new();
    private readonly ObservableCollection<TransferJob> _transferJobs = new();
    private readonly List<string> _logLines = new();
    private readonly CancellationTokenSource _appCancellation = new();
    private readonly DispatcherTimer _deviceRefreshTimer;

    private AppSettings _appSettings = new();
    private IReadOnlyList<ScrcpyOptionDefinition> _optionDefinitions = Array.Empty<ScrcpyOptionDefinition>();
    private List<ScrcpyOptionValue> _optionValues = new();
    private DeviceInfo? _selectedDevice;
    private bool _updatingDeviceSelection;
    private bool _isLoaded;
    private bool _isClosing;
    private bool _isStopping;
    private bool _screenOff;
    private bool _activeNoControl;
    private bool _isRefreshingDevices;
    private bool _lastDeviceRefreshFailed;
    private string? _lastDeviceSnapshot;
    private string _currentRemotePath = "/sdcard/";
    private string _currentInputPackage = string.Empty;
    private bool _isSendingMessage;
    private DateTime _lastCaptureErrorHintAt = DateTime.MinValue;
    private CancellationTokenSource? _transferCancellation;
    private InputSinkWindow? _inputSinkWindow;
    private readonly SemaphoreSlim _textSendGate = new(1, 1);
    private int _inputClickGeneration;
    private HwndSource? _hotKeySource;
    private IntPtr _hotKeyWindow;
    private const int InputSinkHotKeyId = 0x5343;
    private const int ClipboardSyncDelayMilliseconds = 120;

    public MainWindow()
    {
        _paths = new PathService();
        _settings = new SettingsService(_paths);
        _adb = new AdbService(_paths);
        _scrcpy = new ScrcpyProcessService(_paths);
        _inputMonitor = new ScrcpyInputMonitorService();
        _idleBrightness = new IdleBrightnessService(_adb, _settings);
        _deviceRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _deviceRefreshTimer.Tick += DeviceRefreshTimer_Tick;

        InitializeComponent();

        DeviceComboBox.ItemsSource = _devices;
        FileListView.ItemsSource = _fileEntries;
        TransferListView.ItemsSource = _transferJobs;
        ScrcpyDirectoryBox.Text = _paths.BaseDirectory;
        DataDirectoryBox.Text = _settings.ConfigDirectory;

        _scrcpy.LogReceived += Scrcpy_LogReceived;
        _scrcpy.StateChanged += Scrcpy_StateChanged;
        _scrcpy.ProcessExited += Scrcpy_ProcessExited;
        _inputMonitor.ActivityDetected += InputMonitor_ActivityDetected;
        _inputMonitor.ScrcpyClicked += InputMonitor_ScrcpyClicked;
        _inputMonitor.RightShortPressRequested += InputMonitor_RightShortPressRequested;
        _inputMonitor.RightLongPressTriggered += InputMonitor_RightLongPressTriggered;
        _inputMonitor.MonitorError += InputMonitor_MonitorError;
        _idleBrightness.StatusChanged += IdleBrightness_StatusChanged;
        _idleBrightness.ErrorOccurred += IdleBrightness_ErrorOccurred;

        SourceInitialized += MainWindow_SourceInitialized;
        TrySetWindowIcon();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        try
        {
            _appSettings = await _settings.LoadAsync(_appCancellation.Token);
            ApplySettingsToUi();
            await LoadVersionAsync();

            if (_paths.GetMissingFiles().Count == 0)
            {
                await LoadOptionsAsync(_appSettings.Options);
            }
            else
            {
                AppendLog(
                    "缺少 scrcpy 工具文件：" +
                    string.Join("、", _paths.GetMissingFiles()));
            }

            await RefreshDevicesAsync();
            _deviceRefreshTimer.Start();
            await _idleBrightness.RecoverPendingAsync();
            AppendLog("Scrcpy 控制台已就绪");
            BottomStatusText.Text = "就绪";
        }
        catch (Exception ex)
        {
            AppendLog($"初始化失败：{ex.Message}");
            BottomStatusText.Text = "初始化失败";
        }
    }

    private async void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_isClosing)
        {
            return;
        }

        e.Cancel = true;
        _deviceRefreshTimer.Stop();
        BottomStatusText.Text = "正在清理…";

        try
        {
            _transferCancellation?.Cancel();
            _inputSinkWindow?.Cancel();
            _inputSinkWindow = null;
            CaptureSettingsFromUi();
            await _settings.SaveAsync(_appSettings, CancellationToken.None);
            await StopScrcpyAsync();
            await _idleBrightness.RestoreAsync();
        }
        catch (Exception ex)
        {
            await _settings.AppendLogAsync($"关闭清理失败：{ex.Message}");
        }
        finally
        {
            _appCancellation.Cancel();
            if (_hotKeyWindow != IntPtr.Zero)
            {
                NativeMethods.UnregisterHotKey(_hotKeyWindow, InputSinkHotKeyId);
            }
            _hotKeySource?.RemoveHook(MainWindow_WndProc);
            _inputMonitor.Dispose();
            _scrcpy.Dispose();
            _idleBrightness.Stop();
            _isClosing = true;
            Close();
        }
    }

    private async Task LoadVersionAsync()
    {
        if (!File.Exists(_paths.ScrcpyExe))
        {
            VersionText.Text = "未找到 scrcpy.exe";
            return;
        }

        try
        {
            var result = await ProcessRunner.RunAsync(
                _paths.ScrcpyExe,
                ["--version"],
                _paths.BaseDirectory,
                _appCancellation.Token);
            var firstLine = result.CombinedOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()?
                .Trim();
            VersionText.Text = string.IsNullOrWhiteSpace(firstLine) ? "版本读取失败" : firstLine;
        }
        catch (Exception ex)
        {
            VersionText.Text = "版本读取失败";
            AppendLog($"读取 scrcpy 版本失败：{ex.Message}");
        }
    }

    private async void DeviceRefreshTimer_Tick(object? sender, EventArgs e)
    {
        if (_isClosing || _isRefreshingDevices)
        {
            return;
        }

        await RefreshDevicesAsync();
    }

    private async Task RefreshDevicesAsync()
    {
        if (_paths.GetMissingFiles().Count > 0)
        {
            ConnectionStatusText.Text = "工具文件不完整";
            BottomStatusText.Text = "请将 GUI 放在 scrcpy.exe 同一目录";
            return;
        }

        if (_isRefreshingDevices)
        {
            return;
        }

        _isRefreshingDevices = true;
        try
        {
            var devices = await _adb.GetDevicesAsync(_appCancellation.Token);
            var snapshot = string.Join(
                '\n',
                devices
                    .OrderBy(device => device.Serial, StringComparer.Ordinal)
                    .Select(device =>
                        $"{device.Serial}|{device.State}|{device.Model}|{device.TransportId}"));
            var changed = !string.Equals(
                snapshot,
                _lastDeviceSnapshot,
                StringComparison.Ordinal);

            if (changed)
            {
                _updatingDeviceSelection = true;
                _devices.Clear();
                foreach (var device in devices)
                {
                    _devices.Add(device);
                }

                var previousSerial = _selectedDevice?.Serial ?? _appSettings.SelectedSerial;
                var target = _devices.FirstOrDefault(device => device.Serial == previousSerial)
                             ?? _devices.FirstOrDefault(device => device.IsUsable)
                             ?? _devices.FirstOrDefault();
                DeviceComboBox.SelectedItem = target;
                _selectedDevice = target;
                _updatingDeviceSelection = false;

                await UpdateSelectedDeviceAsync();
                _lastDeviceSnapshot = snapshot;

                if (_devices.Count == 0)
                {
                    AppendLog("未检测到 ADB 设备，继续自动检测");
                }
                else
                {
                    AppendLog($"设备列表已自动更新：{_devices.Count} 台");
                }
            }

            UpdateDeviceAvailability();
            _lastDeviceRefreshFailed = false;
        }
        catch (Exception ex)
        {
            _updatingDeviceSelection = false;
            _devices.Clear();
            _selectedDevice = null;
            ConnectionDot.Fill = (Brush)FindResource("DangerBrush");
            ConnectionStatusText.Text = "ADB 不可用";
            DeviceModelText.Text = "—";
            DeviceSystemText.Text = "—";
            if (!_lastDeviceRefreshFailed)
            {
                AppendLog($"自动检测设备失败：{ex.Message}");
            }
            _lastDeviceRefreshFailed = true;
        }
        finally
        {
            _isRefreshingDevices = false;
        }
    }

    private async void DeviceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingDeviceSelection)
        {
            return;
        }

        _selectedDevice = DeviceComboBox.SelectedItem as DeviceInfo;
        await UpdateSelectedDeviceAsync();
        UpdateDeviceAvailability();
    }

    private async Task UpdateSelectedDeviceAsync()
    {
        if (_selectedDevice is null)
        {
            ConnectionDot.Fill = (Brush)FindResource("MutedBrush");
            ConnectionStatusText.Text = "未连接设备";
            DeviceModelText.Text = "—";
            DeviceSystemText.Text = "—";
            return;
        }

        if (!_selectedDevice.IsUsable)
        {
            ConnectionDot.Fill = (Brush)FindResource("DangerBrush");
            ConnectionStatusText.Text = _selectedDevice.State switch
            {
                "unauthorized" => "设备未授权 USB 调试",
                "offline" => "设备离线",
                _ => $"设备状态：{_selectedDevice.State}"
            };
            DeviceModelText.Text = _selectedDevice.Model;
            DeviceSystemText.Text = "—";
            return;
        }

        ConnectionDot.Fill = (Brush)FindResource("TealBrush");
        ConnectionStatusText.Text = "设备已连接";

        try
        {
            var model = await _adb.GetPropertyAsync(
                _selectedDevice.Serial,
                "ro.product.model",
                _appCancellation.Token);
            var android = await _adb.GetPropertyAsync(
                _selectedDevice.Serial,
                "ro.build.version.release",
                _appCancellation.Token);
            var os = await _adb.GetPropertyAsync(
                _selectedDevice.Serial,
                "ro.mi.os.version.name",
                _appCancellation.Token);

            DeviceModelText.Text = string.IsNullOrWhiteSpace(model)
                ? _selectedDevice.Model
                : model;
            DeviceSystemText.Text =
                $"{(string.IsNullOrWhiteSpace(os) ? "HyperOS" : os)} · Android {android}";
            BottomStatusText.Text = $"{DeviceModelText.Text} · USB 已连接";
            if (_isLoaded && _optionValues.Count > 0)
            {
                UpdateCommandPreview();
            }
        }
        catch (Exception ex)
        {
            AppendLog($"读取设备信息失败：{ex.Message}");
            ConnectionStatusText.Text = "设备连接不稳定";
        }
    }

    private void UpdateDeviceAvailability()
    {
        var hasDevice = _selectedDevice?.IsUsable == true;
        var toolsReady = _paths.GetMissingFiles().Count == 0;
        StartStopButton.IsEnabled = toolsReady && (hasDevice || _scrcpy.IsRunning);
        DashboardStartButton.IsEnabled = StartStopButton.IsEnabled;
        UpdateRunningUi(_scrcpy.IsRunning && !_isStopping);
    }

    private async void StartStop_Click(object sender, RoutedEventArgs e)
    {
        if (_scrcpy.IsRunning)
        {
            await StopScrcpyAsync();
        }
        else
        {
            await StartScrcpyAsync();
        }
    }

    private async Task StartScrcpyAsync()
    {
        if (_selectedDevice?.IsUsable != true)
        {
            MessageBox.Show(
                "请先连接手机并允许 USB 调试。",
                "Scrcpy 控制台",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        try
        {
            CaptureSettingsFromUi();
            await _settings.SaveAsync(_appSettings, _appCancellation.Token);
            var arguments = BuildScrcpyArguments();
            CommandPreviewBox.Text = CommandLineParser.FormatForDisplay(arguments);

            if (ValidateOptionConflicts(arguments) is { Count: > 0 } errors)
            {
                MessageBox.Show(
                    string.Join(Environment.NewLine, errors),
                    "参数冲突",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                ParametersNav.IsChecked = true;
                return;
            }

            StartStopButton.IsEnabled = false;
            DashboardStartButton.IsEnabled = false;
            BottomStatusText.Text = "正在启动 scrcpy…";
            await _scrcpy.StartAsync(arguments, _appCancellation.Token);

            if (!_scrcpy.IsRunning)
            {
                AppendLog("scrcpy 启动后立即退出，请检查参数和日志");
                UpdateRunningUi(false);
                return;
            }

            _screenOff = IsOptionEnabled("--turn-screen-off");
            _activeNoControl = IsOptionEnabled("--no-control");
            UpdateRunningUi(true);

            if (_scrcpy.ProcessId is { } pid)
            {
                _inputMonitor.Start(
                    pid,
                    _scrcpy.WindowHandle,
                    _appSettings.LongPressRightClickEnabled,
                    _appSettings.LongPressRightClickMilliseconds);
                AppendLog(_appSettings.LongPressRightClickEnabled
                    ? $"右键长按已启用：保持 {_appSettings.LongPressRightClickMilliseconds} 毫秒切换最近任务"
                    : "右键长按未启用，右键保持返回功能");
            }

            _idleBrightness.Start(
                _selectedDevice.Serial,
                _appSettings.IdleDimEnabled,
                _appSettings.IdleSeconds,
                _appSettings.DimBrightnessPercent,
                screenOn: !_screenOff);

            AppendLog(_screenOff
                ? "scrcpy 已启动，手机屏幕初始为熄灭状态"
                : "scrcpy 已启动，键鼠空闲计时开始");
            BottomStatusText.Text = "scrcpy 正在运行";
        }
        catch (Exception ex)
        {
            AppendLog($"启动 scrcpy 失败：{ex.Message}");
            MessageBox.Show(
                ex.Message,
                "无法启动 scrcpy",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            UpdateRunningUi(false);
        }
    }

    private async Task StopScrcpyAsync()
    {
        if (_isStopping)
        {
            return;
        }

        _isStopping = true;
        try
        {
            _inputSinkWindow?.Cancel();
            _inputSinkWindow = null;
            _inputMonitor.Stop();
            await _idleBrightness.RestoreAsync();
            _idleBrightness.Stop();
            if (_scrcpy.IsRunning)
            {
                await _scrcpy.StopAsync();
            }

            _screenOff = false;
            AppendLog("scrcpy 已停止");
        }
        finally
        {
            _isStopping = false;
            UpdateRunningUi(false);
        }
    }

    private void UpdateRunningUi(bool running)
    {
        StartStopButton.Content = running ? "停止 scrcpy" : "启动 scrcpy";
        DashboardStartIcon.Text = running ? "\uE71A" : "\uE768";
        DashboardStartText.Text = running ? "停止 scrcpy" : "启动 scrcpy";
        ScreenToggleButton.IsEnabled = running && !_activeNoControl;
        QuickScreenButton.IsEnabled = running && !_activeNoControl;
        ScreenToggleButton.Content = _screenOff ? "\uE706" : "\uE708";
        ScreenToggleButton.ToolTip = _screenOff
            ? "点亮手机屏幕（保持投屏）"
            : "熄灭手机屏幕（保持投屏）";
        ControlScreenToggleIcon.Text = _screenOff ? "\uE706" : "\uE708";
        ControlScreenToggleText.Text = _screenOff ? "点亮屏幕" : "熄灭屏幕";
        ControlScreenToggleButton.ToolTip = _screenOff
            ? "点亮手机屏幕（保持投屏）"
            : "熄灭手机屏幕（保持投屏）";
        ControlScreenToggleButton.IsEnabled =
            running && !_activeNoControl && _selectedDevice?.IsUsable == true;
        PhoneControlBar.IsEnabled = _selectedDevice?.IsUsable == true;

        if (!running)
        {
            IdleStatusText.Text = "投屏启动后生效";
            ConnectionStatusText.Text = _selectedDevice?.IsUsable == true
                ? "设备已连接"
                : "未连接设备";
        }

        UpdateDeviceAvailabilityButtons(running);
    }

    private void UpdateDeviceAvailabilityButtons(bool running)
    {
        var hasDevice = _selectedDevice?.IsUsable == true;
        var toolsReady = _paths.GetMissingFiles().Count == 0;
        StartStopButton.IsEnabled = toolsReady && (hasDevice || running);
        DashboardStartButton.IsEnabled = StartStopButton.IsEnabled;
    }

    private async void ScreenToggle_Click(object sender, RoutedEventArgs e)
    {
        if (!_scrcpy.IsRunning)
        {
            return;
        }

        if (_activeNoControl)
        {
            MessageBox.Show(
                "当前启用了 --no-control，scrcpy 不允许控制手机屏幕。",
                "无法切换屏幕",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        try
        {
            var turnOn = _screenOff;
            var modifier = GetOptionValue("--shortcut-mod") ?? "lalt,lsuper";
            await _scrcpy.SendScreenShortcutAsync(
                turnOn,
                modifier,
                _appCancellation.Token);

            _screenOff = !_screenOff;
            _idleBrightness.SetScreenOn(!_screenOff);
            ScreenToggleButton.Content = _screenOff ? "\uE706" : "\uE708";
            ScreenToggleButton.ToolTip = _screenOff
                ? "点亮手机屏幕（保持投屏）"
                : "熄灭手机屏幕（保持投屏）";
            IdleStatusText.Text = _screenOff
                ? "手机屏幕已熄灭，亮度调节暂停"
                : "手机屏幕已点亮，空闲计时重新开始";
        }
        catch (Exception ex)
        {
            AppendLog($"切换手机屏幕失败：{ex.Message}");
        }
    }

    private async void PhoneKeyControl_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button ||
            button.Tag is not string action ||
            _selectedDevice?.IsUsable != true)
        {
            return;
        }

        var (keyCode, displayName) = action switch
        {
            "back" => ("KEYCODE_BACK", "返回键"),
            "home" => ("KEYCODE_HOME", "主屏幕键"),
            "recent" => ("KEYCODE_APP_SWITCH", "最近任务键"),
            "menu" => ("KEYCODE_MENU", "菜单键"),
            "volume_down" => ("KEYCODE_VOLUME_DOWN", "音量减小"),
            "volume_up" => ("KEYCODE_VOLUME_UP", "音量增大"),
            "power" => ("KEYCODE_POWER", "电源键"),
            _ => (string.Empty, string.Empty)
        };

        if (action != "voice_assist" && string.IsNullOrEmpty(keyCode))
        {
            return;
        }

        button.IsEnabled = false;
        try
        {
            if (action == "voice_assist")
            {
                await LaunchVoiceAssistantAsync();
                return;
            }

            await _adb.RunShellAsync(
                _selectedDevice.Serial,
                $"input keyevent {keyCode}",
                _appCancellation.Token);
            AppendLog($"已发送手机按键：{displayName}");
            BottomStatusText.Text = $"{displayName}已发送";
        }
        catch (Exception ex)
        {
            AppendLog($"{displayName}发送失败：{ex.Message}");
            BottomStatusText.Text = $"{displayName}发送失败";
        }
        finally
        {
            button.IsEnabled = true;
            UpdatePhoneControlAvailability();
        }
    }

    private async Task LaunchVoiceAssistantAsync()
    {
        if (_selectedDevice?.IsUsable != true)
        {
            return;
        }

        var serial = _selectedDevice.Serial;

        // Xiaomi registers XiaoAi on VOICE_COMMAND; ASSIST only resolves to the
        // system resolver, so the generic VOICE_ASSIST key does nothing here.
        var output = await _adb.RunShellAsync(
            serial,
            "am start -a android.intent.action.VOICE_COMMAND",
            _appCancellation.Token);

        if (output.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("does not exist", StringComparison.OrdinalIgnoreCase))
        {
            await _adb.RunShellAsync(
                serial,
                "input keyevent KEYCODE_VOICE_ASSIST",
                _appCancellation.Token);
            AppendLog("语音命令 Intent 不可用，已改用 VOICE_ASSIST 键");
            BottomStatusText.Text = "已发送语音助手键";
            return;
        }

        AppendLog("已通过 VOICE_COMMAND 唤醒语音助手");
        BottomStatusText.Text = "已唤醒语音助手";
    }

    private async void ScreenshotGallery_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || _selectedDevice?.IsUsable != true)
        {
            return;
        }

        button.IsEnabled = false;
        var remotePath = string.Empty;
        try
        {
            remotePath = BuildRemoteScreenshotPath(
                "/sdcard/Pictures/Screenshots",
                $"Screenshot_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
            await CaptureRemoteScreenshotAsync(remotePath, _appCancellation.Token);

            var scanUri = "file://" + remotePath;
            await _adb.RunShellAsync(
                _selectedDevice.Serial,
                $"am broadcast -a android.intent.action.MEDIA_SCANNER_SCAN_FILE -d {AdbService.QuotePosix(scanUri)}",
                _appCancellation.Token);

            AppendLog($"系统截屏已保存到手机相册：{remotePath}");
            BottomStatusText.Text = "系统截屏已保存到手机相册";
        }
        catch (Exception ex)
        {
            AppendLog($"系统截屏失败：{ex.Message}");
            BottomStatusText.Text = "系统截屏失败";
        }
        finally
        {
            button.IsEnabled = true;
            UpdatePhoneControlAvailability();
        }
    }

    private async void ScreenshotClipboard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || _selectedDevice?.IsUsable != true)
        {
            return;
        }

        button.IsEnabled = false;
        var localDirectory = Path.Combine(
            Path.GetTempPath(),
            "ScrcpyControl",
            Guid.NewGuid().ToString("N"));
        var remotePath = BuildRemoteScreenshotPath(
            "/sdcard/.scrcpy_control_tmp",
            $"clipboard-{Guid.NewGuid():N}.png");
        var localPath = Path.Combine(localDirectory, Path.GetFileName(remotePath));

        try
        {
            Directory.CreateDirectory(localDirectory);
            await CaptureRemoteScreenshotAsync(remotePath, _appCancellation.Token);
            await _adb.PullAsync(
                _selectedDevice.Serial,
                remotePath,
                localDirectory,
                _ => { },
                _appCancellation.Token);

            var bitmap = LoadBitmapFromFile(localPath);
            await SetImageClipboardWithRetryAsync(bitmap);
            AppendLog("手机截图已复制到 Windows 剪贴板");
            BottomStatusText.Text = "手机截图已复制到 Windows 剪贴板";
        }
        catch (Exception ex)
        {
            AppendLog($"截图复制到剪贴板失败：{ex.Message}");
            BottomStatusText.Text = "截图复制到剪贴板失败";
        }
        finally
        {
            try
            {
                await _adb.RunShellAsync(
                    _selectedDevice.Serial,
                    $"rm -f {AdbService.QuotePosix(remotePath)}",
                    CancellationToken.None);
            }
            catch
            {
                // Remote temporary files are best-effort cleanup.
            }

            try
            {
                File.Delete(localPath);
                Directory.Delete(localDirectory);
            }
            catch
            {
                // Local temporary files are best-effort cleanup.
            }

            button.IsEnabled = true;
            UpdatePhoneControlAvailability();
        }
    }

    private async Task CaptureRemoteScreenshotAsync(
        string remotePath,
        CancellationToken cancellationToken)
    {
        if (_selectedDevice?.IsUsable != true)
        {
            throw new InvalidOperationException("手机未连接或设备不可用");
        }

        var remoteDirectory = remotePath[..(remotePath.LastIndexOf('/') + 1)];
        await _adb.RunShellAsync(
            _selectedDevice.Serial,
            $"mkdir -p {AdbService.QuotePosix(remoteDirectory)} && screencap -p {AdbService.QuotePosix(remotePath)}",
            cancellationToken);
    }

    private static string BuildRemoteScreenshotPath(string directory, string fileName) =>
        $"{directory.TrimEnd('/')}/{fileName}";

    private static BitmapImage LoadBitmapFromFile(string path)
    {
        using var stream = File.OpenRead(path);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static async Task SetImageClipboardWithRetryAsync(BitmapSource image)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                var data = new DataObject();
                data.SetImage(image);
                Clipboard.SetDataObject(data, copy: true);
                if (!Clipboard.ContainsImage())
                {
                    throw new InvalidOperationException("剪贴板图像校验失败");
                }
                return;
            }
            catch when (attempt < 7)
            {
                await Task.Delay(40);
            }
        }
    }

    private void UpdatePhoneControlAvailability()
    {
        var hasDevice = _selectedDevice?.IsUsable == true;
        PhoneControlBar.IsEnabled = hasDevice;
        ControlScreenToggleButton.IsEnabled =
            hasDevice && _scrcpy.IsRunning && !_activeNoControl;
    }

    private void OpenFilesPage_Click(object sender, RoutedEventArgs e)
    {
        FilesNav.IsChecked = true;
    }

    private void NavButton_Checked(object sender, RoutedEventArgs e)
    {
        if (!_isLoaded || sender is not RadioButton radio || radio.Tag is not string tag)
        {
            return;
        }

        DashboardPage.Visibility = tag == "Dashboard" ? Visibility.Visible : Visibility.Collapsed;
        ParametersPage.Visibility = tag == "Parameters" ? Visibility.Visible : Visibility.Collapsed;
        FilesPage.Visibility = tag == "Files" ? Visibility.Visible : Visibility.Collapsed;
        LogsPage.Visibility = tag == "Logs" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = tag == "Settings" ? Visibility.Visible : Visibility.Collapsed;

        if (tag == "Files" && _selectedDevice?.IsUsable == true && _fileEntries.Count == 0)
        {
            _ = LoadRemoteDirectoryAsync(_currentRemotePath);
        }
    }

    private async void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            CaptureSettingsFromUi();
            ApplyAutoStayAwakeToOptions();
            _appSettings.Options = CaptureOptionStates();
            await _settings.SaveAsync(_appSettings, _appCancellation.Token);
            AppendLog("设置已保存");
            BottomStatusText.Text = "设置已保存";

            if (_scrcpy.IsRunning && _selectedDevice is not null)
            {
                _idleBrightness.Start(
                    _selectedDevice.Serial,
                    _appSettings.IdleDimEnabled,
                    _appSettings.IdleSeconds,
                    _appSettings.DimBrightnessPercent,
                    screenOn: !_screenOff);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
                "保存设置失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void ApplySettingsToUi()
    {
        AutoStayAwakeCheckBox.IsChecked = _appSettings.AutoStayAwake;
        IdleDimCheckBox.IsChecked = _appSettings.IdleDimEnabled;
        ClickToInputCheckBox.IsChecked = _appSettings.ClickToInputEnabled;
        AutoSubmitCheckBox.IsChecked = _appSettings.AutoSubmitEnabled;
        AutoSubmitDebounceBox.Text = _appSettings.AutoSubmitDebounceMilliseconds.ToString();
        LongPressRightClickCheckBox.IsChecked = _appSettings.LongPressRightClickEnabled;
        LongPressRightClickBox.Text = _appSettings.LongPressRightClickMilliseconds.ToString();
        EnterSendsMessageCheckBox.IsChecked = _appSettings.EnterSendsMessage;
        AppLevelPasteBox.Text = _appSettings.AppLevelPastePackages;
        IdleSecondsBox.Text = _appSettings.IdleSeconds.ToString();
        DimBrightnessBox.Text = _appSettings.DimBrightnessPercent.ToString();
        ExtraArgumentsBox.Text = _appSettings.ExtraArguments;
        _currentRemotePath = string.IsNullOrWhiteSpace(_appSettings.LastRemotePath)
            ? "/sdcard/"
            : _appSettings.LastRemotePath;
        RemotePathBox.Text = _currentRemotePath;
    }

    private void CaptureSettingsFromUi()
    {
        _appSettings.AutoStayAwake = AutoStayAwakeCheckBox.IsChecked == true;
        _appSettings.IdleDimEnabled = IdleDimCheckBox.IsChecked == true;
        _appSettings.ClickToInputEnabled = ClickToInputCheckBox.IsChecked == true;
        _appSettings.AutoSubmitEnabled = AutoSubmitCheckBox.IsChecked == true;
        _appSettings.AutoSubmitDebounceMilliseconds =
            int.TryParse(AutoSubmitDebounceBox.Text, out var autoSubmitDebounce)
                ? Math.Clamp(autoSubmitDebounce, 50, 5000)
                : 100;
        _appSettings.LongPressRightClickEnabled = LongPressRightClickCheckBox.IsChecked == true;
        _appSettings.LongPressRightClickMilliseconds =
            int.TryParse(LongPressRightClickBox.Text, out var longPressMilliseconds)
                ? Math.Clamp(longPressMilliseconds, 200, 2000)
                : 450;
        _appSettings.AppLevelPastePackages = AppLevelPasteBox.Text.Trim();
        _appSettings.EnterSendsMessage = EnterSendsMessageCheckBox.IsChecked == true;
        _appSettings.IdleSeconds = int.TryParse(IdleSecondsBox.Text, out var seconds)
            ? Math.Clamp(seconds, 1, 3600)
            : 60;
        _appSettings.DimBrightnessPercent = int.TryParse(DimBrightnessBox.Text, out var brightness)
            ? Math.Clamp(brightness, 1, 100)
            : 10;
        _appSettings.ExtraArguments = ExtraArgumentsBox.Text;
        _appSettings.SelectedSerial = _selectedDevice?.Serial ?? string.Empty;
        _appSettings.LastRemotePath = _currentRemotePath;
        _appSettings.Options = CaptureOptionStates();
    }

    private void ApplyAutoStayAwakeToOptions()
    {
        var stayAwake = _optionValues.FirstOrDefault(
            option => option.Definition.CliName == "--stay-awake");
        if (stayAwake is not null)
        {
            stayAwake.IsEnabled = _appSettings.AutoStayAwake;
            RenderOptionRows();
            UpdateCommandPreview();
        }
    }

    private void Scrcpy_LogReceived(object? sender, string line)
    {
        AppendLog(line);
        if (line.Contains("Device display turned off", StringComparison.OrdinalIgnoreCase))
        {
            Dispatcher.BeginInvoke(() => ApplyDeviceScreenState(screenOff: true));
        }
        else if (line.Contains("Device display turned on", StringComparison.OrdinalIgnoreCase))
        {
            Dispatcher.BeginInvoke(() => ApplyDeviceScreenState(screenOff: false));
        }
        else if (line.Contains("Capture/encoding error", StringComparison.OrdinalIgnoreCase))
        {
            // Typically means the panel is off while --no-power-on keeps scrcpy
            // from waking it. Throttled: the server repeats this error per retry.
            var now = DateTime.UtcNow;
            if (now - _lastCaptureErrorHintAt < TimeSpan.FromSeconds(10))
            {
                return;
            }

            _lastCaptureErrorHintAt = now;
            AppendLog("提示：画面采集失败，手机屏幕可能处于熄灭状态。请点击控制台的「点亮屏幕」按钮恢复。");
            Dispatcher.BeginInvoke(() =>
                BottomStatusText.Text = "画面采集失败：屏幕可能已熄灭，点「点亮屏幕」恢复");
        }
    }

    private void ApplyDeviceScreenState(bool screenOff)
    {
        _screenOff = screenOff;
        _idleBrightness.SetScreenOn(!screenOff);
        if (_scrcpy.IsRunning)
        {
            UpdateRunningUi(running: true);
        }
        IdleStatusText.Text = screenOff
            ? "手机屏幕已熄灭，亮度调节暂停"
            : "手机屏幕已点亮，空闲计时重新开始";
    }

    private void Scrcpy_StateChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() => UpdateRunningUi(_scrcpy.IsRunning && !_isStopping));
    }

    private async void Scrcpy_ProcessExited(object? sender, int exitCode)
    {
        try
        {
            // This callback runs on a thread-pool thread, so every WPF object
            // must be touched through the dispatcher. Closing the IME host from
            // here used to throw CrossThreadInvalidAccessException and, being an
            // async void, took the whole process down.
            await Dispatcher.InvokeAsync(() =>
            {
                AppendLog($"scrcpy 已退出，退出代码 {exitCode}");
                UpdateRunningUi(false);

                if (_isStopping || _isClosing)
                {
                    return;
                }

                try
                {
                    _inputSinkWindow?.Cancel();
                }
                catch
                {
                    // The host window may already be closed.
                }

                _inputSinkWindow = null;
                _screenOff = false;
                _activeNoControl = false;
            });

            if (_isStopping || _isClosing)
            {
                return;
            }

            try
            {
                _inputMonitor.Stop();
            }
            catch
            {
                // Hooks may already be torn down.
            }

            await _idleBrightness.RestoreAsync();
            _idleBrightness.Stop();
        }
        catch (Exception ex)
        {
            try
            {
                await _settings.AppendLogAsync($"处理 scrcpy 退出时出错：{ex}");
            }
            catch
            {
                // Logging must never crash the shutdown path.
            }
        }
    }

    private void InputMonitor_ActivityDetected(object? sender, EventArgs e)
    {
        _ = _idleBrightness.NotifyActivityAsync();
        Dispatcher.BeginInvoke(() =>
        {
            IdleStatusText.Text = "检测到 scrcpy 操作";
        });
    }

    private void InputMonitor_ScrcpyClicked(object? sender, ScrcpyClickEventArgs e)
    {
        var generation = Interlocked.Increment(ref _inputClickGeneration);
        _ = HandleScrcpyClickAsync(e, generation);
    }

    private void InputMonitor_RightShortPressRequested(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() => _ = SendBackOrWakeAsync()));
    }

    private async Task SendBackOrWakeAsync()
    {
        try
        {
            var shortcutModifier = GetOptionValue("--shortcut-mod") ?? "lalt,lsuper";
            if (_screenOff)
            {
                // Mirrors scrcpy's own right-click behaviour: wake the device
                // instead of sending BACK while the screen is off.
                await _scrcpy.SendScreenShortcutAsync(
                    turnScreenOn: true,
                    shortcutModifier,
                    _appCancellation.Token);
                ApplyDeviceScreenState(screenOff: false);
                AppendLog("右键短按：手机屏幕已点亮");
                BottomStatusText.Text = "手机屏幕已点亮";
                return;
            }

            await _scrcpy.SendBackShortcutAsync(shortcutModifier, _appCancellation.Token);
            AppendLog("右键短按：已发送返回");
            BottomStatusText.Text = "已发送返回";
        }
        catch (OperationCanceledException) when (_appCancellation.IsCancellationRequested)
        {
            // Application or scrcpy is shutting down.
        }
        catch (Exception ex)
        {
            AppendLog($"发送返回失败：{ex.Message}");
            BottomStatusText.Text = "发送返回失败";
        }
    }

    private void InputMonitor_RightLongPressTriggered(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() => _ = SendAppSwitchAsync()));
    }

    private async Task SendAppSwitchAsync()
    {
        try
        {
            var shortcutModifier = GetOptionValue("--shortcut-mod") ?? "lalt,lsuper";
            await _scrcpy.SendAppSwitchShortcutAsync(
                shortcutModifier,
                _appCancellation.Token);
            AppendLog("检测到右键长按，已切换为最近任务");
            BottomStatusText.Text = "已切换到最近任务";
        }
        catch (OperationCanceledException) when (_appCancellation.IsCancellationRequested)
        {
            // Application or scrcpy is shutting down.
        }
        catch (Exception ex)
        {
            AppendLog($"切换最近任务失败：{ex.Message}");
            BottomStatusText.Text = "切换最近任务失败";
        }
    }

    private async Task HandleScrcpyClickAsync(ScrcpyClickEventArgs click, int generation)
    {
        try
        {
            if (!_appSettings.ClickToInputEnabled ||
                !_scrcpy.IsRunning ||
                _selectedDevice?.IsUsable != true)
            {
                return;
            }

            await Task.Delay(180, _appCancellation.Token).ConfigureAwait(false);
            if (generation != Volatile.Read(ref _inputClickGeneration) ||
                !_scrcpy.IsRunning)
            {
                return;
            }

            var focus = await _adb.GetTextInputFocusAsync(
                _selectedDevice.Serial,
                _appCancellation.Token).ConfigureAwait(false);
            if (!focus.Focused || generation != Volatile.Read(ref _inputClickGeneration))
            {
                return;
            }

            _currentInputPackage = focus.Package;
            AppendLog($"检测到手机输入框，正在打开透明中文输入宿主（{focus.Package}）");
            await Dispatcher.InvokeAsync(() => ShowInputSink(click));
        }
        catch (OperationCanceledException)
        {
            // Application or scrcpy is shutting down.
        }
        catch (Exception ex)
        {
            AppendLog($"创建中文输入宿主失败：{ex.Message}");
        }
    }

    private void ShowInputSink(ScrcpyClickEventArgs click)
    {
        if (!_scrcpy.IsRunning)
        {
            return;
        }

        if (_inputSinkWindow is not null)
        {
            if (_inputSinkWindow.RestoreFocusForContinuation())
            {
                AppendLog("中文输入宿主保持激活，可继续输入下一段文字");
            }

            return;
        }

        var sink = new InputSinkWindow(
            _appSettings.AutoSubmitEnabled,
            _appSettings.AutoSubmitDebounceMilliseconds,
            _appSettings.EnterSendsMessage);
        sink.TextSubmitted += InputSink_TextSubmitted;
        sink.SendMessageRequested += InputSink_SendMessageRequested;
        sink.NewlineRequested += InputSink_NewlineRequested;
        sink.Cancelled += InputSink_Cancelled;
        sink.Closed += InputSink_Closed;
        _inputSinkWindow = sink;
        sink.ShowAtScreenPoint(click.ScreenX, click.ScreenY, _scrcpy.WindowHandle);
        _ = _idleBrightness.NotifyActivityAsync();
        var sendHint = _appSettings.AutoSubmitEnabled
            ? $"停止输入 {_appSettings.AutoSubmitDebounceMilliseconds} 毫秒自动发送；按 Enter 立即发送，Esc 取消"
            : "按 Enter 发送，Esc 取消";
        AppendLog($"透明中文输入宿主已打开：{sendHint}");
        BottomStatusText.Text = _appSettings.AutoSubmitEnabled
            ? $"中文输入宿主已激活，停止输入 {_appSettings.AutoSubmitDebounceMilliseconds} 毫秒后自动发送"
            : "中文输入宿主已激活，按 Enter 发送";
    }

    private async void InputSink_TextSubmitted(object? sender, string text)
    {
        var sink = sender as InputSinkWindow;
        var gateEntered = false;
        try
        {
            await _textSendGate.WaitAsync(_appCancellation.Token);
            gateEntered = true;
            await SendTextThroughClipboardAsync(text);
        }
        catch (OperationCanceledException) when (_appCancellation.IsCancellationRequested)
        {
            // Application or scrcpy is shutting down.
        }
        catch (Exception ex)
        {
            AppendLog($"中文文本发送失败：{ex.Message}");
        }
        finally
        {
            if (gateEntered)
            {
                _textSendGate.Release();
            }

            if (sink is not null && ReferenceEquals(sink, _inputSinkWindow))
            {
                sink.CompleteSubmission();
                if (!_isClosing)
                {
                    sink.RestoreFocusForContinuation();
                }
                else
                {
                    sink.Cancel();
                }
            }
        }
    }

    private void InputSink_Cancelled(object? sender, EventArgs e)
    {
        BottomStatusText.Text = "已取消中文输入";
    }

    private void InputSink_SendMessageRequested(object? sender, EventArgs e)
    {
        _ = SendMessageToDeviceAsync();
    }

    private void InputSink_NewlineRequested(object? sender, EventArgs e)
    {
        _ = SendNewlineToDeviceAsync();
    }

    private async Task SendMessageToDeviceAsync()
    {
        if (_selectedDevice?.IsUsable != true)
        {
            return;
        }

        // Second line of defence against repeated triggers (auto-repeat keys,
        // double events): only one send may be in flight at a time.
        if (_isSendingMessage)
        {
            return;
        }

        _isSendingMessage = true;
        try
        {
            await _textSendGate.WaitAsync(_appCancellation.Token);
            try
            {
                var tap = await _adb.TapSendButtonAsync(
                    _selectedDevice.Serial,
                    _appCancellation.Token);

                if (!tap.Tapped)
                {
                    // Keyboard hidden: the button position cannot be derived
                    // reliably, so fall back to a plain ENTER.
                    await _adb.RunShellAsync(
                        _selectedDevice.Serial,
                        "input keyevent KEYCODE_ENTER",
                        _appCancellation.Token);
                    AppendLog("软键盘未显示，已退回发送 ENTER");
                }
                else
                {
                    AppendLog($"已点击发送按钮（{tap.X},{tap.Y}）");
                }
            }
            finally
            {
                _textSendGate.Release();
            }

            AppendLog("空输入宿主按回车，已触发发送");
            BottomStatusText.Text = "已触发发送";
        }
        catch (OperationCanceledException) when (_appCancellation.IsCancellationRequested)
        {
            // Application or scrcpy is shutting down.
        }
        catch (Exception ex)
        {
            AppendLog($"触发发送失败：{ex.Message}");
            BottomStatusText.Text = "触发发送失败";
        }
        finally
        {
            _isSendingMessage = false;
        }
    }

    private async Task SendNewlineToDeviceAsync()
    {
        if (_selectedDevice?.IsUsable != true)
        {
            return;
        }

        try
        {
            await _textSendGate.WaitAsync(_appCancellation.Token);
            try
            {
                await _adb.RunShellAsync(
                    _selectedDevice.Serial,
                    "input keyevent KEYCODE_ENTER",
                    _appCancellation.Token);
            }
            finally
            {
                _textSendGate.Release();
            }

            AppendLog("空输入宿主按回车，已向手机发送换行");
            BottomStatusText.Text = "已向手机发送换行";
        }
        catch (OperationCanceledException) when (_appCancellation.IsCancellationRequested)
        {
            // Application or scrcpy is shutting down.
        }
        catch (Exception ex)
        {
            AppendLog($"发送换行失败：{ex.Message}");
            BottomStatusText.Text = "发送换行失败";
        }
    }

    private void InputSink_Closed(object? sender, EventArgs e)
    {
        AppendLog("透明中文输入宿主已关闭");
        if (ReferenceEquals(sender, _inputSinkWindow))
        {
            _inputSinkWindow = null;
        }
    }

    private async Task SendTextThroughClipboardAsync(string text)
    {
        if (string.IsNullOrEmpty(text) || !_scrcpy.IsRunning)
        {
            return;
        }

        await SetPersistentClipboardTextWithRetryAsync(text);

        var shortcutModifier = GetOptionValue("--shortcut-mod") ?? "lalt,lsuper";

        // MOD+V makes scrcpy push the Windows clipboard to the device and then
        // inject KEYCODE_PASTE. It needs no window focus, so the IME host keeps
        // the keyboard and no keystroke gets lost while the text is in flight.
        await _scrcpy.SendPasteShortcutAsync(shortcutModifier, _appCancellation.Token);

        if (!ShortcutModifierUsesControl(shortcutModifier) &&
            NeedsAppLevelPaste(_currentInputPackage))
        {
            // Some apps (闲鱼) ignore the injected KEYCODE_PASTE but do handle a
            // real Ctrl+V. MOD+V has already synchronized the clipboard, so a
            // forwarded Ctrl+V finishes the paste without stealing focus.
            await Task.Delay(ClipboardSyncDelayMilliseconds, _appCancellation.Token);
            await _scrcpy.SendTextPasteKeyAsync(_appCancellation.Token);
            await Task.Delay(40, _appCancellation.Token);
        }

        await Task.Delay(40, _appCancellation.Token);
        AppendLog("已通过剪贴板向手机发送中文文本，并保留在 Windows 剪贴板");
        BottomStatusText.Text = "中文文本已发送并保留在剪贴板，可继续输入";
        await _idleBrightness.NotifyActivityAsync();
    }

    private static bool ShortcutModifierUsesControl(string value) =>
        value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(token => token.Contains("ctrl", StringComparison.OrdinalIgnoreCase));

    private bool NeedsAppLevelPaste(string package)
    {
        if (string.IsNullOrWhiteSpace(package))
        {
            return false;
        }

        return _appSettings.AppLevelPastePackages
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(item => string.Equals(item, package, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task SetPersistentClipboardTextWithRetryAsync(string text)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(
                    new DataObject(DataFormats.UnicodeText, text),
                    copy: true);
                if (!string.Equals(
                        Clipboard.GetText(TextDataFormat.UnicodeText),
                        text,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("剪贴板内容校验失败");
                }

                return;
            }
            catch when (attempt < 7)
            {
                await Task.Delay(30);
            }
        }
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        _hotKeyWindow = new WindowInteropHelper(this).Handle;
        _hotKeySource = HwndSource.FromHwnd(_hotKeyWindow);
        _hotKeySource?.AddHook(MainWindow_WndProc);
        if (!NativeMethods.RegisterHotKey(
                _hotKeyWindow,
                InputSinkHotKeyId,
                NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT,
                NativeMethods.VK_F12))
        {
            AppendLog("无法注册 Ctrl+Alt+F12 中文输入兜底快捷键");
        }
        else
        {
            AppendLog("中文输入兜底快捷键 Ctrl+Alt+F12 已注册");
        }
    }

    private IntPtr MainWindow_WndProc(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message == NativeMethods.WM_HOTKEY &&
            wParam.ToInt32() == InputSinkHotKeyId &&
            _scrcpy.IsRunning)
        {
            if (NativeMethods.GetCursorPos(out var point))
            {
                ShowInputSink(new ScrcpyClickEventArgs(point.X, point.Y));
                handled = true;
            }
        }

        return IntPtr.Zero;
    }

    private void TestInputSink_Click(object sender, RoutedEventArgs e)
    {
        if (!_scrcpy.IsRunning)
        {
            MessageBox.Show(
                "请先启动 scrcpy。",
                "测试输入宿主",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (NativeMethods.GetCursorPos(out var point))
        {
            ShowInputSink(new ScrcpyClickEventArgs(point.X, point.Y));
        }
    }

    private void InputMonitor_MonitorError(object? sender, string message)
    {
        AppendLog(message);
        Dispatcher.BeginInvoke(() => IdleStatusText.Text = "键鼠监控不可用，自动亮度已暂停");
    }

    private void IdleBrightness_StatusChanged(object? sender, string message)
    {
        App.Current.Dispatcher.BeginInvoke(() =>
        {
            IdleStatusText.Text = message;
            AppendLog(message);
        });
    }

    private void IdleBrightness_ErrorOccurred(object? sender, string message)
    {
        App.Current.Dispatcher.BeginInvoke(() => AppendLog(message));
    }

    private void AppendLog(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => AppendLog(message));
            return;
        }

        var line = $"[{DateTime.Now:HH:mm:ss}] {message.TrimEnd()}";
        _logLines.Add(line);
        if (_logLines.Count > 4000)
        {
            _logLines.RemoveRange(0, _logLines.Count - 4000);
        }

        FullLogBox.Text = string.Join(Environment.NewLine, _logLines);
        FullLogBox.ScrollToEnd();
        RecentLogBox.Text = string.Join(
            Environment.NewLine,
            _logLines.TakeLast(18));
        RecentLogBox.ScrollToEnd();

        _ = _settings.AppendLogAsync(message);
    }

    private void SaveLog_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "日志文件 (*.log)|*.log|文本文件 (*.txt)|*.txt",
            FileName = $"scrcpy-control-{DateTime.Now:yyyyMMdd-HHmmss}.log"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, FullLogBox.Text);
            BottomStatusText.Text = "日志已保存";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "保存日志失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        _logLines.Clear();
        FullLogBox.Clear();
        RecentLogBox.Clear();
    }

    private void ExtraArguments_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoaded)
        {
            _appSettings.ExtraArguments = ExtraArgumentsBox.Text;
            UpdateCommandPreview();
        }
    }

    private void TrySetWindowIcon()
    {
        try
        {
            // Prefer the icon embedded in the assembly: a single-file publish
            // does not lay the ico next to the exe.
            Icon = new BitmapImage(
                new Uri("pack://application:,,,/Assets/app.ico", UriKind.Absolute));
            return;
        }
        catch
        {
            // Fall through to the loose file below.
        }

        try
        {
            if (!File.Exists(_paths.IconFile))
            {
                return;
            }

            Icon = new BitmapImage(new Uri(_paths.IconFile, UriKind.Absolute));
        }
        catch
        {
            // The icon is optional; the toolchain remains fully usable.
        }
    }
}
