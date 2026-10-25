using System.Text.Json;
using ScrcpyControl.Models;

namespace ScrcpyControl.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly PathService _paths;

    public SettingsService(PathService paths)
    {
        _paths = paths;
        ConfigDirectory = ResolveConfigDirectory(paths.BaseDirectory);
        ProfilesDirectory = Path.Combine(ConfigDirectory, "profiles");
        Directory.CreateDirectory(ProfilesDirectory);
    }

    public string ConfigDirectory { get; }
    public string ProfilesDirectory { get; }
    public string SettingsFile => Path.Combine(ConfigDirectory, "settings.json");
    public string PendingBrightnessFile => Path.Combine(ConfigDirectory, "brightness-restore.json");
    public string LogFile => Path.Combine(ConfigDirectory, "ScrcpyControl.log");

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(SettingsFile))
        {
            return Normalize(new AppSettings());
        }

        try
        {
            await using var stream = File.OpenRead(SettingsFile);
            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
            return Normalize(settings ?? new AppSettings());
        }
        catch
        {
            return Normalize(new AppSettings());
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(ConfigDirectory);
        var temporaryFile = SettingsFile + ".tmp";
        await using (var stream = File.Create(temporaryFile))
        {
            await JsonSerializer.SerializeAsync(stream, Normalize(settings), JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }

        File.Move(temporaryFile, SettingsFile, overwrite: true);
    }

    public async Task SaveProfileAsync(
        string path,
        ScrcpyProfile profile,
        CancellationToken cancellationToken = default)
    {
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, profile, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ScrcpyProfile?> LoadProfileAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<ScrcpyProfile>(
            stream,
            JsonOptions,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task SavePendingBrightnessAsync(
        PendingBrightnessState state,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(ConfigDirectory);
        await using var stream = File.Create(PendingBrightnessFile);
        await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<PendingBrightnessState?> LoadPendingBrightnessAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(PendingBrightnessFile))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(PendingBrightnessFile);
            return await JsonSerializer.DeserializeAsync<PendingBrightnessState>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public void ClearPendingBrightness()
    {
        try
        {
            File.Delete(PendingBrightnessFile);
        }
        catch
        {
            // A stale restore file is harmless; it will be retried next start.
        }
    }

    public async Task AppendLogAsync(string text, CancellationToken cancellationToken = default)
    {
        try
        {
            Directory.CreateDirectory(ConfigDirectory);
            await File.AppendAllTextAsync(
                LogFile,
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}] {text}{Environment.NewLine}",
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Logging must never stop scrcpy control.
        }
    }

    private static AppSettings Normalize(AppSettings settings)
    {
        settings.IdleSeconds = Math.Clamp(settings.IdleSeconds, 1, 3600);
        settings.DimBrightnessPercent = Math.Clamp(settings.DimBrightnessPercent, 1, 100);
        settings.Options ??= new Dictionary<string, SavedOptionState>(StringComparer.Ordinal);
        settings.LastRemotePath = string.IsNullOrWhiteSpace(settings.LastRemotePath)
            ? "/sdcard/"
            : settings.LastRemotePath;
        return settings;
    }

    private static string ResolveConfigDirectory(string baseDirectory)
    {
        try
        {
            var candidate = Path.Combine(baseDirectory, "ScrcpyControlData");
            Directory.CreateDirectory(candidate);
            var probe = Path.Combine(candidate, ".write-test");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return candidate;
        }
        catch
        {
            var fallback = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ScrcpyControl");
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }
}
