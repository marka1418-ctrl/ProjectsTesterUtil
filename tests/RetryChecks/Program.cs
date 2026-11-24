using System.Text.Json;
using ProjectTestingUtility.Models;
using ProjectTestingUtility.Services;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static TreeNode Good() => new() { Name = "Operation", IsCalculated = true, IsSimulated = true };

static TestAttemptResult Attempt(int number, TreeNode? tree = null, string? error = null) => new()
{
    Attempt = number,
    IsTested = error is null,
    Results = tree ?? new TreeNode(),
    Passed = error is null && tree is not null && !TestRetryPolicy.HasFailures(tree),
    Error = error,
    FailureKind = error is null ? "None" : "ConnectionError",
    FailedStage = error is null ? null : "Open",
    VideoPath = $"diagnostics/run/42/attempt-{number}/desktop.mkv"
};

var calls = new List<int>();
var passed = await TestRetryPolicy.ExecuteAsync(number =>
{
    calls.Add(number);
    return Task.FromResult(Attempt(number, Good()));
});
Check(calls.SequenceEqual([1]) && passed.RetryOutcome == "NotNeeded", "Passing test must run once");

var flags = new[] { "IsError", "IsCollisionError", "IsCompensationError", "IsGougeError", "IsHolderError",
    "IsPlungeError", "IsRapidError", "IsToolOverloadError", "IsTravelError", "IsTurnDirectionError" };
foreach (var field in flags.Concat(["IsCalculated", "IsSimulated"]))
{
    var root = Good();
    var leaf = Good();
    typeof(TreeNode).GetProperty(field)!.SetValue(leaf, field is not ("IsCalculated" or "IsSimulated"));
    root.AddChild(leaf);
    calls.Clear();
    var recovered = await TestRetryPolicy.ExecuteAsync(number =>
    {
        calls.Add(number);
        return Task.FromResult(Attempt(number, number == 1 ? root : Good()));
    });
    Check(calls.SequenceEqual([1, 2]), $"Nested {field} must trigger exactly one retry");
    Check(recovered.RetryOutcome == "PassedOnRetry", "Recovery must be explicit");
    Check(ReferenceEquals(recovered.Results, root) && TestRetryPolicy.HasFailures(recovered.Results),
        "Retry must not erase the initial failure from monitor category");
    var encoded = JsonSerializer.Serialize(recovered);
    var decoded = JsonSerializer.Deserialize<TestResult>(encoded)!;
    Check(decoded.Attempts.Count == 2 && decoded.Attempts[0].VideoPath != decoded.Attempts[1].VideoPath,
        "Both attempt records and distinct video paths must survive serialization");
}

calls.Clear();
var failed = await TestRetryPolicy.ExecuteAsync(number =>
{
    calls.Add(number);
    return Task.FromResult(Attempt(number, error: "IPC timeout"));
});
Check(calls.SequenceEqual([1, 2]) && failed.RetryOutcome == "FailedAgain" && !failed.IsTested,
    "Exceptions must get one retry only, not an infinite retry loop");
Check(failed.Attempts.All(x => x.Error == "IPC timeout" && x.FailedStage == "Open"), "Keep both exception reasons");
Check(failed.Outcome == "Inconclusive", "Repeated IPC errors do not assert a CAM operation defect");

var recoveredConnection = await TestRetryPolicy.ExecuteAsync(number => Task.FromResult(number == 1
    ? Attempt(number, error: "Error reading answer : socket error (WSA 10057)")
    : Attempt(number, Good())));
Check(recoveredConnection.Outcome == "PassedOnRetry" && recoveredConnection.RetryOutcome == "RecoveredAfterError" &&
    recoveredConnection.IsTested && !TestRetryPolicy.HasFailures(recoveredConnection.Results),
    "2097: successful retry replaces inconclusive connection failure in the primary monitor category");
Check(!recoveredConnection.Attempts[0].IsTested && recoveredConnection.Attempts[0].FailureKind == "ConnectionError" &&
    recoveredConnection.Attempts[0].Error!.Contains("10057"), "2097: retry recovery retains the original connection evidence");

calls.Clear();
var incomplete = await TestRetryPolicy.ExecuteAsync(number =>
{
    calls.Add(number);
    var attempt = Attempt(number, Good());
    attempt.Passed = false;
    attempt.ValidationComplete = false;
    attempt.ValidationNote = "Background checks did not complete";
    attempt.FailureKind = "ValidationIncomplete";
    attempt.Stages.Add(new TestStageResult { Stage = "BackgroundSimulation", Results = Good(), IsComplete = false });
    return Task.FromResult(attempt);
});
Check(calls.SequenceEqual([1, 2]) && incomplete.Outcome == "Inconclusive" && incomplete.Attempts.All(a => !a.ValidationComplete),
    "Incomplete background validation runs twice and remains inconclusive despite a clean foreground tree");

var backgroundError = Good();
backgroundError.OperationId = "takeover";
backgroundError.IsCollisionError = true;
foreach (var lateFailureKind in new[] { "ConnectionError", "ApplicationExited", "Timeout" })
{
    var observedBeforeFailure = await TestRetryPolicy.ExecuteAsync(number =>
    {
        var attempt = number == 1 ? Attempt(number, error: "Late IPC failure") : Attempt(number, Good());
        if (number == 1)
        {
            attempt.FailureKind = lateFailureKind;
            attempt.Stages.Add(new TestStageResult { Stage = "BackgroundSimulation", Results = backgroundError, IsComplete = false });
        }
        return Task.FromResult(attempt);
    });
    Check(observedBeforeFailure.Outcome == "Failed" && observedBeforeFailure.IsTested && observedBeforeFailure.Results.IsCollisionError &&
        !observedBeforeFailure.Attempts[0].IsTested && observedBeforeFailure.RetryOutcome == "PassedOnRetry",
        "Observed collision is published from stage evidence despite late " + lateFailureKind + " and a clean retry");
}

var retryOperationTree = Good();
retryOperationTree.IsGougeError = true;
var connectionThenOperationFailure = await TestRetryPolicy.ExecuteAsync(number => Task.FromResult(number == 1
    ? Attempt(number, error: "Opening connection failed") : Attempt(number, retryOperationTree)));
Check(connectionThenOperationFailure.Outcome == "Failed" && connectionThenOperationFailure.IsTested &&
    ReferenceEquals(connectionThenOperationFailure.Results, retryOperationTree),
    "An initial infrastructure failure cannot hide the retry's actual operation failure");

var latestIncompleteTree = Good();
latestIncompleteTree.Name = "Latest incomplete validation tree";
var connectionThenIncomplete = await TestRetryPolicy.ExecuteAsync(number =>
{
    if (number == 1) return Task.FromResult(Attempt(number, error: "Opening connection failed"));
    var attempt = Attempt(number, latestIncompleteTree);
    attempt.Passed = false;
    attempt.ValidationComplete = false;
    attempt.FailureKind = "ValidationIncomplete";
    return Task.FromResult(attempt);
});
Check(connectionThenIncomplete.Outcome == "Inconclusive" && ReferenceEquals(connectionThenIncomplete.Results, latestIncompleteTree),
    "An inconclusive retry exposes its latest available tree instead of an empty opening failure");

var orphanError = Good();
orphanError.OperationId = "removed-operation";
orphanError.IsCollisionError = true;
var orphanedStageFailure = await TestRetryPolicy.ExecuteAsync(number =>
{
    var attempt = Attempt(number, Good());
    if (number == 1)
    {
        attempt.Passed = false;
        attempt.Stages.Add(new TestStageResult { Stage = "BackgroundSimulation", Results = orphanError });
    }
    return Task.FromResult(attempt);
});
Check(orphanedStageFailure.Outcome == "Failed" && orphanedStageFailure.Results.OperationId == "removed-operation" && orphanedStageFailure.Results.IsCollisionError,
    "An errored operation absent from the final tree remains visible through the stage tree");
var incompleteReplacement = Good();
incompleteReplacement.OperationId = "replacement-operation";
incompleteReplacement.IsSimulated = false;
var incompleteReplacementFailure = await TestRetryPolicy.ExecuteAsync(number =>
{
    var attempt = Attempt(number, number == 1 ? incompleteReplacement : Good());
    if (number == 1)
        attempt.Stages.Add(new TestStageResult { Stage = "BackgroundSimulation", Results = orphanError });
    return Task.FromResult(attempt);
});
Check(incompleteReplacementFailure.Results.OperationId == "removed-operation" && incompleteReplacementFailure.Results.IsCollisionError,
    "An incomplete unrelated final operation cannot hide a collision from an earlier stage");

var disabled = Good();
var disabledBranch = new TreeNode { IsEnabled = false };
disabledBranch.AddChild(new TreeNode { IsError = true });
disabled.AddChild(disabledBranch);
Check(!TestRetryPolicy.HasFailures(disabled), "Disabled branches cannot create operation failures");
var defaults = new Settings();
Check(!defaults.ValidateBackgroundSimulation && defaults.EffectiveUseJobMode, "Background validation is off by default and preserves configured Job Mode");
defaults.ValidateBackgroundSimulation = true;
Check(!defaults.EffectiveUseJobMode, "Explicit background validation uses a GUI-capable CAM launch");

var unavailable = await TestRetryPolicy.ExecuteAsync(number => number == 1
    ? Task.FromResult(Attempt(1, error: "initial failure"))
    : throw new IOException("Diagnostic disk full"));
Check(unavailable.Attempts.Count == 2 && unavailable.Attempts[0].Error == "initial failure" &&
    unavailable.Attempts[1].FailedStage == "PrepareAttempt", "Retry preparation failure must not lose the first attempt");

var noBackgroundResult = Good();
noBackgroundResult.IsMachiningResultCalculated = false;
Check(!TestRetryPolicy.HasFailures(noBackgroundResult), "Match existing monitor criteria, not a new failure definition");
var recorderFailed = await TestRetryPolicy.ExecuteAsync(number =>
{
    var attempt = Attempt(number, Good());
    attempt.RecordingError = "FFmpeg not found";
    return Task.FromResult(attempt);
});
Check(recorderFailed.Attempts.Count == 1, "Recording failure must not change the CAM test result");

var directory = Path.Combine(Path.GetTempPath(), "ProjectTester-RetryChecks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
var projectPath = Path.Combine(directory, "original.stcp");
File.WriteAllText(projectPath, "known-project-bytes");
var settings = new Settings { Login = "test-user-secret", Password = "test-password-secret", CamlmAuthKey = "test-key-secret" };
var diagnostics = new AttemptDiagnostics(directory, settings);
diagnostics.SaveMetadata(Environment.ProcessPath!, Environment.ProcessId, projectPath);
diagnostics.Step("Open");
diagnostics.SaveResult(failed.Attempts[0]);
var metadata = File.ReadAllText(Path.Combine(directory, "run.json"));
Check(!metadata.Contains(settings.Login) && !metadata.Contains(settings.Password) && !metadata.Contains(settings.CamlmAuthKey),
    "Metadata must omit credentials");
Check(diagnostics.Redact($"{settings.Login} {settings.Password} {settings.CamlmAuthKey}") == "[redacted] [redacted] [redacted]",
    "Exceptions and copied logs must redact configured credentials");
Check(JsonDocument.Parse(metadata).RootElement.GetProperty("ProjectSha256").GetString() == AttemptDiagnostics.HashFile(projectPath),
    "Metadata must identify the exact input bytes");
File.WriteAllText(projectPath, "CAM changed the working copy");
diagnostics.SaveMetadata(Environment.ProcessPath!, Environment.ProcessId, projectPath);
Check(File.ReadAllText(Path.Combine(directory, "run.json")) == metadata,
    "Failure handling must not replace the original input hash with a modified working file hash");
Check(!File.Exists(Path.Combine(directory, "attempt.json.tmp")), "Publish complete JSON atomically");
Console.WriteLine("PASS: retries, 2097 recovery, stage errors, incomplete validation, disabled branches, recorder independence, credentials and metadata.");
Console.WriteLine("Artifacts: " + directory);
