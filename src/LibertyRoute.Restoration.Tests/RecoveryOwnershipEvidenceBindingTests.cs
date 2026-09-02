namespace LibertyRoute.Restoration.Tests;

using System.Globalization;
using System.Net;
using LibertyRoute.Core;

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
        Assert.Equal("DDDB02F06A7E6E29712B2D631A08751B291BC31F84EDF11ED64C4F26CB9CC3F1",
            first.EvidenceFingerprint);
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
            Assert.Equal("C9860BAD63D0E36FB52F9D0A84C9864CE05CF27670F91D1EE07268D74E969E92",
                first.EvidenceFingerprint);
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


    [Fact]
    public void ExactSessionEvidenceUsesSeparateRecoveryBindingVersion()
    {
        var legacy = PersistedOwnedChange.Create(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            DryRunOperationCategory.Route, "route", "before", "after",
            DateTimeOffset.Parse("2026-08-30T00:00:00Z"), 1,
            OwnershipEvidenceSource.MutationLedger, OwnedChangeLifecycle.Applied);
        var exact = PersistedOwnedChange.CreateExactRoute(legacy.SessionId, legacy.ChangeId,
            legacy.Category, legacy.TargetIdentity, legacy.OriginalValue, legacy.AppliedValue,
            legacy.RecordedAtUtc, legacy.SequenceNumber, legacy.EvidenceSource, legacy.Lifecycle,
            new ExactRouteMutationIdentity(1,
                NativeRouteKey.Create(NativeRouteAddressFamily.IPv4, IPAddress.Parse("192.0.2.0"), 24,
                    IPAddress.Parse("192.0.2.1"), 42),
                new NativeRouteProfile(24, 100, 50, 5, 3, false, false, false, false)),
            Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));

        var legacyBinding = RecoveryOwnershipEvidenceBinding.Create(legacy);
        var exactBinding = RecoveryOwnershipEvidenceBinding.Create(exact);
        Assert.Equal(legacyBinding.EvidenceIdentity, exactBinding.EvidenceIdentity);
        Assert.NotEqual(legacyBinding.EvidenceFingerprint, exactBinding.EvidenceFingerprint);
    }
}
