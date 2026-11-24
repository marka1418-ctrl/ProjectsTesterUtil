using System.IO;
using System.Text.Json;

namespace ProjectTesterMonitor;

internal sealed class TesterRunStatus
{
    public string State { get; set; } = "";
    public string Stage { get; set; } = "";
    public string Message { get; set; } = "";
    public DateTimeOffset UpdatedAtUtc { get; set; }

    public bool IsTerminal => State is "Failed" or "Completed";

    public static TesterRunStatus? Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var status = JsonSerializer.Deserialize<TesterRunStatus>(File.ReadAllText(path));
            return status?.State is "Starting" or "Running" or "Completed" or "Failed" ? status : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (JsonException) { return null; }
    }

    public static string? GetSettingsError(string settingsPath)
    {
        try
        {
            if (!File.Exists(settingsPath))
                return "Settings were not found. Open Settings and enter your Login and Password before running tests.";
            using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
            var settings = document.RootElement;
            if (settings.ValueKind != JsonValueKind.Object)
                return "The settings file is invalid. Open Settings and save the configuration again.";
            foreach (var name in new[] { "Login", "Password" })
            {
                if (!settings.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(value.GetString()))
                    return "Login and Password are required to download test projects. Open Settings and enter your credentials.";
            }
            return null;
        }
        catch (JsonException)
        {
            // Parser exceptions can contain setting values. Keep credentials out of the UI/logs.
            return "The settings file contains invalid JSON. Open Settings and save the configuration again.";
        }
        catch (IOException)
        {
            return "The settings file could not be read. Check access to the data folder and open Settings.";
        }
        catch (UnauthorizedAccessException)
        {
            return "The settings file could not be read. Check access to the data folder and open Settings.";
        }
    }

    public static int ResumeProjectId(int minimumProjectId, bool retryLastProject) =>
        retryLastProject && minimumProjectId < int.MaxValue ? minimumProjectId + 1 : minimumProjectId;
}
