using System.Text.Json.Serialization;

namespace ProjectTestingUtility.Models;

public class Settings
{
    public int LaunchCamTimeoutInSeconds { get; set; } = 20;
    public int OpenProjectTimeoutInSeconds { get; set; } = 900;
    public int TestProjectTimeoutInSeconds { get; set; } = 3600;
    public int KillCamProcessTimeoutInSeconds { get; set; } = 300;
    public bool UseJobMode { get; set; } = true;
    public bool ValidateBackgroundSimulation { get; set; } = false;
    public int BackgroundSimulationTimeoutInSeconds { get; set; } = 60;
    public bool KeepSuccessfulRecordings { get; set; }
    [JsonIgnore]
    public bool EffectiveUseJobMode => UseJobMode && !ValidateBackgroundSimulation;
    public int SimulationSpeedPercent { get; set; } = 100;
    public bool CheckGouges { get; set; } = true;
    public bool CheckHolderCollisions { get; set; } = true;
    public bool CheckMachineCollisions { get; set; } = true;
    public bool BreakOnStopCommand { get; set; }
    public bool BreakOnEndOfOperation { get; set; }
    public bool BreakOnErrors { get; set; }
    public string BinDataVersion { get; set; } = "2999";
    public string BaseUrl { get; set; } = "https://webservices.sprutcam.com";
    [JsonIgnore]
    public string AuthorizerUrl => BaseUrl is "http://javasecond.office.sprut.ru:8089" ? "http://javasecond.office.sprut.ru:8094" : BaseUrl;
    [JsonIgnore]
    public bool IsJiraServer => BaseUrl is "http://javasecond.office.sprut.ru:8089";
    public int CountOfTestedProjectsBeforeRestart { get; set; } = 1;
    public string Login { get; set; } = "";
    public string Password { get; set; } = "";
    public string CamlmAuthKey { get; set; } = "";
    public bool OnlyRecommended { get; set; } = true;
    public bool OnlyPublic { get; set; } = false;
    public string MachineType { get; set; } = "";
    public string MachineName { get; set; } = "";
    public int Owner { get; set; } = 0;
    public string[] Operations { get; set; } = [];
    public string[] ToolTypes { get; set; } = [];
    public string[] Axes { get; set; } = [];
    public string[] Tags { get; set; } = [];
}
