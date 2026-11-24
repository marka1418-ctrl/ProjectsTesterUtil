using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using NLog;
using ProjectTestingUtility.Models;

namespace ProjectTestingUtility.Services;

public sealed class AttemptDiagnostics
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _gate = new();
    private readonly Settings _settings;
    public string DirectoryPath { get; }

    public AttemptDiagnostics(string directoryPath, Settings settings)
    {
        DirectoryPath = directoryPath;
        _settings = settings;
        TrySave(() => Directory.CreateDirectory(directoryPath));
    }

    public void Step(string stage)
    {
        lock (_gate)
            TrySave(() => File.AppendAllText(Path.Combine(DirectoryPath, "steps.jsonl"),
                JsonSerializer.Serialize(new { TimeUtc = DateTimeOffset.UtcNow, Stage = stage }) + Environment.NewLine));
    }

    public string Redact(string message)
    {
        foreach (var secret in new[] { _settings.Password, _settings.CamlmAuthKey, _settings.Login })
            if (!string.IsNullOrEmpty(secret)) message = message.Replace(secret, "[redacted]", StringComparison.Ordinal);
        return message;
    }

    public void SaveResult(TestAttemptResult attempt) =>
        TrySave(() => WriteJsonAtomic(Path.Combine(DirectoryPath, "attempt.json"), attempt));

    public void SaveStage(TestStageResult stage, int number) => TrySave(() =>
        WriteJsonAtomic(Path.Combine(DirectoryPath, $"stage-{number:D2}-{stage.Stage}.json"), stage));

    public void SaveActivation(CamWindowActivationResult activation, string phase) => TrySave(() =>
        WriteJsonAtomic(Path.Combine(DirectoryPath, $"window-{phase}.json"), activation));

    public void SaveBackgroundConfiguration(string? nativeLogPath) => TrySave(() =>
        WriteJsonAtomic(Path.Combine(DirectoryPath, "background-configuration.json"),
            BackgroundSimulationConfiguration.ReadForCamLog(nativeLogPath ?? "")));
    public void SaveBackgroundConfiguration(BackgroundSimulationConfiguration configuration) => TrySave(() =>
        WriteJsonAtomic(Path.Combine(DirectoryPath, "background-configuration.json"), configuration));

    public void SaveNativeLog(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        TrySave(() =>
        {
            var actual = path;
            if (!File.Exists(actual))
            {
                var directory = Path.GetDirectoryName(actual);
                var rotated = Path.Combine(directory ?? "", "RotatedLogs");
                if (Directory.Exists(rotated)) actual = Directory.GetFiles(rotated, Path.GetFileNameWithoutExtension(path) + "~*.log")
                    .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault() ?? path;
            }
            if (!File.Exists(actual)) return;
            using var stream = new FileStream(actual, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            using var writer = new StreamWriter(Path.Combine(DirectoryPath, "cam.log"));
            string? line;
            while ((line = reader.ReadLine()) != null)
                writer.WriteLine(line.Contains("token", StringComparison.OrdinalIgnoreCase) || line.Contains("password", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("authorization", StringComparison.OrdinalIgnoreCase) ? "[authentication data omitted]" : Redact(line));
        });
    }

    public void SaveLogs()
    {
        // Capture before the next project can rotate the single shared tester log.
        foreach (var relative in new[] { Path.Combine("logs", "log.log"), "log.txt" })
        {
            TrySave(() =>
            {
                var source = Path.Combine(AppContext.BaseDirectory, relative);
                if (File.Exists(source))
                    File.WriteAllText(Path.Combine(DirectoryPath, relative == "log.txt" ? "monitor.log" : "tester.log"),
                        Redact(File.ReadAllText(source)));
            });
        }
    }

    public void SaveMetadata(string camPath, int? processId, string projectPath)
    {
        // Keep the input hash captured before CAM opens or potentially modifies the file.
        if (File.Exists(Path.Combine(DirectoryPath, "run.json"))) return;
        TrySave(() => WriteJsonAtomic(Path.Combine(DirectoryPath, "run.json"), new
        {
            CamPath = camPath,
            CamVersion = FileVersionInfo.GetVersionInfo(camPath).FileVersion,
            CamProcessId = processId,
            TesterVersion = typeof(AttemptDiagnostics).Assembly.GetName().Version?.ToString(),
            ProjectPath = projectPath,
            ProjectSha256 = File.Exists(projectPath) ? HashFile(projectPath) : null,
            WorkingDirectory = Environment.CurrentDirectory,
            LaunchArguments = _settings.EffectiveUseJobMode ? "/JOB_MODE" : "",
            // Explicit allowlist. Never serialize Settings as a whole here.
            Settings = new
            {
                _settings.UseJobMode, _settings.LaunchCamTimeoutInSeconds,
                _settings.ValidateBackgroundSimulation, _settings.EffectiveUseJobMode, _settings.BackgroundSimulationTimeoutInSeconds,
                _settings.OpenProjectTimeoutInSeconds, _settings.TestProjectTimeoutInSeconds,
                _settings.KillCamProcessTimeoutInSeconds, _settings.CountOfTestedProjectsBeforeRestart,
                _settings.SimulationSpeedPercent, _settings.CheckGouges, _settings.CheckHolderCollisions,
                _settings.CheckMachineCollisions, _settings.BreakOnStopCommand,
                _settings.BreakOnEndOfOperation, _settings.BreakOnErrors
            }
        }));
    }

    public static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public static void WriteJsonAtomic<T>(string path, T value)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, JsonOptions));
        File.Move(temporary, path, true);
    }

    public static void TrySave(Action action)
    {
        try { action(); }
        catch (Exception e) { Logger.Warn(e, "Could not save diagnostic artifact"); }
    }
}
