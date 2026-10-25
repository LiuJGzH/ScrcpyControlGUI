using ScrcpyControl.Models;
using ScrcpyControl.Services;
using ScrcpyControl.Localization;

var toolDirectory = Environment.GetEnvironmentVariable("SCRCPY_DIR");
if (string.IsNullOrWhiteSpace(toolDirectory))
{
    Console.WriteLine("SKIP  set SCRCPY_DIR to a scrcpy 5.0.1 Windows x64 directory");
    return 2;
}
var paths = new PathService(toolDirectory!);
var adb = new AdbService(paths);
var failures = new List<string>();

void Check(bool condition, string message)
{
    Console.WriteLine($"{(condition ? "PASS" : "FAIL")}  {message}");
    if (!condition)
    {
        failures.Add(message);
    }
}

var helpResult = await ProcessRunner.RunAsync(
    paths.ScrcpyExe,
    ["--help"],
    paths.BaseDirectory);
var options = ScrcpyOptionCatalog.ParseHelp(helpResult.CombinedOutput);
Check(options.Count == 109, $"scrcpy help parsed 109 options (actual {options.Count})");
Check(
    options.Count(option => option.ActionOnly) == 7,
    "7 action-only options identified");
var missingTranslations = BilingualScrcpyText.FindMissingTranslations(options);
Check(
    missingTranslations.Count == 0,
    $"all options have Chinese translations (missing: {string.Join(", ", missingTranslations)})");
var forceAdbForward = options.First(option => option.CliName == "--force-adb-forward");
Check(
    !forceAdbForward.Description.Contains("--gamepad=uhid", StringComparison.Ordinal),
    "short-alias-only help entries do not bleed into the previous option description");
Check(
    new AppSettings().AutoSubmitEnabled,
    "automatic submit is enabled by default");
Check(
    new AppSettings().AutoSubmitDebounceMilliseconds == 100,
    "automatic submit debounce defaults to 100 ms");
Check(
    AdbService.ContainsActiveTextInput(
        """
        mServedView=com.tencent.mm.ui.widget.MMEditText{6595375}
        mCurrentEditorInfo:
          inputType=0x224001 imeOptions=0x40000006
          packageName=com.tencent.mm autofillId=1073741826
        mServedInputConnection=RemoteInputConnectionImpl{connection=test}
        """),
    "indented Android 17 editor info is recognized");
Check(
    AdbService.ContainsActiveTextInput(
        "mCurrentEditorInfo=android.view.inputmethod.EditorInfo@abc\n"
        + "mServedInputConnection=RemoteInputConnectionImpl{connection=test}"),
    "inline editor info is recognized");
Check(
    !AdbService.ContainsActiveTextInput(
        "mCurrentEditorInfo=null\nmServedInputConnection=null"),
    "null editor info is rejected");

var devices = await adb.GetDevicesAsync();
var device = devices.FirstOrDefault(item => item.IsUsable);
if (device is null)
{
    Console.WriteLine("SKIP  no authorized ADB device is connected; device tests skipped");
}
else
{
    Console.WriteLine("PASS  one authorized ADB device is available");
}

if (device is not null)
{
    var textInputFocused = await adb.IsTextInputFocusedAsync(device.Serial);
    Console.WriteLine($"INFO  current Android text input focus detected: {textInputFocused}");

    var rootEntries = await adb.ListDirectoryAsync(device.Serial, "/sdcard/");
    Check(rootEntries.Count > 0, "remote /sdcard/ contains entries");
    Check(
        rootEntries.All(entry =>
            !string.IsNullOrWhiteSpace(entry.Name) &&
            !string.IsNullOrWhiteSpace(entry.FullPath)),
        "remote listing entries have names and full paths");

    var testDirectory = Path.Combine(Path.GetTempPath(), "scrcpy-control-integration");
    Directory.CreateDirectory(testDirectory);
    var fileName = "Scrcpy 控制台测试.txt";
    var payload = $"upload-download-{Guid.NewGuid():N}";
    var localSource = Path.Combine(testDirectory, fileName);
    await File.WriteAllTextAsync(localSource, payload);

    var remoteDirectory = "/sdcard/Download/";
    await adb.PushAsync(
        device.Serial,
        localSource,
        remoteDirectory,
        _ => { });

    var downloadEntries = await adb.ListDirectoryAsync(device.Serial, remoteDirectory);
    Check(
        downloadEntries.Any(entry => entry.Name == fileName),
        "uploaded Chinese/space filename appears on the device");

    var pullDirectory = Path.Combine(testDirectory, "pulled");
    Directory.CreateDirectory(pullDirectory);
    await adb.PullAsync(
        device.Serial,
        remoteDirectory + fileName,
        pullDirectory,
        _ => { });

    var downloadedFile = Path.Combine(pullDirectory, fileName);
    Check(File.Exists(downloadedFile), "download produced the local file");
    Check(
        File.Exists(downloadedFile) &&
        await File.ReadAllTextAsync(downloadedFile) == payload,
        "uploaded and downloaded file contents match");

    await adb.RunShellAsync(
        device.Serial,
        $"rm -f {AdbService.QuotePosix(remoteDirectory + fileName)}");
}

Console.WriteLine();
Console.WriteLine(failures.Count == 0
    ? "ALL TESTS PASSED"
    : $"FAILED: {failures.Count}");
return failures.Count == 0 ? 0 : 1;
