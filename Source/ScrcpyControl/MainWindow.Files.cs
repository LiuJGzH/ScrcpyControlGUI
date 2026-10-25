using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using ScrcpyControl.Models;
using ScrcpyControl.Services;
using Forms = System.Windows.Forms;

namespace ScrcpyControl;

public partial class MainWindow
{
    private async Task LoadRemoteDirectoryAsync(string remotePath)
    {
        if (_selectedDevice?.IsUsable != true)
        {
            MessageBox.Show(
                "请先连接手机并允许 USB 调试。",
                "文件管理",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        try
        {
            BottomStatusText.Text = "正在读取手机目录…";
            var normalized = AdbService.NormalizeDirectory(remotePath);
            var entries = await _adb.ListDirectoryAsync(
                _selectedDevice.Serial,
                normalized,
                _appCancellation.Token);

            _fileEntries.Clear();
            foreach (var entry in entries)
            {
                _fileEntries.Add(entry);
            }

            _currentRemotePath = normalized;
            RemotePathBox.Text = normalized;
            _appSettings.LastRemotePath = normalized;
            TransferStatusText.Text = "  空闲";
            BottomStatusText.Text = $"{normalized} · {entries.Count} 项";
        }
        catch (Exception ex)
        {
            TransferStatusText.Text = "  空闲";
            BottomStatusText.Text = "手机目录读取失败";
            AppendLog($"读取手机目录失败：{ex.Message}");
            MessageBox.Show(
                ex.Message,
                "无法读取手机目录",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async void RefreshFiles_Click(object sender, RoutedEventArgs e)
    {
        await LoadRemoteDirectoryAsync(_currentRemotePath);
    }

    private async void BrowseRemoteUp_Click(object sender, RoutedEventArgs e)
    {
        await LoadRemoteDirectoryAsync(AdbService.ParentDirectory(_currentRemotePath));
    }

    private async void GoSdcard_Click(object sender, RoutedEventArgs e)
    {
        await LoadRemoteDirectoryAsync("/sdcard/");
    }

    private async void RemotePathBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            await LoadRemoteDirectoryAsync(RemotePathBox.Text);
            e.Handled = true;
        }
    }

    private async void FileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FileListView.SelectedItem is not RemoteEntry entry)
        {
            return;
        }

        if (entry.IsDirectory || entry.IsSymlink)
        {
            await LoadRemoteDirectoryAsync(entry.FullPath);
        }
    }

    private async void FileList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.C &&
            (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            e.Handled = true;
            await CopySelectedFileEntriesAsync(fullPaths: false);
        }
    }

    private async void CopyFileNames_Click(object sender, RoutedEventArgs e)
    {
        await CopySelectedFileEntriesAsync(fullPaths: false);
    }

    private async void CopyFilePaths_Click(object sender, RoutedEventArgs e)
    {
        await CopySelectedFileEntriesAsync(fullPaths: true);
    }

    private async Task CopySelectedFileEntriesAsync(bool fullPaths)
    {
        var entries = FileListView.SelectedItems
            .OfType<RemoteEntry>()
            .ToList();
        if (entries.Count == 0)
        {
            BottomStatusText.Text = "请先选择要复制的文件或文件夹";
            return;
        }

        var text = string.Join(
            Environment.NewLine,
            entries.Select(entry => fullPaths ? entry.FullPath : entry.Name));
        try
        {
            await SetPersistentClipboardTextWithRetryAsync(text);
            AppendLog($"已复制 {entries.Count} 个文件名称或路径");
            BottomStatusText.Text = $"已复制 {entries.Count} 项";
        }
        catch (Exception ex)
        {
            AppendLog($"复制文件信息失败：{ex.Message}");
            BottomStatusText.Text = "复制文件信息失败";
        }
    }

    private async void TransferList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.C &&
            (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            e.Handled = true;
            await CopySelectedTransferDetailsAsync();
        }
    }

    private async void CopyTransferDetails_Click(object sender, RoutedEventArgs e)
    {
        await CopySelectedTransferDetailsAsync();
    }

    private async Task CopySelectedTransferDetailsAsync()
    {
        var jobs = TransferListView.SelectedItems
            .OfType<TransferJob>()
            .ToList();
        if (jobs.Count == 0)
        {
            BottomStatusText.Text = "请先选择传输任务";
            return;
        }

        var text = string.Join(
            Environment.NewLine,
            jobs.Select(job =>
                $"{job.DirectionLabel}\t{job.DisplayName}\t{job.Status}\t{job.RemotePath}"));
        try
        {
            await SetPersistentClipboardTextWithRetryAsync(text);
            AppendLog($"已复制 {jobs.Count} 条传输任务信息");
            BottomStatusText.Text = $"已复制 {jobs.Count} 条传输信息";
        }
        catch (Exception ex)
        {
            AppendLog($"复制传输信息失败：{ex.Message}");
            BottomStatusText.Text = "复制传输信息失败";
        }
    }

    private async void UploadFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择要上传到手机的文件",
            Multiselect = true,
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true)
        {
            await UploadPathsAsync(dialog.FileNames);
        }
    }

    private async Task UploadPathsAsync(IEnumerable<string> localPaths)
    {
        if (_selectedDevice?.IsUsable != true)
        {
            return;
        }

        var paths = localPaths.Where(File.Exists).ToArray();
        if (paths.Length == 0)
        {
            return;
        }

        var jobs = paths.Select(path => new TransferJob
        {
            Direction = TransferDirection.Upload,
            LocalPath = path,
            RemotePath = AdbService.CombineRemote(_currentRemotePath, Path.GetFileName(path)),
            Status = "等待上传"
        }).ToList();
        await RunTransferQueueAsync(jobs);
    }

    private async void DownloadSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDevice?.IsUsable != true)
        {
            return;
        }

        var selectedEntries = FileListView.SelectedItems
            .OfType<RemoteEntry>()
            .ToArray();
        if (selectedEntries.Length == 0)
        {
            MessageBox.Show(
                "请选择至少一个手机文件或目录。",
                "下载文件",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var selected = selectedEntries
            .Where(entry => !entry.IsDirectory && !entry.IsSymlink)
            .ToArray();
        if (selected.Length == 0)
        {
            MessageBox.Show(
                "请进入文件夹后选择具体文件下载。第一版不支持直接下载整个文件夹。",
                "下载文件",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "选择电脑上的下载目录",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            SelectedPath = Directory.Exists(_appSettings.LastDownloadDirectory)
                ? _appSettings.LastDownloadDirectory
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK)
        {
            return;
        }

        _appSettings.LastDownloadDirectory = dialog.SelectedPath;
        var jobs = selected.Select(entry => new TransferJob
        {
            Direction = TransferDirection.Download,
            LocalPath = Path.Combine(dialog.SelectedPath, entry.Name),
            RemotePath = entry.FullPath,
            Status = "等待下载"
        }).ToList();
        await RunTransferQueueAsync(jobs);
    }

    private async Task RunTransferQueueAsync(IReadOnlyList<TransferJob> jobs)
    {
        if (jobs.Count == 0 || _selectedDevice is null)
        {
            return;
        }

        _transferCancellation?.Cancel();
        _transferCancellation?.Dispose();
        _transferCancellation = new CancellationTokenSource();
        var token = _transferCancellation.Token;

        foreach (var job in jobs)
        {
            _transferJobs.Add(job);
        }

        CancelTransferButton.IsEnabled = true;
        var completed = 0;

        try
        {
            foreach (var job in jobs)
            {
                token.ThrowIfCancellationRequested();
                job.IsRunning = true;
                job.Status = job.Direction == TransferDirection.Upload ? "正在上传" : "正在下载";
                TransferStatusText.Text = $"  {job.DirectionLabel} {job.DisplayName}";

                try
                {
                    if (job.Direction == TransferDirection.Upload)
                    {
                        await _adb.PushAsync(
                            _selectedDevice.Serial,
                            job.LocalPath,
                            _currentRemotePath,
                            progress => UpdateTransferProgress(job, progress),
                            token);
                    }
                    else
                    {
                        await _adb.PullAsync(
                            _selectedDevice.Serial,
                            job.RemotePath,
                            Path.GetDirectoryName(job.LocalPath) ?? _appSettings.LastDownloadDirectory,
                            progress => UpdateTransferProgress(job, progress),
                            token);
                    }

                    job.Progress = 100;
                    job.IsCompleted = true;
                    job.Status = "完成";
                    completed++;
                }
                catch (OperationCanceledException)
                {
                    job.IsFailed = true;
                    job.Status = "已取消";
                    throw;
                }
                catch (Exception ex)
                {
                    job.IsFailed = true;
                    job.Status = $"失败：{ex.Message}";
                    AppendLog($"{job.DirectionLabel}失败：{ex.Message}");
                }
                finally
                {
                    job.IsRunning = false;
                }
            }
        }
        catch (OperationCanceledException)
        {
            TransferStatusText.Text = "  传输已取消";
        }
        finally
        {
            CancelTransferButton.IsEnabled = false;
            TransferProgressBar.Value = 0;
            if (completed > 0)
            {
                TransferStatusText.Text = $"  已完成 {completed}/{jobs.Count} 项";
                if (jobs.Any(job => job.Direction == TransferDirection.Upload))
                {
                    await LoadRemoteDirectoryAsync(_currentRemotePath);
                }
            }
        }
    }

    private void UpdateTransferProgress(TransferJob job, double progress)
    {
        Dispatcher.BeginInvoke(() =>
        {
            job.Progress = progress;
            job.Status = $"{job.DirectionLabel} {progress:0}%";
            TransferProgressBar.Value = progress;
        });
    }

    private void CancelTransfer_Click(object sender, RoutedEventArgs e)
    {
        _transferCancellation?.Cancel();
    }

    private void FileList_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void FileList_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths)
        {
            return;
        }

        await UploadPathsAsync(paths);
    }
}
