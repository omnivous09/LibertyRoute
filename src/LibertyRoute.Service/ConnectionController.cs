using System.Security.Principal;
using LibertyRoute.Core;
using LibertyRoute.Engine;
using LibertyRoute.Networking;
using LibertyRoute.Recovery;

namespace LibertyRoute.Service;

internal enum SessionAuthorizationDecision
{
    NoActiveSession,
    OwnerAuthorized,
    OperationalOverrideAuthorized,
    ForeignOwnerDenied,
    LegacyOwnerDenied,
    InvalidOwnerDenied,
    InconsistentStateDenied
}

internal sealed record SessionAuthorizationResult(
    SessionAuthorizationDecision Decision,
    ConnectionState State,
    Guid? SessionId,
    NetworkTransaction? AuthorizedTransaction = null);

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

    public async Task<NetworkStateSnapshot> CaptureDiagnosticSnapshotAsync(CancellationToken cancellationToken)
    {
        await _networkLock.WaitAsync(cancellationToken);
        try
        {
            return await _network.CaptureStateAsync(cancellationToken);
        }
        finally
        {
            _networkLock.Release();
        }
    }

    public async Task<NetworkTransaction> BeginSafeConnectAsync(
        string? ownerSid,
        CancellationToken cancellationToken)
    {
        var canonicalOwnerSid = WindowsControlCallerIdentityCapture.CanonicalizeUserSid(ownerSid);
        await _networkLock.WaitAsync(cancellationToken);
        try
        {
            if (_active is not null)
                throw new InvalidOperationException("A LibertyRoute network transaction is already active.");

            var existing = await _journal.ReadActiveAsync(cancellationToken);
            if (existing is not null)
            {
                ValidatePersistedOwner(existing);
                throw new InvalidOperationException("An unfinished session exists and must be recovered before connecting.");
            }

            var snapshot = await _network.CaptureStateAsync(cancellationToken);
            var transaction = new NetworkTransaction(
                Guid.NewGuid(),
                ConnectionState.SnapshotCommitted,
                DateTimeOffset.UtcNow,
                snapshot,
                Array.Empty<OwnedNetworkChange>(),
                _engine.Id,
                null,
                canonicalOwnerSid);

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
            ValidatePersistedOwner(tx);

            await RollbackCoreUnderLockAsync(tx, reason, cancellationToken);
        }
        finally
        {
            _networkLock.Release();
        }
    }

    internal async Task<SessionAuthorizationResult> GetStatusAuthorizedAsync(
        ControlCallerIdentity caller,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        await _networkLock.WaitAsync(cancellationToken);
        try
        {
            return await EvaluateSessionAuthorizationUnderLockAsync(caller, cancellationToken);
        }
        finally
        {
            _networkLock.Release();
        }
    }

    internal async Task<SessionAuthorizationResult> RollbackAuthorizedAsync(
        ControlCallerIdentity caller,
        string? reason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        await _networkLock.WaitAsync(cancellationToken);
        try
        {
            var authResult = await EvaluateSessionAuthorizationUnderLockAsync(caller, cancellationToken);
            if (authResult.Decision is SessionAuthorizationDecision.OwnerAuthorized
                or SessionAuthorizationDecision.OperationalOverrideAuthorized)
            {
                var tx = authResult.AuthorizedTransaction
                    ?? throw new InvalidOperationException("Authorized session transaction is unavailable.");
                await RollbackCoreUnderLockAsync(tx, reason, cancellationToken);
            }

            return authResult;
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
        ValidatePersistedOwner(tx);

        _active = tx;
        await RollbackAsync("Recovered unfinished session during service startup.", cancellationToken);
    }

    private async Task<SessionAuthorizationResult> EvaluateSessionAuthorizationUnderLockAsync(
        ControlCallerIdentity caller,
        CancellationToken cancellationToken)
    {
        NetworkTransaction? journalTx;
        try
        {
            journalTx = await _journal.ReadActiveAsync(cancellationToken);
        }
        catch
        {
            return new SessionAuthorizationResult(
                SessionAuthorizationDecision.InconsistentStateDenied,
                ConnectionState.Disconnected,
                _active?.SessionId);
        }

        if (_active is null && journalTx is null)
        {
            return new SessionAuthorizationResult(
                SessionAuthorizationDecision.NoActiveSession,
                ConnectionState.Disconnected,
                null);
        }

        if (_active is not null && journalTx is null)
        {
            return new SessionAuthorizationResult(
                SessionAuthorizationDecision.InconsistentStateDenied,
                _active.State,
                _active.SessionId);
        }

        if (_active is not null && journalTx is not null)
        {
            if (_active.SessionId != journalTx.SessionId ||
                !StringComparer.Ordinal.Equals(_active.OwnerSid, journalTx.OwnerSid))
            {
                return new SessionAuthorizationResult(
                    SessionAuthorizationDecision.InconsistentStateDenied,
                    _active.State,
                    _active.SessionId);
            }
        }

        var targetTx = journalTx!;
        var sessionId = targetTx.SessionId;
        var state = targetTx.State;
        var isOverrideEligible = caller.IsBuiltinAdministrator || caller.IsLocalSystem;

        if (targetTx.OwnerSid is null)
        {
            return new SessionAuthorizationResult(
                isOverrideEligible
                    ? SessionAuthorizationDecision.OperationalOverrideAuthorized
                    : SessionAuthorizationDecision.LegacyOwnerDenied,
                state,
                sessionId,
                isOverrideEligible ? targetTx : null);
        }

        if (!IsValidCanonicalSid(targetTx.OwnerSid))
        {
            return new SessionAuthorizationResult(
                isOverrideEligible
                    ? SessionAuthorizationDecision.OperationalOverrideAuthorized
                    : SessionAuthorizationDecision.InvalidOwnerDenied,
                state,
                sessionId,
                isOverrideEligible ? targetTx : null);
        }

        if (StringComparer.Ordinal.Equals(caller.UserSid, targetTx.OwnerSid))
        {
            return new SessionAuthorizationResult(
                SessionAuthorizationDecision.OwnerAuthorized,
                state,
                sessionId,
                targetTx);
        }

        return new SessionAuthorizationResult(
            isOverrideEligible
                ? SessionAuthorizationDecision.OperationalOverrideAuthorized
                : SessionAuthorizationDecision.ForeignOwnerDenied,
            state,
            sessionId,
            isOverrideEligible ? targetTx : null);
    }

    private async Task RollbackCoreUnderLockAsync(
        NetworkTransaction tx,
        string? reason,
        CancellationToken cancellationToken)
    {
        try
        {
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
    }

    private static bool IsValidCanonicalSid(string? sidText)
    {
        if (string.IsNullOrWhiteSpace(sidText))
            return false;

        try
        {
            var sid = new SecurityIdentifier(sidText);
            return StringComparer.Ordinal.Equals(sidText, sid.Value);
        }
        catch
        {
            return false;
        }
    }

    private static void ValidatePersistedOwner(NetworkTransaction transaction)
    {
        if (transaction.OwnerSid is not null)
            _ = WindowsControlCallerIdentityCapture.CanonicalizeUserSid(transaction.OwnerSid);
    }
}
