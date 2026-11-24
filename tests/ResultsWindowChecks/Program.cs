using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Newtonsoft.Json.Linq;
using ProjectTesterMonitor;

internal static class Program
{
    private const string IsolatedArgument = "--isolated-results-window-checks";
    private static int _checks;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length != 2 || args[0] != IsolatedArgument)
                return StartIsolated();

            var expectedBase = Path.GetFullPath(args[1]).TrimEnd(Path.DirectorySeparatorChar);
            var actualBase = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
            var temporaryRoot = Path.Combine(Path.GetTempPath(), "ProjectsTesterUtil-ResultsWindowChecks");
            Require(actualBase == expectedBase && actualBase.StartsWith(temporaryRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase), "Harness runs only in its unique temporary output folder");
            RunChecks();
            Console.WriteLine($"PASS: {_checks} ResultsWindow checks. Isolated fixtures: {AppPaths.Root}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static int StartIsolated()
    {
        // AppPaths uses the executable directory. Run a private copy of this harness so
        // no fixtures can reach the monitor's real ProjectTesterData folder.
        var output = Path.Combine(Path.GetTempPath(), "ProjectsTesterUtil-ResultsWindowChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory))
            File.Copy(file, Path.Combine(output, Path.GetFileName(file)));

        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("No harness executable path");
        var isDotnetHost = Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase);
        var start = new ProcessStartInfo(isDotnetHost ? executable : Path.Combine(output, Path.GetFileName(executable)))
        {
            WorkingDirectory = output,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (isDotnetHost)
            start.ArgumentList.Add(Path.Combine(output, Path.GetFileName(Assembly.GetExecutingAssembly().Location)));
        start.ArgumentList.Add(IsolatedArgument);
        start.ArgumentList.Add(output);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start isolated harness");
        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();
        if (!child.WaitForExit(45000))
        {
            child.Kill(entireProcessTree: true);
            throw new TimeoutException("Isolated ResultsWindow checks exceeded 45 seconds");
        }
        Console.Write(stdout.GetAwaiter().GetResult());
        Console.Error.Write(stderr.GetAwaiter().GetResult());
        return child.ExitCode;
    }

    private static void RunChecks()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var root = AppPaths.Root;
        Require(root.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase), "Production AppPaths stays inside isolated output");
        foreach (var category in new[] { "goods", "calc_fail", "sim_fail" })
            Directory.CreateDirectory(Path.Combine(root, "results", category));
        Directory.CreateDirectory(Path.Combine(root, "projects"));
        var diagnostics = Path.Combine(root, "diagnostics", "synthetic-run", "101");
        Directory.CreateDirectory(Path.Combine(diagnostics, "attempt-1"));
        Directory.CreateDirectory(Path.Combine(diagnostics, "attempt-2"));
        var videos = new[] { Path.Combine(diagnostics, "attempt-1", "desktop.mkv"), Path.Combine(diagnostics, "attempt-2", "desktop.mkv") };
        foreach (var video in videos)
            File.WriteAllText(video, "Synthetic path fixture; not a playable video.");

        var retried = Result("PassedOnRetry", true, Node("Original operation", false),
            Attempt(1, false, true, Node("Original operation", false), Relative(videos[0])),
            Attempt(2, true, true, Node("Retry operation", true), Relative(videos[1])));
        retried["DiagnosticsDirectory"] = Relative(diagnostics);
        var retryPath = Save("calc_fail", "101", retried);
        var failedAgain = Result("FailedAgain", true, Node("Simulation failure", true, true),
            Attempt(1, false, true, Node("Simulation failure", true, true), null),
            Attempt(2, false, false, Node("Unknown", false), null, "Open", "Synthetic open timeout", "FFmpeg is missing"));
        var twicePath = Save("sim_fail", "102", failedAgain);
        var legacyPath = Save("calc_fail", "103", new JObject
        {
            ["IsTested"] = false,
            ["Results"] = Node("Unknown", false)
        });
        var goodPath = Save("goods", "104", new JObject
        {
            ["IsTested"] = true,
            ["Results"] = Node("Legacy passed operation", true)
        });

        var window = new ResultsWindow("All", null, false);
        try
        {
            var list = (ListBox)window.FindName("ResultsListBox");
            var details = (StackPanel)window.FindName("DetailsPanel");
            Require(list.Items.Count == 4, "All four result fixtures load");
            var originalItem = list.Items.Cast<object>().Single(item => Property(item, "FilePath") == retryPath);
            Require(Property(originalItem, "Category") == "CalcFail", "Passing retry retains initial failure category");
            Require(Property(originalItem, "Status").Contains("Passed on retry"), "Result list highlights a passing retry");
            list.SelectedItem = originalItem;
            var texts = Texts(details);
            Require(texts.Contains("Passed on retry (initial failure retained)"), "Passing retry banner explains retained failure");
            Require(texts.Contains("Attempt 1 - Failed") && texts.Contains("Attempt 2 - Passed"), "Both attempt outcomes appear");
            Require(texts.Contains("Results - Original operation") && texts.Contains("Results - Retry operation"), "Both operation trees appear");
            var buttons = Descendants(details).OfType<Button>().Select(button => button.Content?.ToString()).ToArray();
            Require(buttons.Contains("Open diagnostic folder"), "Existing diagnostic folder is linked");
            Require(buttons.Contains("Open attempt 1 video") && buttons.Contains("Open attempt 2 video"), "Both existing videos are linked");

            Invoke(window, "LoadResultDetails", twicePath);
            texts = Texts(details);
            Require(texts.Contains("Failed twice with the same settings"), "Repeated failure banner appears");
            Require(texts.Contains("Synthetic open timeout") && texts.Contains("Open"), "Attempt error and failed stage appear");
            Require(texts.Contains("Screen recording: FFmpeg is missing"), "Recording failure is visible independently");
            Require(!texts.Any(text => text.Contains("Unknown")), "Incomplete attempt does not show a default operation tree");

            Invoke(window, "LoadResultDetails", legacyPath);
            texts = Texts(details);
            Require(texts.Contains("The test did not complete; no operation results were collected."), "Legacy incomplete result is explained");
            Require(!texts.Any(text => text.Contains("Unknown") || text.Contains("Calculated")), "Legacy incomplete result has no misleading operation status");
            Invoke(window, "LoadResultDetails", goodPath);
            Require(Texts(details).Contains("Results - Legacy passed operation"), "Legacy completed result still displays its tree");

            retried["RetryOutcome"] = "FailedAgain";
            retried["Attempts"]![1]!["Passed"] = false;
            Save("calc_fail", "101", retried);
            File.SetLastWriteTimeUtc(retryPath, DateTime.UtcNow.AddSeconds(2));
            Invoke(window, "AutoRefreshTimer_Tick", null, EventArgs.Empty);
            var refreshedItem = list.Items.Cast<object>().Single(item => Property(item, "FilePath") == retryPath);
            Require(Property(refreshedItem, "Status").Contains("Failed twice"), "Auto-refresh detects changed content at the same filename");
            Require(Texts(details).Contains("Failed twice with the same settings"), "Auto-refresh replaces selected result details");

            Require(Resolve(Relative(videos[0]), false) == videos[0], "Relative existing MKV is accepted");
            Require(Resolve(Relative(diagnostics), true) == diagnostics, "Relative existing diagnostic folder is accepted");
            Require(Resolve(videos[0], false) == null, "Absolute video path is rejected");
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "outside.mkv"), "Outside-root fixture");
            Require(Resolve("../outside.mkv", false) == null, "Traversal outside AppPaths.Root is rejected");
            Require(Resolve("https://example.test/video.mkv", false) == null, "URL is rejected");
            Require(Resolve(Relative(videos[0]) + ":alternate", false) == null, "Alternate data stream path is rejected");
            Require(Resolve("diagnostics/missing.mkv", false) == null, "Missing video is rejected");
            var executableFixture = Path.Combine(diagnostics, "payload.exe");
            File.WriteAllText(executableFixture, "Never executable");
            Require(Resolve(Relative(executableFixture), false) == null, "Non-video executable is rejected");
            var mp4Fixture = Path.Combine(diagnostics, "desktop.mp4");
            File.WriteAllText(mp4Fixture, "Synthetic MP4 path fixture");
            Require(Resolve(Relative(mp4Fixture), false) == mp4Fixture, "Relative existing MP4 is accepted");

            File.Delete(videos[1]);
            Invoke(window, "LoadResultDetails", retryPath);
            texts = Texts(details);
            Require(texts.Contains("The video is unavailable in the local diagnostic folder."), "Missing video receives an explanation");
            Require(!Descendants(details).OfType<Button>().Any(button => button.Content?.ToString() == "Open attempt 2 video"), "Missing video has no launch button");
            Require(!texts.Any(text => text.StartsWith("Error loading details:")), "Synthetic result details render without an exception");

            CheckClassificationAndStages(window, details);
        }
        finally
        {
            window.Close();
            application.Shutdown();
        }
    }

    private static void CheckClassificationAndStages(ResultsWindow window, StackPanel details)
    {
        // Shape of the supplied 2097 packet: first IPC Open failed, identical retry passed.
        var first = Attempt(1, false, false, Node("Unknown", false), null, "Open", "socket error (WSA 10057)");
        var second = Attempt(2, true, true, Node("HYUNDAI_WIA_L2000SY", true), null);
        var legacy = Result("PassedOnRetry", false, Node("Unknown", false), first, second);
        var classified = ResultClassification.Read(legacy);
        Require(classified.Category == "Good" && classified.RecoveredAfterError, "Legacy 2097 IPC failure with passing retry is recovered");
        Require(classified.Results?["Name"]?.ToString() == "HYUNDAI_WIA_L2000SY", "Legacy recovery selects the successful retry tree");
        var path = Save("calc_fail", "2097", legacy);
        Invoke(window, "LoadResultDetails", path);
        var text = Texts(details);
        Require(text.Contains("Passed on retry. The first opening/communication error is retained below."), "Recovered retry banner distinguishes infrastructure from operation failure");
        Require(text.Contains("socket error (WSA 10057)") && !text.Any(t => t.Contains("Unknown")), "Recovery retains the first IPC exception without Unknown operations");
        Invoke(window, "LoadResultsInternal");
        var list = (ListBox)window.FindName("ResultsListBox");
        var item = list.Items.Cast<object>().Single(i => Property(i, "FilePath") == path);
        Require(Property(item, "Category") == "Good", "Legacy recovery list classification ignores its old calc_fail folder");

        var current = (JObject)legacy.DeepClone();
        current["Outcome"] = "PassedOnRetry";
        current["RetryOutcome"] = "RecoveredAfterError";
        current["IsTested"] = true;
        current["Results"] = second["Results"]!.DeepClone();
        current["Attempts"]![0]!["FailureKind"] = "CommunicationError";
        Require(ResultClassification.Read(current).Category == "Good", "Current recovered infrastructure DTO is good with retry history");
        current["Attempts"]![1]!["RecordingWarnings"] = new JArray("Project tab activation could not be confirmed");
        path = Save("goods", "2099", current);
        Invoke(window, "LoadResultDetails", path);
        Require(Texts(details).Contains("Recording warning: Project tab activation could not be confirmed"), "Capture activation warning is visible independently of passed test");

        var incomplete = Result("NotNeeded", true, Node("Clean final tree", true),
            Attempt(1, false, true, Node("Clean final tree", true), null));
        incomplete["Outcome"] = "Inconclusive";
        incomplete["Attempts"]![0]!["ValidationComplete"] = false;
        incomplete["Attempts"]![0]!["ValidationNote"] = "Background validation did not finish";
        var stageTree = Node("Background operation", true);
        stageTree["IsSimulated"] = false;
        incomplete["Attempts"]![0]!["Stages"] = new JArray(new JObject
        {
            ["Stage"] = "BackgroundSimulation", ["IsComplete"] = false,
            ["Note"] = "No finished background result", ["Results"] = stageTree
        });
        classified = ResultClassification.Read(incomplete);
        Require(classified.Folder != "goods" && classified.Status == "Check incomplete", "Inconclusive clean final tree cannot enter goods");
        path = Save("calc_fail", "2098", incomplete);
        Invoke(window, "LoadResultDetails", path);
        var expander = Descendants(details).OfType<Expander>().Single();
        Require(!expander.IsExpanded && expander.Header?.ToString() == "Stage: BackgroundSimulation", "Stage snapshots are collapsed by default");
        Require(Texts(expander).Any(t => t.Contains("simulated=No") && t.Contains("no reported errors")), "Background simulated=false is raw status, not an invented collision failure");

        var collisionStage = (JObject)incomplete.DeepClone();
        collisionStage["Attempts"]![0]!["Stages"]![0]!["Results"]!["IsCollisionError"] = true;
        classified = ResultClassification.Read(collisionStage);
        Require(classified.Category == "SimFail" && classified.Outcome == "Failed", "Observed background collision survives a clean final tree");

        var disabled = Node("Disabled group", false, true);
        disabled["IsEnabled"] = false;
        disabled["Children"] = new JArray(Node("Disabled descendant", false, true));
        var enabledTree = Node("Enabled root", true);
        enabledTree["Children"] = new JArray(disabled);
        Require(ResultClassification.Read(Result("NotNeeded", true, enabledTree)).Category == "Good", "Disabled branch and descendants do not fail classification");

        var realFailure = Result("PassedOnRetry", true, Node("Real collision", true, true),
            Attempt(1, false, true, Node("Real collision", true, true), null), second);
        realFailure["Outcome"] = "Failed";
        Require(ResultClassification.Read(realFailure).Category == "SimFail", "Actual CAM failure stays failed after a clean retry");

        var initialSnapshot = Result("NotNeeded", true, Node("Completed tree", true),
            Attempt(1, true, true, Node("Completed tree", true), null));
        initialSnapshot["Outcome"] = "Passed";
        initialSnapshot["Attempts"]![0]!["ValidationComplete"] = true;
        initialSnapshot["Attempts"]![0]!["Stages"] = new JArray(
            new JObject { ["Stage"] = "Open", ["IsComplete"] = true, ["Results"] = Node("Stale saved collision", true, true) },
            new JObject { ["Stage"] = "AfterCalculate", ["IsComplete"] = false, ["Results"] = Node("Interim calculation", true) },
            new JObject { ["Stage"] = "BackgroundSimulation", ["IsComplete"] = true, ["Results"] = Node("Background completed", true) });
        Require(ResultClassification.Read(initialSnapshot).Category == "Good", "Stale Open collision and interim AfterCalculate completion do not override completed validation");

        var realPacket = @"C:\Users\Marat\Downloads\2097\2097";
        if (File.Exists(Path.Combine(realPacket, "attempt-1", "attempt.json")) && File.Exists(Path.Combine(realPacket, "attempt-2", "attempt.json")))
        {
            var firstActual = JObject.Parse(File.ReadAllText(Path.Combine(realPacket, "attempt-1", "attempt.json")));
            var secondActual = JObject.Parse(File.ReadAllText(Path.Combine(realPacket, "attempt-2", "attempt.json")));
            var packetResult = Result("PassedOnRetry", false, (JObject)firstActual["Results"]!.DeepClone(), firstActual, secondActual);
            classified = ResultClassification.Read(packetResult);
            Require(classified.RecoveredAfterError && classified.Category == "Good", "Actual supplied 2097 attempts normalize to passed after opening/communication recovery");
        }
    }

    private static JObject Result(string outcome, bool isTested, JObject tree, params JObject[] attempts) => new()
    {
        ["ProjectId"] = 101,
        ["RunId"] = "synthetic-run",
        ["IsTested"] = isTested,
        ["Results"] = tree,
        ["RetryOutcome"] = outcome,
        ["Attempts"] = new JArray(attempts)
    };

    private static JObject Attempt(int number, bool passed, bool isTested, JObject tree, string? video,
        string? stage = null, string? error = null, string? recordingError = null) => new()
    {
        ["Attempt"] = number,
        ["Passed"] = passed,
        ["IsTested"] = isTested,
        ["Results"] = tree,
        ["StartedAtUtc"] = "2026-09-21T12:00:00Z",
        ["DurationMs"] = 1234,
        ["VideoPath"] = video,
        ["FailedStage"] = stage,
        ["Error"] = error,
        ["RecordingError"] = recordingError
    };

    private static JObject Node(string name, bool calculated, bool error = false) => new()
    {
        ["Name"] = name,
        ["IsCalculated"] = calculated,
        ["IsSimulated"] = calculated,
        ["IsMachiningResultCalculated"] = calculated,
        ["IsError"] = error,
        ["Children"] = new JArray()
    };

    private static string Save(string category, string projectId, JObject result)
    {
        var path = Path.Combine(AppPaths.Root, "results", category, projectId + ".json");
        File.WriteAllText(path, result.ToString());
        return path;
    }

    private static string Relative(string path) => Path.GetRelativePath(AppPaths.Root, path);

    private static string Property(object item, string name) =>
        item.GetType().GetProperty(name)!.GetValue(item)?.ToString() ?? "";

    private static object? Invoke(ResultsWindow window, string method, params object?[] arguments) =>
        typeof(ResultsWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, arguments);

    private static string? Resolve(string path, bool directory) =>
        (string?)typeof(ResultsWindow).GetMethod("ResolveArtifactPath", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [path, directory]);

    private static string[] Texts(DependencyObject root) =>
        Descendants(root).OfType<TextBlock>().Select(text => text.Text).ToArray();

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var descendant in Descendants(child))
                yield return descendant;
    }

    private static void Require(bool condition, string description)
    {
        if (!condition)
            throw new InvalidOperationException("FAIL: " + description);
        _checks++;
        Console.WriteLine("PASS: " + description);
    }
}
