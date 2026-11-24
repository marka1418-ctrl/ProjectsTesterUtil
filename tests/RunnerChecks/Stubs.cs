using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using ProjectTestingUtility.Models;

namespace RunnerChecks
{
    internal sealed class Scenario
    {
        public static Scenario Current { get; set; } = null!;
        public int ProjectId { get; init; }
        public required string Name { get; init; }
        public bool FirstFails { get; init; }
        public bool RetryFails { get; init; }
        public bool FirstTimesOut { get; init; }
        public bool RecorderMissing { get; init; }
        public bool RecorderBlack { get; init; }
        public bool CleanupHangs { get; init; }
        public bool FirstOpenConnectionError { get; init; }
        public bool BackgroundIncomplete { get; init; }
        public bool ObservedErrorThenConnectionFailure { get; init; }
        public bool ValidateBackground { get; init; }
        public bool BackgroundConfigurationDisabled { get; init; }
        public bool MissingNativeLogPath { get; init; }
        public bool NeedsRetry => FirstFails || FirstTimesOut || FirstOpenConnectionError || BackgroundIncomplete ||
            ObservedErrorThenConnectionFailure || BackgroundConfigurationDisabled;
        public byte[] Original { get; } = [1, 2, 3, 4, 255, 128, 0, 99];
        public int Downloads;
        public int Opens;
        public int Tests;
        public int Recordings;
        public int Stops;
        public ConcurrentQueue<string> Log { get; } = new();
        public ConcurrentQueue<int> ProcessIds { get; } = new();
        public ConcurrentQueue<byte[]> OpenedBytes { get; } = new();
        public ConcurrentQueue<string> ObservedSettings { get; } = new();
        public List<ProjectTestingUtility.CAMConnector.CamUtilityHelper> Helpers { get; } = [];
        public string ResultPath => Path.Combine(AppContext.BaseDirectory, "results", ProjectId + ".json");
        public string MarkerDirectory => Path.Combine(AppContext.BaseDirectory, "fake-cam");
        public bool WasPublishedEarly;
        public bool ConcurrentHelperReuse;
    }

    internal sealed class EmptyProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}

namespace NLog
{
    public static class LogManager
    {
        public static Logger GetCurrentClassLogger() => new();
    }
    public sealed class Logger
    {
        private static void Add(string message) => RunnerChecks.Scenario.Current.Log.Enqueue(message);
        public void Debug(string message) => Add(message);
        public void Info(string message) => Add(message);
        public void Warn(string message) => Add(message);
        public void Warn(Exception exception, string message) => Add(message + ": " + exception.Message);
        public void Error(string message) => Add(message);
        public void Error(Exception exception, string message) => Add(message + ": " + exception.Message);
    }
}

namespace ProjectTestingUtility.Services
{
    public sealed class SettingsService(Settings settings)
    {
        public Settings Settings { get; } = settings;
    }
    public sealed class TechnologyService;
    public sealed class SimulationService;
    public sealed record CamWindowActivationResult
    {
        public bool IsForeground { get; init; }
        public bool IsVisible { get; init; }
        public string? Warning { get; init; }
    }
    public static class CamWindowActivation
    {
        public static CamWindowActivationResult Activate(object application, string? projectPath = null, object? helper = null) =>
            new() { IsForeground = true, IsVisible = true };
    }
    public sealed class WebService
    {
        public Task<int> GetProjectsCountAsync(string filters) => Task.FromResult(1);
        public Task<int[]> GetProjectsIdsAsync(int count, string filters) => Task.FromResult(new[] { RunnerChecks.Scenario.Current.ProjectId });
        public async Task<bool> DownloadProjectByIdAsync(int id, string path, CancellationToken cancellationToken = default)
        {
            var state = RunnerChecks.Scenario.Current;
            Interlocked.Increment(ref state.Downloads);
            await File.WriteAllBytesAsync(path, state.Original);
            return true;
        }
    }

    public sealed class ScreenRecording : IAsyncDisposable
    {
        private readonly RunnerChecks.Scenario _state = RunnerChecks.Scenario.Current;
        private bool _stopped;
        public string? VideoPath { get; private init; }
        public string? Error { get; private set; }
        public static async Task<ScreenRecording> StartAsync(string directory, int maximumSeconds)
        {
            var state = RunnerChecks.Scenario.Current;
            Interlocked.Increment(ref state.Recordings);
            if (state.RecorderMissing)
                return new ScreenRecording { Error = "FFmpeg is unavailable in this synthetic scenario." };
            var path = Path.Combine(directory, "desktop.mkv");
            await File.WriteAllTextAsync(path, "Synthetic stub recording: no desktop capture.");
            return new ScreenRecording { VideoPath = path };
        }
        public Task StopAsync()
        {
            if (!_stopped)
            {
                _stopped = true;
                Interlocked.Increment(ref _state.Stops);
                if (_state.RecorderBlack)
                    Error = "Recording contains only black frames in this synthetic scenario.";
            }
            return Task.CompletedTask;
        }
        public async ValueTask DisposeAsync() => await StopAsync();
    }
}

namespace ProjectTestingUtility.CAMConnector
{
    public sealed class CamUtilityHelper : IDisposable
    {
        private readonly RunnerChecks.Scenario _state = RunnerChecks.Scenario.Current;
        private readonly Settings _settings;
        private readonly int _number;
        private string? _projectPath;
        private bool _busy;
        public bool Disposed { get; private set; }

        public CamUtilityHelper(ProjectTestingUtility.Services.SettingsService settingsService,
            ProjectTestingUtility.Services.SimulationService simulationService,
            ProjectTestingUtility.Services.TechnologyService technologyService)
        {
            _settings = settingsService.Settings;
            lock (_state.Helpers)
            {
                if (_state.Helpers.Any(helper => !helper.Disposed))
                    _state.ConcurrentHelperReuse = true;
                _state.Helpers.Add(this);
                _number = _state.Helpers.Count;
            }
        }

        public void Initialize(string folder, int processId)
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited)
                throw new InvalidOperationException("Fake CAM should be running when Initialize is called.");
            _state.ProcessIds.Enqueue(processId);
            var marker = Path.Combine(_state.MarkerDirectory, processId + ".json");
            var timer = Stopwatch.StartNew();
            while (!File.Exists(marker) && timer.Elapsed < TimeSpan.FromSeconds(1))
                Thread.Sleep(10);
            if (!File.Exists(marker))
                throw new InvalidOperationException("Fake CAM did not write its launch arguments.");
        }

        public void OpenProject(string projectPath)
        {
            _state.WasPublishedEarly |= File.Exists(_state.ResultPath);
            Interlocked.Increment(ref _state.Opens);
            _projectPath = projectPath;
            _state.OpenedBytes.Enqueue(File.ReadAllBytes(projectPath));
            _state.ObservedSettings.Enqueue(JsonSerializer.Serialize(_settings));
            if (_number == 1 && _state.FirstOpenConnectionError)
                throw new IOException("Error reading answer : socket error (WSA 10057)");
        }

        public object GetApplication() => this;
        public object GetIpcHelper() => this;
        public string? GetCamConfigurationFolder() => Path.Combine(AppContext.BaseDirectory, "synthetic-cam-profiles", _state.ProjectId.ToString());
        public string? GetNativeLogPath()
        {
            var profile = Path.Combine(AppContext.BaseDirectory, "synthetic-cam-profiles", _state.ProjectId.ToString());
            var log = Path.Combine(profile, "Log", "fake-cam.log");
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            File.WriteAllText(log, "Synthetic native CAM log; no real application or credentials.");
            var machine = _state.BackgroundConfigurationDisabled ? "0" : "-1";
            File.WriteAllText(Path.Combine(profile, "Synthetic.cfg"),
                "<Setup><Machining_AutoCalcMachiningResult>True</Machining_AutoCalcMachiningResult>" +
                $"<Machining_BackgroundCollisionDetection>{machine}</Machining_BackgroundCollisionDetection>" +
                "<Machining_BackgroundCheckHolder>True</Machining_BackgroundCheckHolder>" +
                "<Machining_BackgroundCDInvisible>0</Machining_BackgroundCDInvisible></Setup>");
            return _state.MissingNativeLogPath ? null : log;
        }

        public TreeNode Test(Action<string>? stageChanged = null, Action<TestStageResult>? stageCaptured = null,
            CancellationToken cancellationToken = default)
        {
            _busy = true;
            Interlocked.Increment(ref _state.Tests);
            _state.WasPublishedEarly |= File.Exists(_state.ResultPath);
            try
            {
                stageChanged?.Invoke("Calculate");
                if (_number == 1)
                    File.WriteAllText(_projectPath!, "Simulated changes made by CAM to its working copy.");
                if (_number == 1 && _state.FirstTimesOut)
                {
                    // WaitAsync times out after 1s; native-like work continues beyond that.
                    Thread.Sleep(1800);
                    stageChanged?.Invoke("LateNativeCallback");
                    stageCaptured?.Invoke(new TestStageResult { Stage = "BackgroundSimulation", Note = "LateNativeSnapshot" });
                }
                TreeNode GoodTree() => new()
                {
                    Name = "root", OperationId = "root", IsCalculated = true, IsSimulated = true,
                    Children = { new TreeNode { Name = "operation", OperationId = "operation", IsCalculated = true, IsSimulated = true } }
                };
                stageCaptured?.Invoke(new TestStageResult { Stage = "AfterCalculate", Results = GoodTree(), IsComplete = true });
                if (_number == 1 && _state.ObservedErrorThenConnectionFailure)
                {
                    var observed = GoodTree();
                    observed.Children[0].IsCollisionError = true;
                    stageCaptured?.Invoke(new TestStageResult { Stage = "BackgroundSimulation", Results = observed, IsComplete = false });
                    throw new IOException("Late socket error after observed background collision");
                }
                if (_settings.ValidateBackgroundSimulation)
                {
                    stageCaptured?.Invoke(new TestStageResult
                    {
                        Stage = "BackgroundSimulation", Results = GoodTree(), IsComplete = !_state.BackgroundIncomplete,
                        Note = _state.BackgroundIncomplete ? "Synthetic background validation did not complete" : null
                    });
                }
                stageChanged?.Invoke("Simulate");
                stageChanged?.Invoke("ReadResults");
                var fails = _number == 1 ? _state.FirstFails : _state.RetryFails;
                var final = GoodTree();
                final.Children[0].IsGougeError = fails;
                stageCaptured?.Invoke(new TestStageResult { Stage = "ForegroundSimulation", Results = final, IsComplete = true });
                return final;
            }
            finally { _busy = false; }
        }

        public void Dispose()
        {
            if (_busy)
                _state.ConcurrentHelperReuse = true;
            Disposed = true;
            if (_state.CleanupHangs) Thread.Sleep(Timeout.Infinite);
        }
    }
}
