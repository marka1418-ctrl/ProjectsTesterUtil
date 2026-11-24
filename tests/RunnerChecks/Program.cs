using System.Diagnostics;
using System.Text.Json;
using ProjectTestingUtility;
using ProjectTestingUtility.Models;
using ProjectTestingUtility.Services;
using RunnerChecks;

// A production runner launches only this harness apphost, never ENCY or a real project.
if (Environment.GetEnvironmentVariable("PROJECTS_TESTER_CHECK_FAKE_CAM") == "1")
{
    var markerDirectory = Path.Combine(AppContext.BaseDirectory, "fake-cam");
    Directory.CreateDirectory(markerDirectory);
    await File.WriteAllTextAsync(Path.Combine(markerDirectory, Environment.ProcessId + ".json"), JsonSerializer.Serialize(args));
    await Task.Delay(TimeSpan.FromMinutes(1));
    return;
}

if (args is not ["--checks"])
{
    // AppRunner writes paths relative to AppContext.BaseDirectory. Copy this harness into
    // an isolated temporary directory so no real project, report or diagnostic is touched.
    var temporaryRoot = Path.Combine(Path.GetTempPath(), "ProjectsTesterUtil-RunnerChecks-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temporaryRoot);
    foreach (var source in Directory.GetFiles(AppContext.BaseDirectory))
        File.Copy(source, Path.Combine(temporaryRoot, Path.GetFileName(source)));
    var executable = Path.Combine(temporaryRoot, Path.GetFileName(Environment.ProcessPath!));
    var info = new ProcessStartInfo(executable)
    {
        UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = temporaryRoot,
        RedirectStandardOutput = true, RedirectStandardError = true
    };
    info.ArgumentList.Add("--checks");
    using var isolated = Process.Start(info)!;
    var standardOutput = isolated.StandardOutput.ReadToEndAsync();
    var standardError = isolated.StandardError.ReadToEndAsync();
    await isolated.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(1));
    Console.Write(await standardOutput);
    Console.Write(await standardError);
    Require(isolated.ExitCode == 0, $"Isolated runner checks failed with exit code {isolated.ExitCode}.");
    Console.WriteLine($"Runner check artifacts: {temporaryRoot}");
    return;
}

Environment.SetEnvironmentVariable("PROJECTS_TESTER_CHECK_FAKE_CAM", "1");
Scenario[] cases =
[
    new() { ProjectId = 91001, Name = "failure then success", FirstFails = true },
    new() { ProjectId = 91002, Name = "persistent failure", FirstFails = true, RetryFails = true },
    new() { ProjectId = 91003, Name = "native timeout then success", FirstTimesOut = true },
    new() { ProjectId = 91004, Name = "initial success" },
    new() { ProjectId = 91005, Name = "missing FFmpeg", FirstFails = true, RecorderMissing = true },
    new() { ProjectId = 91006, Name = "2097 open connection failure then success", FirstOpenConnectionError = true, ValidateBackground = true },
    new() { ProjectId = 91007, Name = "incomplete background validation", BackgroundIncomplete = true, ValidateBackground = true },
    new() { ProjectId = 91008, Name = "observed background error then IPC failure", ObservedErrorThenConnectionFailure = true, ValidateBackground = true },
    new() { ProjectId = 91009, Name = "complete background validation success", ValidateBackground = true },
    new() { ProjectId = 91010, Name = "disabled machine collision profile", BackgroundConfigurationDisabled = true, ValidateBackground = true },
    new() { ProjectId = 91011, Name = "opening failure then operation failure", FirstOpenConnectionError = true, RetryFails = true, ValidateBackground = true },
    new() { ProjectId = 91012, Name = "opening failure then inconclusive background", FirstOpenConnectionError = true, BackgroundIncomplete = true, ValidateBackground = true },
    new() { ProjectId = 91013, Name = "SDK profile fallback without native log path", MissingNativeLogPath = true, ValidateBackground = true },
    new() { ProjectId = 91014, Name = "successful test retains black recording diagnostics without retry", RecorderBlack = true },
    new() { ProjectId = 91015, Name = "completed test with permanently blocked native cleanup", CleanupHangs = true }
];
foreach (var scenario in cases)
{
    Scenario.Current = scenario;
    var settings = new Settings
    {
        // LaunchCamTimeout also limits helper initialization, so zero is not valid here.
        LaunchCamTimeoutInSeconds = 1,
        OpenProjectTimeoutInSeconds = 2,
        TestProjectTimeoutInSeconds = scenario.FirstTimesOut ? 1 : 2,
        KillCamProcessTimeoutInSeconds = 5,
        CountOfTestedProjectsBeforeRestart = 20,
        SimulationSpeedPercent = 37,
        UseJobMode = scenario.ProjectId != 91002,
        ValidateBackgroundSimulation = scenario.ValidateBackground,
        CheckGouges = true,
        CheckHolderCollisions = false,
        CheckMachineCollisions = true,
        BreakOnEndOfOperation = true,
        BreakOnErrors = true
    };
    var settingsBefore = JsonSerializer.Serialize(settings);
    var runner = new AppRunner(new WebService(), new EmptyProvider(), new SettingsService(settings), new SimulationService(), new TechnologyService());
    await runner.Start([Environment.ProcessPath!]).WaitAsync(TimeSpan.FromSeconds(15));
    using (var status = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "run-status.json"))))
        Require(status.RootElement.GetProperty("State").GetString() == "Completed", "Runner did not report completion after cleanup.");
    if (scenario.CleanupHangs)
        Require(scenario.Log.Any(message => message.Contains("cleanup timed out")), "Blocked native cleanup was not diagnosed.");
    Require(File.Exists(scenario.ResultPath), "Runner did not publish a result: " + string.Join("\n", scenario.Log));
    var result = JsonSerializer.Deserialize<TestResult>(await File.ReadAllTextAsync(scenario.ResultPath), new JsonSerializerOptions
    {
        PreferredObjectCreationHandling = System.Text.Json.Serialization.JsonObjectCreationHandling.Populate
    })!;
    var expectedAttempts = scenario.NeedsRetry ? 2 : 1;
    Require(result.Attempts.Count == expectedAttempts, $"{scenario.Name}: wrong attempt count.");
    Require(scenario.Opens == expectedAttempts && scenario.Tests == expectedAttempts - (scenario.FirstOpenConnectionError ? 1 : 0), "Wrong Open/Test invocation count.");
    Require(scenario.Downloads == 1, "A retry downloaded different server state instead of reusing the original.");
    Require(scenario.OpenedBytes.All(bytes => bytes.SequenceEqual(scenario.Original)), "Retry opened modified project bytes.");
    Require(scenario.ObservedSettings.All(value => value == settingsBefore) && JsonSerializer.Serialize(settings) == settingsBefore,
        "Retry changed the simulation settings or launch mode.");
    Require(!scenario.WasPublishedEarly, "A result became visible before all attempts completed.");
    Require(scenario.Log.Count(message => message.StartsWith("Exported project ", StringComparison.Ordinal)) == 1,
        "Runner published the same project more than once.");
    Require(!File.Exists(scenario.ResultPath + ".tmp"), "Atomic result publication left a temporary file.");
    Require(!scenario.ConcurrentHelperReuse && scenario.Helpers.All(helper => helper.Disposed), "A helper was reused or disposed during pending native work.");
    Require(scenario.ProcessIds.Distinct().Count() == expectedAttempts, "Retry did not start a fresh process.");
    Require(scenario.Recordings == expectedAttempts && scenario.Stops == expectedAttempts, "Each attempt must start and finalize its recording.");
    foreach (var pid in scenario.ProcessIds)
    {
        var launchArgs = JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(Path.Combine(scenario.MarkerDirectory, pid + ".json")))!;
        Require(launchArgs.SequenceEqual(settings.EffectiveUseJobMode ? new[] { "/JOB_MODE" } : Array.Empty<string>()), "Actual CAM launch arguments changed.");
        Require(!IsAlive(pid), "Runner left a CAM process running after completion.");
    }

    var diagnostics = Path.Combine(AppContext.BaseDirectory, result.DiagnosticsDirectory!);
    Require(Directory.GetDirectories(diagnostics, "attempt-*").Length == expectedAttempts, "Attempt artifacts are missing.");
    string? firstHash = null;
    foreach (var attempt in result.Attempts)
    {
        var directory = Path.Combine(diagnostics, "attempt-" + attempt.Attempt);
        Require(File.Exists(Path.Combine(directory, "attempt.json")), "Attempt result was not saved.");
        using var metadata = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "run.json")));
        var hash = metadata.RootElement.GetProperty("ProjectSha256").GetString();
        firstHash ??= hash;
        Require(hash == firstHash, "Retry metadata hashes differ from the original pre-open snapshot.");
        if (attempt.Attempt == 2)
            Require(hash == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(scenario.Original)), "Retry project hash differs from original.");
        Require(metadata.RootElement.GetProperty("Settings").GetProperty("SimulationSpeedPercent").GetInt32() == 37,
            "Diagnostic settings did not match the attempt.");
        Require(metadata.RootElement.GetProperty("LaunchArguments").GetString() == (settings.EffectiveUseJobMode ? "/JOB_MODE" : ""),
            "Diagnostic launch arguments differ.");
        if (scenario.RecorderMissing)
            Require(attempt.VideoPath == null && attempt.RecordingError != null, "Missing recorder must be reported separately.");
        else if (expectedAttempts == 2)
            Require(attempt.VideoPath != null && File.Exists(Path.Combine(AppContext.BaseDirectory, attempt.VideoPath)), "Failure/retry video was not retained.");
        if (attempt.IsTested)
            Require(attempt.Stages.Any(stage => stage.Stage == "AfterCalculate") && attempt.Stages.Any(stage => stage.Stage == "ForegroundSimulation"),
                "Completed attempt lost stage evidence.");
        foreach (var stage in attempt.Stages)
            Require(Directory.GetFiles(directory, "stage-*-" + stage.Stage + ".json").Length > 0, "Stage diagnostic file is missing.");
    }

    if (expectedAttempts == 2)
    {
        Require(!result.Attempts[0].Passed, "Initial failure disappeared.");
        var expectedRetry = scenario.RetryFails || scenario.BackgroundIncomplete || scenario.BackgroundConfigurationDisabled ? "FailedAgain"
            : scenario.FirstTimesOut || scenario.FirstOpenConnectionError ? "RecoveredAfterError" : "PassedOnRetry";
        Require(result.RetryOutcome == expectedRetry, "Retry outcome is wrong.");
        Require(File.ReadAllBytes(Path.Combine(diagnostics, "original.stcp")).SequenceEqual(scenario.Original), "Original project was not retained.");
        if (scenario.FirstFails)
            Require(result.Results.Children[0].IsGougeError, "Top-level result hid the original operation failure.");
        if (scenario.FirstTimesOut)
        {
            Require(result.Attempts[0].Error?.Contains("TimeoutException") == true, "Native timeout was not recorded.");
            Require(!result.Attempts[0].IsTested && result.Attempts[1].Passed, "A late native result overwrote the timeout or retry.");
            var steps = await File.ReadAllTextAsync(Path.Combine(diagnostics, "attempt-1", "steps.jsonl"));
            Require(!steps.Contains("LateNativeCallback"), "A timed-out call continued changing the saved attempt stage.");
            Require(!result.Attempts[0].Stages.Any(stage => stage.Note == "LateNativeSnapshot"), "A timed-out call continued changing the saved stage snapshots.");
        }
        if ((scenario.FirstTimesOut || scenario.FirstOpenConnectionError) && !scenario.RetryFails && !scenario.BackgroundIncomplete)
            Require(result.Outcome == "PassedOnRetry" && result.IsTested && !TestRetryPolicy.HasFailures(result.Results),
                "Recovered infrastructure error remained a primary CAM operation failure.");
        if (scenario.FirstOpenConnectionError)
            Require(result.Attempts[0].FailureKind == "ConnectionError" && result.Attempts[0].FailedStage == "Open" &&
                result.Attempts[0].Error!.Contains("10057"), "2097 opening failure was not distinguished from calculation.");
        if (scenario.BackgroundIncomplete || scenario.BackgroundConfigurationDisabled)
            Require(result.Outcome == "Inconclusive" && result.Attempts.Where(attempt => attempt.IsTested).All(attempt => !attempt.ValidationComplete && attempt.FailureKind == "ValidationIncomplete"),
                "Incomplete background coverage became a success or an operation error.");
        if (scenario.BackgroundConfigurationDisabled)
            Require(result.Attempts.All(attempt => attempt.Stages.Last(stage => stage.Stage == "BackgroundSimulation").IsComplete &&
                attempt.ValidationNote!.Contains("disabled")), "Disabled configuration was ignored despite apparently complete background status.");
        if (scenario.FirstOpenConnectionError && scenario.RetryFails)
            Require(result.Outcome == "Failed" && result.IsTested && result.Results.Children[0].IsGougeError,
                "Initial opening failure hid an actual operation failure from the retry.");
        if (scenario.FirstOpenConnectionError && scenario.BackgroundIncomplete)
            Require(result.IsTested && result.Results.Children.Count == 1, "Inconclusive retry's available tree was hidden behind the initial opening error.");
        if (scenario.ObservedErrorThenConnectionFailure)
            Require(result.Outcome == "Failed" && result.Attempts[0].FailureKind == "OperationError" &&
                StageResultPolicy.HasObservedErrors(result.Attempts[0].Stages) && result.Attempts[1].Passed,
                "Late IPC failure or clean retry erased an observed background collision.");
        if (scenario.ObservedErrorThenConnectionFailure)
            Require(result.IsTested && result.Results.Children[0].IsCollisionError && !result.Attempts[0].IsTested,
                "Observed stage collision was not selected for the primary operation report.");
    }
    else
    {
        Require(result.Attempts[0].Passed && result.RetryOutcome == "NotNeeded", "Initial success unexpectedly retried.");
        if (scenario.RecorderBlack)
        {
            var attempt = result.Attempts[0];
            Require(result.Outcome == "Passed" && attempt.VideoPath != null &&
                File.Exists(Path.Combine(AppContext.BaseDirectory, attempt.VideoPath)),
                "A black recording changed the CAM outcome or lost its diagnostic video.");
            Require(File.ReadAllBytes(Path.Combine(diagnostics, "original.stcp")).SequenceEqual(scenario.Original),
                "A black recording lost the original project needed for diagnosis.");
            Require(attempt.RecordingError?.Contains("black frames") == true,
                "The published result lost the black recording warning.");
            using var savedAttempt = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(diagnostics, "attempt-1", "attempt.json")));
            Require(savedAttempt.RootElement.GetProperty("RecordingError").GetString() == attempt.RecordingError &&
                savedAttempt.RootElement.GetProperty("VideoPath").GetString() == attempt.VideoPath,
                "The saved attempt lost its black recording warning or retained video path.");
        }
        else
        {
            Require(result.Attempts[0].VideoPath == null && !File.Exists(Path.Combine(diagnostics, "attempt-1", "desktop.mkv")), "Initial success retained its video.");
            Require(!File.Exists(Path.Combine(diagnostics, "original.stcp")), "Initial success retained an unnecessary project copy.");
        }
    }
    Console.WriteLine("PASS " + scenario.Name);
}
Console.WriteLine("All production runner checks passed. No ENCY instance or desktop recording was started.");

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static bool IsAlive(int pid)
{
    try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
    catch (ArgumentException) { return false; }
}
