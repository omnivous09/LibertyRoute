using System.Reflection;
using System.Security.Cryptography;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LibertyRoute.Core;
using LibertyRoute.Restoration.Windows;
using Xunit;

namespace LibertyRoute.Restoration.Tests;

public sealed class OwnershipLedgerTests : IAsyncLifetime
{
    private static readonly Guid SessionA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SessionB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private const string RouteValue = "destination=10.0.0.0/24;nextHop=10.0.0.1;interfaceIndex=4;metric=1";
    private static readonly DateTimeOffset RecordedAt = DateTimeOffset.Parse("2026-08-26T00:00:00Z");

    private static readonly JsonSerializerOptions RawJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private sealed record RawEnvelope(string Payload, string Sha256);

    private string _root = string.Empty;

    public Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "LibertyRoute.Tests.OwnershipLedger", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Temp cleanup must never fail a test run.
        }

        await Task.CompletedTask;
    }

    private FileOwnershipLedger NewLedger() => new(_root);

    private static PersistedOwnedChange Record(
        Guid? sessionId = null,
        Guid? changeId = null,
        string? target = null,
        string? original = null,
        string? applied = null,
        DryRunOperationCategory category = DryRunOperationCategory.Route,
        int? sequence = null,
        DateTimeOffset? recordedAt = null,
        OwnershipEvidenceSource source = OwnershipEvidenceSource.MutationLedger,
        OwnedChangeLifecycle lifecycle = OwnedChangeLifecycle.Planned,
        RecordPurpose purpose = RecordPurpose.SessionMutation,
        Guid? recoveryAttemptId = null,
        Guid? authorizationEvidenceId = null)
        => PersistedOwnedChange.Create(
            sessionId ?? SessionA,
            changeId ?? Guid.NewGuid(),
            category,
            target ?? "route-10.0.0.0/24",
            original ?? RouteValue,
            applied ?? "<absent>",
            recordedAt ?? RecordedAt,
            sequence,
            source,
            lifecycle,
            purpose,
            recoveryAttemptId,
            authorizationEvidenceId);

    private static PersistedOwnedChange ExactRecord(
        Guid? changeId = null, OwnedChangeLifecycle lifecycle = OwnedChangeLifecycle.Planned)
        => PersistedOwnedChange.CreateExactRoute(SessionA, changeId ?? Guid.NewGuid(),
            DryRunOperationCategory.Route, "route-192.0.2.0/24", RouteValue, "<absent>",
            RecordedAt, 1, OwnershipEvidenceSource.MutationLedger, lifecycle,
            new ExactRouteMutationIdentity(1,
                NativeRouteKey.Create(NativeRouteAddressFamily.IPv4, IPAddress.Parse("192.0.2.0"), 24,
                    IPAddress.Parse("192.0.2.1"), 42),
                new NativeRouteProfile(24, 3600, 1800, 5, 3, false, true, false, false)),
            Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));

    private static PersistedOwnedChange ResignExact(PersistedOwnedChange record) => record with
    {
        ExactRouteEvidenceFingerprint = ExactRouteOwnershipEvidenceBinding.ComputeFingerprint(record)
    };

    private static async Task WriteRawLedgerAsync(string root, Guid sessionId, string payload)
    {
        var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
        var envelope = JsonSerializer.Serialize(new RawEnvelope(payload, checksum), RawJsonOptions);
        await File.WriteAllTextAsync(Path.Combine(root, $"{sessionId:N}.lrw"), envelope);
    }

    [Fact]
    public async Task MissingLedgerReadsAsEmpty()
    {
        var ledger = NewLedger();
        var records = await ledger.ReadForSessionAsync(SessionA, CancellationToken.None);
        Assert.Empty(records);
    }

    [Fact]
    public async Task AppendThenReadReturnsExactRecord()
    {
        var ledger = NewLedger();
        var record = Record(lifecycle: OwnedChangeLifecycle.Applied, sequence: 1);

        await ledger.AppendAsync(record, CancellationToken.None);
        var read = await ledger.ReadForSessionAsync(SessionA, CancellationToken.None);

        var stored = Assert.Single(read);
        Assert.Equal(record, stored);
    }

    [Fact]
    public async Task MultipleRecordsRoundTripInDeterministicOrder()
    {
        var ledger = NewLedger();
        var third = Record(changeId: Guid.NewGuid(), sequence: 3);
        var first = Record(changeId: Guid.NewGuid(), sequence: 1);
        var second = Record(changeId: Guid.NewGuid(), sequence: 2);

        await ledger.AppendAsync(third, CancellationToken.None);
        await ledger.AppendAsync(first, CancellationToken.None);
        await ledger.AppendAsync(second, CancellationToken.None);

        var read = await ledger.ReadForSessionAsync(SessionA, CancellationToken.None);
        Assert.Equal(new[] { first.ChangeId, second.ChangeId, third.ChangeId }, read.Select(record => record.ChangeId));
    }

    [Fact]
    public async Task RecordsWithoutSequenceSortByChangeIdThenTimestamp()
    {
        var ledger = NewLedger();
        var earlierChange = Record(changeId: Guid.Parse("33333333-3333-3333-3333-333333333333"));
        var laterChange = Record(changeId: Guid.Parse("44444444-4444-4444-4444-444444444444"));

        await ledger.AppendAsync(laterChange, CancellationToken.None);
        await ledger.AppendAsync(earlierChange, CancellationToken.None);

        var read = await ledger.ReadForSessionAsync(SessionA, CancellationToken.None);
        Assert.Equal(new[] { earlierChange.ChangeId, laterChange.ChangeId }, read.Select(record => record.ChangeId));
    }

    [Fact]
    public async Task RecordsAreIsolatedPerSession()
    {
        var ledger = NewLedger();
        var sharedChangeId = Guid.NewGuid();
        var recordA = Record(sessionId: SessionA, changeId: sharedChangeId, target: "route-a", lifecycle: OwnedChangeLifecycle.Applied);
        var recordB = Record(sessionId: SessionB, changeId: sharedChangeId, target: "route-b");

        await ledger.AppendAsync(recordA, CancellationToken.None);
        await ledger.AppendAsync(recordB, CancellationToken.None);

        var readA = await ledger.ReadForSessionAsync(SessionA, CancellationToken.None);
        var readB = await ledger.ReadForSessionAsync(SessionB, CancellationToken.None);

        Assert.Equal("route-a", Assert.Single(readA).TargetIdentity);
        Assert.Equal("route-b", Assert.Single(readB).TargetIdentity);
    }

    [Fact]
    public async Task StaleSessionEvidenceIsNotReturnedForActiveSession()
    {
        var ledger = NewLedger();
        await ledger.AppendAsync(Record(sessionId: SessionB, lifecycle: OwnedChangeLifecycle.Applied), CancellationToken.None);

        var activeRead = await ledger.ReadForSessionAsync(SessionA, CancellationToken.None);

        Assert.Empty(activeRead);
    }

    [Fact]
    public async Task IdenticalDuplicateAppendIsNoOp()
    {
        var ledger = NewLedger();
        var record = Record(sequence: 1);

        await ledger.AppendAsync(record, CancellationToken.None);
        await ledger.AppendAsync(record, CancellationToken.None);

        var read = await ledger.ReadForSessionAsync(SessionA, CancellationToken.None);
        Assert.Equal(record, Assert.Single(read));
    }

    [Fact]
    public async Task SameLifecycleDuplicateWithMismatchedImmutableFieldsFailsClosed()
    {
        var ledger = NewLedger();
        var changeId = Guid.NewGuid();
        await ledger.AppendAsync(Record(changeId: changeId, target: "route-original"), CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ledger.AppendAsync(Record(changeId: changeId, target: "route-changed"), CancellationToken.None));
    }

    public static TheoryData<string> ChangedFieldCases => new()
    {
        nameof(PersistedOwnedChange.TargetIdentity),
        nameof(PersistedOwnedChange.OriginalValue),
        nameof(PersistedOwnedChange.AppliedValue),
        nameof(PersistedOwnedChange.Category),
        nameof(PersistedOwnedChange.SequenceNumber),
        nameof(PersistedOwnedChange.EvidenceSource),
        nameof(PersistedOwnedChange.RecordedAtUtc)
    };

    [Theory]
    [MemberData(nameof(ChangedFieldCases))]
    public async Task SameChangeIdWithAnyChangedImmutableFieldFailsClosed(string changedField)
    {
        var ledger = NewLedger();
        var changeId = Guid.NewGuid();
        var original = Record(changeId: changeId, sequence: 7);
        await ledger.AppendAsync(original, CancellationToken.None);

        var mutated = changedField switch
        {
            nameof(PersistedOwnedChange.TargetIdentity) => Record(changeId: changeId, sequence: 7, target: "route-other"),
            nameof(PersistedOwnedChange.OriginalValue) => Record(changeId: changeId, sequence: 7, original: "destination=192.0.2.0/24;nextHop=192.0.2.1;interfaceIndex=4;metric=1"),
            nameof(PersistedOwnedChange.AppliedValue) => Record(changeId: changeId, sequence: 7, applied: "destination=192.0.2.0/24"),
            nameof(PersistedOwnedChange.Category) => Record(changeId: changeId, sequence: 7, category: DryRunOperationCategory.Dns),
            nameof(PersistedOwnedChange.SequenceNumber) => Record(changeId: changeId, sequence: 9),
            nameof(PersistedOwnedChange.EvidenceSource) => Record(changeId: changeId, sequence: 7, source: OwnershipEvidenceSource.TransactionJournal),
            nameof(PersistedOwnedChange.RecordedAtUtc) => Record(changeId: changeId, sequence: 7, recordedAt: RecordedAt.AddMinutes(1)),
            _ => throw new InvalidOperationException($"Unexpected field {changedField}.")
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ledger.AppendAsync(mutated, CancellationToken.None));

        var read = await ledger.ReadForSessionAsync(SessionA, CancellationToken.None);
        Assert.Equal(original, Assert.Single(read));
    }

    [Fact]
    public async Task LifecyclePlannedToAppliedSucceedsAndCompletesEvidence()
    {
        var ledger = NewLedger();
        var changeId = Guid.NewGuid();
        var planned = Record(changeId: changeId, lifecycle: OwnedChangeLifecycle.Planned);
        var applied = Record(changeId: changeId, lifecycle: OwnedChangeLifecycle.Applied);

        await ledger.AppendAsync(planned, CancellationToken.None);
        await ledger.AppendAsync(applied, CancellationToken.None);

        var stored = Assert.Single(await ledger.ReadForSessionAsync(SessionA, CancellationToken.None));
        Assert.Equal(OwnedChangeLifecycle.Applied, stored.Lifecycle);
        Assert.True(stored.IsComplete);
        Assert.Equal(applied.SessionId, stored.SessionId);
        Assert.Equal(applied.Category, stored.Category);
        Assert.Equal(applied.TargetIdentity, stored.TargetIdentity);
        Assert.Equal(applied.OriginalValue, stored.OriginalValue);
        Assert.Equal(applied.AppliedValue, stored.AppliedValue);
        Assert.Equal(applied.RecordedAtUtc, stored.RecordedAtUtc);
        Assert.Equal(applied.SequenceNumber, stored.SequenceNumber);
        Assert.Equal(applied.EvidenceSource, stored.EvidenceSource);
    }

    [Fact]
    public async Task LifecycleAppliedToRevertedSucceedsAndEvidenceBecomesIncomplete()
    {
        var ledger = NewLedger();
        var changeId = Guid.NewGuid();
        await ledger.AppendAsync(Record(changeId: changeId, lifecycle: OwnedChangeLifecycle.Planned), CancellationToken.None);
        await ledger.AppendAsync(Record(changeId: changeId, lifecycle: OwnedChangeLifecycle.Applied), CancellationToken.None);
        await ledger.AppendAsync(Record(changeId: changeId, lifecycle: OwnedChangeLifecycle.Reverted), CancellationToken.None);

        var stored = Assert.Single(await ledger.ReadForSessionAsync(SessionA, CancellationToken.None));
        Assert.Equal(OwnedChangeLifecycle.Reverted, stored.Lifecycle);
        Assert.False(stored.IsComplete);
    }

    [Fact]
    public async Task LifecycleAppliedToPlannedFailsClosed()
    {
        var ledger = NewLedger();
        var changeId = Guid.NewGuid();
        await ledger.AppendAsync(Record(changeId: changeId, lifecycle: OwnedChangeLifecycle.Applied), CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ledger.AppendAsync(Record(changeId: changeId, lifecycle: OwnedChangeLifecycle.Planned), CancellationToken.None));
    }

    [Fact]
    public async Task LifecycleRevertedToAppliedFailsClosed()
    {
        var ledger = NewLedger();
        var changeId = Guid.NewGuid();
        await ledger.AppendAsync(Record(changeId: changeId, lifecycle: OwnedChangeLifecycle.Reverted), CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ledger.AppendAsync(Record(changeId: changeId, lifecycle: OwnedChangeLifecycle.Applied), CancellationToken.None));
    }

    [Fact]
    public async Task LifecyclePlannedToRevertedSkipsAStepAndFailsClosed()
    {
        var ledger = NewLedger();
        var changeId = Guid.NewGuid();
        await ledger.AppendAsync(Record(changeId: changeId, lifecycle: OwnedChangeLifecycle.Planned), CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ledger.AppendAsync(Record(changeId: changeId, lifecycle: OwnedChangeLifecycle.Reverted), CancellationToken.None));
    }

    [Fact]
    public async Task ClearSessionRemovesOnlyThatSession()
    {
        var ledger = NewLedger();
        await ledger.AppendAsync(Record(sessionId: SessionA), CancellationToken.None);
        await ledger.AppendAsync(Record(sessionId: SessionB), CancellationToken.None);

        await ledger.ClearSessionAsync(SessionA, CancellationToken.None);

        Assert.Empty(await ledger.ReadForSessionAsync(SessionA, CancellationToken.None));
        Assert.Single(await ledger.ReadForSessionAsync(SessionB, CancellationToken.None));
    }

    [Fact]
    public async Task ClearSessionWithoutFileIsNoOp()
    {
        var ledger = NewLedger();
        await ledger.ClearSessionAsync(SessionA, CancellationToken.None);
        Assert.Empty(await ledger.ReadForSessionAsync(SessionA, CancellationToken.None));
    }

    [Fact]
    public async Task ExistsAsyncReflectsAppendedRecords()
    {
        var ledger = NewLedger();
        var changeId = Guid.NewGuid();

        Assert.False(await ledger.ExistsAsync(SessionA, changeId, CancellationToken.None));
        await ledger.AppendAsync(Record(changeId: changeId), CancellationToken.None);
        Assert.True(await ledger.ExistsAsync(SessionA, changeId, CancellationToken.None));
        Assert.False(await ledger.ExistsAsync(SessionB, changeId, CancellationToken.None));
    }

    [Fact]
    public async Task RepeatedReadsAreEquivalent()
    {
        var ledger = NewLedger();
        await ledger.AppendAsync(Record(sequence: 2), CancellationToken.None);
        await ledger.AppendAsync(Record(sequence: 1), CancellationToken.None);

        var first = await ledger.ReadForSessionAsync(SessionA, CancellationToken.None);
        var second = await ledger.ReadForSessionAsync(SessionA, CancellationToken.None);

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task MalformedLedgerFailsExplicitly()
    {
        var ledger = NewLedger();
        await File.WriteAllTextAsync(Path.Combine(_root, $"{SessionA:N}.lrw"), "{ this is not json");

        await Assert.ThrowsAsync<InvalidDataException>(
            () => ledger.ReadForSessionAsync(SessionA, CancellationToken.None));
    }

    [Fact]
    public async Task TamperedPayloadFailsChecksumExplicitly()
    {
        var ledger = NewLedger();
        await ledger.AppendAsync(Record(), CancellationToken.None);

        var path = Path.Combine(_root, $"{SessionA:N}.lrw");
        var envelope = JsonSerializer.Deserialize<RawEnvelope>(await File.ReadAllTextAsync(path), RawJsonOptions)!;
        var tamperedPayload = envelope.Payload + " ";
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(new RawEnvelope(tamperedPayload, envelope.Sha256), RawJsonOptions));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => ledger.ReadForSessionAsync(SessionA, CancellationToken.None));
    }

    [Fact]
    public async Task InvalidRecordInsideValidEnvelopeFailsExplicitly()
    {
        var ledger = NewLedger();
        var invalid = new PersistedOwnedChange(SessionA, Guid.NewGuid(), DryRunOperationCategory.Route, "", RouteValue, "<absent>", RecordedAt, null, OwnershipEvidenceSource.MutationLedger, OwnedChangeLifecycle.Planned, false);
        await WriteRawLedgerAsync(_root, SessionA, JsonSerializer.Serialize(new[] { invalid }, RawJsonOptions));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => ledger.ReadForSessionAsync(SessionA, CancellationToken.None));
    }

    [Fact]
    public async Task DuplicateChangeIdInsideLedgerFailsExplicitly()
    {
        var ledger = NewLedger();
        var duplicate = Record();
        await WriteRawLedgerAsync(_root, SessionA, JsonSerializer.Serialize(new[] { duplicate, duplicate }, RawJsonOptions));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => ledger.ReadForSessionAsync(SessionA, CancellationToken.None));
    }

    [Fact]
    public async Task ForeignSessionRecordInsideLedgerFailsExplicitly()
    {
        var ledger = NewLedger();
        await WriteRawLedgerAsync(_root, SessionA, JsonSerializer.Serialize(new[] { Record(sessionId: SessionB) }, RawJsonOptions));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => ledger.ReadForSessionAsync(SessionA, CancellationToken.None));
    }

    [Fact]
    public async Task HandForgedCompletenessFlagFailsClosedOnAppend()
    {
        var ledger = NewLedger();
        var forged = new PersistedOwnedChange(SessionA, Guid.NewGuid(), DryRunOperationCategory.Route, "route-x", RouteValue, "<absent>", RecordedAt, null, OwnershipEvidenceSource.MutationLedger, OwnedChangeLifecycle.Planned, true);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ledger.AppendAsync(forged, CancellationToken.None));
        Assert.Empty(await ledger.ReadForSessionAsync(SessionA, CancellationToken.None));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void CreateRejectsEmptyIdentifiers(bool emptySession, bool emptyChange)
    {
        var failure = PersistedOwnedChange.TryCreate(
            emptySession ? Guid.Empty : SessionA,
            emptyChange ? Guid.Empty : Guid.NewGuid(),
            DryRunOperationCategory.Route,
            "route-x",
            RouteValue,
            "<absent>",
            RecordedAt,
            null,
            OwnershipEvidenceSource.MutationLedger,
            OwnedChangeLifecycle.Planned,
            out var record,
            out var reason);

        Assert.False(failure);
        Assert.Null(record);
        Assert.NotEmpty(reason);
    }

    [Theory]
    [InlineData(nameof(PersistedOwnedChange.TargetIdentity))]
    [InlineData(nameof(PersistedOwnedChange.OriginalValue))]
    [InlineData(nameof(PersistedOwnedChange.AppliedValue))]
    public void CreateRejectsBlankRequiredValues(string blankField)
    {
        var failure = !PersistedOwnedChange.TryCreate(
            SessionA,
            Guid.NewGuid(),
            DryRunOperationCategory.Route,
            blankField == nameof(PersistedOwnedChange.TargetIdentity) ? " " : "route-x",
            blankField == nameof(PersistedOwnedChange.OriginalValue) ? "" : RouteValue,
            blankField == nameof(PersistedOwnedChange.AppliedValue) ? "  " : "<absent>",
            RecordedAt,
            null,
            OwnershipEvidenceSource.MutationLedger,
            OwnedChangeLifecycle.Planned,
            out var record,
            out var reason);

        Assert.True(failure);
        Assert.Null(record);
        Assert.NotEmpty(reason);
    }

    [Fact]
    public void CreateRejectsDefaultTimestampAndNonPositiveSequence()
    {
        Assert.False(PersistedOwnedChange.TryCreate(
            SessionA, Guid.NewGuid(), DryRunOperationCategory.Route, "route-x", RouteValue, "<absent>",
            default, null, OwnershipEvidenceSource.MutationLedger, OwnedChangeLifecycle.Planned,
            out _, out var timestampReason));
        Assert.Contains("timestamp", timestampReason, StringComparison.OrdinalIgnoreCase);

        Assert.False(PersistedOwnedChange.TryCreate(
            SessionA, Guid.NewGuid(), DryRunOperationCategory.Route, "route-x", RouteValue, "<absent>",
            RecordedAt, 0, OwnershipEvidenceSource.MutationLedger, OwnedChangeLifecycle.Planned,
            out _, out var sequenceReason));
        Assert.Contains("sequence", sequenceReason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(OwnedChangeLifecycle.Planned, false)]
    [InlineData(OwnedChangeLifecycle.Applied, true)]
    [InlineData(OwnedChangeLifecycle.Reverted, false)]
    public void IsCompleteReflectsCurrentLifecycleOnly(OwnedChangeLifecycle lifecycle, bool expectedComplete)
    {
        var record = Record(lifecycle: lifecycle);
        Assert.Equal(expectedComplete, record.IsComplete);
        Assert.Equal(expectedComplete, record.ToOwnershipEvidence().IsComplete);
    }

    [Fact]
    public void MappingPreservesEveryFieldExactly()
    {
        var changeId = Guid.NewGuid();
        var record = PersistedOwnedChange.Create(
            SessionA, changeId, DryRunOperationCategory.Route, "route-x", RouteValue, "<absent>",
            RecordedAt, 5, OwnershipEvidenceSource.TransactionJournal, OwnedChangeLifecycle.Applied);

        var evidence = record.ToOwnershipEvidence();

        Assert.Equal(record.SessionId, evidence.SessionId);
        Assert.Equal(record.Category, evidence.Category);
        Assert.Equal(record.TargetIdentity, evidence.TargetIdentity);
        Assert.Equal(record.OriginalValue, evidence.OriginalValue);
        Assert.Equal(record.AppliedValue, evidence.AppliedValue);
        Assert.Equal(record.ChangeId, evidence.ChangeId);
        Assert.Equal(record.RecordedAtUtc, evidence.CreatedAtUtc);
        Assert.Equal(record.SequenceNumber, evidence.SequenceNumber);
        Assert.Equal(record.EvidenceSource, evidence.EvidenceSource);
        Assert.Equal(record.IsComplete, evidence.IsComplete);
    }

    [Fact]
    public void PersistedModelContainsNoSecretLikeMembers()
    {
        var forbiddenNameParts = new[]
        {
            "password", "passwd", "secret", "token", "credential", "certificate", "apikey", "privatekey"
        };
        var allowedPropertyTypes = new[]
        {
            typeof(Guid),
            typeof(Guid?),
            typeof(string),
            typeof(bool),
            typeof(int),
            typeof(int?),
            typeof(DateTimeOffset),
            typeof(DateTimeOffset?),
            typeof(DryRunOperationCategory),
            typeof(OwnershipEvidenceSource),
            typeof(OwnedChangeLifecycle),
            typeof(RecordPurpose),
            typeof(ExactRouteMutationIdentity)
        };

        var properties = typeof(PersistedOwnedChange).GetProperties();
        Assert.NotEmpty(properties);
        foreach (var property in properties)
        {
            var name = property.Name.ToLowerInvariant();
            Assert.DoesNotContain(forbiddenNameParts, part => name.Contains(part, StringComparison.Ordinal));
            Assert.Contains(property.PropertyType, allowedPropertyTypes);
        }
    }

    [Fact]
    public async Task ProvenanceAdversarialCasesFailClosed()
    {
        var legacyRecord = Record(sessionId: SessionA, lifecycle: OwnedChangeLifecycle.Applied);
        var legacyEnvelope = JsonSerializer.Serialize(new[] { legacyRecord }, RawJsonOptions);
        var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(legacyEnvelope)));
        await File.WriteAllTextAsync(Path.Combine(_root, $"{SessionA:N}.lrw"), JsonSerializer.Serialize(new RawEnvelope(legacyEnvelope, checksum), RawJsonOptions));

        var ledger = NewLedger();
        var readLegacy = await ledger.ReadForSessionAsync(SessionA, CancellationToken.None);
        Assert.Equal(RecordPurpose.SessionMutation, readLegacy.Single().Purpose);

        var sessionMutationWithRecovery = PersistedOwnedChange.TryCreate(
            SessionA,
            Guid.NewGuid(),
            DryRunOperationCategory.Route,
            "route-10.0.0.0/24",
            "original",
            "applied",
            DateTimeOffset.UtcNow,
            1,
            OwnershipEvidenceSource.MutationLedger,
            OwnedChangeLifecycle.Applied,
            RecordPurpose.SessionMutation,
            Guid.NewGuid(),
            Guid.NewGuid(),
            out _,
            out var failureSession);
        Assert.False(sessionMutationWithRecovery);
        Assert.Contains("Session mutation provenance", failureSession, StringComparison.OrdinalIgnoreCase);

        var missingAttempt = PersistedOwnedChange.TryCreate(
            SessionA,
            Guid.NewGuid(),
            DryRunOperationCategory.Route,
            "route-10.0.0.0/24",
            "original",
            "applied",
            DateTimeOffset.UtcNow,
            2,
            OwnershipEvidenceSource.MutationLedger,
            OwnedChangeLifecycle.Applied,
            RecordPurpose.RecoveryMutation,
            null,
            Guid.NewGuid(),
            out _,
            out var failureAttempt);
        Assert.False(missingAttempt);
        Assert.Contains("RecoveryAttemptId", failureAttempt, StringComparison.OrdinalIgnoreCase);

        var missingEvidence = PersistedOwnedChange.TryCreate(
            SessionA,
            Guid.NewGuid(),
            DryRunOperationCategory.Route,
            "route-10.0.0.0/24",
            "original",
            "applied",
            DateTimeOffset.UtcNow,
            3,
            OwnershipEvidenceSource.MutationLedger,
            OwnedChangeLifecycle.Applied,
            RecordPurpose.RecoveryMutation,
            Guid.NewGuid(),
            null,
            out _,
            out var failureEvidence);
        Assert.False(missingEvidence);
        Assert.Contains("AuthorizationEvidenceId", failureEvidence, StringComparison.OrdinalIgnoreCase);

        var unknownEnum = JsonSerializer.Deserialize<PersistedOwnedChange>("{\"sessionId\":\"11111111-1111-1111-1111-111111111111\",\"changeId\":\"22222222-2222-2222-2222-222222222222\",\"category\":1,\"targetIdentity\":\"route\",\"originalValue\":\"before\",\"appliedValue\":\"after\",\"recordedAtUtc\":\"2026-08-26T00:00:00Z\",\"sequenceNumber\":1,\"evidenceSource\":1,\"lifecycle\":1,\"isComplete\":true,\"purpose\":9999}", RawJsonOptions);
        Assert.NotNull(unknownEnum);
        var validationFailure = PersistedOwnedChange.Validate(unknownEnum!);
        Assert.Contains("purpose", validationFailure, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LedgerSurfaceExposesNoNetworkMutationOperations()
    {
        var forbiddenNameParts = new[]
        {
            "addroute", "deleteroute", "createipforward", "deleteipforward", "setipforward",
            "mutate", "executemutation", "commitmutation", "connect", "disconnect"
        };
        var forbiddenParameterTypes = new[]
        {
            typeof(NetworkStateSnapshot),
            typeof(IRouteMutationNative),
            typeof(RestorationExecutionPreparation),
            typeof(AuthorizedRestorationRequest)
        };

        var types = new[] { typeof(IOwnershipLedger), typeof(FileOwnershipLedger) };
        foreach (var type in types)
        {
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
            Assert.NotEmpty(methods);
            foreach (var method in methods)
            {
                var name = method.Name.ToLowerInvariant();
                Assert.DoesNotContain(forbiddenNameParts, part => name.Contains(part, StringComparison.Ordinal));
                Assert.All(method.GetParameters(), parameter => Assert.DoesNotContain(parameter.ParameterType, forbiddenParameterTypes));
            }
        }
    }

    [Fact]
    public void LedgerNeverInfersOwnershipFromSnapshots()
    {
        var snapshotAcceptingMembers = typeof(IOwnershipLedger)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Concat(typeof(FileOwnershipLedger).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .SelectMany(method => method.GetParameters())
            .Where(parameter => parameter.ParameterType == typeof(NetworkStateSnapshot))
            .ToArray();

        Assert.Empty(snapshotAcceptingMembers);
    }

    [Fact]
    public async Task NullEnvelopePayloadFailsClosedAsInvalidData()
    {
        var ledger = NewLedger();
        var envelope = JsonSerializer.Serialize(new RawEnvelope(null!, null!), RawJsonOptions);
        await File.WriteAllTextAsync(Path.Combine(_root, $"{SessionA:N}.lrw"), envelope);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => ledger.ReadForSessionAsync(SessionA, CancellationToken.None));
    }

    [Fact]
    public async Task ConcurrentDistinctAppendsForSameSessionDoNotLoseRecords()
    {
        var ledger = NewLedger();
        var records = Enumerable.Range(0, 12)
            .Select(index => Record(changeId: Guid.NewGuid(), sequence: index + 1))
            .ToArray();

        await Task.WhenAll(records.Select(record => ledger.AppendAsync(record, CancellationToken.None)));

        var read = await ledger.ReadForSessionAsync(SessionA, CancellationToken.None);
        Assert.Equal(12, read.Count);
        Assert.Equal(12, read.Select(record => record.ChangeId).Distinct().Count());
        foreach (var record in records)
            Assert.Contains(record, read);
    }

    [Fact]
    public async Task ConcurrentLifecycleTransitionsRemainValid()
    {
        var ledger = NewLedger();
        var changeId = Guid.NewGuid();
        await ledger.AppendAsync(Record(changeId: changeId, lifecycle: OwnedChangeLifecycle.Planned), CancellationToken.None);

        var plannedDuplicates = Enumerable.Range(0, 4)
            .Select(_ => Record(changeId: changeId, lifecycle: OwnedChangeLifecycle.Planned));
        var appliedTransitions = Enumerable.Range(0, 4)
            .Select(_ => Record(changeId: changeId, lifecycle: OwnedChangeLifecycle.Applied));

        await Task.WhenAll(plannedDuplicates.Concat(appliedTransitions)
            .Select(record => ledger.AppendAsync(record, CancellationToken.None)));

        var stored = Assert.Single(await ledger.ReadForSessionAsync(SessionA, CancellationToken.None));
        Assert.Equal(changeId, stored.ChangeId);
        Assert.True(stored.Lifecycle is OwnedChangeLifecycle.Planned or OwnedChangeLifecycle.Applied);
        Assert.Equal(stored.Lifecycle == OwnedChangeLifecycle.Applied, stored.IsComplete);
    }

    [Fact]
    public async Task ConcurrentOperationsForDifferentSessionsRemainIsolated()
    {
        var ledger = NewLedger();
        var sessionARecords = Enumerable.Range(0, 8)
            .Select(index => Record(sessionId: SessionA, changeId: Guid.NewGuid(), target: $"route-a-{index}", sequence: index + 1))
            .ToArray();
        var sessionBRecords = Enumerable.Range(0, 8)
            .Select(index => Record(sessionId: SessionB, changeId: Guid.NewGuid(), target: $"route-b-{index}", sequence: index + 1))
            .ToArray();

        await Task.WhenAll(sessionARecords.Concat(sessionBRecords)
            .Select(record => ledger.AppendAsync(record, CancellationToken.None)));

        var readA = await ledger.ReadForSessionAsync(SessionA, CancellationToken.None);
        var readB = await ledger.ReadForSessionAsync(SessionB, CancellationToken.None);
        Assert.Equal(8, readA.Count);
        Assert.Equal(8, readB.Count);
        Assert.All(readA, record => Assert.StartsWith("route-a-", record.TargetIdentity, StringComparison.Ordinal));
        Assert.All(readB, record => Assert.StartsWith("route-b-", record.TargetIdentity, StringComparison.Ordinal));
    }

    [Fact]
    public async Task CancellationWhileWaitingForSessionGateDoesNotStrandGate()
    {
        var gateAcquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acquisitions = 0;
        var ledger = new FileOwnershipLedger(_root, (sessionId, cancellationToken) =>
        {
            if (Interlocked.Increment(ref acquisitions) == 1)
            {
                gateAcquired.TrySetResult();
                return releaseGate.Task.WaitAsync(cancellationToken);
            }

            return Task.CompletedTask;
        });

        var holderRecord = Record(changeId: Guid.NewGuid());
        var holderTask = ledger.AppendAsync(holderRecord, CancellationToken.None);
        await gateAcquired.Task; // Deterministic: the holder now owns the session gate.

        var waiterCts = new CancellationTokenSource();
        var waiterRecord = Record(changeId: Guid.NewGuid(), target: "route-waiter");
        var waiterTask = ledger.AppendAsync(waiterRecord, waiterCts.Token);
        await Task.Delay(150); // Scheduling grace so the waiter reaches the gate wait; every timing outcome satisfies the assertions below.
        waiterCts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiterTask);

        releaseGate.SetResult();
        await holderTask;

        var afterRecord = Record(changeId: Guid.NewGuid(), target: "route-after-cancel");
        await ledger.AppendAsync(afterRecord, CancellationToken.None); // Must succeed: the gate was not stranded.

        var read = await ledger.ReadForSessionAsync(SessionA, CancellationToken.None);
        Assert.Equal(
            new[] { holderRecord.ChangeId, afterRecord.ChangeId }.OrderBy(changeId => changeId),
            read.Select(record => record.ChangeId).OrderBy(changeId => changeId));
        Assert.DoesNotContain(read, record => record.ChangeId == waiterRecord.ChangeId);
    }

    [Fact]
    public async Task ConcurrentAppendAndClearAreSerialized()
    {
        var ledger = NewLedger();
        var existing = Record(target: "route-existing", lifecycle: OwnedChangeLifecycle.Applied);
        await ledger.AppendAsync(existing, CancellationToken.None);

        var clearStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseClear = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acquisitions = 0;
        var gatedLedger = new FileOwnershipLedger(_root, (sessionId, cancellationToken) =>
        {
            if (Interlocked.Increment(ref acquisitions) == 1)
            {
                clearStarted.TrySetResult();
                return releaseClear.Task.WaitAsync(cancellationToken);
            }

            return Task.CompletedTask;
        });

        var clearTask = gatedLedger.ClearSessionAsync(SessionA, CancellationToken.None);
        await clearStarted.Task; // Deterministic: the clear now owns the session gate.

        var appended = Record(target: "route-after-clear", lifecycle: OwnedChangeLifecycle.Applied);
        var appendTask = gatedLedger.AppendAsync(appended, CancellationToken.None); // Blocks until the clear completes.
        await Task.Delay(150); // Scheduling grace so the append reaches the gate wait; outcome-invariant.
        releaseClear.SetResult();

        await clearTask;
        await appendTask;

        var read = await gatedLedger.ReadForSessionAsync(SessionA, CancellationToken.None);
        var stored = Assert.Single(read);
        Assert.Equal("route-after-clear", stored.TargetIdentity);
        Assert.DoesNotContain(existing, read); // No resurrection from stale pre-clear state.
    }

    [Fact]
    public async Task VersionedReadReturnsStableExactPayloadRevision()
    {
        var ledger = NewLedger();
        await ledger.AppendAsync(Record(), CancellationToken.None);

        var first = await ledger.ReadVersionedAsync(SessionA, CancellationToken.None);
        var second = await ledger.ReadVersionedAsync(SessionA, CancellationToken.None);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_root, $"{SessionA:N}.lrw")));
        var payload = document.RootElement.GetProperty("payload").GetString()!;

        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))), first.LedgerRevision);
        Assert.Equal(first.LedgerRevision, second.LedgerRevision);
        Assert.Matches("^[0-9A-F]{64}$", first.LedgerRevision);
    }

    [Fact]
    public async Task ConditionalTransitionsSupportOnlyAdjacentLifecycleSteps()
    {
        var ledger = NewLedger();
        var planned = Record(changeId: Guid.NewGuid());
        var empty = await ledger.ReadVersionedAsync(SessionA, CancellationToken.None);
        Assert.True(await ledger.TryApplyTransitionsAsync(SessionA, empty.LedgerRevision,
            new[] { new OwnershipRecordTransition(planned.ChangeId, null, planned) }, CancellationToken.None));

        var afterPlanned = await ledger.ReadVersionedAsync(SessionA, CancellationToken.None);
        var applied = planned with { Lifecycle = OwnedChangeLifecycle.Applied, IsComplete = true };
        Assert.True(await ledger.TryApplyTransitionsAsync(SessionA, afterPlanned.LedgerRevision,
            new[] { new OwnershipRecordTransition(planned.ChangeId, OwnedChangeLifecycle.Planned, applied) }, CancellationToken.None));

        var afterApplied = await ledger.ReadVersionedAsync(SessionA, CancellationToken.None);
        var reverted = applied with { Lifecycle = OwnedChangeLifecycle.Reverted, IsComplete = false };
        Assert.True(await ledger.TryApplyTransitionsAsync(SessionA, afterApplied.LedgerRevision,
            new[] { new OwnershipRecordTransition(planned.ChangeId, OwnedChangeLifecycle.Applied, reverted) }, CancellationToken.None));

        var final = await ledger.ReadVersionedAsync(SessionA, CancellationToken.None);
        Assert.Equal(OwnedChangeLifecycle.Reverted, Assert.Single(final.Records).Lifecycle);
        Assert.NotEqual(empty.LedgerRevision, afterPlanned.LedgerRevision);
        Assert.NotEqual(afterPlanned.LedgerRevision, afterApplied.LedgerRevision);
        Assert.NotEqual(afterApplied.LedgerRevision, final.LedgerRevision);
    }

    [Fact]
    public async Task StaleRevisionAndLifecycleMismatchReturnFalseWithoutMutation()
    {
        var ledger = NewLedger();
        var planned = Record();
        var empty = await ledger.ReadVersionedAsync(SessionA, CancellationToken.None);
        Assert.True(await ledger.TryApplyTransitionsAsync(SessionA, empty.LedgerRevision,
            new[] { new OwnershipRecordTransition(planned.ChangeId, null, planned) }, CancellationToken.None));
        var current = await ledger.ReadVersionedAsync(SessionA, CancellationToken.None);
        var applied = planned with { Lifecycle = OwnedChangeLifecycle.Applied, IsComplete = true };

        Assert.False(await ledger.TryApplyTransitionsAsync(SessionA, empty.LedgerRevision,
            new[] { new OwnershipRecordTransition(planned.ChangeId, OwnedChangeLifecycle.Planned, applied) }, CancellationToken.None));
        var reverted = applied with { Lifecycle = OwnedChangeLifecycle.Reverted, IsComplete = false };
        Assert.False(await ledger.TryApplyTransitionsAsync(SessionA, current.LedgerRevision,
            new[] { new OwnershipRecordTransition(planned.ChangeId, OwnedChangeLifecycle.Applied, reverted) }, CancellationToken.None));
        Assert.Equal(current.LedgerRevision, (await ledger.ReadVersionedAsync(SessionA, CancellationToken.None)).LedgerRevision);
    }

    [Fact]
    public async Task ForbiddenTransitionThrowsEvenWhenRevisionIsStale()
    {
        var ledger = NewLedger();
        var stale = await ledger.ReadVersionedAsync(SessionA, CancellationToken.None);
        var planned = Record();
        await ledger.AppendAsync(planned, CancellationToken.None);
        var before = await ledger.ReadVersionedAsync(SessionA, CancellationToken.None);
        var reverted = planned with { Lifecycle = OwnedChangeLifecycle.Reverted, IsComplete = false };

        await Assert.ThrowsAsync<InvalidOperationException>(() => ledger.TryApplyTransitionsAsync(
            SessionA, stale.LedgerRevision,
            new[] { new OwnershipRecordTransition(planned.ChangeId, OwnedChangeLifecycle.Planned, reverted) },
            CancellationToken.None));

        var after = await ledger.ReadVersionedAsync(SessionA, CancellationToken.None);
        Assert.Equal(before.LedgerRevision, after.LedgerRevision);
        Assert.Equal(planned, Assert.Single(after.Records));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ABC")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("GGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGG")]
    public async Task MalformedRevisionIsRejected(string revision)
    {
        var ledger = NewLedger();
        var proposed = Record();
        await Assert.ThrowsAsync<ArgumentException>(() => ledger.TryApplyTransitionsAsync(
            SessionA, revision, new[] { new OwnershipRecordTransition(proposed.ChangeId, null, proposed) }, CancellationToken.None));
    }

    [Fact]
    public async Task RevisionComparisonIsOrdinalAndDoesNotNormalizeCasing()
    {
        var ledger = NewLedger();
        var snapshot = await ledger.ReadVersionedAsync(SessionA, CancellationToken.None);
        var lower = snapshot.LedgerRevision.ToLowerInvariant();
        Assert.NotEqual(snapshot.LedgerRevision, lower);
        var proposed = Record();
        Assert.False(await ledger.TryApplyTransitionsAsync(SessionA, lower,
            new[] { new OwnershipRecordTransition(proposed.ChangeId, null, proposed) }, CancellationToken.None));
    }

    [Fact]
    public async Task MalformedTransitionSetIsRejectedBeforePublication()
    {
        var ledger = NewLedger();
        var snapshot = await ledger.ReadVersionedAsync(SessionA, CancellationToken.None);
        var first = Record();
        var other = Record();

        await Assert.ThrowsAsync<ArgumentException>(() => ledger.TryApplyTransitionsAsync(SessionA, snapshot.LedgerRevision,
            new[]
            {
                new OwnershipRecordTransition(first.ChangeId, null, first),
                new OwnershipRecordTransition(first.ChangeId, null, first)
            }, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => ledger.TryApplyTransitionsAsync(SessionA, snapshot.LedgerRevision,
            new[] { new OwnershipRecordTransition(first.ChangeId, null, other) }, CancellationToken.None));
        Assert.Empty((await ledger.ReadVersionedAsync(SessionA, CancellationToken.None)).Records);
    }

    [Theory]
    [InlineData(null, OwnedChangeLifecycle.Applied)]
    [InlineData(null, OwnedChangeLifecycle.Reverted)]
    [InlineData(OwnedChangeLifecycle.Planned, OwnedChangeLifecycle.Planned)]
    [InlineData(OwnedChangeLifecycle.Planned, OwnedChangeLifecycle.Reverted)]
    [InlineData(OwnedChangeLifecycle.Applied, OwnedChangeLifecycle.Planned)]
    [InlineData(OwnedChangeLifecycle.Reverted, OwnedChangeLifecycle.Applied)]
    public async Task ForbiddenTransitionsAreRejected(OwnedChangeLifecycle? expected, OwnedChangeLifecycle proposedLifecycle)
    {
        var ledger = NewLedger();
        var record = Record(lifecycle: expected ?? OwnedChangeLifecycle.Planned);
        if (expected.HasValue)
            await ledger.AppendAsync(record, CancellationToken.None);
        var snapshot = await ledger.ReadVersionedAsync(SessionA, CancellationToken.None);
        var proposed = record with
        {
            Lifecycle = proposedLifecycle,
            IsComplete = proposedLifecycle == OwnedChangeLifecycle.Applied
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => ledger.TryApplyTransitionsAsync(
            SessionA, snapshot.LedgerRevision,
            new[] { new OwnershipRecordTransition(record.ChangeId, expected, proposed) }, CancellationToken.None));
        Assert.Equal(snapshot.LedgerRevision, (await ledger.ReadVersionedAsync(SessionA, CancellationToken.None)).LedgerRevision);
    }

    [Theory]
    [InlineData("target")]
    [InlineData("purpose")]
    [InlineData("attempt")]
    [InlineData("evidence")]
    public async Task ConditionalTransitionRejectsImmutableIdentityAndProvenanceMutation(string field)
    {
        var ledger = NewLedger();
        var attempt = Guid.NewGuid();
        var evidence = Guid.NewGuid();
        var planned = Record(purpose: RecordPurpose.RecoveryMutation, recoveryAttemptId: attempt,
            authorizationEvidenceId: evidence);
        await ledger.AppendAsync(planned, CancellationToken.None);
        var snapshot = await ledger.ReadVersionedAsync(SessionA, CancellationToken.None);
        var applied = planned with { Lifecycle = OwnedChangeLifecycle.Applied, IsComplete = true };
        applied = field switch
        {
            "target" => applied with { TargetIdentity = "different-operation-target" },
            "purpose" => applied with { Purpose = RecordPurpose.SessionMutation, RecoveryAttemptId = null, AuthorizationEvidenceId = null },
            "attempt" => applied with { RecoveryAttemptId = Guid.NewGuid() },
            _ => applied with { AuthorizationEvidenceId = Guid.NewGuid() }
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => ledger.TryApplyTransitionsAsync(
            SessionA, snapshot.LedgerRevision,
            new[] { new OwnershipRecordTransition(planned.ChangeId, OwnedChangeLifecycle.Planned, applied) }, CancellationToken.None));
        Assert.Equal(planned, Assert.Single((await ledger.ReadVersionedAsync(SessionA, CancellationToken.None)).Records));
    }

    [Fact]
    public async Task MultiRecordTransitionIsAtomicAndPreservesUnmentionedRecords()
    {
        var ledger = NewLedger();
        var one = Record(changeId: Guid.NewGuid(), target: "one");
        var two = Record(changeId: Guid.NewGuid(), target: "two");
        var untouched = Record(changeId: Guid.NewGuid(), target: "untouched", lifecycle: OwnedChangeLifecycle.Applied);
        await ledger.AppendAsync(one, CancellationToken.None);
        await ledger.AppendAsync(two, CancellationToken.None);
        await ledger.AppendAsync(untouched, CancellationToken.None);
        var snapshot = await ledger.ReadVersionedAsync(SessionA, CancellationToken.None);
        var oneApplied = one with { Lifecycle = OwnedChangeLifecycle.Applied, IsComplete = true };
        var twoApplied = two with { Lifecycle = OwnedChangeLifecycle.Applied, IsComplete = true };

        Assert.True(await ledger.TryApplyTransitionsAsync(SessionA, snapshot.LedgerRevision,
            new[]
            {
                new OwnershipRecordTransition(one.ChangeId, OwnedChangeLifecycle.Planned, oneApplied),
                new OwnershipRecordTransition(two.ChangeId, OwnedChangeLifecycle.Planned, twoApplied)
            }, CancellationToken.None));
        var records = (await ledger.ReadVersionedAsync(SessionA, CancellationToken.None)).Records;
        Assert.Equal(oneApplied, records.Single(record => record.ChangeId == one.ChangeId));
        Assert.Equal(twoApplied, records.Single(record => record.ChangeId == two.ChangeId));
        Assert.Equal(untouched, records.Single(record => record.ChangeId == untouched.ChangeId));
    }

    [Fact]
    public async Task InvalidMultiRecordMemberPublishesNothing()
    {
        var ledger = NewLedger();
        var one = Record(changeId: Guid.NewGuid());
        var two = Record(changeId: Guid.NewGuid());
        var snapshot = await ledger.ReadVersionedAsync(SessionA, CancellationToken.None);
        var forbidden = two with { Lifecycle = OwnedChangeLifecycle.Applied, IsComplete = true };

        await Assert.ThrowsAsync<InvalidOperationException>(() => ledger.TryApplyTransitionsAsync(
            SessionA, snapshot.LedgerRevision,
            new[]
            {
                new OwnershipRecordTransition(one.ChangeId, null, one),
                new OwnershipRecordTransition(two.ChangeId, null, forbidden)
            }, CancellationToken.None));
        var after = await ledger.ReadVersionedAsync(SessionA, CancellationToken.None);
        Assert.Empty(after.Records);
        Assert.Equal(snapshot.LedgerRevision, after.LedgerRevision);
    }

    [Fact]
    public async Task MalformedDurableProvenanceAndNullRecordsFailClosedForVersionedRead()
    {
        var malformed = new PersistedOwnedChange(
            SessionA, Guid.NewGuid(), DryRunOperationCategory.Route, "route", "before", "after",
            RecordedAt, 1, OwnershipEvidenceSource.MutationLedger, OwnedChangeLifecycle.Planned, false,
            RecordPurpose.RecoveryMutation, null, Guid.NewGuid());
        await WriteRawLedgerAsync(_root, SessionA, JsonSerializer.Serialize(new PersistedOwnedChange?[] { malformed }, RawJsonOptions));
        await Assert.ThrowsAsync<InvalidDataException>(() => NewLedger().ReadVersionedAsync(SessionA, CancellationToken.None));

        await WriteRawLedgerAsync(_root, SessionA, JsonSerializer.Serialize(new PersistedOwnedChange?[] { null }, RawJsonOptions));
        await Assert.ThrowsAsync<InvalidDataException>(() => NewLedger().ReadVersionedAsync(SessionA, CancellationToken.None));
    }

    [Fact]
    public async Task SeparateSamePathInstancesCannotStaleOverwrite()
    {
        var first = NewLedger();
        var second = NewLedger();
        var snapshot = await first.ReadVersionedAsync(SessionA, CancellationToken.None);
        var winner = Record(target: "winner");
        var loser = Record(target: "loser");

        Assert.True(await first.TryApplyTransitionsAsync(SessionA, snapshot.LedgerRevision,
            new[] { new OwnershipRecordTransition(winner.ChangeId, null, winner) }, CancellationToken.None));
        Assert.False(await second.TryApplyTransitionsAsync(SessionA, snapshot.LedgerRevision,
            new[] { new OwnershipRecordTransition(loser.ChangeId, null, loser) }, CancellationToken.None));
        Assert.Equal(winner, Assert.Single((await first.ReadVersionedAsync(SessionA, CancellationToken.None)).Records));
    }

    [Fact]
    public async Task SamePathGateCoversMatchedExpectationThroughPublication()
    {
        var seed = NewLedger();
        var snapshot = await seed.ReadVersionedAsync(SessionA, CancellationToken.None);
        var matched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new FileOwnershipLedger(_root, async cancellationToken =>
        {
            matched.SetResult();
            await release.Task.WaitAsync(cancellationToken);
        });
        var second = NewLedger();
        var winner = Record(target: "winner");
        var loser = Record(target: "loser");

        var firstTask = first.TryApplyTransitionsAsync(SessionA, snapshot.LedgerRevision,
            new[] { new OwnershipRecordTransition(winner.ChangeId, null, winner) }, CancellationToken.None);
        await matched.Task;
        var secondTask = second.TryApplyTransitionsAsync(SessionA, snapshot.LedgerRevision,
            new[] { new OwnershipRecordTransition(loser.ChangeId, null, loser) }, CancellationToken.None);
        Assert.False(secondTask.IsCompleted);
        release.SetResult();

        Assert.True(await firstTask);
        Assert.False(await secondTask);
        Assert.Equal(winner, Assert.Single((await seed.ReadVersionedAsync(SessionA, CancellationToken.None)).Records));
    }

    [Fact]
    public async Task CancellationAfterExpectationMatchButBeforePublicationPreservesRevision()
    {
        using var cancellation = new CancellationTokenSource();
        var ledger = new FileOwnershipLedger(_root, _ =>
        {
            cancellation.Cancel();
            return Task.CompletedTask;
        });
        var snapshot = await ledger.ReadVersionedAsync(SessionA, CancellationToken.None);
        var proposed = Record();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ledger.TryApplyTransitionsAsync(
            SessionA, snapshot.LedgerRevision,
            new[] { new OwnershipRecordTransition(proposed.ChangeId, null, proposed) }, cancellation.Token));
        var after = await ledger.ReadVersionedAsync(SessionA, CancellationToken.None);
        Assert.Equal(snapshot.LedgerRevision, after.LedgerRevision);
        Assert.Empty(after.Records);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public async Task RecoveryProvenanceSurvivesEveryConditionalLifecycleTransition()
    {
        var ledger = NewLedger();
        var attempt = Guid.NewGuid();
        var evidence = Guid.NewGuid();
        var planned = Record(purpose: RecordPurpose.RecoveryMutation, recoveryAttemptId: attempt,
            authorizationEvidenceId: evidence);
        var snapshot = await ledger.ReadVersionedAsync(SessionA, CancellationToken.None);
        Assert.True(await ledger.TryApplyTransitionsAsync(SessionA, snapshot.LedgerRevision,
            new[] { new OwnershipRecordTransition(planned.ChangeId, null, planned) }, CancellationToken.None));
        snapshot = await ledger.ReadVersionedAsync(SessionA, CancellationToken.None);
        var applied = planned with { Lifecycle = OwnedChangeLifecycle.Applied, IsComplete = true };
        Assert.True(await ledger.TryApplyTransitionsAsync(SessionA, snapshot.LedgerRevision,
            new[] { new OwnershipRecordTransition(planned.ChangeId, OwnedChangeLifecycle.Planned, applied) }, CancellationToken.None));
        snapshot = await ledger.ReadVersionedAsync(SessionA, CancellationToken.None);
        var reverted = applied with { Lifecycle = OwnedChangeLifecycle.Reverted, IsComplete = false };
        Assert.True(await ledger.TryApplyTransitionsAsync(SessionA, snapshot.LedgerRevision,
            new[] { new OwnershipRecordTransition(planned.ChangeId, OwnedChangeLifecycle.Applied, reverted) }, CancellationToken.None));

        var final = Assert.Single((await ledger.ReadVersionedAsync(SessionA, CancellationToken.None)).Records);
        Assert.Equal(RecordPurpose.RecoveryMutation, final.Purpose);
        Assert.Equal(attempt, final.RecoveryAttemptId);
        Assert.Equal(evidence, final.AuthorizationEvidenceId);
    }

    [Fact]
    public async Task ExactEvidenceRoundTripsAndSurvivesEveryLifecycleTransition()
    {
        var ledger = NewLedger();
        var planned = ExactRecord();
        await ledger.AppendAsync(planned, CancellationToken.None);
        var applied = planned with { Lifecycle = OwnedChangeLifecycle.Applied, IsComplete = true };
        await ledger.AppendAsync(applied, CancellationToken.None);
        var reverted = applied with { Lifecycle = OwnedChangeLifecycle.Reverted, IsComplete = false };
        await ledger.AppendAsync(reverted, CancellationToken.None);

        var stored = Assert.Single(await ledger.ReadForSessionAsync(SessionA, CancellationToken.None));
        Assert.Equal(reverted, stored);
        Assert.NotNull(stored.ExactRouteIdentity);
        Assert.NotNull(stored.ExactRouteEvidenceFingerprint);
    }

    [Fact]
    public async Task ExactEvidenceCannotBeEnrichedStrippedOrChangedDuringTransition()
    {
        var ledger = NewLedger();
        var legacy = Record();
        await ledger.AppendAsync(legacy, CancellationToken.None);
        var exactTemplate = ExactRecord();
        var enriched = PersistedOwnedChange.CreateExactRoute(legacy.SessionId, legacy.ChangeId,
            legacy.Category, legacy.TargetIdentity, legacy.OriginalValue, legacy.AppliedValue,
            legacy.RecordedAtUtc, legacy.SequenceNumber, legacy.EvidenceSource,
            OwnedChangeLifecycle.Applied, exactTemplate.ExactRouteIdentity!, exactTemplate.MutationAttemptId!.Value);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ledger.AppendAsync(enriched, CancellationToken.None));

        var exact = ExactRecord();
        await ledger.AppendAsync(exact, CancellationToken.None);
        var stripped = exact with { Lifecycle = OwnedChangeLifecycle.Applied, IsComplete = true,
            ExactRouteEvidenceVersion = null, ExactRouteIdentity = null, MutationAttemptId = null,
            ExactRouteEvidenceFingerprint = null };
        await Assert.ThrowsAsync<InvalidOperationException>(() => ledger.AppendAsync(stripped, CancellationToken.None));
        Assert.Equal(legacy, (await ledger.ReadForSessionAsync(SessionA, CancellationToken.None))
            .Single(record => record.ChangeId == legacy.ChangeId));
        Assert.Equal(exact, (await ledger.ReadForSessionAsync(SessionA, CancellationToken.None))
            .Single(record => record.ChangeId == exact.ChangeId));
    }

    [Fact]
    public async Task PartialAndTamperedExactEvidenceFailAtWriteAndReadBoundaries()
    {
        var exact = ExactRecord();
        var partial = Record() with { ExactRouteEvidenceVersion = 1 };
        await Assert.ThrowsAsync<InvalidOperationException>(() => NewLedger().AppendAsync(partial, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => NewLedger().AppendAsync(exact with
            { ExactRouteEvidenceFingerprint = new string('A', 64) }, CancellationToken.None));

        await WriteRawLedgerAsync(_root, SessionA, JsonSerializer.Serialize(new[] { partial }, RawJsonOptions));
        await Assert.ThrowsAsync<InvalidDataException>(() => NewLedger().ReadForSessionAsync(SessionA, CancellationToken.None));
    }

    [Fact]
    public async Task ConditionalWriteRejectsMalformedExactEvidenceWithoutPublishing()
    {
        var ledger = NewLedger();
        var snapshot = await ledger.ReadVersionedAsync(SessionA, CancellationToken.None);
        var partial = Record() with { MutationAttemptId = Guid.NewGuid() };
        await Assert.ThrowsAsync<ArgumentException>(() => ledger.TryApplyTransitionsAsync(
            SessionA, snapshot.LedgerRevision,
            new[] { new OwnershipRecordTransition(partial.ChangeId, null, partial) }, CancellationToken.None));
        var after = await ledger.ReadVersionedAsync(SessionA, CancellationToken.None);
        Assert.Equal(snapshot.LedgerRevision, after.LedgerRevision);
        Assert.Empty(after.Records);
    }

    [Theory]
    [InlineData("partial")]
    [InlineData("unknown-version")]
    [InlineData("invalid-core")]
    [InlineData("empty-attempt")]
    [InlineData("recovery-hybrid")]
    [InlineData("wrong-fingerprint")]
    [InlineData("non-utc")]
    public async Task EveryMalformedExactShapeFailsAtDurableReadBoundary(string malformedCase)
    {
        var node = JsonSerializer.SerializeToNode(ExactRecord(), RawJsonOptions)!.AsObject();
        switch (malformedCase)
        {
            case "partial":
                node.Remove("exactRouteIdentity");
                break;
            case "unknown-version":
                node["exactRouteEvidenceVersion"] = 2;
                break;
            case "invalid-core":
                node["exactRouteIdentity"]!["profile"]!["protocol"] = 4;
                break;
            case "empty-attempt":
                node["mutationAttemptId"] = Guid.Empty;
                break;
            case "recovery-hybrid":
                node["purpose"] = (int)RecordPurpose.RecoveryMutation;
                node["recoveryAttemptId"] = Guid.NewGuid();
                node["authorizationEvidenceId"] = Guid.NewGuid();
                break;
            case "wrong-fingerprint":
                node["exactRouteEvidenceFingerprint"] = new string('A', 64);
                break;
            default:
                node["recordedAtUtc"] = "2026-08-26T08:00:00+08:00";
                break;
        }
        var payload = new JsonArray(node).ToJsonString(RawJsonOptions);
        await WriteRawLedgerAsync(_root, SessionA, payload);
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            NewLedger().ReadForSessionAsync(SessionA, CancellationToken.None));
        if (malformedCase == "invalid-core")
            Assert.IsAssignableFrom<ArgumentException>(exception.InnerException);
    }

    [Fact]
    public async Task CancelledPublicReadRemainsCancellation()
    {
        await NewLedger().AppendAsync(Record(), CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NewLedger().ReadForSessionAsync(SessionA, cancellation.Token));
    }

    [Theory]
    [InlineData("legacy-applied-to-exact-reverted")]
    [InlineData("exact-applied-to-legacy-reverted")]
    public async Task AppendRejectsLateLifecycleEnrichmentAndStrippingWithoutPublication(string direction)
    {
        var ledger = NewLedger();
        PersistedOwnedChange initial;
        PersistedOwnedChange proposed;
        if (direction.StartsWith("legacy", StringComparison.Ordinal))
        {
            initial = Record(lifecycle: OwnedChangeLifecycle.Applied);
            var template = ExactRecord();
            proposed = PersistedOwnedChange.CreateExactRoute(initial.SessionId, initial.ChangeId,
                initial.Category, initial.TargetIdentity, initial.OriginalValue, initial.AppliedValue,
                initial.RecordedAtUtc, initial.SequenceNumber, initial.EvidenceSource,
                OwnedChangeLifecycle.Reverted, template.ExactRouteIdentity!, template.MutationAttemptId!.Value);
        }
        else
        {
            initial = ExactRecord(lifecycle: OwnedChangeLifecycle.Applied);
            proposed = PersistedOwnedChange.Create(initial.SessionId, initial.ChangeId, initial.Category,
                initial.TargetIdentity, initial.OriginalValue, initial.AppliedValue, initial.RecordedAtUtc,
                initial.SequenceNumber, initial.EvidenceSource, OwnedChangeLifecycle.Reverted);
        }
        await ledger.AppendAsync(initial, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ledger.AppendAsync(proposed, CancellationToken.None));
        Assert.Equal(initial, Assert.Single(await ledger.ReadForSessionAsync(SessionA, CancellationToken.None)));
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("attempt")]
    [InlineData("fingerprint")]
    [InlineData("version")]
    public async Task AppendRejectsEveryExactFieldSubstitutionWithoutPublication(string field)
    {
        var ledger = NewLedger();
        var planned = ExactRecord();
        await ledger.AppendAsync(planned, CancellationToken.None);
        var proposed = planned with { Lifecycle = OwnedChangeLifecycle.Applied, IsComplete = true };
        proposed = field switch
        {
            "identity" => ResignExact(proposed with { ExactRouteIdentity = VaryIdentity(planned.ExactRouteIdentity!) }),
            "attempt" => ResignExact(proposed with { MutationAttemptId = Guid.NewGuid() }),
            "fingerprint" => proposed with { ExactRouteEvidenceFingerprint = new string('A', 64) },
            _ => proposed with { ExactRouteEvidenceVersion = 2 }
        };
        await Assert.ThrowsAnyAsync<Exception>(() => ledger.AppendAsync(proposed, CancellationToken.None));
        Assert.Equal(planned, Assert.Single(await ledger.ReadForSessionAsync(SessionA, CancellationToken.None)));
    }

    [Theory]
    [InlineData("enrich")]
    [InlineData("strip")]
    public async Task CasRejectsExactShapeEnrichmentAndStrippingAtCurrentRevision(string mode)
    {
        var ledger = NewLedger();
        var template = ExactRecord();
        var initial = mode == "enrich" ? Record() : template;
        await ledger.AppendAsync(initial, CancellationToken.None);
        var snapshot = await ledger.ReadVersionedAsync(SessionA, CancellationToken.None);
        PersistedOwnedChange proposed = mode == "enrich"
            ? PersistedOwnedChange.CreateExactRoute(initial.SessionId, initial.ChangeId, initial.Category,
                initial.TargetIdentity, initial.OriginalValue, initial.AppliedValue, initial.RecordedAtUtc,
                initial.SequenceNumber, initial.EvidenceSource, OwnedChangeLifecycle.Applied,
                template.ExactRouteIdentity!, template.MutationAttemptId!.Value)
            : PersistedOwnedChange.Create(initial.SessionId, initial.ChangeId, initial.Category,
                initial.TargetIdentity, initial.OriginalValue, initial.AppliedValue, initial.RecordedAtUtc,
                initial.SequenceNumber, initial.EvidenceSource, OwnedChangeLifecycle.Applied);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ledger.TryApplyTransitionsAsync(
            SessionA, snapshot.LedgerRevision,
            new[] { new OwnershipRecordTransition(initial.ChangeId, OwnedChangeLifecycle.Planned, proposed) },
            CancellationToken.None));
        var after = await ledger.ReadVersionedAsync(SessionA, CancellationToken.None);
        Assert.Equal(snapshot.LedgerRevision, after.LedgerRevision);
        Assert.Equal(initial, Assert.Single(after.Records));
    }

    [Theory]
    [InlineData("identity", false)]
    [InlineData("attempt", false)]
    [InlineData("fingerprint", true)]
    [InlineData("version", true)]
    public async Task CasRejectsEveryExactFieldSubstitutionAtCurrentRevision(string field, bool validationRejectsFirst)
    {
        var ledger = NewLedger();
        var planned = ExactRecord();
        await ledger.AppendAsync(planned, CancellationToken.None);
        var snapshot = await ledger.ReadVersionedAsync(SessionA, CancellationToken.None);
        var proposed = planned with { Lifecycle = OwnedChangeLifecycle.Applied, IsComplete = true };
        proposed = field switch
        {
            "identity" => ResignExact(proposed with { ExactRouteIdentity = VaryIdentity(planned.ExactRouteIdentity!) }),
            "attempt" => ResignExact(proposed with { MutationAttemptId = Guid.NewGuid() }),
            "fingerprint" => proposed with { ExactRouteEvidenceFingerprint = new string('A', 64) },
            _ => proposed with { ExactRouteEvidenceVersion = 2 }
        };
        var exception = await Xunit.Record.ExceptionAsync(() => ledger.TryApplyTransitionsAsync(
            SessionA, snapshot.LedgerRevision,
            new[] { new OwnershipRecordTransition(planned.ChangeId, OwnedChangeLifecycle.Planned, proposed) },
            CancellationToken.None));
        Assert.IsType(validationRejectsFirst ? typeof(ArgumentException) : typeof(InvalidOperationException), exception);
        var after = await ledger.ReadVersionedAsync(SessionA, CancellationToken.None);
        Assert.Equal(snapshot.LedgerRevision, after.LedgerRevision);
        Assert.Equal(planned, Assert.Single(after.Records));
    }

    private static ExactRouteMutationIdentity VaryIdentity(ExactRouteMutationIdentity identity) => new(
        identity.SchemaVersion,
        new NativeRouteKey(identity.Key.AddressFamily, "C6336400", identity.Key.DestinationPrefixLength,
            identity.Key.NextHopAddress, identity.Key.NextHopScopeId, identity.Key.InterfaceLuid),
        identity.Profile);
}
