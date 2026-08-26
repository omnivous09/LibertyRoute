using System.Collections.Concurrent;
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
/// Concurrency: all mutating operations (AppendAsync, ClearSessionAsync) for the same
/// session are serialized by a process-lifetime per-session gate owned by this class,
/// so same-process callers can never interleave read-modify-write sequences for one
/// session. Operations for different sessions remain independently concurrent. Reads
/// are intentionally lock-free: atomic file replacement already guarantees readers
/// never observe an intermediate state. Cross-process writers are NOT supported by
/// this implementation; production wiring must preserve a single-writer ownership
/// model until cross-process coordination is deliberately implemented.
/// </summary>
public sealed class FileOwnershipLedger : IOwnershipLedger
{
    private sealed record Envelope(string Payload, string Sha256);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _rootDirectory;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _sessionGates = new();
    private readonly Func<Guid, CancellationToken, Task>? _onSessionGateAcquired;

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
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
            throw new ArgumentException("Root directory is required.", nameof(rootDirectory));

        _rootDirectory = rootDirectory;
        _onSessionGateAcquired = onSessionGateAcquired;
        Directory.CreateDirectory(_rootDirectory);
    }

    /// <summary>
    /// Returns the process-lifetime gate for one session. Gates are never removed or
    /// disposed while other operations may be waiting on them; the bounded memory cost
    /// of one SemaphoreSlim per session ever touched is accepted deliberately.
    /// </summary>
    private SemaphoreSlim GateFor(Guid sessionId)
        => _sessionGates.GetOrAdd(sessionId, static session => new SemaphoreSlim(1, 1));

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

            var records = await ReadForSessionAsync(record.SessionId, cancellationToken).ConfigureAwait(false);
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

            await WriteAsync(record.SessionId, updated, cancellationToken).ConfigureAwait(false);
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

        var path = PathFor(sessionId);
        if (!File.Exists(path))
            return Array.Empty<PersistedOwnedChange>();

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

        var seenChangeIds = new HashSet<Guid>();
        foreach (var record in records)
        {
            var failure = PersistedOwnedChange.Validate(record);
            if (!string.IsNullOrEmpty(failure))
                throw new InvalidDataException($"Ownership ledger contains an invalid record: {failure}");

            if (record.SessionId != sessionId)
                throw new InvalidDataException("Ownership ledger contains a record belonging to another session.");

            if (!seenChangeIds.Add(record.ChangeId))
                throw new InvalidDataException($"Ownership ledger contains duplicate change id {record.ChangeId}.");
        }

        return OwnershipLedgerOrdering.Order(records);
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

    private static void VerifyChecksum(Envelope envelope)
    {
        if (envelope.Payload is null || envelope.Sha256 is null)
            throw new InvalidDataException("Ownership ledger envelope is missing its payload or checksum.");

        byte[] calculated;
        byte[] expected;
        try
        {
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

    private async Task WriteAsync(Guid sessionId, IReadOnlyList<PersistedOwnedChange> records, CancellationToken cancellationToken)
    {
        var ordered = OwnershipLedgerOrdering.Order(records);
        var payload = JsonSerializer.Serialize(ordered, JsonOptions);
        var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
        var envelope = JsonSerializer.Serialize(new Envelope(payload, checksum), JsonOptions);

        Directory.CreateDirectory(_rootDirectory);
        var path = PathFor(sessionId);
        var temp = Path.Combine(_rootDirectory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        await using (var stream = new FileStream(
            temp,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.WriteThrough | FileOptions.Asynchronous))
        {
            var bytes = Encoding.UTF8.GetBytes(envelope);
            await stream.WriteAsync(bytes, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, path, overwrite: true);
    }

    private string PathFor(Guid sessionId)
        => Path.Combine(_rootDirectory, $"{sessionId:N}.lrw");
}