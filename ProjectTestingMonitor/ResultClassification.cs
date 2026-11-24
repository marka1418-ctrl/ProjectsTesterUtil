using Newtonsoft.Json.Linq;

namespace ProjectTesterMonitor;

internal sealed record ResultClassification(string Category, string Folder, string Status, string Outcome,
    JToken? Results, bool IsTested, bool RecoveredAfterError)
{
    internal static readonly string[] ErrorFields = ["IsError", "IsCollisionError", "IsCompensationError",
        "IsGougeError", "IsHolderError", "IsPlungeError", "IsRapidError", "IsToolOverloadError",
        "IsTravelError", "IsTurnDirectionError"];

    internal static bool IsTrue(JToken? value) => bool.TryParse(value?.ToString(), out var result) && result;
    internal static bool IsFalse(JToken? value) => bool.TryParse(value?.ToString(), out var result) && !result;
    internal static bool IsEnabled(JToken? node) => node is JObject && !IsFalse(node["IsEnabled"]);
    internal static IEnumerable<JToken> EnabledNodes(JToken? node)
    {
        if (!IsEnabled(node)) yield break;
        yield return node!;
        foreach (var child in node!["Children"] as JArray ?? [])
            foreach (var descendant in EnabledNodes(child)) yield return descendant;
    }
    internal static bool HasObservedErrors(JToken? node) =>
        EnabledNodes(node).Any(n => ErrorFields.Any(field => IsTrue(n[field])));
    internal static bool IsTestStage(JToken stage) => stage["Stage"]?.ToString() is
        "AfterCalculate" or "BackgroundSimulation" or "ForegroundSimulation";
    internal static bool HasStageErrors(JToken? attempt) =>
        (attempt?["Stages"] as JArray ?? []).Any(stage => IsTestStage(stage) && HasObservedErrors(stage["Results"]));

    internal static ResultClassification Read(JObject json)
    {
        var attempts = (json["Attempts"] as JArray)?.OfType<JObject>().ToArray() ?? [];
        var first = attempts.FirstOrDefault();
        var last = attempts.LastOrDefault();
        var declared = json["Outcome"]?.ToString();
        var earlierCamFailure = attempts.Take(Math.Max(0, attempts.Length - 1)).Any(a =>
            HasObservedErrors(a["Results"]) || HasStageErrors(a) ||
            a["FailureKind"]?.ToString() == "OperationError" ||
            (IsTrue(a["IsTested"]) && a["ValidationComplete"] == null && !IsTrue(a["Passed"])));
        var recovered = attempts.Length > 1 && last != null && IsTrue(last["Passed"]) &&
            IsTrue(last["IsTested"]) && !IsFalse(last["ValidationComplete"]) &&
            !earlierCamFailure && first != null && !IsTrue(first["Passed"]) &&
            (declared == null || declared == "PassedOnRetry" || json["RetryOutcome"]?.ToString() == "RecoveredAfterError");
        var effective = recovered ? last! : json;
        var tree = effective["Results"];
        var tested = IsTrue(effective["IsTested"]);
        var nodes = EnabledNodes(tree).ToArray();
        var calcFailure = tested && nodes.Any(n => IsFalse(n["IsCalculated"]));
        var observed = HasObservedErrors(tree) || attempts.Any(HasStageErrors) ||
            (earlierCamFailure && attempts.Any(a => HasObservedErrors(a["Results"])));
        var lastBackground = (last?["Stages"] as JArray ?? [])
            .LastOrDefault(stage => stage["Stage"]?.ToString() == "BackgroundSimulation");
        var incomplete = declared == "Inconclusive" || !tested || tree is not JObject ||
            string.IsNullOrWhiteSpace(tree["Name"]?.ToString()) ||
            (!recovered && last != null && (IsFalse(last["ValidationComplete"]) ||
                (declared == null && last["ValidationComplete"] == null && IsFalse(lastBackground?["IsComplete"]))));
        var simulationFailure = tested && !incomplete && nodes.Any(n => IsFalse(n["IsSimulated"]));
        if (calcFailure)
            return new("CalcFail", "calc_fail", "Calculation failed", "Failed", tree, tested, false);
        if (observed || simulationFailure || earlierCamFailure || declared == "Failed")
            return new("SimFail", "sim_fail", "Simulation failed", "Failed", tree, tested, false);
        if (incomplete)
            return new("CalcFail", "calc_fail", tested ? "Check incomplete" : "Test interrupted", "Inconclusive", tree, tested, false);
        return new("Good", "goods", recovered ? "Passed on retry (opening/communication recovered)" : "Good",
            recovered ? "PassedOnRetry" : "Passed", tree, tested, recovered);
    }
}
