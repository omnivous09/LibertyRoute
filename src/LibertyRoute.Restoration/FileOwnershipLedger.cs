using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LibertyRoute.Restoration;

/// <summary>
/// File-backed ownership ledger. It stores one checksummed JSON envelope per session
/// under an application-controlled root directory (production default:
/// %ProgramData%\LibertyRoute\ownership). The format mirrors FileTransactionJournal:
/// deterministic camelCase JSON, SHA-256 payload checksum with fixed-time comparison,
/// and atomic temp-file + flush-to-disk + move writes.
///
/// Failure behavior is fail-closed: a missing ledger reads as empty, but malformed
/// JSON, checksum mismatches, invalid records, duplicate change ids, or foreign-session
/// records throw instead of being silently discarded.
///
/// Concurrency: all operations for the same canonical session-ledger path share one
/// process-lifetime gate across every FileOwnershipLedger instance. Conditional
/// transitions hold that gate continuously from durable reread through publication.
/// Operations for different session paths remain independently concurrent.
/// Cross-process writers are NOT supported by this implementation; production wiring
/// must preserve a single-writer ownership model until cross-process coordination is
/// deliberately implemented.
/// </summary>
internal static class FileOwnershipLedgerLocks
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);

    internal static SemaphoreSlim Get(string canonicalPath)
        => Gates.GetOrAdd(canonicalPath, static _ => new SemaphoreSlim(1, 1));
}

public sealed class FileOwnershipLedger : IConditionalOwnershipLedger
{
    private const uint MoveFileReplaceExisting = 0x1;
    private const uint MoveFileWriteThrough = 0x8;
    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool MoveFileExDelegate(string existingFileName, string newFileName, uint flags);

    private static readonly Lazy<MoveFileExDelegate> MoveFileExWindows = new(LoadMoveFileExWindows);
    private sealed record Envelope(string Payload, string Sha256);
    private sealed record ValidatedLedger(IReadOnlyList<PersistedOwnedChange> Records, string Payload, string Revision);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _rootDirectory;
    private readonly Func<Guid, CancellationToken, Task>? _onSessionGateAcquired;
    private readonly Func<CancellationToken, Task>? _onConditionalExpectationMatched;

    public FileOwnershipLedger()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "LibertyRoute",
            "ownership"))
    {
    }

    public FileOwnershipLedger(string rootDirectory)
        : this(rootDirectory, onSessionGateAcquired: null)
    {
    }

    /// <summary>
    /// Test seam: <paramref name="onSessionGateAcquired"/> is awaited inside the
    /// per-session gate immediately after acquisition, letting deterministic
    /// concurrency tests hold a session gate open. Production constructors never
    /// supply it.
    /// </summary>
    internal FileOwnershipLedger(string rootDirectory, Func<Guid, CancellationToken, Task>? onSessionGateAcquired)
        : this(rootDirectory, onSessionGateAcquired, onConditionalExpectationMatched: null)
    {
    }

    internal FileOwnershipLedger(string rootDirectory, Func<CancellationToken, Task> onConditionalExpectationMatched)
        : this(rootDirectory, onSessionGateAcquired: null, onConditionalExpectationMatched)
    {
    }

    private FileOwnershipLedger(
        string rootDirectory,
        Func<Guid, CancellationToken, Task>? onSessionGateAcquired,
        Func<CancellationToken, Task>? onConditionalExpectationMatched)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
            throw new ArgumentException("Root directory is required.", nameof(rootDirectory));

        _rootDirectory = Path.GetFullPath(rootDirectory);
        _onSessionGateAcquired = onSessionGateAcquired;
        _onConditionalExpectationMatched = onConditionalExpectationMatched;
        Directory.CreateDirectory(_rootDirectory);
    }

    /// <summary>
    /// Returns the process-lifetime gate for one session. Gates are never removed or
    /// disposed while other operations may be waiting on them; the bounded memory cost
    /// of one SemaphoreSlim per session ever touched is accepted deliberately.
    /// </summary>
    private SemaphoreSlim GateFor(Guid sessionId)
        => FileOwnershipLedgerLocks.Get(PathFor(sessionId));

    public async Task AppendAsync(PersistedOwnedChange record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        var validationFailure = PersistedOwnedChange.Validate(record);
        if (!string.IsNullOrEmpty(validationFailure))
            throw new InvalidOperationException(validationFailure);

        var gate = GateFor(record.SessionId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_onSessionGateAcquired is not null)
                await _onSessionGateAcquired(record.SessionId, cancellationToken).ConfigureAwait(false);

            var records = (await ReadValidatedCoreAsync(record.SessionId, cancellationToken).ConfigureAwait(false))?.Records
                ?? Array.Empty<PersistedOwnedChange>();
            var existing = records.FirstOrDefault(candidate => candidate.ChangeId == record.ChangeId);

            IReadOnlyList<PersistedOwnedChange> updated;
            if (existing is null)
            {
                updated = records.Concat(new[] { record }).ToArray();
            }
            else if (existing.Lifecycle == record.Lifecycle)
            {
                if (!existing.ImmutableFieldsMatch(record))
                    throw new InvalidOperationException(
                        $"Ownership ledger change {record.ChangeId} conflicts with the stored record: identity and evidence fields are immutable.");

                return; // Idempotent duplicate append.
            }
            else if (PersistedOwnedChange.IsValidTransition(existing.Lifecycle, record.Lifecycle))
            {
                if (!existing.ImmutableFieldsMatch(record))
                    throw new InvalidOperationException(
                        $"Ownership ledger change {record.ChangeId} cannot advance lifecycle because identity or evidence fields differ from the stored record.");

                updated = records
                    .Select(candidate => candidate.ChangeId == record.ChangeId ? record : candidate)
                    .ToArray();
            }
            else
            {
                throw new InvalidOperationException(
                    $"Ownership ledger lifecycle transition {existing.Lifecycle} to {record.Lifecycle} is not permitted for change {record.ChangeId}.");
            }

            await WriteCoreAsync(record.SessionId, updated, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<PersistedOwnedChange>> ReadForSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        if (sessionId == Guid.Empty)
            throw new ArgumentException("Session id is required.", nameof(sessionId));

        var gate = GateFor(sessionId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return (await ReadValidatedCoreAsync(sessionId, cancellationToken).ConfigureAwait(false))?.Records
                ?? Array.Empty<PersistedOwnedChange>();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<OwnershipLedgerSnapshot> ReadVersionedAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        if (sessionId == Guid.Empty)
            throw new ArgumentException("Session id is required.", nameof(sessionId));

        var gate = GateFor(sessionId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await ReadValidatedCoreAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (current is not null)
                return new OwnershipLedgerSnapshot(sessionId, current.Records, current.Revision);

            var emptyPayload = JsonSerializer.Serialize(Array.Empty<PersistedOwnedChange>(), JsonOptions);
            return new OwnershipLedgerSnapshot(sessionId, Array.Empty<PersistedOwnedChange>(), ComputeRevision(emptyPayload));
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<ValidatedLedger?> ReadValidatedCoreAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var path = PathFor(sessionId);
        if (!File.Exists(path))
            return null;

        var envelopeJson = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);

        Envelope envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<Envelope>(envelopeJson, JsonOptions)
                       ?? throw new InvalidDataException("Ownership ledger envelope is invalid.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Ownership ledger envelope is malformed.", exception);
        }

        VerifyChecksum(envelope);

        List<PersistedOwnedChange> records;
        try
        {
            records = JsonSerializer.Deserialize<List<PersistedOwnedChange>>(envelope.Payload, JsonOptions)
                      ?? throw new InvalidDataException("Ownership ledger payload is invalid.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Ownership ledger payload is malformed.", exception);
        }

        ValidateRecords(sessionId, records, durableState: true);

        var ordered = OwnershipLedgerOrdering.Order(records);
        return new ValidatedLedger(ordered, envelope.Payload, ComputeRevision(envelope.Payload));
    }

    public async Task ClearSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        if (sessionId == Guid.Empty)
            throw new ArgumentException("Session id is required.", nameof(sessionId));

        var gate = GateFor(sessionId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_onSessionGateAcquired is not null)
                await _onSessionGateAcquired(sessionId, cancellationToken).ConfigureAwait(false);

            var path = PathFor(sessionId);
            if (File.Exists(path))
                File.Delete(path);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> ExistsAsync(Guid sessionId, Guid changeId, CancellationToken cancellationToken)
    {
        if (changeId == Guid.Empty)
            throw new ArgumentException("Change id is required.", nameof(changeId));

        var records = await ReadForSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return records.Any(record => record.ChangeId == changeId);
    }

    public async Task<bool> TryApplyTransitionsAsync(
        Guid sessionId,
        string expectedLedgerRevision,
        IReadOnlyList<OwnershipRecordTransition> transitions,
        CancellationToken cancellationToken)
    {
        ValidateTransitionArguments(sessionId, expectedLedgerRevision, transitions);

        var gate = GateFor(sessionId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await ReadValidatedCoreAsync(sessionId, cancellationToken).ConfigureAwait(false);
            var currentRecords = current?.Records ?? Array.Empty<PersistedOwnedChange>();
            var currentRevision = current?.Revision ?? ComputeRevision(
                JsonSerializer.Serialize(Array.Empty<PersistedOwnedChange>(), JsonOptions));

            if (!StringComparer.Ordinal.Equals(currentRevision, expectedLedgerRevision))
                return false;

            var byId = currentRecords.ToDictionary(record => record.ChangeId);
            foreach (var transition in transitions)
            {
                byId.TryGetValue(transition.ChangeId, out var existing);
                if (!transition.ExpectedLifecycle.HasValue)
                {
                    if (existing is not null)
                        return false;
                }
                else if (existing is null || existing.Lifecycle != transition.ExpectedLifecycle.Value)
                {
                    return false;
                }
            }

            if (_onConditionalExpectationMatched is not null)
                await _onConditionalExpectationMatched(cancellationToken).ConfigureAwait(false);

            foreach (var transition in transitions)
            {
                byId.TryGetValue(transition.ChangeId, out var existing);
                ValidatePermittedTransition(transition, existing);
                byId[transition.ChangeId] = transition.Proposed;
            }

            var replacement = OwnershipLedgerOrdering.Order(byId.Values);
            ValidateRecords(sessionId, replacement, durableState: false);
            await WriteCoreAsync(sessionId, replacement, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    private static void ValidateTransitionArguments(
        Guid sessionId,
        string expectedLedgerRevision,
        IReadOnlyList<OwnershipRecordTransition> transitions)
    {
        if (sessionId == Guid.Empty)
            throw new ArgumentException("Session id is required.", nameof(sessionId));
        if (!IsSha256Identity(expectedLedgerRevision))
            throw new ArgumentException("Expected ledger revision must be exactly 64 hexadecimal characters.", nameof(expectedLedgerRevision));
        ArgumentNullException.ThrowIfNull(transitions);
        if (transitions.Count == 0)
            throw new ArgumentException("At least one ownership transition is required.", nameof(transitions));

        var seen = new HashSet<Guid>();
        foreach (var transition in transitions)
        {
            if (transition is null)
                throw new ArgumentException("Ownership transitions must not contain null entries.", nameof(transitions));
            if (transition.ChangeId == Guid.Empty || transition.Proposed is null)
                throw new ArgumentException("Each ownership transition requires a change id and proposed record.", nameof(transitions));
            if (!seen.Add(transition.ChangeId))
                throw new ArgumentException($"Duplicate ownership transition change id {transition.ChangeId}.", nameof(transitions));
            if (transition.ChangeId != transition.Proposed.ChangeId)
                throw new ArgumentException("Transition change id must match the proposed record.", nameof(transitions));
            if (transition.Proposed.SessionId != sessionId)
                throw new ArgumentException("Proposed ownership records must belong to the requested session.", nameof(transitions));
            if (transition.ExpectedLifecycle.HasValue && !Enum.IsDefined(transition.ExpectedLifecycle.Value))
                throw new ArgumentException("Expected ownership lifecycle is invalid.", nameof(transitions));
            var failure = PersistedOwnedChange.Validate(transition.Proposed);
            if (!string.IsNullOrEmpty(failure))
                throw new ArgumentException($"Proposed ownership record is invalid: {failure}", nameof(transitions));
            if (!IsPermittedTransitionShape(transition.ExpectedLifecycle, transition.Proposed.Lifecycle))
                throw new InvalidOperationException(
                    $"Ownership lifecycle transition {transition.ExpectedLifecycle?.ToString() ?? "absent"} to {transition.Proposed.Lifecycle} is forbidden.");
        }
    }

    private static void ValidatePermittedTransition(
        OwnershipRecordTransition transition,
        PersistedOwnedChange? existing)
    {
        if (!IsPermittedTransitionShape(transition.ExpectedLifecycle, transition.Proposed.Lifecycle))
            throw new InvalidOperationException(
                $"Ownership lifecycle transition {transition.ExpectedLifecycle?.ToString() ?? "absent"} to {transition.Proposed.Lifecycle} is forbidden.");
        if (existing is not null && !existing.ImmutableFieldsMatch(transition.Proposed))
            throw new InvalidOperationException("Ownership immutable identity or recovery provenance changed.");
    }

    private static bool IsPermittedTransitionShape(
        OwnedChangeLifecycle? expected,
        OwnedChangeLifecycle proposed)
        => (expected, proposed) switch
        {
            (null, OwnedChangeLifecycle.Planned) => true,
            (OwnedChangeLifecycle.Planned, OwnedChangeLifecycle.Applied) => true,
            (OwnedChangeLifecycle.Applied, OwnedChangeLifecycle.Reverted) => true,
            _ => false
        };

    private static bool IsSha256Identity(string? value)
        => value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f');

    private static void VerifyChecksum(Envelope envelope)
    {
        if (envelope.Payload is null || envelope.Sha256 is null)
            throw new InvalidDataException("Ownership ledger envelope is missing its payload or checksum.");

        byte[] calculated;
        byte[] expected;
        try
        {
            if (!IsSha256Identity(envelope.Sha256))
                throw new FormatException("Checksum must be exactly 64 hexadecimal characters.");
            calculated = Convert.FromHexString(
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(envelope.Payload))));
            expected = Convert.FromHexString(envelope.Sha256);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("Ownership ledger checksum is malformed.", exception);
        }

        if (!CryptographicOperations.FixedTimeEquals(calculated, expected))
            throw new InvalidDataException("Ownership ledger checksum mismatch.");
    }

    private static void ValidateRecords(Guid sessionId, IReadOnlyList<PersistedOwnedChange>? records, bool durableState)
    {
        void Fail(string message)
        {
            if (durableState)
                throw new InvalidDataException(message);
            throw new InvalidOperationException(message);
        }

        if (records is null)
        {
            Fail("Ownership ledger records collection is required.");
            return;
        }

        var seenChangeIds = new HashSet<Guid>();
        for (var index = 0; index < records.Count; index++)
        {
            var record = records[index];
            if (record is null)
            {
                Fail($"Ownership ledger record at index {index} is required.");
                continue;
            }
            var failure = PersistedOwnedChange.Validate(record);
            if (!string.IsNullOrEmpty(failure))
                Fail($"Ownership ledger contains an invalid record: {failure}");
            if (record.SessionId != sessionId)
                Fail("Ownership ledger contains a record belonging to another session.");
            if (!seenChangeIds.Add(record.ChangeId))
                Fail($"Ownership ledger contains duplicate change id {record.ChangeId}.");
        }
    }

    private async Task WriteCoreAsync(Guid sessionId, IReadOnlyList<PersistedOwnedChange> records, CancellationToken cancellationToken)
    {
        var ordered = OwnershipLedgerOrdering.Order(records);
        var payload = JsonSerializer.Serialize(ordered, JsonOptions);
        var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
        var envelope = JsonSerializer.Serialize(new Envelope(payload, checksum), JsonOptions);

        Directory.CreateDirectory(_rootDirectory);
        var path = PathFor(sessionId);
        var temp = Path.Combine(_rootDirectory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                temp,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough | FileOptions.Asynchronous))
            {
                var bytes = Encoding.UTF8.GetBytes(envelope);
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            DurableReplace(temp, path);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    private static string ComputeRevision(string payload)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));

    private static void DurableReplace(string sourcePath, string destinationPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.Move(sourcePath, destinationPath, overwrite: true);
            return;
        }

        if (!MoveFileExWindows.Value(sourcePath, destinationPath, MoveFileReplaceExisting | MoveFileWriteThrough))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The durable ownership-ledger replacement failed.");
    }

    private static MoveFileExDelegate LoadMoveFileExWindows()
    {
        var library = NativeLibrary.Load("kernel32.dll");
        var address = NativeLibrary.GetExport(library, "MoveFileExW");
        return Marshal.GetDelegateForFunctionPointer<MoveFileExDelegate>(address);
    }

    private string PathFor(Guid sessionId)
        => Path.Combine(_rootDirectory, $"{sessionId:N}.lrw");
}
