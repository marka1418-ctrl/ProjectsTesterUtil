using System.Text.Json;
using ProjectTesterMonitor;

var directory = Path.Combine(Path.GetTempPath(), "ProjectsTesterUtil-RunStatusChecks", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
var statusPath = Path.Combine(directory, "run-status.json");
var settingsPath = Path.Combine(directory, "settings.json");
var checks = 0;
void Require(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + description);
    checks++;
    Console.WriteLine("PASS: " + description);
}

Require(TesterRunStatus.Read(statusPath) == null, "Missing status retains crash recovery behavior");
foreach (var state in new[] { "Starting", "Running", "Failed", "Completed" })
{
    File.WriteAllText(statusPath, JsonSerializer.Serialize(new
    {
        State = state,
        Stage = "GetProjectsCount",
        Message = "Synthetic status message",
        UpdatedAtUtc = DateTimeOffset.UtcNow
    }));
    var status = TesterRunStatus.Read(statusPath);
    Require(status?.State == state && status.Stage == "GetProjectsCount" && status.Message == "Synthetic status message",
        state + " status is read using the tester contract");
    Require(status!.IsTerminal == (state is "Failed" or "Completed"), state + " terminal classification is correct");
}
foreach (var invalid in new[] { "{", "null", "[]", "{\"State\":\"Unknown\"}" })
{
    File.WriteAllText(statusPath, invalid);
    Require(TesterRunStatus.Read(statusPath) == null, "Invalid or unknown status is ignored");
}

Require(TesterRunStatus.GetSettingsError(settingsPath) != null, "Missing settings block launch");
foreach (var value in new[] { "{}", "{\"Login\":\"user\"}", "{\"Password\":\"value\"}",
    "{\"Login\":\" \",\"Password\":\"value\"}", "{\"Login\":42,\"Password\":\"value\"}", "[]" })
{
    File.WriteAllText(settingsPath, value);
    Require(TesterRunStatus.GetSettingsError(settingsPath) != null, "Missing, blank or invalid credential fields block launch");
}
var sentinel = "synthetic-secret-that-must-never-appear-in-errors";
File.WriteAllText(settingsPath, JsonSerializer.Serialize(new { Login = "synthetic-user", Password = sentinel }));
Require(TesterRunStatus.GetSettingsError(settingsPath) == null, "Configured credentials allow launch without importing or displaying them");
File.WriteAllText(settingsPath, "{\"Login\":\"" + sentinel + "\",\"Password\":" + sentinel + "}");
var error = TesterRunStatus.GetSettingsError(settingsPath);
Require(error != null && !error.Contains(sentinel), "Malformed settings do not leak credential text through parser errors");
Require(TesterRunStatus.ResumeProjectId(int.MaxValue, true) == int.MaxValue, "Empty result set preserves int.MaxValue without overflow");
Require(TesterRunStatus.ResumeProjectId(123, true) == 124, "Manual rerun includes the last completed project");
Require(TesterRunStatus.ResumeProjectId(123, false) == 123, "Crash recovery retains its existing resume boundary");
Console.WriteLine($"PASS: {checks} run-status and settings checks. Fixtures: {directory}");
