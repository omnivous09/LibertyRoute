namespace LibertyRoute.Restoration;

public enum DryRunOperationCategory
{
    Tunnel,
    Firewall,
    Route,
    Address,
    Gateway,
    Dns,
    Proxy,
    Adapter,
    Verification,
    JournalClearEligibility
}

public enum DryRunAction
{
    RestoreBaseline,
    ManualReview,
    VerifyOnly
}

public enum DryRunSafetyState
{
    SafeToPlan,
    RequiresOwnershipProof,
    Unsupported,
    Unverifiable,
    ManualReview
}

public sealed record DryRunRestorationOperation(
    DryRunOperationCategory Category,
    DryRunAction Action,
    string TargetIdentity,
    string OriginalValue,
    string CurrentValue,
    string Reason,
    int ExecutionOrder,
    bool OwnershipRequired,
    bool AutomaticExecutionAllowed,
    DryRunSafetyState SafetyState);

public sealed record DryRunRestorationSummary(
    int TotalDifferences,
    int TotalOperations,
    int SafeOperations,
    int ManualReviewOperations,
    int UnsupportedOperations,
    bool IsFullyExecutableInFuture,
    IReadOnlyList<string> BlockingReasons);

public sealed record DryRunRestorationResult(
    IReadOnlyList<DryRunRestorationOperation> Operations,
    DryRunRestorationSummary Summary);

public static class DryRunRestorationExecutor
{
    public static DryRunRestorationResult CreateDryRun(RestorationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var operations = plan.Differences
            .Select(CreateOperation)
            .OrderBy(operation => CategoryOrder(operation.Category))
            .ThenBy(operation => operation.TargetIdentity, StringComparer.Ordinal)
            .ThenBy(operation => operation.Action)
            .ThenBy(operation => operation.OriginalValue, StringComparer.Ordinal)
            .ThenBy(operation => operation.CurrentValue, StringComparer.Ordinal)
            .Select((operation, index) => operation with { ExecutionOrder = index + 1 })
            .ToArray();

        var blockingReasons = operations
            .Where(operation => !operation.AutomaticExecutionAllowed)
            .Select(operation => operation.Reason)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(reason => reason, StringComparer.Ordinal)
            .ToArray();
        var safeCount = operations.Count(operation => operation.SafetyState == DryRunSafetyState.SafeToPlan);
        var manualCount = operations.Count(operation => operation.SafetyState is DryRunSafetyState.ManualReview or DryRunSafetyState.RequiresOwnershipProof);
        var unsupportedCount = operations.Count(operation => operation.SafetyState == DryRunSafetyState.Unsupported);

        return new DryRunRestorationResult(
            operations,
            new DryRunRestorationSummary(
                plan.Differences.Count,
                operations.Length,
                safeCount,
                manualCount,
                unsupportedCount,
                operations.All(operation => operation.AutomaticExecutionAllowed),
                blockingReasons));
    }

    private static DryRunRestorationOperation CreateOperation(RestorationDifference difference)
    {
        if (difference.Classification == DifferenceClassification.Added)
        {
            return new DryRunRestorationOperation(
                CategoryFor(difference.Category),
                DryRunAction.ManualReview,
                difference.Identity,
                difference.OriginalValue,
                difference.CurrentValue,
                "The value appeared after the baseline; it is not automatically deleted. OwnershipRequired.",
                0,
                true,
                false,
                DryRunSafetyState.ManualReview);
        }

        if (difference.Category == RestorationCategory.Adapter && difference.Classification == DifferenceClassification.Missing)
        {
            return new DryRunRestorationOperation(
                DryRunOperationCategory.Adapter,
                DryRunAction.ManualReview,
                difference.Identity,
                difference.OriginalValue,
                difference.CurrentValue,
                "Adapter creation or restoration is unsupported in the read-only harness.",
                0,
                true,
                false,
                DryRunSafetyState.Unsupported);
        }

        if (difference.Classification == DifferenceClassification.Unverifiable)
        {
            return new DryRunRestorationOperation(
                CategoryFor(difference.Category),
                DryRunAction.ManualReview,
                difference.Identity,
                difference.OriginalValue,
                difference.CurrentValue,
                difference.Reason,
                0,
                true,
                false,
                DryRunSafetyState.Unverifiable);
        }

        return new DryRunRestorationOperation(
            CategoryFor(difference.Category),
            DryRunAction.RestoreBaseline,
            difference.Identity,
            difference.OriginalValue,
            difference.CurrentValue,
            $"{difference.Reason} OwnershipRequired.",
            0,
            true,
            false,
                DryRunSafetyState.SafeToPlan);
    }

    private static DryRunOperationCategory CategoryFor(RestorationCategory category) => category switch
    {
        RestorationCategory.Adapter => DryRunOperationCategory.Adapter,
        RestorationCategory.Address => DryRunOperationCategory.Address,
        RestorationCategory.Gateway => DryRunOperationCategory.Gateway,
        RestorationCategory.Dns => DryRunOperationCategory.Dns,
        RestorationCategory.Route => DryRunOperationCategory.Route,
        _ => throw new ArgumentOutOfRangeException(nameof(category))
    };

    private static int CategoryOrder(DryRunOperationCategory category) => category switch
    {
        DryRunOperationCategory.Tunnel => 10,
        DryRunOperationCategory.Firewall => 20,
        DryRunOperationCategory.Route => 30,
        DryRunOperationCategory.Address => 40,
        DryRunOperationCategory.Gateway => 50,
        DryRunOperationCategory.Dns => 60,
        DryRunOperationCategory.Proxy => 70,
        DryRunOperationCategory.Adapter => 80,
        DryRunOperationCategory.Verification => 90,
        DryRunOperationCategory.JournalClearEligibility => 100,
        _ => 110
    };
}
