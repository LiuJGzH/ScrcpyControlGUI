namespace ScrcpyControl.Models;

public sealed class RemoteEntry
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public bool IsDirectory { get; init; }
    public bool IsSymlink { get; init; }
    public long Size { get; init; }
    public string Permissions { get; init; } = string.Empty;
    public string Owner { get; init; } = string.Empty;
    public string Group { get; init; } = string.Empty;
    public string Modified { get; init; } = string.Empty;

    public string KindLabel => IsDirectory ? "文件夹" : IsSymlink ? "链接" : "文件";
    public string SizeLabel => IsDirectory ? "—" : FormatSize(Size);

    private static string FormatSize(long size)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = size;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{size} B" : $"{value:0.##} {units[unit]}";
    }
}
