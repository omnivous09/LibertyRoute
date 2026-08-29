using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LibertyRoute.Core;

namespace LibertyRoute.Recovery;

internal static class FileTransactionJournalLocks
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.OrdinalIgnoreCase);

    public static SemaphoreSlim Get(string journalPath)
    {
        var canonical = Canonicalize(journalPath);
        return Locks.GetOrAdd(canonical, static _ => new SemaphoreSlim(1, 1));
    }

    public static string Canonicalize(string journalPath)
    {
        if (string.IsNullOrWhiteSpace(journalPath))
            throw new ArgumentException("Journal path is required.", nameof(journalPath));

        var fullPath = Path.GetFullPath(journalPath);
        var normalized = fullPath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        normalized = normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return normalized.ToUpperInvariant();
    }
}

public sealed class FileTransactionJournal : ITransactionJournal
{
    private sealed record Envelope(string Payload, string Sha256);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly SemaphoreSlim _journalGate;

    public string JournalPath { get; }

    public FileTransactionJournal()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "LibertyRoute",
            "transactions",
            "active.lrj"))
    {
    }

    internal FileTransactionJournal(string journalPath)
    {
        if (string.IsNullOrWhiteSpace(journalPath))
            throw new ArgumentException("Journal path is required.", nameof(journalPath));

        JournalPath = Path.GetFullPath(journalPath);
        _journalGate = FileTransactionJournalLocks.Get(JournalPath);
        Directory.CreateDirectory(Path.GetDirectoryName(JournalPath)!);
    }

    public async Task WriteAsync(NetworkTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        await _journalGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var payload = JsonSerializer.Serialize(transaction, JsonOptions);
            var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
            var envelope = JsonSerializer.Serialize(new Envelope(payload, checksum), JsonOptions);

            var directory = Path.GetDirectoryName(JournalPath)!;
            Directory.CreateDirectory(directory);
            var temp = Path.Combine(directory, $".{Path.GetFileName(JournalPath)}.{Guid.NewGuid():N}.tmp");

            await using (var stream = new FileStream(
               temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
               FileOptions.WriteThrough | FileOptions.Asynchronous))
            {
               var bytes = Encoding.UTF8.GetBytes(envelope);
               await stream.WriteAsync(bytes, cancellationToken);
               await stream.FlushAsync(cancellationToken);
               stream.Flush(flushToDisk: true);
            }

            File.Move(temp, JournalPath, overwrite: true);
        }
        finally
        {
            _journalGate.Release();
        }
    }

    public async Task<NetworkTransaction?> ReadActiveAsync(CancellationToken cancellationToken)
    {
        await _journalGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadActiveCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _journalGate.Release();
        }
    }

    private async Task<NetworkTransaction?> ReadActiveCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(JournalPath))
            return null;

        var envelopeJson = await File.ReadAllTextAsync(JournalPath, cancellationToken).ConfigureAwait(false);
        var envelope = JsonSerializer.Deserialize<Envelope>(envelopeJson, JsonOptions)
                       ?? throw new InvalidDataException("Transaction journal envelope is invalid.");

        var calculated = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(envelope.Payload)));

        if (!CryptographicOperations.FixedTimeEquals(
               Convert.FromHexString(calculated),
               Convert.FromHexString(envelope.Sha256)))
        {
            throw new InvalidDataException("Transaction journal checksum mismatch.");
        }

        var transaction = JsonSerializer.Deserialize<NetworkTransaction>(envelope.Payload, JsonOptions)
               ?? throw new InvalidDataException("Transaction payload is invalid.");

        if (transaction.SessionId == Guid.Empty)
            throw new InvalidDataException("Transaction session id is required.");
        if (transaction.Snapshot == null)
            throw new InvalidDataException("Transaction snapshot is required.");
        if (transaction.RecoveryCompletion is not null)
        {
            var recoveryFailure = RecoveryCompletion.Validate(transaction.RecoveryCompletion);
            if (!string.IsNullOrEmpty(recoveryFailure))
               throw new InvalidDataException($"Recovery completion state is invalid: {recoveryFailure}");

            var manifestFailure = RecoveryCompletion.ValidateDurableManifest(transaction.RecoveryCompletion, transaction);
            if (!string.IsNullOrEmpty(manifestFailure))
               throw new InvalidDataException($"Recovery manifest validation failed: {manifestFailure}");
        }

        return transaction;
    }

    public async Task<bool> TryClearTerminalRecoveryAsync(
        Guid expectedSessionId,
        string expectedAuthorizedTransactionFingerprint,
        Guid expectedRecoveryAttemptId,
        string expectedRecoveryManifestFingerprint,
        CancellationToken cancellationToken)
    {
        if (expectedSessionId == Guid.Empty)
            throw new ArgumentException("Expected session id is required.", nameof(expectedSessionId));
        if (string.IsNullOrWhiteSpace(expectedAuthorizedTransactionFingerprint))
            throw new ArgumentException("Expected authorized transaction fingerprint is required.", nameof(expectedAuthorizedTransactionFingerprint));
        if (expectedRecoveryAttemptId == Guid.Empty)
            throw new ArgumentException("Expected recovery attempt id is required.", nameof(expectedRecoveryAttemptId));
        if (string.IsNullOrWhiteSpace(expectedRecoveryManifestFingerprint))
            throw new ArgumentException("Expected recovery manifest fingerprint is required.", nameof(expectedRecoveryManifestFingerprint));

        await _journalGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var active = await ReadActiveCoreAsync(cancellationToken).ConfigureAwait(false);
            if (active is null)
               return false;

            var completion = active.RecoveryCompletion;
            if (completion is null)
               return false;
            if (active.SessionId != expectedSessionId)
               return false;
            if (completion.RecoveryAttemptId != expectedRecoveryAttemptId)
               return false;
            if (!string.Equals(completion.AuthorizedTransactionFingerprint, expectedAuthorizedTransactionFingerprint, StringComparison.Ordinal))
               return false;
            if (!string.Equals(completion.RecoveryManifestFingerprint, expectedRecoveryManifestFingerprint, StringComparison.Ordinal))
               return false;
            if (completion.Phase != RecoveryPhase.TerminalCommitted)
               return false;

            File.Delete(JournalPath);
            return true;
        }
        finally
        {
            _journalGate.Release();
        }
    }

    public async Task<bool> TryClearTerminalRecoveryAsync(
        Guid expectedSessionId,
        NetworkTransaction expectedTransaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expectedTransaction);
        if (expectedTransaction.RecoveryCompletion is null)
            return false;

        return await TryClearTerminalRecoveryAsync(
            expectedSessionId,
            expectedTransaction.RecoveryCompletion.AuthorizedTransactionFingerprint,
            expectedTransaction.RecoveryCompletion.RecoveryAttemptId,
            expectedTransaction.RecoveryCompletion.RecoveryManifestFingerprint,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task ClearAsync(Guid expectedSessionId, CancellationToken cancellationToken)
    {
        await _journalGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var active = await ReadActiveCoreAsync(cancellationToken).ConfigureAwait(false);
            if (active is null)
               return;

            if (active.SessionId != expectedSessionId)
               throw new InvalidOperationException("Refusing to clear a journal owned by another session.");

            File.Delete(JournalPath);
        }
        finally
        {
            _journalGate.Release();
        }
    }
}
