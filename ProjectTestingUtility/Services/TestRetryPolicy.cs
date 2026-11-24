using ProjectTestingUtility.Models;

namespace ProjectTestingUtility.Services;

public static class TestRetryPolicy
{
    // Same criteria as the monitor: unfinished calculation/simulation or any error flag.
    public static bool HasFailures(TreeNode node) => node.IsEnabled && (
        !node.IsCalculated || !node.IsSimulated || node.IsError || node.IsCollisionError ||
        node.IsCompensationError || node.IsGougeError || node.IsHolderError ||
        node.IsPlungeError || node.IsRapidError || node.IsToolOverloadError ||
        node.IsTravelError || node.IsTurnDirectionError || node.Children.Any(HasFailures));

    public static async Task<TestResult> ExecuteAsync(Func<int, Task<TestAttemptResult>> attempt)
    {
        var first = await InvokeAttemptAsync(attempt, 1);
        var result = new TestResult
        {
            Attempts = [first]
        };
        if (!first.Passed)
        {
            var second = await InvokeAttemptAsync(attempt, 2);
            result.Attempts.Add(second);
            result.RetryOutcome = second.Passed ? "PassedOnRetry" : "FailedAgain";
            if (!first.IsTested && second.Passed && !HasOperationFailure(first))
            {
                // A recovered IPC/startup failure is not a failed CAM calculation.
                result.RetryOutcome = "RecoveredAfterError";
            }
        }
        var failedAttempt = result.Attempts.FirstOrDefault(HasOperationFailure);
        result.Outcome = failedAttempt is not null
            ? "Failed" : result.Attempts[^1].Passed
                ? result.Attempts.Count == 1 ? "Passed" : "PassedOnRetry"
                : "Inconclusive";

        // The primary tree must explain the overall outcome. An earlier IPC failure
        // cannot hide actual operation errors found by the retry, and a later success
        // cannot erase operation errors already observed by the initial attempt.
        var effectiveAttempt = failedAttempt ?? result.Attempts.LastOrDefault(candidate =>
            HasResultTree(candidate.Results) || candidate.Stages.Any(stage => HasResultTree(stage.Results))) ?? result.Attempts[^1];
        (result.Results, result.IsTested) = EffectiveResult(effectiveAttempt, failedAttempt is not null);
        return result;
    }

    private static bool HasOperationFailure(TestAttemptResult attempt) =>
        attempt.FailureKind == "OperationError" || StageResultPolicy.HasObservedErrors(attempt.Stages) ||
        attempt.IsTested && HasFailures(attempt.Results);

    private static (TreeNode Results, bool IsTested) EffectiveResult(TestAttemptResult attempt, bool operationFailure)
    {
        var errorStage = attempt.Stages.FirstOrDefault(stage => StageResultPolicy.HasObservedErrors([stage]));
        if (operationFailure && errorStage is not null)
        {
            if (HasResultTree(attempt.Results))
            {
                var merged = StageResultPolicy.MergeObservedErrors(attempt.Results, attempt.Stages);
                if (StageResultPolicy.HasObservedErrors([new TestStageResult { Stage = "ForegroundSimulation", Results = merged }]))
                    return (merged, true);
            }
            // An exception may prevent a final snapshot, or the errored operation may
            // be absent from that tree. Publish the actual stage evidence instead.
            return (StageResultPolicy.MergeObservedErrors(errorStage.Results, attempt.Stages), true);
        }
        if (HasResultTree(attempt.Results)) return (attempt.Results, attempt.IsTested);
        var latestStage = attempt.Stages.LastOrDefault(stage => HasResultTree(stage.Results));
        return (latestStage?.Results ?? attempt.Results, attempt.IsTested);
    }

    private static bool HasResultTree(TreeNode tree) => !string.IsNullOrWhiteSpace(tree.Name) ||
        !string.IsNullOrWhiteSpace(tree.OperationId) || tree.Children.Count > 0 || tree.IsCalculated || tree.IsSimulated;

    private static async Task<TestAttemptResult> InvokeAttemptAsync(Func<int, Task<TestAttemptResult>> attempt, int number)
    {
        var started = DateTimeOffset.UtcNow;
        try { return await attempt(number); }
        catch (Exception exception)
        {
            // Preparation/diagnostic errors must not discard an earlier completed attempt.
            return new TestAttemptResult
            {
                Attempt = number,
                StartedAtUtc = started,
                DurationMs = (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds,
                FailedStage = "PrepareAttempt",
                FailureKind = "PreparationError",
                Error = $"Attempt could not start ({exception.GetType().Name}). See the tester log."
            };
        }
    }
}
