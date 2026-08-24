using LibertyRoute.Core;

namespace LibertyRoute.Recovery;

public sealed class RecoveryManager
{
    private readonly ITransactionJournal _journal;

    public RecoveryManager(ITransactionJournal journal) => _journal = journal;

    public Task<NetworkTransaction?> DetectUnfinishedSessionAsync(CancellationToken cancellationToken)
        => _journal.ReadActiveAsync(cancellationToken);

    public static bool NeedsRecovery(NetworkTransaction transaction) =>
        transaction.State is not ConnectionState.Disconnected;
}
