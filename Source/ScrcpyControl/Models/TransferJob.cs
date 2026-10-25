using ScrcpyControl.Infrastructure;

namespace ScrcpyControl.Models;

public enum TransferDirection
{
    Upload,
    Download
}

public sealed class TransferJob : ObservableObject
{
    private string _status = "等待中";
    private double _progress;
    private bool _isRunning;
    private bool _isCompleted;
    private bool _isFailed;

    public required TransferDirection Direction { get; init; }
    public required string LocalPath { get; init; }
    public required string RemotePath { get; init; }

    public string DisplayName => Path.GetFileName(LocalPath);
    public string DirectionLabel => Direction == TransferDirection.Upload ? "上传" : "下载";

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public double Progress
    {
        get => _progress;
        set => SetProperty(ref _progress, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        set => SetProperty(ref _isRunning, value);
    }

    public bool IsCompleted
    {
        get => _isCompleted;
        set => SetProperty(ref _isCompleted, value);
    }

    public bool IsFailed
    {
        get => _isFailed;
        set => SetProperty(ref _isFailed, value);
    }
}
