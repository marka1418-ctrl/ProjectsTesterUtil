using System.Diagnostics;
using System.Reflection;
using ProjectTestingUtility.Services;

// No desktop capture: every live FFmpeg check uses a generated test pattern.
string[] syntheticInput = ["-re", "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=10"];
var executable = ScreenRecording.FindExecutable() ?? throw new InvalidOperationException("FFmpeg is required for these checks.");
if (args is ["--desktop-smoke", var desktopDirectory])
{
    await using var live = await ScreenRecording.StartAsync(desktopDirectory, 30);
    await Task.Delay(3500);
    await live.StopAsync();
    Require(live.Error == null, live.Error ?? "Live desktop capture failed.");
    Require(live.VideoPath != null, "Live desktop video is missing.");
    await DecodeVideoAsync(executable, live.VideoPath!);
    Console.WriteLine("PASS production desktop capture, image verification and decode: " + live.VideoPath);
    return;
}
if (args is ["--child", var childDirectory])
{
    await using var childRecording = await ScreenRecording.StartAsync(childDirectory, 60, executable, syntheticInput);
    Require(childRecording.Error == null, childRecording.Error ?? "Child recording failed.");
    // Observe the process identity to verify OS cleanup after the owner is forcibly terminated.
    var process = (Process)typeof(ScreenRecording).GetField("_process", BindingFlags.NonPublic | BindingFlags.Instance)!
        .GetValue(childRecording)!;
    await File.WriteAllTextAsync(Path.Combine(childDirectory, "ffmpeg.pid"), process.Id.ToString());
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return;
}

var checkRoot = Path.Combine(Path.GetTempPath(), "ProjectsTesterUtil-RecordingChecks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(checkRoot);
Console.WriteLine($"Synthetic recording artifacts: {checkRoot}");

var virtualDesktop = ScreenRecording.GetVirtualDesktop();
Require(virtualDesktop.Width > 0 && virtualDesktop.Height > 0, "Virtual desktop metrics are invalid.");
var multiMonitorInput = ScreenRecording.DesktopInput(new ScreenRecording.DesktopCaptureRegion(-1920, -240, 5760, 2160));
Require(multiMonitorInput.Contains("-1920") && multiMonitorInput.Contains("-240") && multiMonitorInput.Contains("5760x2160"),
    "Virtual desktop input lost negative monitor coordinates or capture dimensions.");
Console.WriteLine($"PASS virtual desktop capture bounds include secondary displays ({virtualDesktop.Width}x{virtualDesktop.Height})");

await using (var missing = await ScreenRecording.StartAsync(Path.Combine(checkRoot, "missing"), 10, null, syntheticInput))
{
    Require(missing.Error?.Contains("not found") == true, "Missing FFmpeg should report an error.");
    Require(missing.VideoPath == null, "Missing FFmpeg must not claim a video exists.");
}
Console.WriteLine("PASS missing executable is non-fatal");

var originalPath = Environment.GetEnvironmentVariable("PATH");
try
{
    Environment.SetEnvironmentVariable("PATH", "Z:\\<invalid>|entry;\"?:\\bad*path\"");
    // Abort before the public desktop API if a bundled recorder is unexpectedly present.
    Require(ScreenRecording.FindExecutable() == null, "Malformed PATH check requires no recorder beside the harness.");
    await using var invalidPath = await ScreenRecording.StartAsync(Path.Combine(checkRoot, "invalid PATH"), 10);
    Require(invalidPath.Error != null && invalidPath.VideoPath == null, "Malformed PATH must remain a non-fatal recording error.");
}
finally
{
    Environment.SetEnvironmentVariable("PATH", originalPath);
}
Console.WriteLine("PASS malformed PATH entries cannot prevent the test runner from continuing");

await using (var invalid = await ScreenRecording.StartAsync(Path.Combine(checkRoot, "invalid"), 10,
    executable, ["-f", "not-a-real-input", "-i", "unused"]))
{
    Require(invalid.Error != null, "Rejected FFmpeg input should report an error.");
    Require(invalid.VideoPath == null, "Rejected input must not claim a video exists.");
    Require(new FileInfo(Path.Combine(checkRoot, "invalid", "ffmpeg.log")).Length > 0, "FFmpeg error log is missing.");
}
Console.WriteLine("PASS rejected input is non-fatal with an error log");

await using (var recording = await ScreenRecording.StartAsync(Path.Combine(checkRoot, "graceful stop"), 30, executable, syntheticInput))
{
    Require(recording.Error == null, recording.Error ?? "Unexpected startup error.");
    await Task.Delay(750);
    await Task.WhenAll(recording.StopAsync(), recording.StopAsync());
    Require(recording.Error == null, recording.Error ?? "Unexpected shutdown error.");
    Require(recording.VideoPath != null && new FileInfo(recording.VideoPath).Length > 0, "Video was not saved.");
    await DecodeVideoAsync(executable, recording.VideoPath!);
}
Console.WriteLine("PASS concurrent idempotent stop finalizes a decodable video");

await using (var limited = await ScreenRecording.StartAsync(Path.Combine(checkRoot, "duration limit"), 2, executable, syntheticInput))
{
    await Task.Delay(2800);
    await limited.StopAsync();
    Require(limited.Error?.Contains("limit") == true, "A duration cutoff must be reported.");
    Require(limited.VideoPath != null, "A duration cutoff should retain the video.");
    await DecodeVideoAsync(executable, limited.VideoPath!);
}
Console.WriteLine("PASS automatic duration cutoff retains a decodable video and reports truncation");

// Reproduce the incident: valid H.264 frames with only a small white cursor.
await using (var black = await ScreenRecording.StartAsync(Path.Combine(checkRoot, "black with cursor"), 30, executable,
    ["-re", "-f", "lavfi", "-i", "color=c=black:s=1920x1080:r=10,drawbox=x=960:y=540:w=15:h=25:color=white:t=fill"]))
{
    await Task.Delay(4200);
    Require(black.Error?.Contains("black image") == true, "Black capture must be reported while recording is still running.");
    await black.StopAsync();
    Require(black.VideoPath != null, "A suspect video must remain available as diagnostic evidence.");
    await DecodeVideoAsync(executable, black.VideoPath!);
    using var health = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(checkRoot, "black with cursor", "capture-health.json")));
    Require(!health.RootElement.GetProperty("Health").GetProperty("HasVisibleFrames").GetBoolean(), "A mouse cursor must not pass image verification.");
}
Console.WriteLine("PASS black desktop with cursor is detected during recording and retained");

await using (var lost = await ScreenRecording.StartAsync(Path.Combine(checkRoot, "capture lost then recovered"), 30, executable,
    ["-re", "-f", "lavfi", "-i", "testsrc2=s=320x240:r=10,drawbox=x=0:y=0:w=iw:h=ih:color=black:t=fill:enable='between(t,2,6)'"]))
{
    await Task.Delay(8000);
    await lost.StopAsync();
    Require(lost.Error?.Contains("black image") == true, "A later capture failure must not be hidden by healthy startup/recovery.");
    using var health = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(checkRoot, "capture lost then recovered", "capture-health.json")));
    var state = health.RootElement.GetProperty("Health");
    Require(state.GetProperty("HasVisibleFrames").GetBoolean() && !state.GetProperty("CurrentFrameBlack").GetBoolean(), "Expected recovery after the black segment.");
    Require(state.GetProperty("Intervals").GetArrayLength() >= 3, "Capture history must retain the loss and recovery.");
}
Console.WriteLine("PASS loss and recovery of the image are retained in capture history");

string[] blackInput = ["-re", "-f", "lavfi", "-i", "color=c=black:s=320x240:r=10"];
var fallbackDirectory = Path.Combine(checkRoot, "verified fallback");
await using (var fallback = await ScreenRecording.StartVerifiedAsync(fallbackDirectory, 30, executable,
    blackInput, syntheticInput, fallbackAllowed: true))
{
    Require(fallback.Error == null, fallback.Error ?? "A verified fallback should be usable.");
    Require(File.Exists(Path.Combine(fallbackDirectory, "desktop.mkv")), "The failed primary recording must be preserved.");
    await fallback.StopAsync();
    Require(fallback.VideoPath?.Contains("capture-ddagrab") == true, "The selected video must point to the working fallback.");
    await DecodeVideoAsync(executable, fallback.VideoPath!);
}
Console.WriteLine("PASS black primary switches to verified fallback without overwriting evidence");

var blockedFallbackDirectory = Path.Combine(checkRoot, "multi display fallback blocked");
await using (var unavailable = await ScreenRecording.StartVerifiedAsync(blockedFallbackDirectory, 30, executable,
    blackInput, syntheticInput, fallbackAllowed: false))
{
    Require(unavailable.Error != null, "Unverified capture must carry an error.");
    Require(!Directory.Exists(Path.Combine(blockedFallbackDirectory, "capture-ddagrab")), "Fallback must not silently capture the wrong monitor.");
}
Console.WriteLine("PASS unavailable fallback is explicit and cannot lose secondary displays");

var failedFallbackDirectory = Path.Combine(checkRoot, "both capture methods black");
await using (var unavailable = await ScreenRecording.StartVerifiedAsync(failedFallbackDirectory, 30, executable,
    blackInput, blackInput, fallbackAllowed: true))
{
    Require(unavailable.Error?.Contains("Neither GDI nor DXGI") == true, "Both black methods must report unverified capture.");
    var primaryProcess = (Process)typeof(ScreenRecording).GetField("_process", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(unavailable)!;
    Require(!primaryProcess.HasExited, "Keep the original recorder running for later recovery.");
    await unavailable.StopAsync();
    Require(unavailable.VideoPath == Path.Combine(failedFallbackDirectory, "desktop.mkv"), "Keep the original recorder available for later recovery.");
    Require(File.Exists(Path.Combine(failedFallbackDirectory, "capture-ddagrab", "desktop.mkv")), "Fallback evidence was lost.");
}
Console.WriteLine("PASS two black capture methods retain evidence and report failure");

if (OperatingSystem.IsWindows())
{
    var crashDirectory = Path.Combine(checkRoot, "owner crash");
    Directory.CreateDirectory(crashDirectory);
    var childStart = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
    childStart.ArgumentList.Add("--child");
    childStart.ArgumentList.Add(crashDirectory);
    using var child = Process.Start(childStart)!;
    try
    {
        var pidFile = Path.Combine(crashDirectory, "ffmpeg.pid");
        var deadline = Stopwatch.StartNew();
        while (!File.Exists(pidFile) && !child.HasExited && deadline.Elapsed < TimeSpan.FromSeconds(15))
            await Task.Delay(100);
        Require(File.Exists(pidFile), "Child recorder did not start.");
        using var ffmpeg = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(pidFile)));
        child.Kill(); // Deliberately do not kill the process tree: the job must do that.
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await ffmpeg.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Require(ffmpeg.HasExited, "FFmpeg survived its owner's forced termination.");
    }
    finally
    {
        if (!child.HasExited)
            child.Kill(entireProcessTree: true);
    }
    Console.WriteLine("PASS forced owner termination kills only its FFmpeg child through a Windows job");
}

Console.WriteLine("All screen recording checks passed. No desktop was captured.");

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static async Task DecodeVideoAsync(string executable, string path)
{
    var info = new ProcessStartInfo(executable)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardError = true,
        RedirectStandardOutput = true
    };
    foreach (var argument in new[] { "-hide_banner", "-v", "error", "-i", path, "-f", "null", "-" })
        info.ArgumentList.Add(argument);
    using var process = Process.Start(info)!;
    var stderr = process.StandardError.ReadToEndAsync();
    var stdout = process.StandardOutput.ReadToEndAsync();
    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    await stdout;
    Require(process.ExitCode == 0, $"Video cannot be decoded: {await stderr}");
}
