using System.Text.Json;
using NLog;

namespace ProjectTestingUtility.Services;

/// <summary>Terminal run status shared with the monitor, including failures before CAM starts.</summary>
public static class RunStatusReporter
{
    public static void Write(string state, string stage, string message)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "run-status.json");
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new
            {
                State = state, Stage = stage, Message = message, UpdatedAtUtc = DateTimeOffset.UtcNow
            }));
            File.Move(temporary, path, true);
        }
        catch (Exception exception)
        {
            LogManager.GetCurrentClassLogger().Warn(exception, "Could not write tester run status");
        }
    }

    public static string FailureMessage(Exception error, string stage) => error switch
    {
        UnauthorizedAccessException when stage == "ProjectService" => "Project service authorization failed. Check Login and Password in Settings.",
        UnauthorizedAccessException => "The tester cannot access a required file or folder. Extract the utility to a writable folder and check the tester log.",
        ArgumentException => "Tester launch arguments are invalid. Check the selected ENCY executable.",
        FileNotFoundException => "A required file was not found. Check the ENCY path and extract the complete utility archive.",
        HttpRequestException => "The project service request failed. Check the server URL, network connection and tester log.",
        TaskCanceledException or TimeoutException => $"The tester timed out during {stage}. See ProjectTesterData/logs/log.log.",
        _ => $"Testing stopped during {stage} ({error.GetType().Name}). See ProjectTesterData/logs/log.log."
    };
}
