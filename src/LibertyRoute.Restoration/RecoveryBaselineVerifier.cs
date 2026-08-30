using LibertyRoute.Core;

namespace LibertyRoute.Restoration;

public sealed record RecoveryBaselineVerification(bool IsVerified, string Reason);

public interface IRecoveryBaselineVerifier
{
    RecoveryBaselineVerification Verify(
        NetworkStateSnapshot authorizedBaseline,
        NetworkStateSnapshot freshlyCaptured,
        RecoveryManifest manifest);
}

public sealed class RecoveryBaselineVerifier : IRecoveryBaselineVerifier
{
    public RecoveryBaselineVerification Verify(
        NetworkStateSnapshot authorizedBaseline,
        NetworkStateSnapshot freshlyCaptured,
        RecoveryManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(authorizedBaseline);
        ArgumentNullException.ThrowIfNull(freshlyCaptured);
        ArgumentNullException.ThrowIfNull(manifest);

        if (!StringComparer.Ordinal.Equals(manifest.OperationCategory, DryRunOperationCategory.Route.ToString()))
            return new(false, "Only Route recovery operations are supported.");

        var baseline = (authorizedBaseline.Routes ?? Array.Empty<RouteState>())
            .Where(route => StringComparer.Ordinal.Equals(RestorationPlanner.RouteIdentity(route), manifest.TargetIdentity))
            .Select(RestorationPlanner.RouteValue)
            .ToArray();
        var current = (freshlyCaptured.Routes ?? Array.Empty<RouteState>())
            .Where(route => StringComparer.Ordinal.Equals(RestorationPlanner.RouteIdentity(route), manifest.TargetIdentity))
            .Select(RestorationPlanner.RouteValue)
            .ToArray();

        if (baseline.Count(value => StringComparer.Ordinal.Equals(value, manifest.OriginalValue)) != 1)
            return new(false, "The authorized route baseline is missing or ambiguous.");
        if (current.Count(value => StringComparer.Ordinal.Equals(value, manifest.OriginalValue)) != 1)
            return new(false, "The freshly captured route does not exactly match the authorized baseline.");
        var expectedValues = baseline.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        var currentValues = current.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        if (!expectedValues.SequenceEqual(currentValues, StringComparer.Ordinal))
            return new(false, "The freshly captured route target contains missing, duplicate, or unexpected state.");

        return new(true, "The authorized route target exactly matches its baseline.");
    }
}
