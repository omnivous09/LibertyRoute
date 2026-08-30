namespace LibertyRoute.Restoration.Tests;

using System.Globalization;

public sealed class RecoveryOwnershipEvidenceBindingTests
{
    [Fact]
    public void BindingIsStableAndChangesWithImmutableEvidence()
    {
        var record = PersistedOwnedChange.Create(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            DryRunOperationCategory.Route, "2|10.0.0.0|8-with-a-multi-digit-byte-count", "baseline-value-with-multi-digit-length", "applied-value-with-multi-digit-length",
            DateTimeOffset.Parse("2026-08-30T00:00:00Z"), 1,
            OwnershipEvidenceSource.MutationLedger, OwnedChangeLifecycle.Applied);

        var first = RecoveryOwnershipEvidenceBinding.Create(record);
        var second = RecoveryOwnershipEvidenceBinding.Create(record);
        var changed = RecoveryOwnershipEvidenceBinding.Create(record with { TargetIdentity = "2|192.0.2.0|24" });

        Assert.Equal(first, second);
        Assert.Equal(64, first.EvidenceFingerprint.Length);
        Assert.NotEqual(first.EvidenceFingerprint, changed.EvidenceFingerprint);
    }

    [Fact]
    public void BindingIsIdenticalAcrossMateriallyDifferentCultures()
    {
        var record = PersistedOwnedChange.Create(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            DryRunOperationCategory.Route, "2|10.0.0.0|8", "baseline", "applied",
            DateTimeOffset.Parse("2026-08-30T00:00:00Z", CultureInfo.InvariantCulture), 1234567,
            OwnershipEvidenceSource.MutationLedger, OwnedChangeLifecycle.Applied);
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-EG");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ar-EG");
            var first = RecoveryOwnershipEvidenceBinding.Create(record);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
            var second = RecoveryOwnershipEvidenceBinding.Create(record);
            Assert.Equal(first, second);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }
}
