using System.Security.Cryptography;
using ProjectTestingUtility.Services;

var root = Path.Combine(Path.GetTempPath(), "ProjectsTesterUtil-BackgroundConfigurationChecks", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Path.Combine(root, "Log", "RotatedLogs"));
var configurationPath = Path.Combine(root, "Synthetic.cfg");
var logPath = Path.Combine(root, "Log", "RotatedLogs", "ency-123.log");
var checks = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + description);
    checks++;
    Console.WriteLine("PASS: " + description);
}
string Settings(string enabled = "True", string machine = "-1", string holder = "1", string hidden = "0") =>
    $"<Setup><Machining_AutoCalcMachiningResult>{enabled}</Machining_AutoCalcMachiningResult>" +
    $"<Machining_BackgroundCollisionDetection>{machine}</Machining_BackgroundCollisionDetection>" +
    $"<Machining_BackgroundCheckHolder>{holder}</Machining_BackgroundCheckHolder>" +
    $"<Machining_BackgroundCDInvisible>{hidden}</Machining_BackgroundCDInvisible></Setup>";

Check(!BackgroundSimulationConfiguration.Read(configurationPath).Available, "Missing configuration is unavailable");
File.WriteAllText(configurationPath, Settings());
var originalHash = SHA256.HashData(File.ReadAllBytes(configurationPath));
var read = BackgroundSimulationConfiguration.ReadForCamLog(logPath);
Check(read.Available && read.Enabled == true && read.MachineCollisions == true && read.HolderCollisions == true && read.HiddenNodes == false,
    "Real persisted key names and Delphi boolean forms are recognized");
Check(read.SourcePath == configurationPath, "Configuration is located from the native log profile");
Check(originalHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(configurationPath))), "Reading never modifies persisted CAM settings");
Check(read.Note.Contains("may differ") && read.Note.Contains("do not prove"), "Snapshot explicitly limits runtime and completion claims");
foreach (var value in new[] { "true", "True", " 1 ", "-1" })
{
    File.WriteAllText(configurationPath, Settings(machine: value));
    Check(BackgroundSimulationConfiguration.Read(configurationPath).MachineCollisions == true, "True representation is supported: " + value);
}
foreach (var value in new[] { "False", "false", " 0 " })
{
    File.WriteAllText(configurationPath, Settings(machine: value));
    Check(BackgroundSimulationConfiguration.Read(configurationPath).MachineCollisions == false, "False representation is supported: " + value);
}
foreach (var value in new[] { "2", "-2", "", "unknown" })
{
    File.WriteAllText(configurationPath, Settings(machine: value));
    var unknown = BackgroundSimulationConfiguration.Read(configurationPath);
    Check(unknown.Available && unknown.MachineCollisions == null && unknown.Note.Contains("invalid"),
        "An invalid field stays unknown rather than becoming false: " + value);
}
File.WriteAllText(configurationPath, "<Setup><Machining_BackgroundCollisionDetection>True</Machining_BackgroundCollisionDetection>" +
    "<Machining_BackgroundCollisionDetection>False</Machining_BackgroundCollisionDetection></Setup>");
Check(!BackgroundSimulationConfiguration.Read(configurationPath).Available, "Duplicate keys cannot select an arbitrary background policy");
File.WriteAllText(configurationPath, "<Setup><Unrelated>secret-sentinel</Unrelated></Setup>");
var unrelated = BackgroundSimulationConfiguration.Read(configurationPath);
Check(!unrelated.Available && !unrelated.Note.Contains("secret-sentinel"), "Only allowed keys are retained");
File.WriteAllText(configurationPath, "<Setup><invalid secret-sentinel");
var malformed = BackgroundSimulationConfiguration.Read(configurationPath);
Check(!malformed.Available && !malformed.Note.Contains("secret-sentinel"), "Malformed XML cannot leak configuration contents through errors");
File.WriteAllText(configurationPath, "<!DOCTYPE Setup [<!ENTITY leak SYSTEM 'file:///missing-private-file'>]><Setup>&leak;</Setup>");
Check(!BackgroundSimulationConfiguration.Read(configurationPath).Available, "External entity and DTD processing is rejected");
File.WriteAllText(configurationPath, Settings());
File.WriteAllText(Path.Combine(root, "Other.cfg"), Settings(machine: "False"));
var ambiguous = BackgroundSimulationConfiguration.ReadForCamLog(logPath);
Check(!ambiguous.Available && ambiguous.Note.Contains("Multiple"), "Multiple profile configuration files are not guessed");
Check(!BackgroundSimulationConfiguration.ReadForCamLog("relative/Log/ency.log").Available, "Relative native log path is rejected");
Check(!BackgroundSimulationConfiguration.ReadForCamLog(Path.Combine(root, "Elsewhere", "ency.log")).Available,
    "A native path without a Log profile marker is not guessed");
var sdkProfile = Path.Combine(root, "sdk-profile");
var configFolder = Path.Combine(sdkProfile, "Config");
Directory.CreateDirectory(configFolder);
var sdkConfig = Path.Combine(sdkProfile, "Synthetic.cfg");
File.WriteAllText(sdkConfig, Settings());
Check(BackgroundSimulationConfiguration.ReadForProfileFolder(sdkProfile).SourcePath == sdkConfig,
    "SDK profile folder selects its single direct configuration");
Check(BackgroundSimulationConfiguration.ReadForProfileFolder(configFolder).SourcePath == sdkConfig,
    "SDK Config child falls back to the profile's configuration");
Check(BackgroundSimulationConfiguration.ReadForProfileFolder(configFolder + Path.DirectorySeparatorChar).SourcePath == sdkConfig,
    "SDK Config folder with a trailing separator resolves the same profile");
var otherFolder = Path.Combine(sdkProfile, "Other");
Directory.CreateDirectory(otherFolder);
Check(!BackgroundSimulationConfiguration.ReadForProfileFolder(otherFolder).Available,
    "An arbitrary child folder cannot cause parent configuration guesses");
var directConfig = Path.Combine(configFolder, "Direct.cfg");
File.WriteAllText(directConfig, Settings(machine: "False"));
var direct = BackgroundSimulationConfiguration.ReadForProfileFolder(configFolder);
Check(direct.SourcePath == directConfig && direct.MachineCollisions == false,
    "A direct SDK Config configuration takes precedence over the parent");
File.WriteAllText(Path.Combine(configFolder, "Second.cfg"), Settings());
Check(!BackgroundSimulationConfiguration.ReadForProfileFolder(configFolder).Available,
    "Ambiguous direct Config files do not fall back to the parent");
Check(!BackgroundSimulationConfiguration.ReadForProfileFolder("Config").Available,
    "Relative SDK Config paths remain unavailable until CAM unfolds them");
Console.WriteLine($"PASS: {checks} background configuration checks. Fixtures: {root}");
