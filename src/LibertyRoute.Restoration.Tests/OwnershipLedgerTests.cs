using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
        OwnedChangeLifecycle lifecycle = OwnedChangeLifecycle.Planned)
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
            lifecycle);

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
            typeof(RecordPurpose)
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
}
