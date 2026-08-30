using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
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

public sealed class FileTransactionJournal : IRecoveryTransactionJournal
{
    private const uint MoveFileReplaceExisting = 0x1;
    private const uint MoveFileWriteThrough = 0x8;
    private sealed record Envelope(string Payload, string Sha256);
    private sealed record ValidatedJournal(NetworkTransaction Transaction, string Payload, string Revision);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly SemaphoreSlim _journalGate;
    private readonly Action<string, string> _durableReplace;
    private readonly Func<CancellationToken, Task>? _onRecoveryExpectationMatched;

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
        : this(journalPath, DurableReplaceWindows, onRecoveryExpectationMatched: null)
    {
    }

    internal FileTransactionJournal(string journalPath, Action<string, string> durableReplace)
        : this(journalPath, durableReplace, onRecoveryExpectationMatched: null)
    {
    }

    internal FileTransactionJournal(
        string journalPath,
        Func<CancellationToken, Task> onRecoveryExpectationMatched)
        : this(journalPath, DurableReplaceWindows, onRecoveryExpectationMatched)
    {
    }

    private FileTransactionJournal(
        string journalPath,
        Action<string, string> durableReplace,
        Func<CancellationToken, Task>? onRecoveryExpectationMatched)
    {
        if (string.IsNullOrWhiteSpace(journalPath))
            throw new ArgumentException("Journal path is required.", nameof(journalPath));

        JournalPath = Path.GetFullPath(journalPath);
        _journalGate = FileTransactionJournalLocks.Get(JournalPath);
        _durableReplace = durableReplace ?? throw new ArgumentNullException(nameof(durableReplace));
        _onRecoveryExpectationMatched = onRecoveryExpectationMatched;
        Directory.CreateDirectory(Path.GetDirectoryName(JournalPath)!);
    }

    public async Task WriteAsync(NetworkTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        await _journalGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteCoreAsync(transaction, cancellationToken).ConfigureAwait(false);
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
            return (await ReadValidatedCoreAsync(cancellationToken).ConfigureAwait(false))?.Transaction;
        }
        finally
        {
            _journalGate.Release();
        }
    }

    public async Task<RecoveryJournalSnapshot?> ReadActiveRecoveryAsync(CancellationToken cancellationToken)
    {
        await _journalGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await ReadValidatedCoreAsync(cancellationToken).ConfigureAwait(false);
            return current is null ? null : new RecoveryJournalSnapshot(current.Transaction, current.Revision);
        }
        finally
        {
            _journalGate.Release();
        }
    }

    private async Task<ValidatedJournal?> ReadValidatedCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(JournalPath))
            return null;

        var envelopeJson = await File.ReadAllTextAsync(JournalPath, cancellationToken).ConfigureAwait(false);
        Envelope envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<Envelope>(envelopeJson, JsonOptions)
                       ?? throw new InvalidDataException("Transaction journal envelope is invalid.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Transaction journal envelope is malformed.", exception);
        }
        if (envelope.Payload is null || envelope.Sha256 is null)
            throw new InvalidDataException("Transaction journal envelope is missing its payload or checksum.");

        var calculated = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(envelope.Payload)));

        byte[] expectedChecksum;
        try
        {
            expectedChecksum = Convert.FromHexString(envelope.Sha256);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("Transaction journal checksum is malformed.", exception);
        }
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(calculated), expectedChecksum))
        {
            throw new InvalidDataException("Transaction journal checksum mismatch.");
        }

        NetworkTransaction transaction;
        try
        {
            transaction = JsonSerializer.Deserialize<NetworkTransaction>(envelope.Payload, JsonOptions)
                ?? throw new InvalidDataException("Transaction payload is invalid.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Transaction payload is malformed.", exception);
        }

        var structuralFailure = ValidateFingerprintStructure(transaction);
        if (!string.IsNullOrEmpty(structuralFailure))
            throw new InvalidDataException($"Transaction payload is structurally invalid: {structuralFailure}");
        if (transaction.RecoveryCompletion is not null)
        {
            var recoveryFailure = RecoveryCompletion.Validate(transaction.RecoveryCompletion);
            if (!string.IsNullOrEmpty(recoveryFailure))
               throw new InvalidDataException($"Recovery completion state is invalid: {recoveryFailure}");

            var manifestFailure = RecoveryCompletion.ValidateDurableManifest(transaction.RecoveryCompletion, transaction);
            if (!string.IsNullOrEmpty(manifestFailure))
               throw new InvalidDataException($"Recovery manifest validation failed: {manifestFailure}");
        }

        return new ValidatedJournal(transaction, envelope.Payload, calculated);
    }

    public async Task<bool> TryAdvanceRecoveryAsync(
        RecoveryTransitionExpectation expected,
        NetworkTransaction proposed,
        CancellationToken cancellationToken)
    {
        ValidateExpectation(expected);
        ArgumentNullException.ThrowIfNull(proposed);

        await _journalGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await ReadValidatedCoreAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("An active transaction journal is required for a recovery transition.");

            if (!ExpectationMatches(expected, current))
                return false;

            if (_onRecoveryExpectationMatched is not null)
                await _onRecoveryExpectationMatched(cancellationToken).ConfigureAwait(false);

            var proposedFailure = ValidateTransaction(proposed, requireRecoveryCompletion: true);
            if (!string.IsNullOrEmpty(proposedFailure))
                throw new InvalidDataException($"Proposed recovery transaction is invalid: {proposedFailure}");

            ValidateTransition(current.Transaction, proposed);
            await WriteCoreAsync(proposed, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _journalGate.Release();
        }
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
            var validated = await ReadValidatedCoreAsync(cancellationToken).ConfigureAwait(false);
            if (validated is null)
               return false;
            var active = validated.Transaction;

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
            var validated = await ReadValidatedCoreAsync(cancellationToken).ConfigureAwait(false);
            if (validated is null)
               return;
            var active = validated.Transaction;

            if (active.SessionId != expectedSessionId)
               throw new InvalidOperationException("Refusing to clear a journal owned by another session.");

            File.Delete(JournalPath);
        }
        finally
        {
            _journalGate.Release();
        }
    }

    private async Task WriteCoreAsync(NetworkTransaction transaction, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(transaction, JsonOptions);
        var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
        var envelope = JsonSerializer.Serialize(new Envelope(payload, checksum), JsonOptions);
        var directory = Path.GetDirectoryName(JournalPath)!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(JournalPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                FileOptions.WriteThrough | FileOptions.Asynchronous))
            {
                var bytes = Encoding.UTF8.GetBytes(envelope);
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            _durableReplace(temp, JournalPath);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    private static void ValidateExpectation(RecoveryTransitionExpectation expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (expected.SessionId == Guid.Empty)
            throw new ArgumentException("Expected session id is required.", nameof(expected));
        if (!IsSha256Identity(expected.JournalRevision))
            throw new ArgumentException("Expected journal revision is required.", nameof(expected));
        if (!IsSha256Identity(expected.AuthorizedTransactionFingerprint))
            throw new ArgumentException("Expected authorized transaction fingerprint is required.", nameof(expected));
        if (expected.ExpectedPhase.HasValue)
        {
            if (!Enum.IsDefined(expected.ExpectedPhase.Value))
                throw new ArgumentException("Expected recovery phase is invalid.", nameof(expected));
            if (!expected.RecoveryAttemptId.HasValue || expected.RecoveryAttemptId.Value == Guid.Empty)
                throw new ArgumentException("Expected recovery attempt id is required.", nameof(expected));
            if (!IsSha256Identity(expected.RecoveryManifestFingerprint))
                throw new ArgumentException("Expected recovery manifest fingerprint is required.", nameof(expected));
        }
        else if (expected.RecoveryAttemptId.HasValue || expected.RecoveryManifestFingerprint is not null)
        {
            throw new ArgumentException("A no-recovery expectation cannot include recovery identity.", nameof(expected));
        }
    }

    private static bool IsSha256Identity(string? value)
        => value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f');

    private static void DurableReplaceWindows(string sourcePath, string destinationPath)
    {
        if (!MoveFileEx(sourcePath, destinationPath, MoveFileReplaceExisting | MoveFileWriteThrough))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The durable journal replacement failed.");
    }

    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string existingFileName, string newFileName, uint flags);

    private static bool ExpectationMatches(RecoveryTransitionExpectation expected, ValidatedJournal current)
    {
        var transaction = current.Transaction;
        var completion = transaction.RecoveryCompletion;
        if (transaction.SessionId != expected.SessionId ||
            !StringComparer.Ordinal.Equals(current.Revision, expected.JournalRevision) ||
            !StringComparer.Ordinal.Equals(
                RecoveryFingerprinting.ComputeAuthorizedTransactionFingerprint(transaction),
                expected.AuthorizedTransactionFingerprint))
            return false;

        if (!expected.ExpectedPhase.HasValue)
            return completion is null;
        return completion is not null && completion.Phase == expected.ExpectedPhase.Value &&
            completion.RecoveryAttemptId == expected.RecoveryAttemptId &&
            StringComparer.Ordinal.Equals(completion.AuthorizedTransactionFingerprint, expected.AuthorizedTransactionFingerprint) &&
            StringComparer.Ordinal.Equals(completion.RecoveryManifestFingerprint, expected.RecoveryManifestFingerprint);
    }

    private static string ValidateTransaction(NetworkTransaction transaction, bool requireRecoveryCompletion)
    {
        var structuralFailure = ValidateFingerprintStructure(transaction);
        if (!string.IsNullOrEmpty(structuralFailure))
            return structuralFailure;
        if (requireRecoveryCompletion && transaction.RecoveryCompletion is null)
            return "Recovery completion is required.";
        if (transaction.RecoveryCompletion is not null)
        {
            var completionFailure = RecoveryCompletion.Validate(transaction.RecoveryCompletion);
            if (!string.IsNullOrEmpty(completionFailure))
                return completionFailure;
            var manifestFailure = RecoveryCompletion.ValidateDurableManifest(transaction.RecoveryCompletion, transaction);
            if (!string.IsNullOrEmpty(manifestFailure))
                return manifestFailure;
        }
        return string.Empty;
    }

    private static string ValidateFingerprintStructure(NetworkTransaction transaction)
    {
        if (transaction.SessionId == Guid.Empty)
            return "Transaction session id is required.";
        if (!Enum.IsDefined(transaction.State))
            return "Transaction state is invalid.";
        if (transaction.Snapshot is null)
            return "Transaction snapshot is required.";
        if (string.IsNullOrWhiteSpace(transaction.Snapshot.MachineName))
            return "Snapshot machine name is required.";
        if (transaction.Snapshot.Adapters is null)
            return "Snapshot adapters collection is required.";
        if (transaction.Changes is null)
            return "Transaction changes collection is required.";

        for (var index = 0; index < transaction.Snapshot.Adapters.Count; index++)
        {
            var adapter = transaction.Snapshot.Adapters[index];
            if (adapter is null)
                return $"Snapshot adapter at index {index} is required.";
            if (string.IsNullOrWhiteSpace(adapter.Id) || string.IsNullOrWhiteSpace(adapter.Name) ||
                string.IsNullOrWhiteSpace(adapter.Description) || string.IsNullOrWhiteSpace(adapter.NetworkInterfaceType) ||
                string.IsNullOrWhiteSpace(adapter.OperationalStatus))
                return $"Snapshot adapter at index {index} has missing identity fields.";
            var failure = ValidateStringCollection(adapter.UnicastAddresses, $"adapter {index} unicast addresses") ??
                ValidateStringCollection(adapter.Gateways, $"adapter {index} gateways") ??
                ValidateStringCollection(adapter.DnsServers, $"adapter {index} DNS servers");
            if (failure is not null)
                return failure;
        }

        if (transaction.Snapshot.Routes is not null)
        {
            for (var index = 0; index < transaction.Snapshot.Routes.Count; index++)
            {
                var route = transaction.Snapshot.Routes[index];
                if (route is null)
                    return $"Snapshot route at index {index} is required.";
                if (string.IsNullOrWhiteSpace(route.Destination) || string.IsNullOrWhiteSpace(route.NextHop) ||
                    string.IsNullOrWhiteSpace(route.AddressFamily))
                    return $"Snapshot route at index {index} has missing identity fields.";
            }
        }

        if (transaction.Snapshot.DnsInterfaces is not null)
        {
            for (var index = 0; index < transaction.Snapshot.DnsInterfaces.Count; index++)
            {
                var dns = transaction.Snapshot.DnsInterfaces[index];
                if (dns is null)
                    return $"Snapshot DNS interface at index {index} is required.";
                if (string.IsNullOrWhiteSpace(dns.InterfaceId) || string.IsNullOrWhiteSpace(dns.InterfaceName) ||
                    !Enum.IsDefined(dns.IPv4ConfigurationSource) || !Enum.IsDefined(dns.IPv6ConfigurationSource))
                    return $"Snapshot DNS interface at index {index} has invalid identity fields.";
                var failure = ValidateStringCollection(dns.DnsServers, $"DNS interface {index} servers") ??
                    ValidateOptionalStringCollection(dns.IPv4DnsServers, $"DNS interface {index} IPv4 servers") ??
                    ValidateOptionalStringCollection(dns.IPv6DnsServers, $"DNS interface {index} IPv6 servers") ??
                    ValidateOptionalStringCollection(dns.IPv4StaticDnsServers, $"DNS interface {index} IPv4 static servers") ??
                    ValidateOptionalStringCollection(dns.IPv4DhcpDnsServers, $"DNS interface {index} IPv4 DHCP servers") ??
                    ValidateOptionalStringCollection(dns.IPv6StaticDnsServers, $"DNS interface {index} IPv6 static servers") ??
                    ValidateOptionalStringCollection(dns.IPv6DhcpDnsServers, $"DNS interface {index} IPv6 DHCP servers");
                if (failure is not null)
                    return failure;
            }
        }

        for (var index = 0; index < transaction.Changes.Count; index++)
        {
            var change = transaction.Changes[index];
            if (change is null)
                return $"Transaction change at index {index} is required.";
            if (change.ChangeId == Guid.Empty || string.IsNullOrWhiteSpace(change.Kind) || string.IsNullOrWhiteSpace(change.Target))
                return $"Transaction change at index {index} has invalid identity fields.";
        }
        return string.Empty;
    }

    private static string? ValidateStringCollection(IReadOnlyList<string>? values, string name)
    {
        if (values is null)
            return $"The {name} collection is required.";
        return values.Any(string.IsNullOrWhiteSpace) ? $"The {name} collection contains an invalid value." : null;
    }

    private static string? ValidateOptionalStringCollection(IReadOnlyList<string>? values, string name)
        => values is null ? null : ValidateStringCollection(values, name);

    private static void ValidateTransition(NetworkTransaction current, NetworkTransaction proposed)
    {
        var from = current.RecoveryCompletion;
        var to = proposed.RecoveryCompletion!;
        if (from is null)
        {
            if (to.Phase != RecoveryPhase.IntentRecorded)
                throw new InvalidOperationException("The first recovery transition must record IntentRecorded.");
            if (current.SessionId != proposed.SessionId ||
                !StringComparer.Ordinal.Equals(
                    RecoveryFingerprinting.ComputeAuthorizedTransactionFingerprint(current),
                    to.AuthorizedTransactionFingerprint))
                throw new InvalidOperationException("The initial recovery transition changed authorized transaction identity.");
            return;
        }

        var allowed = (from.Phase, to.Phase) switch
        {
            (RecoveryPhase.IntentRecorded, RecoveryPhase.Prepared) => true,
            (RecoveryPhase.Prepared, RecoveryPhase.ExecutionStarted) => true,
            (RecoveryPhase.ExecutionStarted, RecoveryPhase.ExecutionCompleted) => true,
            (RecoveryPhase.ExecutionCompleted, RecoveryPhase.BaselineVerified) => true,
            (RecoveryPhase.BaselineVerified, RecoveryPhase.LedgerFinalizing) => true,
            (RecoveryPhase.LedgerFinalizing, RecoveryPhase.LedgerFinalized) => true,
            (RecoveryPhase.LedgerFinalized, RecoveryPhase.TerminalCommitted) => true,
            (_, RecoveryPhase.ManualRecoveryRequired) when IsManualOrigin(from.Phase) && to.ManualRecoveryOriginPhase == from.Phase => true,
            _ => false
        };
        if (!allowed)
            throw new InvalidOperationException($"Recovery phase transition {from.Phase} -> {to.Phase} is forbidden.");

        var immutableFailure = RecoveryCompletion.ValidateImmutableIdentity(current, proposed);
        if (!string.IsNullOrEmpty(immutableFailure))
            throw new InvalidOperationException(immutableFailure);
    }

    private static bool IsManualOrigin(RecoveryPhase phase) => phase is
        RecoveryPhase.IntentRecorded or RecoveryPhase.Prepared or RecoveryPhase.ExecutionStarted or
        RecoveryPhase.ExecutionCompleted or RecoveryPhase.BaselineVerified or
        RecoveryPhase.LedgerFinalizing or RecoveryPhase.LedgerFinalized;
}
