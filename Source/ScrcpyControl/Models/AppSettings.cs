namespace ScrcpyControl.Models;

public sealed class AppSettings
{
    public bool AutoStayAwake { get; set; } = true;
    public bool IdleDimEnabled { get; set; } = true;
    public bool ClickToInputEnabled { get; set; } = true;
    public bool AutoSubmitEnabled { get; set; } = true;
    public int AutoSubmitDebounceMilliseconds { get; set; } = 100;
    public bool LongPressRightClickEnabled { get; set; } = true;
    public int LongPressRightClickMilliseconds { get; set; } = 450;
    public bool EnterSendsMessage { get; set; } = true;
    public string AppLevelPastePackages { get; set; } = "com.taobao.idlefish";
    public int IdleSeconds { get; set; } = 60;
    public int DimBrightnessPercent { get; set; } = 10;
    public string ExtraArguments { get; set; } = string.Empty;
    public string SelectedSerial { get; set; } = string.Empty;
    public string LastRemotePath { get; set; } = "/sdcard/";
    public string LastDownloadDirectory { get; set; } = string.Empty;
    public Dictionary<string, SavedOptionState> Options { get; set; } = new(StringComparer.Ordinal);
}

public sealed class PendingBrightnessState
{
    public string Serial { get; set; } = string.Empty;
    public int Brightness { get; set; }
    public int BrightnessMode { get; set; }
    public DateTimeOffset SavedAt { get; set; } = DateTimeOffset.Now;
}
