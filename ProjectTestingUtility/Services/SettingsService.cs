using NLog;
using ProjectTestingUtility.Models;
using System.Text.Json;

namespace ProjectTestingUtility.Services;

public class SettingsService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public Settings Settings { get; set; } = new();

    public void Initialize()
    {
        Logger.Debug("Initialize settings");

        var settingsPath = Path.Combine(AppContext.BaseDirectory, "settings.json");

        if (!File.Exists(settingsPath))
        {
            Logger.Debug($"Settings file not found, create new, path - {settingsPath}");
            try
            {
                File.WriteAllText(settingsPath, JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception e)
            {
                Logger.Error(e, "Failed to create settings file");
            }
        }
        else
        {
            Logger.Debug($"Try read settings from file, path - {settingsPath}");
            try
            {
                var json = File.ReadAllText(settingsPath);
                var settings = JsonSerializer.Deserialize<Settings>(json);
                Settings = settings ?? throw new Exception("Deserialized settings are null");
            }
            catch (Exception e)
            {
                Logger.Error(e, "Failed to deserialize settings file");
                throw;
            }
        }

        Logger.Debug("End settings initialization, values:\n" +
                        $"LaunchCamTimeoutInSeconds - {Settings.LaunchCamTimeoutInSeconds}\n" +
                        $"OpenProjectTimeoutInSeconds - {Settings.OpenProjectTimeoutInSeconds}\n" +
                        $"TestProjectTimeoutInSeconds - {Settings.TestProjectTimeoutInSeconds}\n" +
                        $"KillCamProcessTimeoutInSeconds - {Settings.KillCamProcessTimeoutInSeconds}\n" +
                        $"UseJobMode - {Settings.UseJobMode}\n" +
                        $"ValidateBackgroundSimulation - {Settings.ValidateBackgroundSimulation}\n" +
                        $"EffectiveUseJobMode - {Settings.EffectiveUseJobMode}\n" +
                        $"BackgroundSimulationTimeoutInSeconds - {Settings.BackgroundSimulationTimeoutInSeconds}\n" +
                        $"SimulationSpeedPercent - {Settings.SimulationSpeedPercent}\n" +
                        $"CheckGouges - {Settings.CheckGouges}\n" +
                        $"CheckHolderCollisions - {Settings.CheckHolderCollisions}\n" +
                        $"CheckMachineCollisions - {Settings.CheckMachineCollisions}\n" +
                        $"BreakOnStopCommand - {Settings.BreakOnStopCommand}\n" +
                        $"BreakOnEndOfOperation - {Settings.BreakOnEndOfOperation}\n" +
                        $"BreakOnErrors - {Settings.BreakOnErrors}\n" +
                        $"KillCamProcessTimeoutInSeconds - {Settings.KillCamProcessTimeoutInSeconds}\n" +
                        $"BaseUrl - {Settings.BaseUrl}\n" +
                        $"AuthorizerUrl - {Settings.AuthorizerUrl}\n" +
                        $"IsJiraServer - {Settings.IsJiraServer}\n" +
                        $"CountOfTestedProjectsBeforeRestart - {Settings.CountOfTestedProjectsBeforeRestart}\n" +
                        $"OnlyRecommended - {Settings.OnlyRecommended}\n" +
                        $"OnlyPublic - {Settings.OnlyPublic}\n" +
                        $"MachineType - {Settings.MachineType}\n" +
                        $"MachineName - {Settings.MachineName}\n" +
                        $"Owner - {Settings.Owner}\n" +
                        $"Operations - {string.Join(",", Settings.Operations)}\n" +
                        $"ToolTypes - {string.Join(",", Settings.ToolTypes)}\n" +
                        $"Axes - {string.Join(",", Settings.Axes)}\n" +
                        $"Tags - {string.Join(",", Settings.Tags)}");
    }
}
