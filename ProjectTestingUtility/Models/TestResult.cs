namespace ProjectTestingUtility.Models;

public class TestResult
{
    public bool IsTested { get; set; }
    public TreeNode Results { get; set; } = new();
    public int ProjectId { get; set; }
    public string RunId { get; set; } = "";
    public string RetryOutcome { get; set; } = "NotNeeded";
    public string Outcome { get; set; } = "Inconclusive";
    public string? DiagnosticsDirectory { get; set; }
    public List<TestAttemptResult> Attempts { get; set; } = [];
}

public class TestAttemptResult
{
    public int Attempt { get; set; }
    public bool IsTested { get; set; }
    public bool Passed { get; set; }
    public TreeNode Results { get; set; } = new();
    public string? FailedStage { get; set; }
    public string? Error { get; set; }
    public string FailureKind { get; set; } = "None";
    public List<TestStageResult> Stages { get; set; } = [];
    public bool ValidationComplete { get; set; } = true;
    public string? ValidationNote { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public long DurationMs { get; set; }
    public string? VideoPath { get; set; }
    public string? RecordingError { get; set; }
    public List<string> RecordingWarnings { get; set; } = [];
}
