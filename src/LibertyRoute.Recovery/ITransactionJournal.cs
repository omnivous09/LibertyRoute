using LibertyRoute.Core;

namespace LibertyRoute.Recovery;

public interface ITransactionJournal
{
    Task WriteAsync(NetworkTransaction transaction, CancellationToken cancellationToken);
    Task<NetworkTransaction?> ReadActiveAsync(CancellationToken cancellationToken);
    Task ClearAsync(Guid expectedSessionId, CancellationToken cancellationToken);
    string JournalPath { get; }
}

public sealed record RecoveryJournalSnapshot(
    NetworkTransaction Transaction,
    string JournalRevision);

public sealed record RecoveryTransitionExpectation(
    Guid SessionId,
    string JournalRevision,
    RecoveryPhase? ExpectedPhase,
    Guid? RecoveryAttemptId,
    string AuthorizedTransactionFingerprint,
    string? RecoveryManifestFingerprint);

public interface IRecoveryTransactionJournal : ITransactionJournal
{
    Task<RecoveryJournalSnapshot?> ReadActiveRecoveryAsync(CancellationToken cancellationToken);

    Task<bool> TryAdvanceRecoveryAsync(
        RecoveryTransitionExpectation expected,
        NetworkTransaction proposed,
        CancellationToken cancellationToken);

    Task<bool> TryClearTerminalRecoveryAsync(
        Guid expectedSessionId,
        string expectedAuthorizedTransactionFingerprint,
        Guid expectedRecoveryAttemptId,
        string expectedRecoveryManifestFingerprint,
        CancellationToken cancellationToken);
}
