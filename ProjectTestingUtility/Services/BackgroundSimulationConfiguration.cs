using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace ProjectTestingUtility.Services;

/// <summary>A read-only observation of persisted settings, not a runtime configuration guarantee.</summary>
public sealed class BackgroundSimulationConfiguration
{
    private const string Limitations = "Persisted settings may differ from the running CAM configuration and do not prove that background collision checking completed.";

    public bool? Enabled { get; private set; }
    public bool? MachineCollisions { get; private set; }
    public bool? HolderCollisions { get; private set; }
    public bool? HiddenNodes { get; private set; }
    public bool Available { get; private set; }
    public string? SourcePath { get; private set; }
    public string Note { get; private set; } = Limitations;

    public static BackgroundSimulationConfiguration Read(string configurationPath)
    {
        var snapshot = new BackgroundSimulationConfiguration();
        try
        {
            if (string.IsNullOrWhiteSpace(configurationPath) || !File.Exists(configurationPath))
                return Unavailable("The persisted background configuration file was not found.");
            snapshot.SourcePath = Path.GetFullPath(configurationPath);
            using var reader = XmlReader.Create(configurationPath, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 16 * 1024 * 1024
            });
            var document = XDocument.Load(reader);
            snapshot.Enabled = ReadBoolean(document, "Machining_AutoCalcMachiningResult");
            snapshot.MachineCollisions = ReadBoolean(document, "Machining_BackgroundCollisionDetection");
            snapshot.HolderCollisions = ReadBoolean(document, "Machining_BackgroundCheckHolder");
            snapshot.HiddenNodes = ReadBoolean(document, "Machining_BackgroundCDInvisible");
            snapshot.Available = snapshot.Enabled.HasValue || snapshot.MachineCollisions.HasValue ||
                snapshot.HolderCollisions.HasValue || snapshot.HiddenNodes.HasValue;
            if (!snapshot.Available)
                snapshot.Note = "No unambiguous background settings were found. " + Limitations;
            else if (snapshot.Enabled is null || snapshot.MachineCollisions is null ||
                snapshot.HolderCollisions is null || snapshot.HiddenNodes is null)
                snapshot.Note = "Some background settings are missing, duplicated or invalid. " + Limitations;
            return snapshot;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or XmlException or ArgumentException or NotSupportedException)
        {
            // Do not echo parser details or arbitrary configuration content into diagnostics.
            snapshot.Note = $"The persisted background configuration could not be read ({exception.GetType().Name}). " + Limitations;
            return snapshot;
        }
    }

    public static BackgroundSimulationConfiguration ReadForCamLog(string nativeLogPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(nativeLogPath) || !Path.IsPathFullyQualified(nativeLogPath))
                return Unavailable("An absolute native CAM log path is required to locate its persisted configuration.");

            // Both Log/ency.log and Log/RotatedLogs/ency-123.log identify the same profile.
            // Derive the profile from CAM's reported path rather than product-name guesses.
            var directory = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(nativeLogPath))!);
            while (!directory.Name.Equals("Log", StringComparison.OrdinalIgnoreCase))
            {
                if (directory.Parent is null)
                    return Unavailable("The native CAM log path does not identify a Log profile directory.");
                directory = directory.Parent;
            }
            var profile = directory.Parent;
            if (profile is null || !profile.Exists)
                return Unavailable("The CAM profile directory was not found.");
            return ReadForProfileFolder(profile.FullName);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Unavailable($"The CAM profile could not be located ({exception.GetType().Name}).");
        }
    }

    public static BackgroundSimulationConfiguration ReadForProfileFolder(string folder)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder))
                return Unavailable("An absolute CAM profile or Config folder is required to locate persisted settings.");
            var profile = new DirectoryInfo(Path.GetFullPath(folder));
            if (!profile.Exists)
                return Unavailable("The CAM profile directory was not found.");
            var candidates = profile.EnumerateFiles("*.cfg", SearchOption.TopDirectoryOnly).Take(2).ToArray();
            // CAM Paths.ConfigFolder may point to a Config child while the persisted
            // application configuration lives beside it. No other parent is guessed.
            if (candidates.Length == 0 && profile.Name.Equals("Config", StringComparison.OrdinalIgnoreCase) && profile.Parent is not null)
                candidates = profile.Parent.EnumerateFiles("*.cfg", SearchOption.TopDirectoryOnly).Take(2).ToArray();
            if (candidates.Length != 1)
                return Unavailable(candidates.Length == 0
                    ? "No persisted configuration file was found in the CAM profile."
                    : "Multiple configuration files were found in the CAM profile; none was selected.");
            return Read(candidates[0].FullName);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Unavailable($"The CAM profile could not be located ({exception.GetType().Name}).");
        }
    }

    private static bool? ReadBoolean(XDocument document, string name)
    {
        var values = document.Descendants().Where(element => element.Name.LocalName == name).Take(2).ToArray();
        if (values.Length != 1) return null;
        var text = values[0].Value.Trim();
        if (bool.TryParse(text, out var boolean)) return boolean;
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            return number switch { -1 or 1 => true, 0 => false, _ => null };
        return null;
    }

    private static BackgroundSimulationConfiguration Unavailable(string explanation) =>
        new() { Note = explanation + " " + Limitations };
}
