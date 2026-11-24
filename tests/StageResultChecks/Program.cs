using System.Text.Json;
using ProjectTestingUtility.Models;
using ProjectTestingUtility.Services;

var checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + message);
    checks++;
    Console.WriteLine("PASS: " + message);
}
static TreeNode Node(string id, string name = "Same name") => new()
{
    OperationId = id, Name = name, IsCalculated = true, IsSimulated = true, IsMachiningResultCalculated = true
};
static TreeNode Group(params TreeNode[] children)
{
    var root = Node("root", "Root");
    foreach (var child in children) root.AddChild(child);
    return root;
}
static TestStageResult Snapshot(string stage, TreeNode tree, bool complete = true) =>
    new() { Stage = stage, Results = tree, IsComplete = complete };

var flags = new[] { "IsError", "IsCollisionError", "IsCompensationError", "IsGougeError", "IsHolderError",
    "IsPlungeError", "IsRapidError", "IsToolOverloadError", "IsTravelError", "IsTurnDirectionError" };
foreach (var flag in flags)
{
    var earlier = Group(Node("a"), Node("b"));
    typeof(TreeNode).GetProperty(flag)!.SetValue(earlier.Children[0], true);
    earlier.Children[0].IsCalculated = false;
    earlier.Children[0].IsSimulated = false;
    earlier.Children[0].IsMachiningResultCalculated = false;
    var final = Group(Node("b"), Node("a"));
    var merged = StageResultPolicy.MergeObservedErrors(final, [Snapshot("AfterCalculate", earlier, false)]);
    Check((bool)typeof(TreeNode).GetProperty(flag)!.GetValue(merged.Children[1])! &&
        !(bool)typeof(TreeNode).GetProperty(flag)!.GetValue(merged.Children[0])!, flag + " follows stable ID despite duplicate names and reordering");
    Check(merged.Children[1].IsCalculated && merged.Children[1].IsSimulated && merged.Children[1].IsMachiningResultCalculated,
        flag + " does not carry transient incomplete flags into the final result");
    Check(!(bool)typeof(TreeNode).GetProperty(flag)!.GetValue(final.Children[1])!, flag + " leaves raw final snapshot unchanged");
    Check(ReferenceEquals(merged.Children[1].Parent, merged) && !ReferenceEquals(merged.Children[1], final.Children[1]),
        flag + " result is an independent tree with correct parent links");
}

foreach (var stage in new[] { "AfterCalculate", "BackgroundSimulation", "ForegroundSimulation" })
{
    var earlier = Node("a");
    earlier.IsCollisionError = true;
    Check(StageResultPolicy.MergeObservedErrors(Node("a"), [Snapshot(stage, earlier, false)]).IsCollisionError,
        stage + " retains observed collision evidence even when completeness was not established");
}
foreach (var stage in new[] { "Open", "Unknown", "", "SimulationReset" })
{
    var stale = Node("a");
    stale.IsCollisionError = true;
    Check(!StageResultPolicy.MergeObservedErrors(Node("a"), [Snapshot(stage, stale)]).IsCollisionError,
        stage + " cannot introduce stale project collision evidence");
}

var disabledObserved = Group(Node("a"));
disabledObserved.IsEnabled = false;
disabledObserved.Children[0].IsCollisionError = true;
Check(!StageResultPolicy.MergeObservedErrors(Group(Node("a")), [Snapshot("BackgroundSimulation", disabledObserved)]).Children[0].IsCollisionError,
    "A disabled source branch does not contribute errors from enabled descendants");
var enabledObserved = Group(Node("a"));
enabledObserved.Children[0].IsCollisionError = true;
var disabledFinal = Group(Node("a"));
disabledFinal.IsEnabled = false;
Check(!StageResultPolicy.MergeObservedErrors(disabledFinal, [Snapshot("BackgroundSimulation", enabledObserved)]).Children[0].IsCollisionError,
    "A disabled final branch does not receive observed errors");

var absentId = Node("");
absentId.IsError = true;
Check(!StageResultPolicy.MergeObservedErrors(Node(""), [Snapshot("AfterCalculate", absentId)]).IsError,
    "Missing IDs never fall back to matching duplicate operation names");
var duplicateSource = Group(Node("a"), Node("a"));
duplicateSource.Children[0].IsError = true;
Check(!StageResultPolicy.MergeObservedErrors(Group(Node("a")), [Snapshot("AfterCalculate", duplicateSource)]).Children[0].IsError,
    "Ambiguous source IDs cannot assign an error to another operation");
var uniqueSource = Group(Node("a"));
uniqueSource.Children[0].IsError = true;
Check(StageResultPolicy.MergeObservedErrors(Group(Node("a"), Node("a")), [Snapshot("AfterCalculate", uniqueSource)]).Children.All(node => !node.IsError),
    "Ambiguous final IDs cannot multiply one observed error");
var differentId = Node("different");
differentId.IsError = true;
Check(!StageResultPolicy.MergeObservedErrors(Node("a"), [Snapshot("AfterCalculate", differentId)]).IsError,
    "Unmatched operation IDs cannot contaminate final results");
Check(StageResultPolicy.HasObservedErrors([Snapshot("AfterCalculate", differentId)]),
    "An unmatched operation still makes observed stage errors available to overall assessment");
Check(!StageResultPolicy.HasObservedErrors([Snapshot("Open", differentId)]),
    "Overall stage assessment ignores saved errors from the opened project");
Check(!StageResultPolicy.HasObservedErrors([Snapshot("BackgroundSimulation", disabledObserved)]),
    "Overall stage assessment skips disabled branches");

var finalIncomplete = Node("a");
finalIncomplete.IsCalculated = false;
finalIncomplete.IsSimulated = false;
finalIncomplete.IsHolderError = true;
var finalPreserved = StageResultPolicy.MergeObservedErrors(finalIncomplete, [Snapshot("ForegroundSimulation", Node("a"))]);
Check(!finalPreserved.IsCalculated && !finalPreserved.IsSimulated && finalPreserved.IsHolderError,
    "Final incompleteness and final errors survive clean snapshots");
var groupError = Group(Node("a"));
groupError.IsError = true;
Check(StageResultPolicy.MergeObservedErrors(Group(Node("a")), [Snapshot("AfterCalculate", groupError)]).IsError,
    "Aggregate group errors are retained by their own stable ID");
var collision = Node("a");
collision.IsCollisionError = true;
var gouge = Node("a");
gouge.IsGougeError = true;
var combined = StageResultPolicy.MergeObservedErrors(Node("a"),
    [Snapshot("AfterCalculate", collision), Snapshot("BackgroundSimulation", gouge), Snapshot("ForegroundSimulation", Node("a"))]);
Check(combined.IsCollisionError && combined.IsGougeError, "A later clean stage cannot erase errors recorded in earlier stages");
Check(collision.IsCollisionError && !collision.IsGougeError && gouge.IsGougeError && !gouge.IsCollisionError,
    "Combining stages does not mutate the raw evidence");
var incompleteClean = Node("a");
incompleteClean.IsSimulated = false;
var observation = Snapshot("BackgroundSimulation", incompleteClean, false);
Check(!StageResultPolicy.HasObservedErrors([observation]),
    "An incomplete stage without error flags is uncertainty rather than an operation error");
observation.Note = "Background collision completion cannot be confirmed by this SDK";
var encoded = JsonSerializer.Serialize(observation);
var decoded = JsonSerializer.Deserialize<TestStageResult>(encoded)!;
Check(!decoded.IsComplete && decoded.Note == observation.Note && decoded.Stage == observation.Stage,
    "Stage uncertainty and explanation survive serialization independently of errors");
Console.WriteLine($"PASS: {checks} stage-result checks");
