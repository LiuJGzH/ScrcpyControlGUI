namespace ScrcpyControl.Services;

public sealed class PathService
{
    public PathService(string? baseDirectory = null)
    {
        BaseDirectory = Path.GetFullPath(baseDirectory ?? AppContext.BaseDirectory);
    }

    public string BaseDirectory { get; }
    public string ScrcpyExe => Path.Combine(BaseDirectory, "scrcpy.exe");
    public string AdbExe => Path.Combine(BaseDirectory, "adb.exe");
    public string ServerJar => Path.Combine(BaseDirectory, "scrcpy-server");
    public string IconFile => Path.Combine(BaseDirectory, "scrcpy.png");

    public IReadOnlyList<string> GetMissingFiles()
    {
        var missing = new List<string>();
        foreach (var path in new[] { ScrcpyExe, AdbExe, ServerJar })
        {
            if (!File.Exists(path))
            {
                missing.Add(Path.GetFileName(path));
            }
        }

        return missing;
    }
}
