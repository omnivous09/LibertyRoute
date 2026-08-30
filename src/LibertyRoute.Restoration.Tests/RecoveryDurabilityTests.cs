using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LibertyRoute.Core;
using LibertyRoute.Recovery;
using LibertyRoute.Restoration;
using Xunit;

namespace LibertyRoute.Restoration.Tests;

public sealed class RecoveryDurabilityTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static NetworkStateSnapshot Snapshot()
        => new(
            DateTimeOffset.Parse("2026-08-26T00:00:00Z"),
            "server01",
            new[]
            {
                new AdapterState(
                    "adapter-1",
                    "Ethernet",
                    "Primary adapter",
                    "Ethernet",
                    "Up",
                    new[] { "10.0.0.10/24" },
                    new[] { "10.0.0.1" },
                    new[] { "10.0.0.2", "10.0.0.3" })
            },
            new[]
            {
                new RouteState
                {
                    Destination = "10.0.0.0/24",
                    NextHop = "10.0.0.1",
                    InterfaceIndex = 1,
                    Metric = 1,
                    AddressFamily = "IPv4"
                }
            },
            new[]
            {
                new DnsInterfaceState(
                    "adapter-1",
                    "Ethernet",
                    true,
                    new[] { "10.0.0.2" },
                    new[] { "10.0.0.2" },
                    new[] { "2001:db8::1" },
                    DnsConfigurationSource.Static,
                    DnsConfigurationSource.Static,
                    new[] { "10.0.0.2" },
                    new[] { "10.0.0.2" },
                    new[] { "2001:db8::1" },
                    new[] { "2001:db8::1" })
            });

    private static NetworkTransaction Transaction(string? ownerSid = "S-1-5-21-1001", RecoveryCompletion? recoveryCompletion = null)
        => new(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            ConnectionState.Connected,
            DateTimeOffset.Parse("2026-08-26T00:00:00Z"),
            Snapshot(),
            new[]
            {
                new OwnedNetworkChange(
                    Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                    "route",
                    "adapter-1",
                    "before",
                    "after",
                    DateTimeOffset.Parse("2026-08-26T00:00:01Z"))
            },
            "engine-1",
            null,
            ownerSid,
            recoveryCompletion);

    [Fact]
    public void LegacyNetworkTransactionJsonCompatibility()
    {
        var tx = Transaction();
        var json = JsonSerializer.Serialize(tx, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<NetworkTransaction>(json, JsonOptions);

        Assert.NotNull(roundTrip);
        Assert.Null(roundTrip!.RecoveryCompletion);
        Assert.Equal(tx.SessionId, roundTrip.SessionId);
        Assert.Equal(tx.OwnerSid, roundTrip.OwnerSid);
    }

    [Fact]
    public void RecoveryCompletionRoundTripAndIdentityProperties()
    {
        var completion = new RecoveryCompletion(
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            RecoveryPhase.TerminalCommitted,
            "authorized-fingerprint",
            "manifest-fingerprint",
            "evidence-bindings",
            "prepared-fingerprint",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            "failure",
            "manual note");

        var tx = Transaction(recoveryCompletion: completion);
        var roundTrip = JsonSerializer.Deserialize<NetworkTransaction>(JsonSerializer.Serialize(tx, JsonOptions), JsonOptions);

        Assert.NotNull(roundTrip);
        Assert.Equal(completion, roundTrip!.RecoveryCompletion);
        Assert.Equal("manifest-fingerprint", roundTrip.RecoveryCompletion!.ManifestIdentity);
        Assert.Equal("manifest-fingerprint", roundTrip.RecoveryCompletion.RecoveryManifestIdentity);
    }

    [Fact]
    public void OwnershipProvenanceRoundTripAndMalformedValuesRejected()
    {
        var attemptId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var evidenceId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
        var record = PersistedOwnedChange.Create(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"),
            DryRunOperationCategory.Route,
            "route-10.0.0.0/24",
            "before",
            "after",
            DateTimeOffset.Parse("2026-08-26T00:00:00Z"),
            7,
            OwnershipEvidenceSource.MutationLedger,
            OwnedChangeLifecycle.Applied,
            RecordPurpose.RecoveryMutation,
            attemptId,
            evidenceId);

        var roundTrip = JsonSerializer.Deserialize<PersistedOwnedChange>(JsonSerializer.Serialize(record, JsonOptions), JsonOptions);
        Assert.NotNull(roundTrip);
        Assert.Equal(RecordPurpose.RecoveryMutation, roundTrip!.Purpose);
        Assert.Equal(attemptId, roundTrip.RecoveryAttemptId);
        Assert.Equal(evidenceId, roundTrip.AuthorizationEvidenceId);

        var invalid = PersistedOwnedChange.TryCreate(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"),
            DryRunOperationCategory.Route,
            "route-10.0.0.0/24",
            "before",
            "after",
            DateTimeOffset.Parse("2026-08-26T00:00:00Z"),
            8,
            OwnershipEvidenceSource.MutationLedger,
            OwnedChangeLifecycle.Applied,
            RecordPurpose.RecoveryMutation,
            Guid.Empty,
            Guid.NewGuid(),
            out var _,
            out var reason);

        Assert.False(invalid);
        Assert.Contains("RecoveryAttemptId", reason);
    }

    [Fact]
    public void DeterministicAuthorizedFingerprintAndProgressDoesNotAlterIt()
    {
        var tx = Transaction();
        var before = RecoveryFingerprinting.ComputeAuthorizedTransactionFingerprint(tx);
        var progressed = tx with
        {
            RecoveryCompletion = new RecoveryCompletion(
                Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                RecoveryPhase.IntentRecorded,
                before,
                "recover-manifest",
                "evidence-bindings",
                null,
                DateTimeOffset.UtcNow,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null)
        };

        Assert.Equal(before, RecoveryFingerprinting.ComputeAuthorizedTransactionFingerprint(progressed));
    }

    [Fact]
    public void AuthorizationRelevantMutationAltersFingerprint()
    {
        var before = RecoveryFingerprinting.ComputeAuthorizedTransactionFingerprint(Transaction());
        var altered = Transaction(ownerSid: "S-1-5-21-1002");
        Assert.NotEqual(before, RecoveryFingerprinting.ComputeAuthorizedTransactionFingerprint(altered));
    }

    [Fact]
    public void ManifestFingerprintMutationMatrixAndProgressIndependence()
    {
        var attemptId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var changeId = OwnershipIdentity.DeriveChangeId(Guid.Parse("11111111-1111-1111-1111-111111111111"), "recover-route");
        var manifest = RecoveryManifest.Create(
            attemptId,
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "S-1-5-21-1001",
            "authorized-fingerprint",
            new[]
            {
                RecoveryEvidenceBinding.Create(Guid.Parse("44444444-4444-4444-4444-444444444444"), "evidence:route", "binding-fingerprint")
            },
            changeId,
            "route-change",
            "route",
            "route-10.0.0.0/24",
            "10.0.0.0/24",
            "10.0.0.0/24",
            1,
            "prep-fingerprint");

        var before = RecoveryFingerprinting.ComputeRecoveryManifestFingerprint(manifest);

        var mutated = new[]
        {
            RecoveryManifest.Create(attemptId, Guid.Parse("22222222-2222-2222-2222-222222222222"), "S-1-5-21-1001", manifest.AuthorizedTransactionFingerprint, manifest.OriginalEvidenceBindings, changeId, manifest.OperationIdentity, manifest.OperationCategory, manifest.TargetIdentity, manifest.OriginalValue, manifest.AppliedValue, manifest.SequenceOrder, manifest.PreparationFingerprint),
            RecoveryManifest.Create(attemptId, manifest.SessionId, "S-1-5-21-1002", manifest.AuthorizedTransactionFingerprint, manifest.OriginalEvidenceBindings, changeId, manifest.OperationIdentity, manifest.OperationCategory, manifest.TargetIdentity, manifest.OriginalValue, manifest.AppliedValue, manifest.SequenceOrder, manifest.PreparationFingerprint),
            RecoveryManifest.Create(attemptId, manifest.SessionId, manifest.OwnerSid, "other-authorized-fingerprint", manifest.OriginalEvidenceBindings, changeId, manifest.OperationIdentity, manifest.OperationCategory, manifest.TargetIdentity, manifest.OriginalValue, manifest.AppliedValue, manifest.SequenceOrder, manifest.PreparationFingerprint),
            RecoveryManifest.Create(attemptId, manifest.SessionId, manifest.OwnerSid, manifest.AuthorizedTransactionFingerprint, new[] { RecoveryEvidenceBinding.Create(Guid.Parse("55555555-5555-5555-5555-555555555555"), "evidence:route-2", "binding-fingerprint-2") }, changeId, manifest.OperationIdentity, manifest.OperationCategory, manifest.TargetIdentity, manifest.OriginalValue, manifest.AppliedValue, manifest.SequenceOrder, manifest.PreparationFingerprint),
            RecoveryManifest.Create(attemptId, manifest.SessionId, manifest.OwnerSid, manifest.AuthorizedTransactionFingerprint, manifest.OriginalEvidenceBindings, Guid.NewGuid(), manifest.OperationIdentity, manifest.OperationCategory, manifest.TargetIdentity, manifest.OriginalValue, manifest.AppliedValue, manifest.SequenceOrder, manifest.PreparationFingerprint),
            RecoveryManifest.Create(attemptId, manifest.SessionId, manifest.OwnerSid, manifest.AuthorizedTransactionFingerprint, manifest.OriginalEvidenceBindings, changeId, "other-operation", manifest.OperationCategory, manifest.TargetIdentity, manifest.OriginalValue, manifest.AppliedValue, manifest.SequenceOrder, manifest.PreparationFingerprint),
            RecoveryManifest.Create(attemptId, manifest.SessionId, manifest.OwnerSid, manifest.AuthorizedTransactionFingerprint, manifest.OriginalEvidenceBindings, changeId, manifest.OperationIdentity, "other-category", manifest.TargetIdentity, manifest.OriginalValue, manifest.AppliedValue, manifest.SequenceOrder, manifest.PreparationFingerprint),
            RecoveryManifest.Create(attemptId, manifest.SessionId, manifest.OwnerSid, manifest.AuthorizedTransactionFingerprint, manifest.OriginalEvidenceBindings, changeId, manifest.OperationIdentity, manifest.OperationCategory, "other-target", manifest.OriginalValue, manifest.AppliedValue, manifest.SequenceOrder, manifest.PreparationFingerprint),
            RecoveryManifest.Create(attemptId, manifest.SessionId, manifest.OwnerSid, manifest.AuthorizedTransactionFingerprint, manifest.OriginalEvidenceBindings, changeId, manifest.OperationIdentity, manifest.OperationCategory, manifest.TargetIdentity, "other-original", manifest.AppliedValue, manifest.SequenceOrder, manifest.PreparationFingerprint),
            RecoveryManifest.Create(attemptId, manifest.SessionId, manifest.OwnerSid, manifest.AuthorizedTransactionFingerprint, manifest.OriginalEvidenceBindings, changeId, manifest.OperationIdentity, manifest.OperationCategory, manifest.TargetIdentity, manifest.OriginalValue, "other-applied", manifest.SequenceOrder, manifest.PreparationFingerprint),
            RecoveryManifest.Create(attemptId, manifest.SessionId, manifest.OwnerSid, manifest.AuthorizedTransactionFingerprint, manifest.OriginalEvidenceBindings, changeId, manifest.OperationIdentity, manifest.OperationCategory, manifest.TargetIdentity, manifest.OriginalValue, manifest.AppliedValue, 2, manifest.PreparationFingerprint),
            RecoveryManifest.Create(attemptId, manifest.SessionId, manifest.OwnerSid, manifest.AuthorizedTransactionFingerprint, manifest.OriginalEvidenceBindings, changeId, manifest.OperationIdentity, manifest.OperationCategory, manifest.TargetIdentity, manifest.OriginalValue, manifest.AppliedValue, manifest.SequenceOrder, "other-preparation")
        };

        foreach (var candidate in mutated)
            Assert.NotEqual(before, RecoveryFingerprinting.ComputeRecoveryManifestFingerprint(candidate));

        var completion = new RecoveryCompletion(
            attemptId,
            RecoveryPhase.LedgerFinalized,
            manifest.AuthorizedTransactionFingerprint,
            before,
            "evidence-binding-1",
            manifest.PreparationFingerprint,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            "failure")
        { Manifest = manifest, ManualRecoveryOriginPhase = RecoveryPhase.Prepared };

        var progressed = completion with
        {
            Phase = RecoveryPhase.ManualRecoveryRequired,
            FailureReason = "manual recovery reason",
            ManualRecoveryNote = "manual recovery note",
            ManualRecoveryOriginPhase = RecoveryPhase.Prepared,
            IntentRecordedAtUtc = DateTimeOffset.UtcNow,
            PreparedAtUtc = DateTimeOffset.UtcNow,
            ExecutionStartedAtUtc = DateTimeOffset.UtcNow,
            ExecutionCompletedAtUtc = DateTimeOffset.UtcNow,
            BaselineVerifiedAtUtc = DateTimeOffset.UtcNow,
            LedgerFinalizingAtUtc = DateTimeOffset.UtcNow,
            LedgerFinalizedAtUtc = DateTimeOffset.UtcNow,
            TerminalCommittedAtUtc = DateTimeOffset.UtcNow
        };

        Assert.Equal(before, RecoveryFingerprinting.ComputeRecoveryManifestFingerprint(progressed));
    }

    [Fact]
    public void RecoveryPhaseTransitionsAreStrictlyMonotonic()
    {
        Assert.True(RecoveryCompletion.IsValidTransition(RecoveryPhase.IntentRecorded, RecoveryPhase.Prepared));
        Assert.True(RecoveryCompletion.IsValidTransition(RecoveryPhase.Prepared, RecoveryPhase.ExecutionStarted));
        Assert.True(RecoveryCompletion.IsValidTransition(RecoveryPhase.LedgerFinalized, RecoveryPhase.TerminalCommitted));
        Assert.True(RecoveryCompletion.IsValidTransition(RecoveryPhase.ExecutionStarted, RecoveryPhase.ManualRecoveryRequired));
        Assert.False(RecoveryCompletion.IsValidTransition(RecoveryPhase.Prepared, RecoveryPhase.Prepared));
        Assert.False(RecoveryCompletion.IsValidTransition(RecoveryPhase.Prepared, RecoveryPhase.IntentRecorded));
        Assert.False(RecoveryCompletion.IsValidTransition(RecoveryPhase.IntentRecorded, RecoveryPhase.ExecutionStarted));
    }

    [Fact]
    public void ManualRecoveryValidationUsesIntentRecordedOriginHistory()
    {
        var completion = CompletionAt(RecoveryPhase.IntentRecorded)
            .WithPhase(RecoveryPhase.ManualRecoveryRequired, failureReason: "failed");

        Assert.Equal(string.Empty, RecoveryCompletion.Validate(completion));
    }

    [Fact]
    public void ManualRecoveryValidationUsesPreparedOriginHistory()
    {
        var completion = CompletionAt(RecoveryPhase.Prepared)
            .WithPhase(RecoveryPhase.ManualRecoveryRequired, failureReason: "failed");

        Assert.Equal(string.Empty, RecoveryCompletion.Validate(completion));
        Assert.Null(completion.ExecutionStartedAtUtc);
    }

    [Fact]
    public void ManualRecoveryValidationUsesExecutionStartedOriginHistory()
    {
        var completion = CompletionAt(RecoveryPhase.ExecutionStarted)
            .WithPhase(RecoveryPhase.ManualRecoveryRequired, failureReason: "failed");

        Assert.Equal(string.Empty, RecoveryCompletion.Validate(completion));
        Assert.Null(completion.ExecutionCompletedAtUtc);
    }

    [Fact]
    public void MalformedManualRecoveryChronologyFailsClosed()
    {
        var completion = CompletionAt(RecoveryPhase.Prepared)
            .WithPhase(RecoveryPhase.ManualRecoveryRequired, failureReason: "failed") with
            { ExecutionStartedAtUtc = DateTimeOffset.Parse("2026-08-26T00:00:03Z") };

        Assert.NotEqual(string.Empty, RecoveryCompletion.Validate(completion));
    }

    [Fact]
    public void TerminalCommittedValidationRemainsStrict()
    {
        var completion = CompletionAt(RecoveryPhase.TerminalCommitted) with { LedgerFinalizedAtUtc = null };

        Assert.NotEqual(string.Empty, RecoveryCompletion.Validate(completion));
    }

    [Theory]
    [InlineData(RecoveryPhase.IntentRecorded, RecoveryPhase.Prepared)]
    [InlineData(RecoveryPhase.IntentRecorded, RecoveryPhase.ExecutionStarted)]
    [InlineData(RecoveryPhase.Prepared, RecoveryPhase.ExecutionStarted)]
    [InlineData(RecoveryPhase.ExecutionStarted, RecoveryPhase.ExecutionCompleted)]
    [InlineData(RecoveryPhase.ExecutionCompleted, RecoveryPhase.BaselineVerified)]
    [InlineData(RecoveryPhase.BaselineVerified, RecoveryPhase.LedgerFinalizing)]
    [InlineData(RecoveryPhase.LedgerFinalizing, RecoveryPhase.LedgerFinalized)]
    [InlineData(RecoveryPhase.LedgerFinalized, RecoveryPhase.TerminalCommitted)]
    public void OrdinaryPhaseRejectsTimestampAfterDeclaredPhase(RecoveryPhase declaredPhase, RecoveryPhase futurePhase)
    {
        var timestamp = DateTimeOffset.Parse("2026-08-26T01:00:00Z");
        var completion = CompletionAt(declaredPhase);
        completion = futurePhase switch
        {
            RecoveryPhase.Prepared => completion with { PreparedAtUtc = timestamp },
            RecoveryPhase.ExecutionStarted => completion with { ExecutionStartedAtUtc = timestamp },
            RecoveryPhase.ExecutionCompleted => completion with { ExecutionCompletedAtUtc = timestamp },
            RecoveryPhase.BaselineVerified => completion with { BaselineVerifiedAtUtc = timestamp },
            RecoveryPhase.LedgerFinalizing => completion with { LedgerFinalizingAtUtc = timestamp },
            RecoveryPhase.LedgerFinalized => completion with { LedgerFinalizedAtUtc = timestamp },
            RecoveryPhase.TerminalCommitted => completion with { TerminalCommittedAtUtc = timestamp },
            _ => throw new ArgumentOutOfRangeException(nameof(futurePhase))
        };

        Assert.Contains("after its declared phase", RecoveryCompletion.Validate(completion), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(RecoveryPhase.IntentRecorded)]
    [InlineData(RecoveryPhase.Prepared)]
    [InlineData(RecoveryPhase.ExecutionStarted)]
    [InlineData(RecoveryPhase.ExecutionCompleted)]
    [InlineData(RecoveryPhase.BaselineVerified)]
    [InlineData(RecoveryPhase.LedgerFinalizing)]
    [InlineData(RecoveryPhase.LedgerFinalized)]
    [InlineData(RecoveryPhase.TerminalCommitted)]
    public void ValidOrdinaryPhaseHistoryPasses(RecoveryPhase phase)
    {
        Assert.Equal(string.Empty, RecoveryCompletion.Validate(CompletionAt(phase)));
    }

    [Fact]
    public void PhaseAdvancementPreservesExactIntentRecordedTimestamp()
    {
        var intentAt = DateTimeOffset.Parse("2026-08-26T00:00:00.1234567Z");
        var prepared = CompletionAt(RecoveryPhase.IntentRecorded, intentAt)
            .WithPhase(RecoveryPhase.Prepared, DateTimeOffset.Parse("2026-08-26T00:00:01Z"));
        var executionStarted = prepared.WithPhase(RecoveryPhase.ExecutionStarted, DateTimeOffset.Parse("2026-08-26T00:00:02Z"));

        Assert.Equal(intentAt, prepared.IntentRecordedAtUtc);
        Assert.Equal(intentAt, executionStarted.IntentRecordedAtUtc);
    }

    [Fact]
    public void ManifestFingerprintOverloadRequiresExactAuthorizationFingerprint()
    {
        var completion = CompletionAt(RecoveryPhase.IntentRecorded);
        var expected = RecoveryFingerprinting.ComputeRecoveryManifestFingerprint(completion.Manifest!);

        Assert.Equal(expected, RecoveryFingerprinting.ComputeRecoveryManifestFingerprint(completion, "authorized-fingerprint"));
        Assert.Throws<InvalidOperationException>(() => RecoveryFingerprinting.ComputeRecoveryManifestFingerprint(completion, "AUTHORIZED-FINGERPRINT"));
        Assert.Throws<ArgumentException>(() => RecoveryFingerprinting.ComputeRecoveryManifestFingerprint(completion, string.Empty));
        Assert.Throws<ArgumentException>(() => RecoveryFingerprinting.ComputeRecoveryManifestFingerprint(completion, " "));
    }

    [Fact]
    public void DurableManifestExactDuplicateRepresentationsPass()
    {
        var transaction = DurableTransactionAt(RecoveryPhase.Prepared);

        Assert.Equal(string.Empty, RecoveryCompletion.ValidateDurableManifest(transaction.RecoveryCompletion!, transaction));
    }

    [Fact]
    public void CanonicalEvidenceBindingsPreserveColonsOrderAndValidate()
    {
        var transaction = DurableTransactionAt(RecoveryPhase.Prepared);
        var existingManifest = transaction.RecoveryCompletion!.Manifest!;
        var later = RecoveryEvidenceBinding.Create(
            Guid.Parse("44444444-4444-4444-4444-444444444444"),
            "evidence:route",
            "sha256:route:fingerprint");
        var earlier = RecoveryEvidenceBinding.Create(
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            "evidence:dns",
            "sha256:dns:fingerprint");
        var manifest = RecoveryManifest.Create(
            existingManifest.RecoveryAttemptId,
            existingManifest.SessionId,
            existingManifest.OwnerSid,
            existingManifest.AuthorizedTransactionFingerprint,
            new[] { later, earlier },
            existingManifest.RecoveryOwnershipChangeId,
            existingManifest.OperationIdentity,
            existingManifest.OperationCategory,
            existingManifest.TargetIdentity,
            existingManifest.OriginalValue,
            existingManifest.AppliedValue,
            existingManifest.SequenceOrder,
            existingManifest.PreparationFingerprint);
        var canonical = RecoveryManifest.FormatCanonicalEvidenceBindings(manifest.OriginalEvidenceBindings);
        var completion = transaction.RecoveryCompletion with
        {
            Manifest = manifest,
            RecoveryManifestFingerprint = RecoveryFingerprinting.ComputeRecoveryManifestFingerprint(manifest),
            OriginalEvidenceBindings = canonical
        };

        Assert.Equal(earlier, manifest.OriginalEvidenceBindings[0]);
        Assert.Equal(later, manifest.OriginalEvidenceBindings[1]);
        Assert.Equal(
            "33333333-3333-3333-3333-333333333333:evidence:dns:sha256:dns:fingerprint|" +
            "44444444-4444-4444-4444-444444444444:evidence:route:sha256:route:fingerprint",
            canonical);
        Assert.Equal(string.Empty, RecoveryCompletion.ValidateDurableManifest(completion, transaction));
    }

    [Fact]
    public void DurableManifestPreparationFingerprintMismatchFailsClosed()
    {
        var transaction = DurableTransactionAt(RecoveryPhase.Prepared);
        var completion = transaction.RecoveryCompletion! with { PreparationFingerprint = "PREP-FINGERPRINT" };

        Assert.Contains("preparation fingerprint", RecoveryCompletion.ValidateDurableManifest(completion, transaction), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IntentRecordedExactPreparationFingerprintsValidate()
    {
        var transaction = DurableTransactionAt(RecoveryPhase.IntentRecorded);

        Assert.Equal("prep-fingerprint", transaction.RecoveryCompletion!.PreparationFingerprint);
        Assert.Equal("prep-fingerprint", transaction.RecoveryCompletion.Manifest!.PreparationFingerprint);
        Assert.Equal(string.Empty, RecoveryCompletion.ValidateDurableManifest(transaction.RecoveryCompletion, transaction));
    }

    [Fact]
    public void IntentRecordedManifestOnlyPreparationFingerprintFailsClosed()
    {
        var transaction = DurableTransactionAt(RecoveryPhase.IntentRecorded);
        var manifest = transaction.RecoveryCompletion!.Manifest! with { PreparationFingerprint = null };
        var completion = transaction.RecoveryCompletion with
        {
            Manifest = manifest,
            RecoveryManifestFingerprint = RecoveryFingerprinting.ComputeRecoveryManifestFingerprint(manifest)
        };

        Assert.Contains("preparation fingerprint", RecoveryCompletion.ValidateDurableManifest(completion, transaction), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IntentRecordedCompletionOnlyPreparationFingerprintFailsClosed()
    {
        var transaction = DurableTransactionAt(RecoveryPhase.IntentRecorded);
        var completion = transaction.RecoveryCompletion! with { PreparationFingerprint = null };

        Assert.Contains("preparation fingerprint", RecoveryCompletion.ValidateDurableManifest(completion, transaction), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(RecoveryPhase.Prepared)]
    [InlineData(RecoveryPhase.ExecutionStarted)]
    [InlineData(RecoveryPhase.ExecutionCompleted)]
    [InlineData(RecoveryPhase.BaselineVerified)]
    [InlineData(RecoveryPhase.LedgerFinalizing)]
    [InlineData(RecoveryPhase.LedgerFinalized)]
    [InlineData(RecoveryPhase.TerminalCommitted)]
    public void PreparedAndLaterMatchingPreparationFingerprintsValidate(RecoveryPhase phase)
    {
        var transaction = DurableTransactionAt(phase);

        Assert.Equal(
            transaction.RecoveryCompletion!.Manifest!.PreparationFingerprint,
            transaction.RecoveryCompletion.PreparationFingerprint);
        Assert.Equal(string.Empty, RecoveryCompletion.ValidateDurableManifest(transaction.RecoveryCompletion, transaction));
    }

    [Theory]
    [InlineData("44444444-4444-4444-4444-444444444444:other:identity:binding-fingerprint")]
    [InlineData("44444444-4444-4444-4444-444444444444:evidence:route:other-fingerprint")]
    [InlineData("44444444-4444-4444-4444-444444444444:Evidence:route:binding-fingerprint")]
    public void DurableManifestEvidenceMismatchFailsClosed(string evidenceBindings)
    {
        var transaction = DurableTransactionAt(RecoveryPhase.Prepared);
        var completion = transaction.RecoveryCompletion! with { OriginalEvidenceBindings = evidenceBindings };

        Assert.Contains("evidence bindings", RecoveryCompletion.ValidateDurableManifest(completion, transaction), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DurableManifestEvidenceOrderingMismatchFailsClosed()
    {
        var transaction = DurableTransactionAt(RecoveryPhase.Prepared);
        var first = RecoveryEvidenceBinding.Create(Guid.Parse("33333333-3333-3333-3333-333333333333"), "evidence:dns", "dns-fingerprint");
        var second = transaction.RecoveryCompletion!.Manifest!.OriginalEvidenceBindings[0];
        var manifest = transaction.RecoveryCompletion.Manifest with { OriginalEvidenceBindings = new[] { first, second } };
        var completion = transaction.RecoveryCompletion with
        {
            Manifest = manifest,
            RecoveryManifestFingerprint = RecoveryFingerprinting.ComputeRecoveryManifestFingerprint(manifest),
            OriginalEvidenceBindings =
                "44444444-4444-4444-4444-444444444444:evidence:route:binding-fingerprint|" +
                "33333333-3333-3333-3333-333333333333:evidence:dns:dns-fingerprint"
        };

        Assert.Contains("evidence bindings", RecoveryCompletion.ValidateDurableManifest(completion, transaction), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("bindingIdentity")]
    [InlineData("bindingFingerprint")]
    [InlineData("changeId")]
    [InlineData("sequence")]
    public async Task InternallyRefingerprintedMalformedManifestFailsJournalRead(string malformedField)
    {
        var root = Path.Combine(Path.GetTempPath(), "LibertyRoute.Recovery.Tests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var transaction = DurableTransactionAt(RecoveryPhase.Prepared);
            var manifest = transaction.RecoveryCompletion!.Manifest!;
            manifest = malformedField switch
            {
                "operation" => manifest with { OperationIdentity = " " },
                "bindingIdentity" => manifest with { OriginalEvidenceBindings = new[] { manifest.OriginalEvidenceBindings[0] with { EvidenceIdentity = " " } } },
                "bindingFingerprint" => manifest with { OriginalEvidenceBindings = new[] { manifest.OriginalEvidenceBindings[0] with { EvidenceFingerprint = "" } } },
                "changeId" => manifest with { RecoveryOwnershipChangeId = Guid.Empty },
                "sequence" => manifest with { SequenceOrder = -1 },
                _ => throw new ArgumentOutOfRangeException(nameof(malformedField))
            };
            var completion = transaction.RecoveryCompletion with
            {
                Manifest = manifest,
                RecoveryManifestFingerprint = RecoveryFingerprinting.ComputeRecoveryManifestFingerprint(manifest)
            };
            var journal = new FileTransactionJournal(Path.Combine(root, "active.lrj"));
            await journal.WriteAsync(transaction with { RecoveryCompletion = completion }, CancellationToken.None);

            await Assert.ThrowsAsync<InvalidDataException>(() => journal.ReadActiveAsync(CancellationToken.None));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void IntentRecordedRequiresPreparationFingerprint(string? preparationFingerprint)
    {
        var completion = CompletionAt(RecoveryPhase.IntentRecorded) with { PreparationFingerprint = preparationFingerprint };
        Assert.Contains("preparation fingerprint", RecoveryCompletion.Validate(completion), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void ManifestFactoryRequiresPreparationFingerprint(string? preparationFingerprint)
    {
        Assert.Throws<ArgumentException>(() => RecoveryManifest.Create(
            Guid.NewGuid(), Guid.NewGuid(), null, "authorized",
            new[] { RecoveryEvidenceBinding.Create(Guid.NewGuid(), "identity", "fingerprint") },
            Guid.NewGuid(), "operation", "route", "target", "before", "after", 1,
            preparationFingerprint));
    }

    [Fact]
    public void IntentPreparationComparisonIsOrdinalExact()
    {
        var transaction = DurableTransactionAt(RecoveryPhase.IntentRecorded);
        var changed = transaction.RecoveryCompletion! with { PreparationFingerprint = "PREP-FINGERPRINT" };
        Assert.Contains("preparation fingerprint", RecoveryCompletion.ValidateDurableManifest(changed, transaction), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WithPhaseRejectsInvalidSourceAndPreservesImmutableIdentity()
    {
        var intent = CompletionAt(RecoveryPhase.IntentRecorded);
        Assert.Throws<InvalidOperationException>(() => (intent with { IntentRecordedAtUtc = null }).WithPhase(RecoveryPhase.Prepared));

        var prepared = intent.WithPhase(RecoveryPhase.Prepared, DateTimeOffset.Parse("2026-08-26T00:00:01Z"));
        Assert.Equal(intent.IntentRecordedAtUtc, prepared.IntentRecordedAtUtc);
        Assert.Equal(intent.PreparationFingerprint, prepared.PreparationFingerprint);
        Assert.Same(intent.Manifest, prepared.Manifest);
        Assert.Equal(intent.RecoveryManifestFingerprint, prepared.RecoveryManifestFingerprint);
    }

    [Fact]
    public async Task VersionedReadUsesExactValidatedPayloadRevision()
    {
        var (journal, path, root) = NewRecoveryJournal();
        try
        {
            await journal.WriteAsync(Transaction(), CancellationToken.None);
            var snapshot = await journal.ReadActiveRecoveryAsync(CancellationToken.None);
            Assert.NotNull(snapshot);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            var payload = document.RootElement.GetProperty("payload").GetString()!;
            Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))), snapshot!.JournalRevision);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ConditionalRecoveryAdvanceSupportsInitialAndEveryAdjacentPhase()
    {
        var (journal, _, root) = NewRecoveryJournal();
        try
        {
            var current = Transaction();
            await journal.WriteAsync(current, CancellationToken.None);
            var proposed = DurableTransactionAt(RecoveryPhase.IntentRecorded);
            var snapshot = (await journal.ReadActiveRecoveryAsync(CancellationToken.None))!;
            Assert.True(await journal.TryAdvanceRecoveryAsync(Expectation(snapshot), proposed, CancellationToken.None));

            foreach (var phase in new[] { RecoveryPhase.Prepared, RecoveryPhase.ExecutionStarted, RecoveryPhase.ExecutionCompleted, RecoveryPhase.BaselineVerified, RecoveryPhase.LedgerFinalizing, RecoveryPhase.LedgerFinalized, RecoveryPhase.TerminalCommitted })
            {
                snapshot = (await journal.ReadActiveRecoveryAsync(CancellationToken.None))!;
                proposed = snapshot.Transaction with
                {
                    RecoveryCompletion = snapshot.Transaction.RecoveryCompletion!.WithPhase(
                        phase, DateTimeOffset.Parse("2026-08-26T00:00:00Z").AddSeconds((int)phase))
                };
                Assert.True(await journal.TryAdvanceRecoveryAsync(Expectation(snapshot), proposed, CancellationToken.None));
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(RecoveryPhase.IntentRecorded)]
    [InlineData(RecoveryPhase.Prepared)]
    [InlineData(RecoveryPhase.ExecutionStarted)]
    [InlineData(RecoveryPhase.ExecutionCompleted)]
    [InlineData(RecoveryPhase.BaselineVerified)]
    [InlineData(RecoveryPhase.LedgerFinalizing)]
    [InlineData(RecoveryPhase.LedgerFinalized)]
    public async Task ConditionalRecoveryAdvanceSupportsManualRecoveryWithExactOrigin(RecoveryPhase phase)
    {
        var (journal, _, root) = NewRecoveryJournal();
        try
        {
            await journal.WriteAsync(DurableTransactionAt(phase), CancellationToken.None);
            var snapshot = (await journal.ReadActiveRecoveryAsync(CancellationToken.None))!;
            var proposed = snapshot.Transaction with
            {
                RecoveryCompletion = snapshot.Transaction.RecoveryCompletion!.WithPhase(
                    RecoveryPhase.ManualRecoveryRequired, failureReason: "failure")
            };
            Assert.True(await journal.TryAdvanceRecoveryAsync(Expectation(snapshot), proposed, CancellationToken.None));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task StaleExpectationsReturnFalseAndPreserveReplacement()
    {
        var (journal, _, root) = NewRecoveryJournal();
        try
        {
            var intent = DurableTransactionAt(RecoveryPhase.IntentRecorded);
            await journal.WriteAsync(intent, CancellationToken.None);
            var snapshot = (await journal.ReadActiveRecoveryAsync(CancellationToken.None))!;
            var prepared = intent with { RecoveryCompletion = intent.RecoveryCompletion!.WithPhase(RecoveryPhase.Prepared, DateTimeOffset.Parse("2026-08-26T00:00:01Z")) };
            await journal.WriteAsync(intent with { LastError = "replacement" }, CancellationToken.None);

            Assert.False(await journal.TryAdvanceRecoveryAsync(Expectation(snapshot), prepared, CancellationToken.None));
            Assert.Equal("replacement", (await journal.ReadActiveAsync(CancellationToken.None))!.LastError);

            var fresh = (await journal.ReadActiveRecoveryAsync(CancellationToken.None))!;
            var expectations = new[]
            {
                Expectation(fresh) with { SessionId = Guid.NewGuid() },
                Expectation(fresh) with { ExpectedPhase = RecoveryPhase.Prepared },
                Expectation(fresh) with { RecoveryAttemptId = Guid.NewGuid() },
                Expectation(fresh) with { AuthorizedTransactionFingerprint = fresh.Transaction.RecoveryCompletion!.AuthorizedTransactionFingerprint.ToLowerInvariant() },
                Expectation(fresh) with { RecoveryManifestFingerprint = fresh.Transaction.RecoveryCompletion!.RecoveryManifestFingerprint.ToLowerInvariant() }
            };
            foreach (var expectation in expectations)
                Assert.False(await journal.TryAdvanceRecoveryAsync(expectation, prepared, CancellationToken.None));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ForbiddenAndImmutableMutationTransitionsNeverPublish()
    {
        var (journal, _, root) = NewRecoveryJournal();
        try
        {
            var intent = DurableTransactionAt(RecoveryPhase.IntentRecorded);
            await journal.WriteAsync(intent, CancellationToken.None);
            var snapshot = (await journal.ReadActiveRecoveryAsync(CancellationToken.None))!;
            var nonAdjacent = DurableTransactionAt(RecoveryPhase.ExecutionStarted);
            await Assert.ThrowsAsync<InvalidOperationException>(() => journal.TryAdvanceRecoveryAsync(Expectation(snapshot), nonAdjacent, CancellationToken.None));

            var preparedCompletion = intent.RecoveryCompletion!.WithPhase(RecoveryPhase.Prepared, DateTimeOffset.Parse("2026-08-26T00:00:01Z"));
            var mutatedManifest = preparedCompletion.Manifest! with { OperationIdentity = "changed" };
            var mutated = intent with
            {
                RecoveryCompletion = preparedCompletion with
                {
                    Manifest = mutatedManifest,
                    RecoveryManifestFingerprint = RecoveryFingerprinting.ComputeRecoveryManifestFingerprint(mutatedManifest)
                }
            };
            await Assert.ThrowsAsync<InvalidOperationException>(() => journal.TryAdvanceRecoveryAsync(Expectation(snapshot), mutated, CancellationToken.None));
            Assert.Equal(RecoveryPhase.IntentRecorded, (await journal.ReadActiveAsync(CancellationToken.None))!.RecoveryCompletion!.Phase);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ConcurrentSameRevisionAdvanceHasExactlyOneWinner()
    {
        var (first, path, root) = NewRecoveryJournal();
        try
        {
            var second = new FileTransactionJournal(path);
            var intent = DurableTransactionAt(RecoveryPhase.IntentRecorded);
            await first.WriteAsync(intent, CancellationToken.None);
            var snapshot = (await first.ReadActiveRecoveryAsync(CancellationToken.None))!;
            var proposed = intent with { RecoveryCompletion = intent.RecoveryCompletion!.WithPhase(RecoveryPhase.Prepared, DateTimeOffset.Parse("2026-08-26T00:00:01Z")) };
            var results = await Task.WhenAll(
                first.TryAdvanceRecoveryAsync(Expectation(snapshot), proposed, CancellationToken.None),
                second.TryAdvanceRecoveryAsync(Expectation(snapshot), proposed, CancellationToken.None));
            Assert.Equal(1, results.Count(value => value));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task MalformedExpectationAndProposedStateFailWithoutPublication()
    {
        var (journal, _, root) = NewRecoveryJournal();
        try
        {
            var intent = DurableTransactionAt(RecoveryPhase.IntentRecorded);
            await journal.WriteAsync(intent, CancellationToken.None);
            var snapshot = (await journal.ReadActiveRecoveryAsync(CancellationToken.None))!;
            var prepared = intent with { RecoveryCompletion = intent.RecoveryCompletion!.WithPhase(RecoveryPhase.Prepared, DateTimeOffset.Parse("2026-08-26T00:00:01Z")) };

            await Assert.ThrowsAsync<ArgumentException>(() => journal.TryAdvanceRecoveryAsync(
                Expectation(snapshot) with { JournalRevision = " " }, prepared, CancellationToken.None));
            await Assert.ThrowsAsync<ArgumentException>(() => journal.TryAdvanceRecoveryAsync(
                Expectation(snapshot) with { ExpectedPhase = null }, prepared, CancellationToken.None));

            var malformed = prepared with { RecoveryCompletion = prepared.RecoveryCompletion! with { PreparationFingerprint = null } };
            await Assert.ThrowsAsync<InvalidDataException>(() => journal.TryAdvanceRecoveryAsync(
                Expectation(snapshot), malformed, CancellationToken.None));
            Assert.Equal(snapshot.JournalRevision, (await journal.ReadActiveRecoveryAsync(CancellationToken.None))!.JournalRevision);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("revision", "ABC")]
    [InlineData("revision", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("revision", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("revision", "GGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGG")]
    [InlineData("authorized", "short")]
    [InlineData("manifest", "not-hex")]
    public async Task MalformedExpectationHashesThrow(string field, string value)
    {
        var (journal, _, root) = NewRecoveryJournal();
        try
        {
            var intent = DurableTransactionAt(RecoveryPhase.IntentRecorded);
            await journal.WriteAsync(intent, CancellationToken.None);
            var snapshot = (await journal.ReadActiveRecoveryAsync(CancellationToken.None))!;
            var expectation = field switch
            {
                "revision" => Expectation(snapshot) with { JournalRevision = value },
                "authorized" => Expectation(snapshot) with { AuthorizedTransactionFingerprint = value },
                _ => Expectation(snapshot) with { RecoveryManifestFingerprint = value }
            };
            var proposed = intent with { RecoveryCompletion = intent.RecoveryCompletion!.WithPhase(RecoveryPhase.Prepared, DateTimeOffset.Parse("2026-08-26T00:00:01Z")) };
            await Assert.ThrowsAsync<ArgumentException>(() => journal.TryAdvanceRecoveryAsync(expectation, proposed, CancellationToken.None));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("adaptersNull")]
    [InlineData("adapterElementNull")]
    [InlineData("adapterNestedNull")]
    [InlineData("routesElementNull")]
    [InlineData("dnsElementNull")]
    [InlineData("changesNull")]
    [InlineData("changeElementNull")]
    public async Task RechecksummedMalformedPreRecoveryTransactionFailsBeforeComparison(string shape)
    {
        var (journal, path, root) = NewRecoveryJournal();
        try
        {
            var baseline = Transaction();
            await journal.WriteAsync(baseline, CancellationToken.None);
            var validSnapshot = (await journal.ReadActiveRecoveryAsync(CancellationToken.None))!;
            var adapter = baseline.Snapshot.Adapters[0];
            var malformed = shape switch
            {
                "adaptersNull" => baseline with { Snapshot = baseline.Snapshot with { Adapters = null! } },
                "adapterElementNull" => baseline with { Snapshot = baseline.Snapshot with { Adapters = new AdapterState[] { null! } } },
                "adapterNestedNull" => baseline with { Snapshot = baseline.Snapshot with { Adapters = new[] { adapter with { DnsServers = null! } } } },
                "routesElementNull" => baseline with { Snapshot = baseline.Snapshot with { Routes = new RouteState[] { null! } } },
                "dnsElementNull" => baseline with { Snapshot = baseline.Snapshot with { DnsInterfaces = new DnsInterfaceState[] { null! } } },
                "changesNull" => baseline with { Changes = null! },
                _ => baseline with { Changes = new OwnedNetworkChange[] { null! } }
            };
            await journal.WriteAsync(malformed, CancellationToken.None);
            var before = await File.ReadAllBytesAsync(path);
            var proposed = DurableTransactionAt(RecoveryPhase.IntentRecorded);
            await Assert.ThrowsAsync<InvalidDataException>(() => journal.TryAdvanceRecoveryAsync(
                Expectation(validSnapshot), proposed, CancellationToken.None));
            Assert.Equal(before, await File.ReadAllBytesAsync(path));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task StaleCallerCannotOverwriteRealDifferentSessionReplacement()
    {
        var (journal, _, root) = NewRecoveryJournal();
        try
        {
            var original = Transaction();
            await journal.WriteAsync(original, CancellationToken.None);
            var stale = (await journal.ReadActiveRecoveryAsync(CancellationToken.None))!;
            var replacement = original with { SessionId = Guid.Parse("22222222-2222-2222-2222-222222222222") };
            await journal.WriteAsync(replacement, CancellationToken.None);
            var replacementSnapshot = (await journal.ReadActiveRecoveryAsync(CancellationToken.None))!;

            Assert.False(await journal.TryAdvanceRecoveryAsync(
                Expectation(stale), DurableTransactionAt(RecoveryPhase.IntentRecorded), CancellationToken.None));
            var after = (await journal.ReadActiveRecoveryAsync(CancellationToken.None))!;
            Assert.Equal(replacement.SessionId, after.Transaction.SessionId);
            Assert.Equal(replacementSnapshot.JournalRevision, after.JournalRevision);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task SamePathGateCoversExpectationMatchThroughPublication()
    {
        var (writer, path, root) = NewRecoveryJournal();
        try
        {
            var intent = DurableTransactionAt(RecoveryPhase.IntentRecorded);
            await writer.WriteAsync(intent, CancellationToken.None);
            var snapshot = (await writer.ReadActiveRecoveryAsync(CancellationToken.None))!;
            var proposed = intent with { RecoveryCompletion = intent.RecoveryCompletion!.WithPhase(RecoveryPhase.Prepared, DateTimeOffset.Parse("2026-08-26T00:00:01Z")) };
            var expectationMatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseAfterMatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var first = new FileTransactionJournal(path, async cancellationToken =>
            {
                expectationMatched.SetResult();
                await releaseAfterMatch.Task.WaitAsync(cancellationToken);
            });
            var second = new FileTransactionJournal(path);

            var firstAdvance = first.TryAdvanceRecoveryAsync(Expectation(snapshot), proposed, CancellationToken.None);
            await expectationMatched.Task;
            var secondAdvance = second.TryAdvanceRecoveryAsync(Expectation(snapshot), proposed, CancellationToken.None);
            Assert.False(secondAdvance.IsCompleted);
            releaseAfterMatch.SetResult();
            Assert.True(await firstAdvance);
            Assert.False(await secondAdvance);
            var final = (await writer.ReadActiveRecoveryAsync(CancellationToken.None))!;
            Assert.Equal(RecoveryPhase.Prepared, final.Transaction.RecoveryCompletion!.Phase);
            Assert.NotEqual(snapshot.JournalRevision, final.JournalRevision);
            Assert.Equal(string.Empty, RecoveryCompletion.Validate(final.Transaction.RecoveryCompletion));
            Assert.Equal(string.Empty, RecoveryCompletion.ValidateDurableManifest(final.Transaction.RecoveryCompletion, final.Transaction));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task SamePhaseAndCancellationBeforePublicationDoNotReplaceJournal()
    {
        var (journal, _, root) = NewRecoveryJournal();
        try
        {
            var intent = DurableTransactionAt(RecoveryPhase.IntentRecorded);
            await journal.WriteAsync(intent, CancellationToken.None);
            var snapshot = (await journal.ReadActiveRecoveryAsync(CancellationToken.None))!;
            await Assert.ThrowsAsync<InvalidOperationException>(() => journal.TryAdvanceRecoveryAsync(
                Expectation(snapshot), intent, CancellationToken.None));

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var prepared = intent with { RecoveryCompletion = intent.RecoveryCompletion!.WithPhase(RecoveryPhase.Prepared, DateTimeOffset.Parse("2026-08-26T00:00:01Z")) };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => journal.TryAdvanceRecoveryAsync(
                Expectation(snapshot), prepared, cancellation.Token));
            Assert.Equal(snapshot.JournalRevision, (await journal.ReadActiveRecoveryAsync(CancellationToken.None))!.JournalRevision);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static (FileTransactionJournal Journal, string Path, string Root) NewRecoveryJournal()
    {
        var root = Path.Combine(Path.GetTempPath(), "LibertyRoute.Recovery.Cas.Tests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "active.lrj");
        return (new FileTransactionJournal(path), path, root);
    }

    private static RecoveryTransitionExpectation Expectation(RecoveryJournalSnapshot snapshot)
    {
        var completion = snapshot.Transaction.RecoveryCompletion;
        return new RecoveryTransitionExpectation(
            snapshot.Transaction.SessionId,
            snapshot.JournalRevision,
            completion?.Phase,
            completion?.RecoveryAttemptId,
            RecoveryFingerprinting.ComputeAuthorizedTransactionFingerprint(snapshot.Transaction),
            completion?.RecoveryManifestFingerprint);
    }

    private static NetworkTransaction DurableTransactionAt(RecoveryPhase phase)
    {
        var transaction = Transaction();
        var authorizedFingerprint = RecoveryFingerprinting.ComputeAuthorizedTransactionFingerprint(transaction);
        var completion = CompletionAt(phase);
        var manifest = completion.Manifest! with
        {
            AuthorizedTransactionFingerprint = authorizedFingerprint,
            PreparationFingerprint = completion.PreparationFingerprint
        };
        completion = completion with
        {
            AuthorizedTransactionFingerprint = authorizedFingerprint,
            RecoveryManifestFingerprint = RecoveryFingerprinting.ComputeRecoveryManifestFingerprint(manifest),
            Manifest = manifest
        };
        return transaction with { RecoveryCompletion = completion };
    }

    private static RecoveryCompletion CompletionAt(RecoveryPhase phase, DateTimeOffset? intentAt = null)
    {
        var attemptId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var authorizedFingerprint = "authorized-fingerprint";
        var manifest = RecoveryManifest.Create(
            attemptId,
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "S-1-5-21-1001",
            authorizedFingerprint,
            new[] { RecoveryEvidenceBinding.Create(Guid.Parse("44444444-4444-4444-4444-444444444444"), "evidence:route", "binding-fingerprint") },
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            "route-change",
            "route",
            "route-10.0.0.0/24",
            "before",
            "after",
            1,
            "prep-fingerprint");
        var start = intentAt ?? DateTimeOffset.Parse("2026-08-26T00:00:00Z");
        var completion = new RecoveryCompletion(
            attemptId,
            RecoveryPhase.IntentRecorded,
            authorizedFingerprint,
            RecoveryFingerprinting.ComputeRecoveryManifestFingerprint(manifest),
            "44444444-4444-4444-4444-444444444444:evidence:route:binding-fingerprint",
            "prep-fingerprint",
            start)
        { Manifest = manifest };

        if (phase == RecoveryPhase.IntentRecorded)
            return completion;

        foreach (var next in new[] { RecoveryPhase.Prepared, RecoveryPhase.ExecutionStarted, RecoveryPhase.ExecutionCompleted, RecoveryPhase.BaselineVerified, RecoveryPhase.LedgerFinalizing, RecoveryPhase.LedgerFinalized, RecoveryPhase.TerminalCommitted })
        {
            completion = completion.WithPhase(next, start.AddSeconds((int)next));
            if (next == phase)
                return completion;
        }

        throw new ArgumentOutOfRangeException(nameof(phase));
    }

    [Fact]
    public async Task DistinctJournalInstancesShareSamePathLock()
    {
        var path = Path.Combine(Path.GetTempPath(), "LibertyRoute.Recovery.Tests." + Guid.NewGuid().ToString("N") + ".lrj");
        var first = new FileTransactionJournal(path);
        var second = new FileTransactionJournal(path);

        var registryType = typeof(FileTransactionJournal).Assembly.GetType("LibertyRoute.Recovery.FileTransactionJournalLocks")!;
        var gateMethod = registryType.GetMethod("Get", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        var firstGate = gateMethod.Invoke(null, new object[] { path });
        var secondGate = gateMethod.Invoke(null, new object[] { path });
        Assert.Same(firstGate, secondGate);

        var gate = (SemaphoreSlim)firstGate!;
        await gate.WaitAsync();
        var blocked = second.WriteAsync(Transaction(), CancellationToken.None);
        await Task.Yield();
        Assert.False(blocked.IsCompleted);
        gate.Release();
        await blocked;
    }

    [Fact]
    public async Task ConditionalClearMatchesOnlyExactTerminalIdentity()
    {
        var root = Path.Combine(Path.GetTempPath(), "LibertyRoute.Recovery.Tests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "active.lrj");

        try
        {
            var journal = new FileTransactionJournal(path);
            var other = new FileTransactionJournal(path);
            var sessionId = Guid.Parse("11111111-1111-1111-1111-111111111111");
            var attemptId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

            var baseTransaction = Transaction(ownerSid: "S-1-5-21-1001");
            var tx = baseTransaction with { SessionId = sessionId };
            var authFingerprint = RecoveryFingerprinting.ComputeAuthorizedTransactionFingerprint(tx);

            var changeId = OwnershipIdentity.DeriveChangeId(sessionId, "recover-route");
            var manifest = RecoveryManifest.Create(
                attemptId,
                sessionId,
                "S-1-5-21-1001",
                authFingerprint,
                new[]
                {
                    RecoveryEvidenceBinding.Create(Guid.Parse("44444444-4444-4444-4444-444444444444"), "evidence:route", "binding-fingerprint")
                },
                changeId,
                "route-change",
                "route",
                "route-10.0.0.0/24",
                "10.0.0.0/24",
                "10.0.0.0/24",
                1,
                "prep-fingerprint");

            var manifestFingerprint = RecoveryFingerprinting.ComputeRecoveryManifestFingerprint(manifest);

            var completion = new RecoveryCompletion(
                attemptId,
                RecoveryPhase.TerminalCommitted,
                authFingerprint,
                manifestFingerprint,
                "44444444-4444-4444-4444-444444444444:evidence:route:binding-fingerprint",
                "prep-fingerprint",
                DateTimeOffset.Parse("2026-08-26T00:00:00Z"),
                DateTimeOffset.Parse("2026-08-26T00:00:00Z"),
                DateTimeOffset.Parse("2026-08-26T00:00:00Z"),
                DateTimeOffset.Parse("2026-08-26T00:00:00Z"),
                DateTimeOffset.Parse("2026-08-26T00:00:00Z"),
                DateTimeOffset.Parse("2026-08-26T00:00:00Z"),
                DateTimeOffset.Parse("2026-08-26T00:00:00Z"),
                DateTimeOffset.Parse("2026-08-26T00:00:00Z"),
                null,
                null)
            { Manifest = manifest };

            var txWithCompletion = tx with { RecoveryCompletion = completion };
            await journal.WriteAsync(txWithCompletion, CancellationToken.None);

            Assert.True(await journal.TryClearTerminalRecoveryAsync(sessionId, authFingerprint, attemptId, manifestFingerprint, CancellationToken.None));
            Assert.False(File.Exists(path));

            var wrongAttemptId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
            var wrongManifest = manifest with { RecoveryAttemptId = wrongAttemptId };
            var wrongManifestFingerprint = RecoveryFingerprinting.ComputeRecoveryManifestFingerprint(wrongManifest);

            var wrongCompletion = new RecoveryCompletion(
                wrongAttemptId,
                RecoveryPhase.TerminalCommitted,
                authFingerprint,
                wrongManifestFingerprint,
                "44444444-4444-4444-4444-444444444444:evidence:route:binding-fingerprint",
                "prep-fingerprint",
                DateTimeOffset.Parse("2026-08-26T00:00:00Z"),
                DateTimeOffset.Parse("2026-08-26T00:00:00Z"),
                DateTimeOffset.Parse("2026-08-26T00:00:00Z"),
                DateTimeOffset.Parse("2026-08-26T00:00:00Z"),
                DateTimeOffset.Parse("2026-08-26T00:00:00Z"),
                DateTimeOffset.Parse("2026-08-26T00:00:00Z"),
                DateTimeOffset.Parse("2026-08-26T00:00:00Z"),
                DateTimeOffset.Parse("2026-08-26T00:00:00Z"),
                null,
                null)
            { Manifest = wrongManifest };

            var wrongTx = tx with { RecoveryCompletion = wrongCompletion };
            await journal.WriteAsync(wrongTx, CancellationToken.None);

            Assert.False(await journal.TryClearTerminalRecoveryAsync(sessionId, authFingerprint, attemptId, manifestFingerprint, CancellationToken.None));
            Assert.True(File.Exists(path));

            var gate = (SemaphoreSlim)typeof(FileTransactionJournal).Assembly.GetType("LibertyRoute.Recovery.FileTransactionJournalLocks")!
                .GetMethod("Get", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, new object[] { path })!;
            await gate.WaitAsync();
            var blocked = other.TryClearTerminalRecoveryAsync(sessionId, authFingerprint, attemptId, manifestFingerprint, CancellationToken.None);
            await Task.Yield();
            Assert.False(blocked.IsCompleted);
            gate.Release();
            Assert.False(await blocked);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task WriteAsyncBlocksTerminalClearAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "LibertyRoute.Recovery.Tests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "active.lrj");

        try
        {
            var first = new FileTransactionJournal(path);
            var second = new FileTransactionJournal(path);
            var sessionId = Guid.Parse("11111111-1111-1111-1111-111111111111");
            var tx = Transaction() with { SessionId = sessionId };

            await first.WriteAsync(tx, CancellationToken.None);

            var gate = (SemaphoreSlim)typeof(FileTransactionJournal).Assembly.GetType("LibertyRoute.Recovery.FileTransactionJournalLocks")!
                .GetMethod("Get", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .Invoke(null, new object[] { path })!;

            await gate.WaitAsync();
            var blockedWrite = first.WriteAsync(Transaction() with { SessionId = sessionId }, CancellationToken.None);
            await Task.Yield();
            Assert.False(blockedWrite.IsCompleted);
            gate.Release();
            await blockedWrite;
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ClearAsyncBlocksTerminalClearAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "LibertyRoute.Recovery.Tests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "active.lrj");

        try
        {
            var first = new FileTransactionJournal(path);
            var second = new FileTransactionJournal(path);
            var sessionId = Guid.Parse("11111111-1111-1111-1111-111111111111");
            var tx = Transaction() with { SessionId = sessionId };

            await first.WriteAsync(tx, CancellationToken.None);

            var gate = (SemaphoreSlim)typeof(FileTransactionJournal).Assembly.GetType("LibertyRoute.Recovery.FileTransactionJournalLocks")!
                .GetMethod("Get", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .Invoke(null, new object[] { path })!;

            await gate.WaitAsync();
            var blockedClear = first.ClearAsync(sessionId, CancellationToken.None);
            await Task.Yield();
            Assert.False(blockedClear.IsCompleted);
            gate.Release();
            await blockedClear;
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task TwoTerminalClearsSerializeCorrectly()
    {
        var root = Path.Combine(Path.GetTempPath(), "LibertyRoute.Recovery.Tests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "active.lrj");

        try
        {
            var first = new FileTransactionJournal(path);
            var second = new FileTransactionJournal(path);
            var sessionId = Guid.Parse("11111111-1111-1111-1111-111111111111");
            var attemptId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

            var baseTransaction = Transaction(ownerSid: "S-1-5-21-1001");
            var tx = baseTransaction with { SessionId = sessionId };
            var authFingerprint = RecoveryFingerprinting.ComputeAuthorizedTransactionFingerprint(tx);

            var changeId = OwnershipIdentity.DeriveChangeId(sessionId, "recover-route");
            var manifest = RecoveryManifest.Create(
                attemptId,
                sessionId,
                "S-1-5-21-1001",
                authFingerprint,
                new[]
                {
                    RecoveryEvidenceBinding.Create(Guid.Parse("44444444-4444-4444-4444-444444444444"), "evidence:route", "binding-fingerprint")
                },
                changeId,
                "route-change",
                "route",
                "route-10.0.0.0/24",
                "10.0.0.0/24",
                "10.0.0.0/24",
                1,
                "prep-fingerprint");

            var manifestFingerprint = RecoveryFingerprinting.ComputeRecoveryManifestFingerprint(manifest);

            var completion = new RecoveryCompletion(
                attemptId,
                RecoveryPhase.TerminalCommitted,
                authFingerprint,
                manifestFingerprint,
                "44444444-4444-4444-4444-444444444444:evidence:route:binding-fingerprint",
                "prep-fingerprint",
                DateTimeOffset.Parse("2026-08-26T00:00:00Z"),
                DateTimeOffset.Parse("2026-08-26T00:00:00Z"),
                DateTimeOffset.Parse("2026-08-26T00:00:00Z"),
                DateTimeOffset.Parse("2026-08-26T00:00:00Z"),
                DateTimeOffset.Parse("2026-08-26T00:00:00Z"),
                DateTimeOffset.Parse("2026-08-26T00:00:00Z"),
                DateTimeOffset.Parse("2026-08-26T00:00:00Z"),
                DateTimeOffset.Parse("2026-08-26T00:00:00Z"),
                null,
                null)
            { Manifest = manifest };

            var txWithCompletion = tx with { RecoveryCompletion = completion };
            await first.WriteAsync(txWithCompletion, CancellationToken.None);

            var gate = (SemaphoreSlim)typeof(FileTransactionJournal).Assembly.GetType("LibertyRoute.Recovery.FileTransactionJournalLocks")!
                .GetMethod("Get", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .Invoke(null, new object[] { path })!;

            await gate.WaitAsync();
            var blockedClear = second.TryClearTerminalRecoveryAsync(sessionId, authFingerprint, attemptId, manifestFingerprint, CancellationToken.None);
            await Task.Yield();
            Assert.False(blockedClear.IsCompleted);
            gate.Release();
            Assert.True(await blockedClear);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ReplacementMismatchRemainsIntact()
    {
        var root = Path.Combine(Path.GetTempPath(), "LibertyRoute.Recovery.Tests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "active.lrj");

        try
        {
            var journal = new FileTransactionJournal(path);
            var sessionId = Guid.Parse("11111111-1111-1111-1111-111111111111");
            var attemptId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

            var baseTransaction = Transaction(ownerSid: "S-1-5-21-1001");
            var tx = baseTransaction with { SessionId = sessionId };
            var authFingerprint = RecoveryFingerprinting.ComputeAuthorizedTransactionFingerprint(tx);

            var changeId = OwnershipIdentity.DeriveChangeId(sessionId, "recover-route");
            var manifest = RecoveryManifest.Create(
                attemptId,
                sessionId,
                "S-1-5-21-1001",
                authFingerprint,
                new[]
                {
                    RecoveryEvidenceBinding.Create(Guid.Parse("44444444-4444-4444-4444-444444444444"), "evidence:route", "binding-fingerprint")
                },
                changeId,
                "route-change",
                "route",
                "route-10.0.0.0/24",
                "10.0.0.0/24",
                "10.0.0.0/24",
                1,
                "prep-fingerprint");

            var manifestFingerprint = RecoveryFingerprinting.ComputeRecoveryManifestFingerprint(manifest);

            var completion = new RecoveryCompletion(
                attemptId,
                RecoveryPhase.TerminalCommitted,
                authFingerprint,
                manifestFingerprint,
                "44444444-4444-4444-4444-444444444444:evidence:route:binding-fingerprint",
                "prep-fingerprint",
                DateTimeOffset.Parse("2026-08-26T00:00:00Z"),
                DateTimeOffset.Parse("2026-08-26T00:00:00Z"),
                DateTimeOffset.Parse("2026-08-26T00:00:00Z"),
                DateTimeOffset.Parse("2026-08-26T00:00:00Z"),
                DateTimeOffset.Parse("2026-08-26T00:00:00Z"),
                DateTimeOffset.Parse("2026-08-26T00:00:00Z"),
                DateTimeOffset.Parse("2026-08-26T00:00:00Z"),
                DateTimeOffset.Parse("2026-08-26T00:00:00Z"),
                null,
                null)
            { Manifest = manifest };

            var txWithCompletion = tx with { RecoveryCompletion = completion };
            await journal.WriteAsync(txWithCompletion, CancellationToken.None);

            Assert.True(await journal.TryClearTerminalRecoveryAsync(sessionId, authFingerprint, attemptId, manifestFingerprint, CancellationToken.None));
            Assert.False(File.Exists(path));

            var differentAttemptId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
            var differentManifest = manifest with { RecoveryAttemptId = differentAttemptId };
            var differentManifestFingerprint = RecoveryFingerprinting.ComputeRecoveryManifestFingerprint(differentManifest);

            var differentCompletion = new RecoveryCompletion(
                differentAttemptId,
                RecoveryPhase.TerminalCommitted,
                authFingerprint,
                differentManifestFingerprint,
                "44444444-4444-4444-4444-444444444444:evidence:route:binding-fingerprint",
                "prep-fingerprint",
                DateTimeOffset.Parse("2026-08-26T00:00:00Z"),
                DateTimeOffset.Parse("2026-08-26T00:00:00Z"),
                DateTimeOffset.Parse("2026-08-26T00:00:00Z"),
                DateTimeOffset.Parse("2026-08-26T00:00:00Z"),
                DateTimeOffset.Parse("2026-08-26T00:00:00Z"),
                DateTimeOffset.Parse("2026-08-26T00:00:00Z"),
                DateTimeOffset.Parse("2026-08-26T00:00:00Z"),
                DateTimeOffset.Parse("2026-08-26T00:00:00Z"),
                null,
                null)
            { Manifest = differentManifest };

            var txReplacement = tx with { RecoveryCompletion = differentCompletion };
            await journal.WriteAsync(txReplacement, CancellationToken.None);

            Assert.False(await journal.TryClearTerminalRecoveryAsync(sessionId, authFingerprint, attemptId, manifestFingerprint, CancellationToken.None));
            Assert.True(File.Exists(path));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ManifestFingerprintMutatesOnRecoveryAttemptIdChange()
    {
        var attemptId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var changeId = OwnershipIdentity.DeriveChangeId(Guid.Parse("11111111-1111-1111-1111-111111111111"), "recover-route");
        var manifest = RecoveryManifest.Create(
            attemptId,
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "S-1-5-21-1001",
            "authorized-fingerprint",
            new[]
            {
                RecoveryEvidenceBinding.Create(Guid.Parse("44444444-4444-4444-4444-444444444444"), "evidence:route", "binding-fingerprint")
            },
            changeId,
            "route-change",
            "route",
            "route-10.0.0.0/24",
            "10.0.0.0/24",
            "10.0.0.0/24",
            1,
            "prep-fingerprint");

        var before = RecoveryFingerprinting.ComputeRecoveryManifestFingerprint(manifest);

        var mutated = RecoveryManifest.Create(
            Guid.NewGuid(),
            manifest.SessionId,
            manifest.OwnerSid,
            manifest.AuthorizedTransactionFingerprint,
            manifest.OriginalEvidenceBindings,
            changeId,
            manifest.OperationIdentity,
            manifest.OperationCategory,
            manifest.TargetIdentity,
            manifest.OriginalValue,
            manifest.AppliedValue,
            manifest.SequenceOrder,
            manifest.PreparationFingerprint);

        Assert.NotEqual(before, RecoveryFingerprinting.ComputeRecoveryManifestFingerprint(mutated));
    }

    [Fact]
    public void AuthorizedTransactionFingerprintMutatesOnStateChange()
    {
        var tx = Transaction();
        var before = RecoveryFingerprinting.ComputeAuthorizedTransactionFingerprint(tx);

        var altered = tx with { State = ConnectionState.RollingBack };
        var after = RecoveryFingerprinting.ComputeAuthorizedTransactionFingerprint(altered);

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void AuthorizedTransactionFingerprintMutatesOnStartedAtUtcChange()
    {
        var tx = Transaction();
        var before = RecoveryFingerprinting.ComputeAuthorizedTransactionFingerprint(tx);

        var altered = tx with { StartedAtUtc = DateTimeOffset.UtcNow.AddDays(1) };
        var after = RecoveryFingerprinting.ComputeAuthorizedTransactionFingerprint(altered);

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void AuthorizedTransactionFingerprintMutatesOnEngineIdChange()
    {
        var tx = Transaction();
        var before = RecoveryFingerprinting.ComputeAuthorizedTransactionFingerprint(tx);

        var altered = tx with { EngineId = "engine-2" };
        var after = RecoveryFingerprinting.ComputeAuthorizedTransactionFingerprint(altered);

        Assert.NotEqual(before, after);
    }

    [Fact]
    public async Task MalformedManifestFingerprintThrowsOnRead()
    {
        var root = Path.Combine(Path.GetTempPath(), "LibertyRoute.Recovery.Tests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "active.lrj");

        try
        {
            var journal = new FileTransactionJournal(path);
            var sessionId = Guid.Parse("11111111-1111-1111-1111-111111111111");
            var attemptId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
            var authFingerprint = "authorized-fingerprint";
            var manifestFingerprint = "wrong-manifest-fingerprint";

            var changeId = OwnershipIdentity.DeriveChangeId(sessionId, "recover-route");
            var manifest = RecoveryManifest.Create(
                attemptId,
                sessionId,
                "S-1-5-21-1001",
                authFingerprint,
                new[]
                {
                    RecoveryEvidenceBinding.Create(Guid.Parse("44444444-4444-4444-4444-444444444444"), "evidence:route", "binding-fingerprint")
                },
                changeId,
                "route-change",
                "route",
                "route-10.0.0.0/24",
                "10.0.0.0/24",
                "10.0.0.0/24",
                1,
                "prep-fingerprint");

            var completion = new RecoveryCompletion(
                attemptId,
                RecoveryPhase.TerminalCommitted,
                authFingerprint,
                manifestFingerprint,
                "44444444-4444-4444-4444-444444444444:evidence:route:binding-fingerprint",
                "prep-fingerprint",
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                null,
                null)
            { Manifest = manifest };

            var tx = Transaction(ownerSid: "S-1-5-21-1001", recoveryCompletion: completion);
            var txWithSession = tx with { SessionId = sessionId };
            await journal.WriteAsync(txWithSession, CancellationToken.None);

            await Assert.ThrowsAsync<InvalidDataException>(() => journal.ReadActiveAsync(CancellationToken.None));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ChangeIdDerivationIsStable()
    {
        var sessionId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var operationIdentity = "test-operation";
        var expectedChangeId = Guid.Parse("482aff7c-7505-71b1-15a4-3521282c0b8f");

        var derived1 = OwnershipIdentity.DeriveChangeId(sessionId, operationIdentity);
        var derived2 = OwnershipIdentity.DeriveChangeId(sessionId, operationIdentity);

        Assert.Equal(expectedChangeId, derived1);
        Assert.Equal(derived1, derived2);
    }

    [Fact]
    public void AuthorizedTransactionFingerprintIsStableAcrossJsonSerialization()
    {
        var tx = Transaction();
        var sessionTx = tx with { SessionId = Guid.Parse("11111111-1111-1111-1111-111111111111") };

        var fp1 = RecoveryFingerprinting.ComputeAuthorizedTransactionFingerprint(sessionTx);

        // Serialize using FileTransactionJournal's options
        var fileJournalJsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        var json = JsonSerializer.Serialize(sessionTx, fileJournalJsonOptions);
        var deserialized = JsonSerializer.Deserialize<NetworkTransaction>(json, fileJournalJsonOptions);

        Assert.NotNull(deserialized);
        var fp2 = RecoveryFingerprinting.ComputeAuthorizedTransactionFingerprint(deserialized!);

        Assert.Equal(fp1, fp2);
    }

    [Fact]
    public void AuthorizedTransactionFingerprintIsStableAcrossJsonSerializationWithRecoveryCompletion()
    {
        var sessionId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var baseTransaction = Transaction(ownerSid: "S-1-5-21-1001");
        var tx = baseTransaction with { SessionId = sessionId };

        var fp1 = RecoveryFingerprinting.ComputeAuthorizedTransactionFingerprint(tx);

        // Create a recovery completion with the computed fingerprint
        var completion = new RecoveryCompletion(
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            RecoveryPhase.TerminalCommitted,
            fp1,
            "manifest-fingerprint",
            "evidence-bindings",
            "prep-fingerprint",
            DateTimeOffset.Parse("2026-08-26T00:00:00Z"),
            DateTimeOffset.Parse("2026-08-26T00:00:00Z"),
            DateTimeOffset.Parse("2026-08-26T00:00:00Z"),
            DateTimeOffset.Parse("2026-08-26T00:00:00Z"),
            DateTimeOffset.Parse("2026-08-26T00:00:00Z"),
            DateTimeOffset.Parse("2026-08-26T00:00:00Z"),
            DateTimeOffset.Parse("2026-08-26T00:00:00Z"),
            DateTimeOffset.Parse("2026-08-26T00:00:00Z"),
            null,
            null);

        var txWithCompletion = tx with { RecoveryCompletion = completion };

        // Serialize using FileTransactionJournal's options
        var fileJournalJsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        var json = JsonSerializer.Serialize(txWithCompletion, fileJournalJsonOptions);
        var deserialized = JsonSerializer.Deserialize<NetworkTransaction>(json, fileJournalJsonOptions);

        Assert.NotNull(deserialized);
        var fp2 = RecoveryFingerprinting.ComputeAuthorizedTransactionFingerprint(deserialized!);

        Assert.Equal(fp1, fp2);
    }
 }
