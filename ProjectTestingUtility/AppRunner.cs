using NLog;
using ProjectTestingUtility.CAMConnector;
using ProjectTestingUtility.Models;
using ProjectTestingUtility.Services;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace ProjectTestingUtility;

public class AppRunner(WebService webService, IServiceProvider serviceProvider, SettingsService settingsService,
                      SimulationService simulationService, TechnologyService technologyService)
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly Settings _settings = settingsService.Settings;
    private Process? _camProcess;
    private CamUtilityHelper? _helper;
    private string? _nativeLogPath;
    private string? _camConfigurationFolder;
    private Task? _pendingCamCall;
    private int _projectsInProcess;

    private static string ResultsDirectory
    {
        get
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "results");
            Directory.CreateDirectory(directory);
            return directory;
        }
    }
    private static string ProjectsDirectory
    {
        get
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "projects");
            Directory.CreateDirectory(directory);
            return directory;
        }
    }

    public async Task Start(string[] args)
    {
        var runStage = "ValidateLaunch";
        try
        {
            Logger.Debug($"Args: {string.Join(",", args)}");

            if (args.Length is 0) throw new ArgumentException("Args are empty");
            
            var camPath = args[0];
            if (!File.Exists(camPath)) throw new FileNotFoundException("CAM exe not found", camPath);
            var primeProcessName = $"{Path.GetFileNameWithoutExtension(camPath)}_prime";
            var localFileIndex = Array.IndexOf(args, "--project-file");
            var localFile = localFileIndex >= 0 && localFileIndex + 1 < args.Length ? Path.GetFullPath(args[localFileIndex + 1]) : null;
            if (localFileIndex >= 0 && (localFile is null || !File.Exists(localFile)))
                throw new FileNotFoundException("Local project file was not found", localFile);
            if (args.Contains("--keep-recordings")) _settings.KeepSuccessfulRecordings = true;
            var lastTestedId = 0;
            if (args.Length >= 2 && int.TryParse(args[1], out var parsedId)) lastTestedId = parsedId;

            ExtractReadMe();

            var camFolder = Path.GetDirectoryName(camPath);
            if (string.IsNullOrEmpty(camFolder)) throw new Exception($"Failed to get cam folder from file path - {camPath}");
            
            var filters = ConvertFiltersToJsonString(lastTestedId);
            runStage = "ProjectService";
            RunStatusReporter.Write("Starting", runStage, "Authorizing and getting the project list before starting ENCY.");
            var projectsCount = localFile is null ? await webService.GetProjectsCountAsync(filters) : 1;

            if (projectsCount is 0)
            {
                Logger.Debug("Projects not found");
                RunStatusReporter.Write("Completed", "ProjectService", "No projects match the selected filters. ENCY was not started.");
                return;
            }

            var idIndex = Array.IndexOf(args, "--project-id");
            var localId = idIndex >= 0 && idIndex + 1 < args.Length && int.TryParse(args[idIndex + 1], out var specifiedId)
                ? specifiedId : int.TryParse(Path.GetFileNameWithoutExtension(localFile), out var fileId) ? fileId : 0;
            var projectsIds = localFile is null ? await webService.GetProjectsIdsAsync(projectsCount, filters) : [localId];

            if (projectsIds.Length is 0) throw new Exception("Projects id's not found");

            // Do not touch any CAM processes before configuration and project service access succeed.
            runStage = "PrepareCam";
            // Prime may contain the user's other projects. Only own backend PIDs are closed.

            var runId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];

            foreach (var projectId in projectsIds)
            {
                Logger.Debug($"Start testing project with id {projectId}");
                runStage = "TestProjects";
                RunStatusReporter.Write("Running", runStage, $"Testing project {projectId}.");
                var projectPath = Path.Combine(ProjectsDirectory, $"{projectId}.stcp");
                var diagnosticDirectory = Path.Combine(AppContext.BaseDirectory, "diagnostics", runId, projectId.ToString());
                AttemptDiagnostics.TrySave(() => Directory.CreateDirectory(diagnosticDirectory));
                var originalPath = Path.Combine(diagnosticDirectory, "original.stcp");
                var downloaded = false;
                var result = await TestRetryPolicy.ExecuteAsync(async attemptNumber =>
                {
                    var diagnostics = new AttemptDiagnostics(Path.Combine(diagnosticDirectory, $"attempt-{attemptNumber}"), _settings);
                    return await RunAttemptAsync(attemptNumber, camPath, camFolder, projectPath, diagnostics, async () =>
                    {
                        if (!downloaded)
                        {
                            if (localFile is not null) File.Copy(localFile, projectPath, true);
                            else
                            {
                                using var downloadTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                                if (!await webService.DownloadProjectByIdAsync(projectId, projectPath, downloadTimeout.Token))
                                    throw new IOException($"Failed to download project {projectId}");
                            }
                            File.Copy(projectPath, originalPath, true);
                            downloaded = true;
                        }
                        else
                        {
                            // Reuse identical bytes even if CAM touched the working copy.
                            File.Copy(originalPath, projectPath, true);
                        }
                    });
                });
                result.ProjectId = projectId;
                result.RunId = runId;
                result.DiagnosticsDirectory = Path.GetRelativePath(AppContext.BaseDirectory, diagnosticDirectory);
                if (result.Attempts[0].Passed && !_settings.KeepSuccessfulRecordings &&
                    string.IsNullOrWhiteSpace(result.Attempts[0].RecordingError))
                {
                    // Successful first attempts need no retained desktop video.
                    var first = result.Attempts[0];
                    if (first.VideoPath is not null)
                        AttemptDiagnostics.TrySave(() => File.Delete(Path.Combine(AppContext.BaseDirectory, first.VideoPath)));
                    first.VideoPath = null;
                    AttemptDiagnostics.TrySave(() => File.Delete(originalPath));
                    AttemptDiagnostics.TrySave(() => AttemptDiagnostics.WriteJsonAtomic(
                        Path.Combine(diagnosticDirectory, "attempt-1", "attempt.json"), first));
                }
                ExportResults(projectId, result);

                if (!result.Attempts[^1].Passed || ++_projectsInProcess >= Math.Max(1, _settings.CountOfTestedProjectsBeforeRestart))
                    await StopCamAsync();
                Logger.Debug($"End of testing project with id {projectId}");
            }
            
            runStage = "Cleanup";
            await StopCamAsync();
            RunStatusReporter.Write("Completed", "TestProjects", "Testing completed. Open Results to view both attempts and recordings.");
        }
        catch (Exception e)
        {
            Logger.Error(e, "An error occurred while app running");
            RunStatusReporter.Write("Failed", runStage, RunStatusReporter.FailureMessage(e, runStage));
            throw;
        }
        finally
        {
            await StopCamAsync();
        }
    }

    private async Task<TestAttemptResult> RunAttemptAsync(int number, string camPath, string camFolder,
        string projectPath, AttemptDiagnostics diagnostics, Func<Task> prepareProject)
    {
        var attempt = new TestAttemptResult { Attempt = number, StartedAtUtc = DateTimeOffset.UtcNow };
        var timer = Stopwatch.StartNew();
        var stage = "Prepare";
        var finished = false;
        var stageGate = new object();
        void SetStage(string value)
        {
            lock (stageGate)
            {
                if (finished) return;
                stage = value;
                diagnostics.Step(value);
                Logger.Info($"Attempt {number}, stage {value}, project {projectPath}");
            }
        }
        var maximumSeconds = (int)Math.Clamp(4L * _settings.LaunchCamTimeoutInSeconds +
            _settings.OpenProjectTimeoutInSeconds + _settings.TestProjectTimeoutInSeconds + 60, 60, int.MaxValue);
        ScreenRecording? recording = null;
        using var attemptCancellation = new CancellationTokenSource();
        string? nativeLogPath = null;
        void CaptureStage(TestStageResult snapshot)
        {
            lock (stageGate)
            {
                if (finished) return;
                attempt.Stages.Add(snapshot);
                diagnostics.SaveStage(snapshot, attempt.Stages.Count);
            }
        }
        void SaveActivation(CamWindowActivationResult activation, string phase)
        {
            diagnostics.SaveActivation(activation, phase);
            if (!string.IsNullOrWhiteSpace(activation.Warning))
            {
                lock (stageGate) if (!finished) attempt.RecordingWarnings.Add(phase + ": " + activation.Warning);
                Logger.Warn($"CAM window activation ({phase}): {activation.Warning}");
            }
        }
        try
        {
            if (number == 2)
            {
                SetStage("RestartForRetry");
                // Do not change simulation settings, Job Mode or timeouts for the retry.
                await StopCamAsync();
            }
            SetStage("DownloadOrRestoreProject");
            await prepareProject();
            // Record the CAM scenario, after download and any retry cleanup have completed.
            if (_camProcess is { HasExited: true }) await StopCamAsync();
            SetStage("StartRecording");
            recording = await ScreenRecording.StartAsync(diagnostics.DirectoryPath, maximumSeconds);
            if (recording.Error is not null) Logger.Warn($"Recording unavailable; continuing CAM test: {recording.Error}");
            if (_camProcess is null || _camProcess.HasExited)
            {
                await StopCamAsync();
                SetStage("Launch");
                var launchArgs = _settings.EffectiveUseJobMode ? "/JOB_MODE" : "";
                _camProcess = Process.Start(new ProcessStartInfo(camPath, launchArgs)
                {
                    WorkingDirectory = Environment.CurrentDirectory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }) ?? throw new InvalidOperationException("Could not start CAM");
                _ = DrainCamOutputAsync(_camProcess.StandardOutput);
                _ = DrainCamOutputAsync(_camProcess.StandardError);
                Logger.Info($"Run CAM process, path - {camPath}, args - {launchArgs}, PID - {_camProcess.Id}");
                if (await Task.Run(() => _camProcess.WaitForExit(TimeSpan.FromSeconds(_settings.LaunchCamTimeoutInSeconds))))
                    throw new InvalidOperationException($"CAM exited during launch, exit code {_camProcess.ExitCode}");
                _camProcess.Refresh();
                if (!_camProcess.Responding) throw new InvalidOperationException("CAM process is not responding");
                var newHelper = new CamUtilityHelper(settingsService, simulationService, technologyService);
                _helper = newHelper;
                SetStage("Connect");
                var processId = _camProcess.Id;
                _pendingCamCall = Task.Run(() =>
                {
                    newHelper.Initialize(camFolder, processId);
                    _nativeLogPath = newHelper.GetNativeLogPath();
                    _camConfigurationFolder = newHelper.GetCamConfigurationFolder();
                });
                await _pendingCamCall.WaitAsync(TimeSpan.FromSeconds(_settings.LaunchCamTimeoutInSeconds));
            }
            nativeLogPath = _nativeLogPath;
            diagnostics.SaveMetadata(camPath, _camProcess.Id, projectPath);
            var helper = _helper ?? throw new InvalidOperationException("CAM connection unavailable");
            var backgroundConfiguration = BackgroundSimulationConfiguration.ReadForCamLog(nativeLogPath ?? "");
            if (!backgroundConfiguration.Available)
                backgroundConfiguration = BackgroundSimulationConfiguration.ReadForProfileFolder(_camConfigurationFolder ?? "");
            diagnostics.SaveBackgroundConfiguration(backgroundConfiguration);
            SetStage("ActivateWindow");
            var activationTask = Task.Run(() => CamWindowActivation.Activate(helper.GetApplication(), null, helper.GetIpcHelper()));
            _pendingCamCall = activationTask;
            var initialActivation = await activationTask.WaitAsync(TimeSpan.FromSeconds(_settings.LaunchCamTimeoutInSeconds));
            SaveActivation(initialActivation, "before-open");
            SetStage("Open");
            _pendingCamCall = Task.Run(() => helper.OpenProject(projectPath));
            await _pendingCamCall.WaitAsync(TimeSpan.FromSeconds(_settings.OpenProjectTimeoutInSeconds));
            activationTask = Task.Run(() => CamWindowActivation.Activate(helper.GetApplication(), projectPath, helper.GetIpcHelper()));
            _pendingCamCall = activationTask;
            SaveActivation(await activationTask.WaitAsync(TimeSpan.FromSeconds(_settings.LaunchCamTimeoutInSeconds)), "after-open");
            SetStage("Test");
            void TestStage(string value)
            {
                lock (stageGate) if (finished || attemptCancellation.IsCancellationRequested) return;
                SetStage(value);
                if (value is "Calculate" or "Simulate")
                    SaveActivation(CamWindowActivation.Activate(helper.GetApplication(), projectPath, helper.GetIpcHelper()), value);
            }
            var testTask = Task.Run(() => helper.Test(TestStage, CaptureStage, attemptCancellation.Token));
            _pendingCamCall = testTask;
            attempt.Results = await testTask.WaitAsync(TimeSpan.FromSeconds(_settings.TestProjectTimeoutInSeconds));
            attempt.IsTested = true;
            var operationFailure = TestRetryPolicy.HasFailures(attempt.Results) || StageResultPolicy.HasObservedErrors(attempt.Stages);
            var background = attempt.Stages.LastOrDefault(s => s.Stage == "BackgroundSimulation");
            var configuredBackground = backgroundConfiguration.Enabled == true &&
                (!_settings.CheckMachineCollisions || backgroundConfiguration.MachineCollisions == true);
            attempt.ValidationComplete = !_settings.ValidateBackgroundSimulation || background?.IsComplete == true && configuredBackground;
            attempt.ValidationNote = attempt.ValidationComplete ? null : background?.Note ?? "Background collision validation was not completed.";
            if (_settings.ValidateBackgroundSimulation && !configuredBackground)
                attempt.ValidationNote = "Background modeling / Machine collision checking is disabled or could not be confirmed in the CAM profile. " + backgroundConfiguration.Note;
            attempt.Passed = !operationFailure && attempt.ValidationComplete;
            attempt.FailureKind = operationFailure ? "OperationError" : attempt.ValidationComplete ? "None" : "ValidationIncomplete";
            if (!attempt.Passed) attempt.FailedStage = operationFailure ? "OperationResults" : "BackgroundSimulation";
            SetStage(attempt.Passed ? "Passed" : "Failed");
        }
        catch (Exception e)
        {
            attemptCancellation.Cancel();
            lock (stageGate) { attempt.FailedStage = stage; finished = true; }
            attempt.Error = diagnostics.Redact(e.ToString());
            attempt.FailureKind = StageResultPolicy.HasObservedErrors(attempt.Stages) ? "OperationError"
                : e is TimeoutException ? "Timeout"
                : e.Message.Contains("socket", StringComparison.OrdinalIgnoreCase) ? "ConnectionError"
                : attempt.FailedStage == "Open" ? "OpenError" : "ExecutionError";
            try { if (_camProcess is { HasExited: true }) { if (attempt.FailureKind != "OperationError") attempt.FailureKind = "ApplicationExited"; attempt.Error += $"\nCAM exit code: {_camProcess.ExitCode}"; } } catch (InvalidOperationException) { }
            Logger.Error($"Attempt {number} failed at {attempt.FailedStage}: {attempt.Error}");
            diagnostics.SaveMetadata(camPath, _camProcess?.Id, projectPath);
        }
        finally
        {
            lock (stageGate) finished = true;
            attempt.DurationMs = timer.ElapsedMilliseconds;
            // Finalize the recording while the failing CAM is still visible.
            if (recording is not null)
            {
                await recording.StopAsync();
                attempt.VideoPath = recording.VideoPath is null ? null : Path.GetRelativePath(AppContext.BaseDirectory, recording.VideoPath);
                attempt.RecordingError = recording.Error;
            }
            else attempt.RecordingError = "Recording did not start: project preparation or retry cleanup failed.";
            diagnostics.SaveResult(attempt);
            if (!attempt.Passed || number == 2) diagnostics.SaveLogs();
            diagnostics.SaveNativeLog(nativeLogPath);
        }
        return attempt;
    }

    private static async Task DrainCamOutputAsync(StreamReader reader)
    {
        // Native launchers can emit account tokens; never forward or persist their console output.
        try { var buffer = new char[4096]; while (await reader.ReadAsync(buffer.AsMemory()) > 0) { } }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
    }

    private static void ExportResults(int projectId, TestResult result)
    {
        // Publish once, after both attempts; the monitor must never consume a partial result.
        AttemptDiagnostics.WriteJsonAtomic(Path.Combine(ResultsDirectory, $"{projectId}.json"), result);
        Logger.Info($"Exported project {projectId}, attempts {result.Attempts.Count}, retry {result.RetryOutcome}");
    }

    private string ConvertFiltersToJsonString(int lastTestedId)
    {
        var criteria = new List<SearchCriteria> { new() { Field = "binDataVersion", MatchMode = "less", Value = _settings.BinDataVersion }, };

        switch (_settings)
        {
            case { OnlyRecommended: false, OnlyPublic: false }:
                break;
            case { OnlyRecommended: true, OnlyPublic: false }:
                criteria.Add(new SearchCriteria { Field = "referenceProject", MatchMode = "Contains", Value = "true" });
                break;
            case { OnlyRecommended: false, OnlyPublic: true }:
                criteria.Add(new SearchCriteria { Field = "visibility", MatchMode = "Equals", Value = (int)ProjectVisibility.Public });
                break;
            case { OnlyRecommended: true, OnlyPublic: true }:
                criteria.Add(new SearchCriteria { Field = "referenceProject", MatchMode = "Contains", Value = "true" });
                criteria.Add(new SearchCriteria { Field = "visibility", MatchMode = "Equals", Value = (int)ProjectVisibility.Public });
                break;
        }

        if (lastTestedId > 0)
            criteria.Add(new SearchCriteria { Field = "id", MatchMode = "less", Value = lastTestedId.ToString() });

        if (!string.IsNullOrEmpty(_settings.MachineType))
            criteria.Add(new SearchCriteria { Field = "machine.groupExtended", MatchMode = "Equals", Value = _settings.MachineType });

        if (!string.IsNullOrEmpty(_settings.MachineName))
            criteria.Add(new SearchCriteria { Field = "machine.name", MatchMode = "Contains", Value = _settings.MachineName });
        
        if (_settings.Owner > 0)
            criteria.Add(new SearchCriteria { Field = "ownerCompanyId", MatchMode = "Equals", Value = _settings.Owner });

        if (_settings.Operations.Any())
            criteria.AddRange(_settings.Operations.Select(operation => new SearchCriteria { Field = "operations.type", MatchMode = "Equals", Value = operation }));

        if (_settings.ToolTypes.Any())
            criteria.AddRange(_settings.ToolTypes.Select(toolType => new SearchCriteria { Field = "tools.toolType", MatchMode = "Equals", Value = toolType })); 
        
        if (_settings.Axes.Any())
            criteria.AddRange(_settings.Axes.Select(axis => new SearchCriteria { Field = "machine.machineStateParameters.address", MatchMode = "Equals", Value = axis }));

        if (_settings.Tags.Any())
            criteria.AddRange(_settings.Tags.Select(tag => new SearchCriteria { Field = "tags.label", MatchMode = "Equals", Value = tag }));

        return Uri.EscapeDataString(JsonSerializer.Serialize(criteria));
    }

    public async Task Stop(int exitCode)
    {
        if (serviceProvider is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync();
        Logger.Debug("Shutdown app");
        Environment.ExitCode = exitCode;
    }

    private async Task StopCamAsync()
    {
        if (_camProcess is not null)
        {
            if (!_camProcess.HasExited)
            {
                _camProcess.Kill();
                await _camProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(_settings.KillCamProcessTimeoutInSeconds));
            }
            _camProcess.Dispose();
            _camProcess = null;
        }
        // A timed-out Task.Run does not cancel native IPC. Never reuse its helper/context.
        var pending = _pendingCamCall;
        var helper = _helper;
        _pendingCamCall = null;
        _helper = null;
        _nativeLogPath = null;
        _camConfigurationFolder = null;
        _projectsInProcess = 0;
        if (pending is not null && !pending.IsCompleted)
        {
            try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception) { /* Observe below or when native IPC eventually returns. */ }
        }
        if (pending is { IsCompleted: false })
        {
            Logger.Warn("CAM IPC did not return after process exit; retiring its helper before retry");
            _ = pending.ContinueWith(completed =>
            {
                _ = completed.Exception;
                try { helper?.Dispose(); }
                catch (Exception error) { Logger.Warn(error, "Could not release retired CAM helper"); }
            }, TaskScheduler.Default);
        }
        else
        {
            _ = pending?.Exception;
            if (helper is not null)
            {
                // Native Dispose can block after its backend has exited. Keep it off
                // the runner thread and never let cleanup hold the session forever.
                var cleanup = Task.Run(helper.Dispose);
                try { await cleanup.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (TimeoutException)
                {
                    Logger.Warn("CAM helper cleanup timed out; continuing shutdown with saved results");
                    _ = cleanup.ContinueWith(completed => { _ = completed.Exception; }, TaskScheduler.Default);
                }
                catch (Exception error) { Logger.Warn(error, "Could not release CAM helper"); }
            }
        }
    }
    
    private void KillPrimeAndWaitForExit(string processName)
    {
        var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(processName));
        foreach (var process in processes)
        {
            process.Kill();
            if (!process.WaitForExit(TimeSpan.FromSeconds(_settings.KillCamProcessTimeoutInSeconds)))
            {
                Logger.Error($"CAM process not killed after {_settings.KillCamProcessTimeoutInSeconds} seconds");
            }
        }
    }

    private void ExtractReadMe()
    {
        try
        {
            Logger.Debug("Extract ReadMe.txt");

            using (var resourceStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ProjectTestingUtility.ReadMe.txt"))
            {
                if (resourceStream is null)
                {
                    Logger.Warn("ReadMe.txt not found in resources");
                    return;
                }

                var outputPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ReadMe.txt");
                using (var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write))
                {
                    resourceStream.CopyTo(fileStream);
                }
            }

            Logger.Debug("ReadMe.txt has been extracted");
        }
        catch (Exception e)
        {
            Logger.Error(e, "Failed to extract ReadMe.txt");
        }
    }

}
