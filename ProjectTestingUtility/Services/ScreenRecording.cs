using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ProjectTestingUtility.Services;

/// <summary>Best-effort desktop recording. A capture failure must not fail a project test.</summary>
public sealed class ScreenRecording : IAsyncDisposable
{
    private const int MaximumLogCharacters = 1024 * 1024;
    private readonly SemaphoreSlim _stopLock = new(1, 1);
    private readonly TaskCompletionSource _firstFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Stopwatch _elapsed = new();
    private readonly CaptureFrameHealth _health = new();
    private readonly CancellationTokenSource _healthCancellation = new();
    private readonly object _errorGate = new();
    private Task _healthTask = Task.CompletedTask;
    private string? _directory;
    private string _backend = "explicit-input";
    private bool _captureEnvironment;
    private bool _blackReported;
    private bool _missingTelemetryReported;
    private Process? _process;
    private SafeFileHandle? _job;
    private Task _exitTask = Task.CompletedTask;
    private Task _stderrTask = Task.CompletedTask;
    private Task _stdoutTask = Task.CompletedTask;
    private string? _outputPath;
    private int _maximumSeconds;
    private volatile bool _stopRequested;
    private volatile bool _exitedBeforeStop;
    private volatile bool _hasFrames;
    private bool _stopped;

    private ScreenRecording() { }

    public string? VideoPath { get; private set; }
    public string? Error { get; private set; }

    public static async Task<ScreenRecording> StartAsync(string outputDirectory, int maximumSeconds)
    {
        try
        {
            var capture = GetVirtualDesktop();
            Directory.CreateDirectory(outputDirectory);
            try { File.WriteAllText(Path.Combine(outputDirectory, "capture-region.json"), System.Text.Json.JsonSerializer.Serialize(capture)); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            var environment = DesktopCaptureEnvironment.Capture();
            WriteJson(outputDirectory, "capture-environment-start.json", environment);
            var executable = FindExecutable();
            return await StartVerifiedAsync(outputDirectory, maximumSeconds, executable, DesktopInput(capture),
                DesktopDuplicationInput(), environment.DisplayCount == 1, captureEnvironment: true);
        }
        catch (Exception exception)
        {
            return new ScreenRecording { Error = $"Screen recording could not start: {exception.Message}" };
        }
    }

    // Input injection exercises the production fallback path without capturing a user's desktop.
    internal static async Task<ScreenRecording> StartVerifiedAsync(string outputDirectory, int maximumSeconds,
        string? executable, IReadOnlyList<string> primaryInput, IReadOnlyList<string> fallbackInput,
        bool fallbackAllowed, bool captureEnvironment = false)
    {
        ScreenRecording? primary = null;
        ScreenRecording? fallback = null;
        try
        {
            primary = await StartCoreAsync(outputDirectory, maximumSeconds, executable, primaryInput, "gdigrab", captureEnvironment);
            if (await HasVisibleCaptureAsync(primary))
            {
                WriteJson(outputDirectory, "capture-selection.json", new { Backend = "gdigrab", VerifiedVisibleFrame = true });
                return primary;
            }

            // DXGI captures one output, unlike gdigrab's entire virtual desktop.
            // Never silently lose secondary monitors by selecting output zero.
            if (executable == null || !fallbackAllowed)
            {
                primary.AddError("Desktop capture could not be verified. DXGI fallback requires exactly one active display; see capture-health.json and capture-environment-start.json.");
                WriteJson(outputDirectory, "capture-selection.json", new { Backend = "gdigrab", VerifiedVisibleFrame = false, Fallback = "Unavailable: no recorder or display count is not one" });
                return primary;
            }

            // Keep the original recorder alive until the fallback is verified. If both
            // fail, GDI may recover when the interactive desktop becomes available again.
            fallback = await StartCoreAsync(Path.Combine(outputDirectory, "capture-ddagrab"), maximumSeconds,
                executable, fallbackInput, "ddagrab", captureEnvironment);
            if (await HasVisibleCaptureAsync(fallback))
            {
                await primary.StopAsync();
                WriteJson(outputDirectory, "capture-selection.json", new { Backend = "ddagrab", VerifiedVisibleFrame = true, Fallback = "GDI did not provide a visible image", OriginalVideo = primary.VideoPath });
                return fallback;
            }
            await fallback.StopAsync();
            primary.AddError("Neither GDI nor DXGI provided a verified desktop image. The recording is retained for diagnosis; check the active desktop, display and Windows session on the test machine.");
            WriteJson(outputDirectory, "capture-selection.json", new { Backend = "gdigrab", VerifiedVisibleFrame = false, FallbackError = fallback.Error });
            return primary;
        }
        catch (Exception exception)
        {
            if (fallback != null) await fallback.StopAsync();
            if (primary != null)
            {
                await primary.StopAsync();
                primary.AddError($"Desktop capture selection failed: {exception.Message}");
                return primary;
            }
            // Executable discovery is also best-effort: a malformed PATH entry must
            // not prevent the project test from launching CAM.
            return new ScreenRecording { Error = $"Screen recording could not start: {exception.Message}" };
        }
    }

    internal static string[] DesktopInput(DesktopCaptureRegion capture) =>
    [
        "-thread_queue_size", "8", "-f", "gdigrab", "-framerate", "10", "-draw_mouse", "1",
        "-offset_x", capture.X.ToString(CultureInfo.InvariantCulture),
        "-offset_y", capture.Y.ToString(CultureInfo.InvariantCulture),
        "-video_size", $"{capture.Width}x{capture.Height}", "-i", "desktop"
    ];

    internal static string[] DesktopDuplicationInput() =>
    [
        "-f", "lavfi", "-i", "ddagrab=output_idx=0:framerate=10:draw_mouse=1,hwdownload,format=bgra"
    ];

    private static async Task<bool> HasVisibleCaptureAsync(ScreenRecording recording)
    {
        if (recording._stopped || recording._process == null) return false;
        await Task.WhenAny(recording._health.QualityAvailable, recording._exitTask, Task.Delay(TimeSpan.FromSeconds(5)));
        return !recording._exitTask.IsCompleted && recording._health.HasVisibleFrames;
    }

    internal static DesktopCaptureRegion GetVirtualDesktop()
    {
        // Query physical pixels even if the tester runs on a scaled display.
        var previous = SetThreadDpiAwarenessContext(new IntPtr(-4)); // PER_MONITOR_AWARE_V2
        try
        {
            var capture = new DesktopCaptureRegion(GetSystemMetrics(76), GetSystemMetrics(77),
                GetSystemMetrics(78), GetSystemMetrics(79));
            if (capture.Width <= 0 || capture.Height <= 0)
                throw new InvalidOperationException("The virtual desktop has no capturable display.");
            return capture;
        }
        finally
        {
            if (previous != IntPtr.Zero)
                SetThreadDpiAwarenessContext(previous);
        }
    }

    internal sealed record DesktopCaptureRegion(int X, int Y, int Width, int Height);

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    // The explicit input allows lifecycle checks with a synthetic video instead of the user's desktop.
    internal static async Task<ScreenRecording> StartAsync(string outputDirectory, int maximumSeconds,
        string? executable, IReadOnlyList<string> inputArguments)
        => await StartCoreAsync(outputDirectory, maximumSeconds, executable, inputArguments, "explicit-input", false);

    private static async Task<ScreenRecording> StartCoreAsync(string outputDirectory, int maximumSeconds,
        string? executable, IReadOnlyList<string> inputArguments, string backend, bool captureEnvironment)
    {
        var recording = new ScreenRecording
        {
            _maximumSeconds = maximumSeconds, _directory = outputDirectory,
            _backend = backend, _captureEnvironment = captureEnvironment
        };
        try
        {
            Directory.CreateDirectory(outputDirectory);
            if (maximumSeconds <= 0)
                throw new ArgumentOutOfRangeException(nameof(maximumSeconds), "Recording duration must be positive.");
            if (executable == null)
                throw new FileNotFoundException("FFmpeg was not found. Reinstall the utility with its tools/ffmpeg folder.");

            recording._outputPath = Path.GetFullPath(Path.Combine(outputDirectory, "desktop.mkv"));
            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetFullPath(outputDirectory)
            };
            AddArguments(startInfo, ["-hide_banner", "-loglevel", "warning", "-nostats", "-y"]);
            AddArguments(startInfo, inputArguments);
            AddArguments(startInfo,
            [
                // A low-resolution branch checks actual input pixels once per second.
                // Metadata is flushed to stdout independently of the encoded-frame count.
                "-filter_complex", "[0:v]split=2[record][check];[record]scale=trunc(iw/2)*2:trunc(ih/2)*2[out];[check]fps=1,scale=160:90,format=yuv420p,blackframe=amount=0:threshold=32,metadata=mode=print:key=lavfi.blackframe.pblack:file=-:direct=1,nullsink",
                "-map", "[out]", "-an", "-c:v", "libx264", "-threads", "2",
                "-preset", "ultrafast", "-crf", "28", "-pix_fmt", "yuv420p",
                "-t", maximumSeconds.ToString(CultureInfo.InvariantCulture),
                "-progress", "pipe:1", "-f", "matroska", recording._outputPath
            ]);

            recording._process = new Process { StartInfo = startInfo };
            if (!recording._process.Start())
                throw new InvalidOperationException("FFmpeg could not be started.");
            recording._elapsed.Start();
            recording._stderrTask = DrainErrorAsync(recording._process.StandardError,
                Path.Combine(outputDirectory, "ffmpeg.log"));
            recording._stdoutTask = recording.ReadProgressAsync(recording._process.StandardOutput);
            recording._exitTask = recording.ObserveExitAsync(recording._process);
            recording.AttachKillOnCloseJob();
            recording._healthTask = recording.MonitorHealthAsync();

            // Do not open ENCY until FFmpeg has actually captured its first frame.
            var ready = await Task.WhenAny(recording._firstFrame.Task, recording._exitTask, Task.Delay(TimeSpan.FromSeconds(10)));
            if (ready == recording._firstFrame.Task)
            {
                recording.RefreshVideoPath();
                return recording;
            }

            recording.AddError(ready == recording._exitTask
                ? "FFmpeg exited before recording started. See ffmpeg.log."
                : "FFmpeg did not capture a frame within 10 seconds. See ffmpeg.log.");
            await recording.StopAsync();
        }
        catch (Exception exception)
        {
            recording.AddError($"Screen recording could not start: {exception.Message}");
            await recording.StopAsync();
        }
        return recording;
    }

    public async Task StopAsync()
    {
        await _stopLock.WaitAsync();
        try
        {
            if (_stopped)
                return;
            _stopped = true;
            if (_process == null)
                return;

            try
            {
                if (_process.HasExited)
                    _exitedBeforeStop = true;
                _stopRequested = true;
                if (!_process.HasExited)
                {
                    try
                    {
                        // FFmpeg finalizes the Matroska stream when it receives q on stdin.
                        await _process.StandardInput.WriteLineAsync("q").WaitAsync(TimeSpan.FromSeconds(2));
                        await _process.StandardInput.FlushAsync().WaitAsync(TimeSpan.FromSeconds(2));
                    }
                    catch (Exception exception) when (exception is IOException or InvalidOperationException or TimeoutException)
                    {
                        // The process may have exited between HasExited and the write.
                    }
                    try
                    {
                        await _exitTask.WaitAsync(TimeSpan.FromSeconds(10));
                    }
                    catch (TimeoutException)
                    {
                        AddError("FFmpeg did not stop within 10 seconds; recording was interrupted.");
                        _process.Kill(entireProcessTree: true);
                        await _exitTask.WaitAsync(TimeSpan.FromSeconds(5));
                    }
                }

                if (_process.ExitCode != 0)
                    AddError($"FFmpeg exited with code {_process.ExitCode}. See ffmpeg.log.");
                else if (_exitedBeforeStop)
                    AddError(_elapsed.Elapsed.TotalSeconds >= _maximumSeconds - 1
                        ? $"Recording reached its {_maximumSeconds}-second limit before the test finished."
                        : "FFmpeg stopped before the test finished. See ffmpeg.log.");

                await Task.WhenAll(_stderrTask, _stdoutTask).WaitAsync(TimeSpan.FromSeconds(5));
                RefreshVideoPath();
                if (VideoPath == null)
                    AddError("FFmpeg produced no video frames. See ffmpeg.log.");
            }
            catch (Exception exception)
            {
                AddError($"Screen recording could not be finalized: {exception.Message}");
            }
            finally
            {
                try
                {
                    _healthCancellation.Cancel();
                    try { await _healthTask; } catch (OperationCanceledException) { }
                    ReportHealth(final: true);
                    if (_captureEnvironment && _directory != null)
                        WriteJson(_directory, "capture-environment-stop.json", DesktopCaptureEnvironment.Capture());
                }
                catch (Exception exception)
                {
                    AddError($"Capture diagnostics could not be finalized: {exception.Message}");
                }
                // Closing this job also kills FFmpeg if the normal shutdown above failed.
                _job?.Dispose();
                try
                {
                    if (!_process.HasExited)
                        _process.Kill(entireProcessTree: true);
                }
                catch (Exception) { }
                _process.Dispose();
                _elapsed.Stop();
                RefreshVideoPath();
            }
        }
        finally
        {
            _stopLock.Release();
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private async Task ObserveExitAsync(Process process)
    {
        await process.WaitForExitAsync();
        if (!_stopRequested)
            _exitedBeforeStop = true;
    }

    private async Task ReadProgressAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                _health.Observe(line);
                if (line.StartsWith("frame=", StringComparison.Ordinal) &&
                    long.TryParse(line.AsSpan(6).Trim(), out var frames) && frames > 0)
                {
                    _hasFrames = true;
                    _firstFrame.TrySetResult();
                }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
    }

    private static async Task DrainErrorAsync(StreamReader reader, string logPath)
    {
        StreamWriter? writer = null;
        try
        {
            try { writer = new StreamWriter(logPath, append: false); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }

            var buffer = new char[4096];
            var written = 0;
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory())) > 0)
            {
                // Always drain stderr, even after the log limit or a disk error.
                if (writer == null || written >= MaximumLogCharacters)
                    continue;
                try
                {
                    var toWrite = Math.Min(count, MaximumLogCharacters - written);
                    await writer.WriteAsync(buffer.AsMemory(0, toWrite));
                    written += toWrite;
                    if (written == MaximumLogCharacters)
                        await writer.WriteLineAsync("\n[FFmpeg log truncated at 1 MiB of characters]");
                    await writer.FlushAsync();
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    try { writer.Dispose(); } catch (IOException) { }
                    writer = null;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
        finally
        {
            if (writer != null)
                try { await writer.DisposeAsync(); } catch (IOException) { }
        }
    }

    private void RefreshVideoPath()
    {
        try
        {
            if (_hasFrames && _outputPath != null && new FileInfo(_outputPath) is { Exists: true, Length: > 0 })
                VideoPath = _outputPath;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private async Task MonitorHealthAsync()
    {
        try
        {
            while (!_healthCancellation.IsCancellationRequested)
            {
                await Task.Delay(1000, _healthCancellation.Token);
                ReportHealth(final: false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { AddError($"Desktop image monitoring stopped: {exception.Message}"); }
    }

    private void ReportHealth(bool final)
    {
        var snapshot = _health.GetSnapshot();
        if (!_blackReported && (snapshot.EverUnhealthy || final && snapshot.SampleCount > 0 && !snapshot.HasVisibleFrames))
        {
            _blackReported = true;
            AddError("Desktop capture contains an almost entirely black image (a mouse cursor alone does not count as a picture). See capture-health.json and capture-environment-black.json. The capture source, display or Windows session may be unavailable.");
            if (_captureEnvironment && _directory != null)
                WriteJson(_directory, "capture-environment-black.json", DesktopCaptureEnvironment.Capture());
        }
        var telemetryMissing = final ? _hasFrames && snapshot.SampleCount == 0
            : _elapsed.Elapsed.TotalSeconds > 10 && (snapshot.TelemetryAge == null || snapshot.TelemetryAge.Value.TotalSeconds > 10);
        if (!_missingTelemetryReported && telemetryMissing)
        {
            _missingTelemetryReported = true;
            AddError("Desktop image verification stopped receiving frames. The video may contain a frozen or unavailable capture source; see capture-health.json.");
        }
        if (_directory != null)
            WriteJson(_directory, "capture-health.json", new { Backend = _backend, Final = final, Health = snapshot, RecordingError = Error });
    }

    private static void WriteJson(string directory, string name, object value)
    {
        try
        {
            File.WriteAllText(Path.Combine(directory, name), System.Text.Json.JsonSerializer.Serialize(value, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private void AddError(string message)
    {
        lock (_errorGate) Error = Error == null ? message : $"{Error} {message}";
    }

    private static void AddArguments(ProcessStartInfo info, IEnumerable<string> arguments)
    {
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
    }

    internal static string? FindExecutable()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", "ffmpeg.exe"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "tools", "ffmpeg", "ffmpeg.exe")),
            Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe")
        };
        foreach (var candidate in candidates)
            if (File.Exists(candidate))
                return candidate;
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var path = directory.Trim().Trim('"');
            if (path.Length > 0)
            {
                var candidate = Path.Combine(path, "ffmpeg.exe");
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        return null;
    }

    private void AttachKillOnCloseJob()
    {
        if (!OperatingSystem.IsWindows() || _process == null)
            return;
        try
        {
            var job = CreateJobObject(IntPtr.Zero, null);
            if (job.IsInvalid)
            {
                job.Dispose();
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
            _job = job;
            var limits = new JobExtendedLimitInformation();
            limits.BasicLimitInformation.LimitFlags = 0x00002000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<JobExtendedLimitInformation>()) ||
                !AssignProcessToJobObject(job, _process.Handle))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        catch (Exception exception)
        {
            _job?.Dispose();
            _job = null;
            AddError($"FFmpeg crash cleanup could not be enabled; its duration limit remains active: {exception.Message}");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobIoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobExtendedLimitInformation
    {
        public JobBasicLimitInformation BasicLimitInformation;
        public JobIoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateJobObjectW")]
    private static extern SafeFileHandle CreateJobObject(IntPtr securityAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass,
        ref JobExtendedLimitInformation information, uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
}
