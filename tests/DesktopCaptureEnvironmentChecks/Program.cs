using System.Diagnostics;
using System.Text.Json;
using ProjectTestingUtility.Services;

// Query only metadata. Never capture the desktop or change its lock/connection state.
var snapshot = DesktopCaptureEnvironment.Capture();
Require(snapshot.WindowsSupported == OperatingSystem.IsWindows(), "Incorrect platform support result.");
if (OperatingSystem.IsWindows())
{
    using var process = Process.GetCurrentProcess();
    Require(snapshot.ProcessSessionId == process.SessionId, "The native query described another session.");
    Require(snapshot.SessionState != null || snapshot.QueryErrors.Any(error => error.Query.StartsWith("SessionState")),
        "A failed session query was silently converted into a known state.");
    Require(snapshot.InputDesktopAccessible != false || snapshot.QueryErrors.Any(error => error.Query == "InputDesktop"),
        "An inaccessible input desktop must retain the native query failure.");
    Require(snapshot.VirtualDesktop == null || snapshot.VirtualDesktop is { Width: > 0, Height: > 0 },
        "Display coordinates were published without a capturable size.");
}

var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
var restored = JsonSerializer.Deserialize<DesktopCaptureEnvironmentSnapshot>(json)!;
Require(restored.SessionLocked == snapshot.SessionLocked && restored.IsRemoteSession == snapshot.IsRemoteSession &&
    restored.InputDesktopAccessible == snapshot.InputDesktopAccessible && restored.VirtualDesktop == snapshot.VirtualDesktop,
    "JSON diagnostics lost unknown state or display coordinates.");
Console.WriteLine(json);
Console.WriteLine("PASS live session metadata and JSON serialization. No desktop was captured.");

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
