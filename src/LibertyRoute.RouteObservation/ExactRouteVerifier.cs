using LibertyRoute.Core;

namespace LibertyRoute.RouteObservation;

public enum ExactRouteVerificationStatus
{
    VerifiedPresent,
    VerifiedAbsent,
    NoMatch,
    DuplicateFullKeyMatches,
    ReducedIdentityCollision,
    ExpectedProfileMismatch,
    IncompleteObservation
}

public static class ExactRouteVerifier
{
    // This is descriptive data, not transferable authority for a side-effect decision.
    public sealed record Verification(
        ExactRouteVerificationStatus Status,
        int FullKeyMatchCount,
        int ReducedIdentityMatchCount,
        string Reason);

    public static Verification VerifyPresent(
        ExactRouteObservation observation, ExactNativeRouteRow expected)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(expected);
        if (!observation.Complete) return Incomplete(observation);
        if (!NativeRouteEvidenceValidator.IsValid(expected))
            return Malformed("Expected interface-index corroboration is malformed.");
        if (observation.Rows.Any(row => !NativeRouteEvidenceValidator.IsValid(row)))
            return Malformed("Native route observation contains malformed semantic evidence.");

        var expectedKey = expected.Key;
        var full = observation.Rows.Where(row => row.Key.Equals(expectedKey)).ToArray();
        var reduced = observation.Rows.Where(row => expectedKey.HasSameReducedIdentity(row.Key)).ToArray();
        if (full.Length > 1)
            return Result(ExactRouteVerificationStatus.DuplicateFullKeyMatches, full, reduced, "More than one full native-key row was observed.");
        if (full.Length == 0)
            return reduced.Length == 0
                ? Result(ExactRouteVerificationStatus.NoMatch, full, reduced, "The expected native route key was not observed.")
                : Result(ExactRouteVerificationStatus.ReducedIdentityCollision, full, reduced, "A conflicting row shares the destination identity.");
        if (reduced.Length != 1)
            return Result(ExactRouteVerificationStatus.ReducedIdentityCollision, full, reduced, "The full-key row is ambiguous under the reduced destination identity.");
        if (full[0].InterfaceIndex != expected.InterfaceIndex || !expected.Profile.IsSatisfiedBy(full[0].Profile))
            return Result(ExactRouteVerificationStatus.ExpectedProfileMismatch, full, reduced, "The native row does not satisfy the expected normalized profile.");
        return Result(ExactRouteVerificationStatus.VerifiedPresent, full, reduced, "Exactly one acceptable native row was observed.");
    }

    public static Verification VerifyAbsent(ExactRouteObservation observation, NativeRouteKey expectedKey)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(expectedKey);
        if (!observation.Complete) return Incomplete(observation);
        if (observation.Rows.Any(row => !NativeRouteEvidenceValidator.IsValid(row)))
            return Malformed("Native route observation contains malformed semantic evidence.");

        var full = observation.Rows.Where(row => row.Key.Equals(expectedKey)).ToArray();
        var reduced = observation.Rows.Where(row => expectedKey.HasSameReducedIdentity(row.Key)).ToArray();
        if (full.Length != 0)
            return Result(full.Length > 1 ? ExactRouteVerificationStatus.DuplicateFullKeyMatches : ExactRouteVerificationStatus.ExpectedProfileMismatch,
                full, reduced, "The owned native route key is still present.");
        if (reduced.Length != 0)
            return Result(ExactRouteVerificationStatus.ReducedIdentityCollision, full, reduced, "A conflicting destination row prevents unambiguous absence proof.");
        return Result(ExactRouteVerificationStatus.VerifiedAbsent, full, reduced, "The native route key is absent without a reduced-identity conflict.");
    }

    private static Verification Incomplete(ExactRouteObservation observation) => new(
        ExactRouteVerificationStatus.IncompleteObservation, 0, 0,
        observation.IncompleteReasons.FirstOrDefault() ?? "Native route observation is incomplete.");

    private static Verification Malformed(string reason) => new(
        ExactRouteVerificationStatus.IncompleteObservation, 0, 0,
        reason);

    private static Verification Result(
        ExactRouteVerificationStatus status, IReadOnlyCollection<ExactNativeRouteRow> full,
        IReadOnlyCollection<ExactNativeRouteRow> reduced, string reason) => new(status, full.Count, reduced.Count, reason);
}
