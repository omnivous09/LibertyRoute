using LibertyRoute.Core;
using LibertyRoute.Engine;
using LibertyRoute.Networking;
using LibertyRoute.Recovery;

namespace LibertyRoute.Service;

public sealed class ConnectionController
{
    private readonly SemaphoreSlim _networkLock = new(1, 1);
    private readonly INetworkStateManager _network;
    private readonly ITransactionJournal _journal;
    private readonly IConnectionEngine _engine;
    private NetworkTransaction? _active;

    public ConnectionController(
        INetworkStateManager network,
        ITransactionJournal journal,
        IConnectionEngine engine)
    {
        _network = network;
        _journal = journal;
        _engine = engine;
    }

    public ConnectionState State => _active?.State ?? ConnectionState.Disconnected;

    public async Task<NetworkTransaction> BeginSafeConnectAsync(CancellationToken cancellationToken)
    {
        await _networkLock.WaitAsync(cancellationToken);
        try
        {
            if (_active is not null)
                throw new InvalidOperationException("A LibertyRoute network transaction is already active.");

            if (await _journal.ReadActiveAsync(cancellationToken) is not null)
                throw new InvalidOperationException("An unfinished session exists and must be recovered before connecting.");

            var snapshot = await _network.CaptureStateAsync(cancellationToken);
            var transaction = new NetworkTransaction(
                Guid.NewGuid(),
                ConnectionState.SnapshotCommitted,
                DateTimeOffset.UtcNow,
                snapshot,
                Array.Empty<OwnedNetworkChange>(),
                _engine.Id,
                null);

            // Critical invariant: persist the rollback state before any network mutation.
            await _journal.WriteAsync(transaction, cancellationToken);
            _active = transaction;
            return transaction;
        }
        finally
        {
            _networkLock.Release();
        }
    }

    public async Task RollbackAsync(string? reason, CancellationToken cancellationToken)
    {
        await _networkLock.WaitAsync(cancellationToken);
        try
        {
            var tx = _active ?? await _journal.ReadActiveAsync(cancellationToken);
            if (tx is null)
                return;

            tx = tx with { State = ConnectionState.RollingBack, LastError = reason };
            await _journal.WriteAsync(tx, cancellationToken);

            // Phase 1 creates no privileged network changes, so rollback consists of
            // proving the captured baseline is still observable.
            await _engine.StopAsync(cancellationToken);
            await _network.VerifyRestorationAsync(tx.Snapshot, cancellationToken);

            tx = tx with { State = ConnectionState.Disconnected };
            await _journal.WriteAsync(tx, cancellationToken);
            await _journal.ClearAsync(tx.SessionId, cancellationToken);
            _active = null;
        }
        catch
        {
            if (_active is not null)
            {
                _active = _active with { State = ConnectionState.RestorationFailed };
                try { await _journal.WriteAsync(_active, CancellationToken.None); } catch { }
            }
            throw;
        }
        finally
        {
            _networkLock.Release();
        }
    }

    public async Task RecoverOnStartupAsync(CancellationToken cancellationToken)
    {
        var tx = await _journal.ReadActiveAsync(cancellationToken);
        if (tx is null)
            return;

        _active = tx;
        await RollbackAsync("Recovered unfinished session during service startup.", cancellationToken);
    }
}
