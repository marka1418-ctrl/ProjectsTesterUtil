using ProjectTestingUtility.Models;

namespace ProjectTestingUtility.Services;

public static class StageResultPolicy
{
    /// <summary>Keep an observed error actionable even if its operation is absent from the final tree.</summary>
    public static bool HasObservedErrors(IEnumerable<TestStageResult> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        return snapshots.Where(snapshot => IsTestStage(snapshot.Stage))
            .Any(snapshot => EnabledNodes(snapshot.Results).Any(HasErrorFlags));
    }

    /// <summary>
    /// Preserve errors observed after recalculation even if a later simulation resets them.
    /// Completion flags always come from the final tree; an unfinished intermediate stage
    /// is not itself an operation failure. The raw trees remain available in stage results.
    /// </summary>
    public static TreeNode MergeObservedErrors(TreeNode finalTree, IEnumerable<TestStageResult> snapshots)
    {
        ArgumentNullException.ThrowIfNull(finalTree);
        ArgumentNullException.ThrowIfNull(snapshots);

        var merged = Clone(finalTree);
        var targets = UniqueEnabledNodes(merged);
        foreach (var snapshot in snapshots)
        {
            // Open contains saved project state, and unknown stages have no established
            // reset boundary. Neither can establish an error in the current test.
            if (!IsTestStage(snapshot.Stage))
                continue;

            foreach (var source in UniqueEnabledNodes(snapshot.Results))
            {
                if (!targets.TryGetValue(source.Key, out var target))
                    continue;
                var observed = source.Value;
                target.IsError |= observed.IsError;
                target.IsCollisionError |= observed.IsCollisionError;
                target.IsCompensationError |= observed.IsCompensationError;
                target.IsGougeError |= observed.IsGougeError;
                target.IsHolderError |= observed.IsHolderError;
                target.IsPlungeError |= observed.IsPlungeError;
                target.IsRapidError |= observed.IsRapidError;
                target.IsToolOverloadError |= observed.IsToolOverloadError;
                target.IsTravelError |= observed.IsTravelError;
                target.IsTurnDirectionError |= observed.IsTurnDirectionError;
            }
        }
        return merged;
    }

    private static bool IsTestStage(string stage) =>
        stage is "AfterCalculate" or "BackgroundSimulation" or "ForegroundSimulation";

    private static bool HasErrorFlags(TreeNode node) =>
        node.IsError || node.IsCollisionError || node.IsCompensationError || node.IsGougeError ||
        node.IsHolderError || node.IsPlungeError || node.IsRapidError || node.IsToolOverloadError ||
        node.IsTravelError || node.IsTurnDirectionError;

    private static Dictionary<string, TreeNode> UniqueEnabledNodes(TreeNode tree)
    {
        // Never use names or positions: operations can have duplicate names and be reordered.
        // Ambiguous IDs are excluded on either side instead of attributing one error twice.
        return EnabledNodes(tree)
            .Where(node => !string.IsNullOrWhiteSpace(node.OperationId))
            .GroupBy(node => node.OperationId!, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
    }

    private static IEnumerable<TreeNode> EnabledNodes(TreeNode node)
    {
        if (!node.IsEnabled) yield break;
        yield return node;
        foreach (var child in node.Children)
            foreach (var descendant in EnabledNodes(child))
                yield return descendant;
    }

    private static TreeNode Clone(TreeNode node)
    {
        var copy = new TreeNode
        {
            OperationId = node.OperationId,
            Name = node.Name,
            IsEnabled = node.IsEnabled,
            IsCalculated = node.IsCalculated,
            IsSimulated = node.IsSimulated,
            IsMachiningResultCalculated = node.IsMachiningResultCalculated,
            IsError = node.IsError,
            IsCollisionError = node.IsCollisionError,
            IsCompensationError = node.IsCompensationError,
            IsGougeError = node.IsGougeError,
            IsHolderError = node.IsHolderError,
            IsPlungeError = node.IsPlungeError,
            IsRapidError = node.IsRapidError,
            IsToolOverloadError = node.IsToolOverloadError,
            IsTravelError = node.IsTravelError,
            IsTurnDirectionError = node.IsTurnDirectionError
        };
        foreach (var child in node.Children)
            copy.AddChild(Clone(child));
        return copy;
    }
}
