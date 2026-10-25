using ScrcpyControl.Models;

namespace ScrcpyControl.Services;

public sealed class IdleBrightnessService : IAsyncDisposable
{
    private readonly AdbService _adb;
    private readonly SettingsService _settings;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _loopCancellation;
    private Task? _loopTask;
    private string _serial = string.Empty;
    private bool _enabled;
    private int _idleSeconds = 60;
    private int _dimBrightnessPercent = 10;
    private bool _screenOn = true;
    private bool _dimmed;
    private DateTimeOffset _lastActivity = DateTimeOffset.Now;
    private PendingBrightnessState? _savedBrightness;

    public IdleBrightnessService(AdbService adb, SettingsService settings)
    {
        _adb = adb;
        _settings = settings;
    }

    public event EventHandler<string>? StatusChanged;
    public event EventHandler<string>? ErrorOccurred;

    public bool IsDimmed => _dimmed;

    public void Start(
        string serial,
        bool enabled,
        int idleSeconds,
        int dimBrightnessPercent,
        bool screenOn)
    {
        StopLoop();
        _serial = serial;
        _enabled = enabled;
        _idleSeconds = Math.Clamp(idleSeconds, 1, 3600);
        _dimBrightnessPercent = Math.Clamp(dimBrightnessPercent, 1, 100);
        _screenOn = screenOn;
        _dimmed = false;
        _lastActivity = DateTimeOffset.Now;

        if (!_enabled)
        {
            StatusChanged?.Invoke(this, "自动降低亮度已关闭");
            return;
        }

        _loopCancellation = new CancellationTokenSource();
        _loopTask = RunLoopAsync(_loopCancellation.Token);
        StatusChanged?.Invoke(this, $"键鼠空闲 {_idleSeconds} 秒后降低手机亮度");
    }

    public void Stop()
    {
        StopLoop();
        _serial = string.Empty;
        _screenOn = true;
    }

    public void SetScreenOn(bool screenOn)
    {
        _screenOn = screenOn;
        if (screenOn)
        {
            _lastActivity = DateTimeOffset.Now;
        }
    }

    public async Task NotifyActivityAsync()
    {
        _lastActivity = DateTimeOffset.Now;
        if (_dimmed)
        {
            await RestoreAsync().ConfigureAwait(false);
        }
    }

    public async Task RestoreAsync()
    {
        if (!_dimmed && _savedBrightness is null)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var state = _savedBrightness;
            if (state is null || string.IsNullOrWhiteSpace(state.Serial))
            {
                return;
            }

            try
            {
                await _adb.SetBrightnessAsync(
                    state.Serial,
                    state.Brightness,
                    state.BrightnessMode).ConfigureAwait(false);
                _settings.ClearPendingBrightness();
                _savedBrightness = null;
                _dimmed = false;
                StatusChanged?.Invoke(this, "手机亮度已恢复");
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"恢复亮度失败：{ex.Message}");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RecoverPendingAsync()
    {
        var pending = await _settings.LoadPendingBrightnessAsync().ConfigureAwait(false);
        if (pending is null)
        {
            return;
        }

        try
        {
            await _adb.SetBrightnessAsync(
                pending.Serial,
                pending.Brightness,
                pending.BrightnessMode).ConfigureAwait(false);
            _settings.ClearPendingBrightness();
            StatusChanged?.Invoke(this, "已恢复上次异常退出前的手机亮度");
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, $"待恢复亮度尚未处理：{ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await RestoreAsync().ConfigureAwait(false);
        Stop();
        _gate.Dispose();
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (_dimmed ||
                    !_enabled ||
                    !_screenOn ||
                    string.IsNullOrWhiteSpace(_serial))
                {
                    continue;
                }

                if ((DateTimeOffset.Now - _lastActivity).TotalSeconds < _idleSeconds)
                {
                    continue;
                }

                await DimAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when scrcpy stops or the application exits.
        }
    }

    private async Task DimAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_dimmed || !_screenOn)
            {
                return;
            }

            var current = await _adb.GetBrightnessAsync(_serial, cancellationToken).ConfigureAwait(false);
            var saved = new PendingBrightnessState
            {
                Serial = _serial,
                Brightness = current.Brightness,
                BrightnessMode = current.Mode,
                SavedAt = DateTimeOffset.Now
            };
            await _settings.SavePendingBrightnessAsync(saved, cancellationToken).ConfigureAwait(false);

            var target = Math.Clamp(
                (int)Math.Round(255d * _dimBrightnessPercent / 100d),
                1,
                255);

            await _adb.SetBrightnessAsync(_serial, target, 0, cancellationToken).ConfigureAwait(false);
            _savedBrightness = saved;
            _dimmed = true;
            StatusChanged?.Invoke(
                this,
                $"已空闲 {_idleSeconds} 秒，手机亮度降至 {_dimBrightnessPercent}%");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, $"降低亮度失败：{ex.Message}");
            _lastActivity = DateTimeOffset.Now;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void StopLoop()
    {
        _loopCancellation?.Cancel();
        _loopCancellation?.Dispose();
        _loopCancellation = null;
        _loopTask = null;
    }
}
