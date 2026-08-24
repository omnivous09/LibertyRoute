using LibertyRoute.Core;

namespace LibertyRoute.Recovery;

public interface ITransactionJournal
{
    Task WriteAsync(NetworkTransaction transaction, CancellationToken cancellationToken);
    Task<NetworkTransaction?> ReadActiveAsync(CancellationToken cancellationToken);
    Task ClearAsync(Guid expectedSessionId, CancellationToken cancellationToken);
    string JournalPath { get; }
}
