using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LibertyRoute.ControlProtocol;
using LibertyRoute.Core;
using LibertyRoute.Engine;
using LibertyRoute.Networking;
using LibertyRoute.Recovery;
using LibertyRoute.Restoration;
using LibertyRoute.Service;

namespace LibertyRoute.Restoration.Tests;

public sealed class SessionOwnershipTests
{
    [Fact]
    public async Task ConnectCancellationBeforeBaselineCompletionPublishesNothing()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var network = new Network
        {
            BeforeCapture = async token =>
            {
                entered.TrySetResult();
                await new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task.WaitAsync(token);
            }
        };
        var journal = new Journal();
        var controller = new ConnectionController(network, journal, new Engine());
        using var cancellation = new CancellationTokenSource();
        var operation = controller.BeginSafeConnectAsync(OwnerA, cancellation.Token);

        await entered.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);

        Assert.Empty(journal.Writes);
        Assert.Null(journal.Active);
    }

    [Fact]
    public async Task ConnectCancellationImmediatelyBeforePublicationPublishesNothing()
    {
        using var cancellation = new CancellationTokenSource();
        var network = new Network { AfterCapture = cancellation.Cancel };
        var journal = new Journal();
        var controller = new ConnectionController(network, journal, new Engine());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            controller.BeginSafeConnectAsync(OwnerA, cancellation.Token));

        Assert.Empty(journal.Writes);
        Assert.Null(journal.Active);
    }

    [Fact]
    public async Task ConnectPublicationAndBookkeepingSettleAfterCallerCancellation()
    {
        var writeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var journal = new Journal
        {
            OnWrite = async _ => { writeEntered.TrySetResult(); await releaseWrite.Task; }
        };
        var controller = new ConnectionController(new Network(), journal, new Engine());
        using var cancellation = new CancellationTokenSource();
        var operation = controller.BeginSafeConnectAsync(OwnerA, cancellation.Token);

        await writeEntered.Task;
        cancellation.Cancel();
        releaseWrite.TrySetResult();
        var transaction = await operation;

        Assert.Equal(transaction, journal.Active);
        Assert.Equal(ConnectionState.SnapshotCommitted, controller.State);
        Assert.All(journal.WriteTokens, token => Assert.Equal(CancellationToken.None, token));
    }

    [Fact]
    public async Task RollbackAfterRollingBackPublicationIgnoresCallerCancellationForSafetyWork()
    {
        var rollingBackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRollingBack = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var journal = new Journal
        {
            OnWrite = async transaction =>
            {
                if (transaction.State == ConnectionState.RollingBack)
                {
                    rollingBackEntered.TrySetResult();
                    await releaseRollingBack.Task;
                }
            }
        };
        var network = new Network();
        var engine = new Engine();
        var controller = new ConnectionController(network, journal, engine);
        await controller.BeginSafeConnectAsync(OwnerA, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var operation = controller.RollbackAsync("shutdown", cancellation.Token);

        await rollingBackEntered.Task;
        cancellation.Cancel();
        releaseRollingBack.TrySetResult();
        await operation;

        Assert.Null(journal.Active);
        Assert.Equal(1, journal.Clears);
        Assert.Equal(CancellationToken.None, engine.StopTokens.Single());
        Assert.Equal(CancellationToken.None, network.VerifyTokens.Single());
        Assert.All(journal.WriteTokens, token => Assert.Equal(CancellationToken.None, token));
    }

    [Fact]
    public async Task RollbackCancellationBeforeRollingBackPublishesNothing()
    {
        var transaction = Transaction(OwnerA);
        var journal = new Journal { Active = transaction };
        var controller = new ConnectionController(new Network(), journal, new Engine());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            controller.RollbackAsync("shutdown", cancellation.Token));

        Assert.Empty(journal.Writes);
        Assert.Equal(transaction, journal.Active);
    }

    [Fact]
    public async Task PostBoundaryFailurePersistsRestorationFailedFromLocalTransaction()
    {
        var transaction = Transaction(OwnerA);
        var journal = new Journal { Active = transaction };
        var engine = new Engine { StopException = new IOException("stop failure") };
        var controller = new ConnectionController(new Network(), journal, engine);

        await Assert.ThrowsAsync<IOException>(() => controller.RollbackAsync("shutdown", CancellationToken.None));

        Assert.NotNull(journal.Active);
        Assert.Equal(transaction.SessionId, journal.Active!.SessionId);
        Assert.Equal(ConnectionState.RestorationFailed, journal.Active.State);
        Assert.Equal(0, journal.Clears);
        Assert.Equal(CancellationToken.None, journal.WriteTokens.Last());
    }

    private const string OwnerA = "S-1-5-21-1001";
    private const string OwnerB = "S-1-5-21-1002";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-sid")]
    public async Task InvalidOwnerFailsBeforeSnapshotCapture(string? ownerSid)
    {
        var network = new Network();
        var journal = new Journal();
        var controller = Controller(network, journal);

        await Assert.ThrowsAsync<ArgumentException>(
            () => controller.BeginSafeConnectAsync(ownerSid, CancellationToken.None));

        Assert.Equal(0, network.Captures);
        Assert.Empty(journal.Writes);
        Assert.Equal(ConnectionState.Disconnected, controller.State);
    }

    [Fact]
    public async Task CanonicalOwnerAndGeneratedSessionAreInFirstWriteBeforePublication()
    {
        var network = new Network();
        ConnectionController? controller = null;
        var journal = new Journal
        {
            OnWrite = transaction =>
            {
                Assert.NotEqual(Guid.Empty, transaction.SessionId);
                Assert.Equal(OwnerA, transaction.OwnerSid);
                Assert.Equal(ConnectionState.Disconnected, controller!.State);
                return Task.CompletedTask;
            }
        };
        controller = Controller(network, journal);

        var transaction = await controller.BeginSafeConnectAsync(OwnerA, CancellationToken.None);

        var first = Assert.Single(journal.Writes);
        Assert.Same(transaction, first);
        Assert.Equal(transaction.SessionId, first.SessionId);
        Assert.Equal(OwnerA, first.OwnerSid);
        Assert.Equal(ConnectionState.SnapshotCommitted, controller.State);
    }

    [Fact]
    public async Task CaptureFailureWritesNoSession()
    {
        var network = new Network { CaptureException = new InvalidOperationException("capture failed") };
        var journal = new Journal();
        var controller = Controller(network, journal);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => controller.BeginSafeConnectAsync(OwnerA, CancellationToken.None));

        Assert.Empty(journal.Writes);
        Assert.Null(journal.Active);
        Assert.Equal(ConnectionState.Disconnected, controller.State);
    }

    [Fact]
    public async Task CancelledOrFailedJournalWriteDoesNotPublishActiveSession()
    {
        foreach (var failure in new Exception[]
                 {
                     new OperationCanceledException(),
                     new IOException("write failed")
                 })
        {
            var journal = new Journal { WriteException = failure };
            var controller = Controller(new Network(), journal);

            await Assert.ThrowsAnyAsync<Exception>(
                () => controller.BeginSafeConnectAsync(OwnerA, CancellationToken.None));

            Assert.Null(journal.Active);
            Assert.Equal(ConnectionState.Disconnected, controller.State);
            Assert.Equal(1, journal.WriteAttempts);
        }
    }

    [Fact]
    public async Task ConcurrentCreationHasOneWinnerAndCannotReplaceOwner()
    {
        var journal = new Journal();
        var controller = Controller(new Network(), journal);
        var attempts = new[]
        {
            Capture(() => controller.BeginSafeConnectAsync(OwnerA, CancellationToken.None)),
            Capture(() => controller.BeginSafeConnectAsync(OwnerB, CancellationToken.None))
        };

        var outcomes = await Task.WhenAll(attempts);

        var winner = Assert.Single(outcomes, outcome => outcome.Transaction is not null).Transaction!;
        Assert.Single(outcomes, outcome => outcome.Exception is InvalidOperationException);
        Assert.Single(journal.Writes);
        Assert.Equal(winner.OwnerSid, journal.Active!.OwnerSid);
        Assert.Contains(winner.OwnerSid, new[] { OwnerA, OwnerB });
    }

    [Fact]
    public async Task RollbackStateRewritesPreserveOwnerExactly()
    {
        var journal = new Journal();
        var engine = new Engine();
        var controller = Controller(new Network(), journal, engine);
        var created = await controller.BeginSafeConnectAsync(OwnerA, CancellationToken.None);

        await controller.RollbackAsync("controlled test", CancellationToken.None);

        Assert.Equal(3, journal.Writes.Count);
        Assert.All(journal.Writes, transaction =>
        {
            Assert.Equal(created.SessionId, transaction.SessionId);
            Assert.Equal(OwnerA, transaction.OwnerSid);
        });
        Assert.Equal(new[]
        {
            ConnectionState.SnapshotCommitted,
            ConnectionState.RollingBack,
            ConnectionState.Disconnected
        }, journal.Writes.Select(transaction => transaction.State));
        Assert.Equal(1, engine.Stops);
    }

    [Fact]
    public async Task JournalRoundTripPreservesOwnerAndExplicitNull()
    {
        var root = TempDirectory();
        try
        {
            var journal = new FileTransactionJournal(Path.Combine(root, "active.lrj"));
            var owned = Transaction(OwnerA);
            await journal.WriteAsync(owned, CancellationToken.None);
            Assert.Equal(OwnerA, (await journal.ReadActiveAsync(CancellationToken.None))!.OwnerSid);

            var unknown = Transaction(ownerSid: null);
            await journal.WriteAsync(unknown, CancellationToken.None);
            Assert.Null((await journal.ReadActiveAsync(CancellationToken.None))!.OwnerSid);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LegacyMissingOwnerRemainsUnknownAndCannotBeAdopted()
    {
        var root = TempDirectory();
        try
        {
            var path = Path.Combine(root, "active.lrj");
            var transaction = Transaction(ownerSid: null);
            var payload = JsonSerializer.Serialize(transaction, CamelCase)
                .Replace(",\"ownerSid\":null", string.Empty, StringComparison.Ordinal);
            var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(
                new { payload, sha256 = checksum }, CamelCase));

            var fileJournal = new FileTransactionJournal(path);
            var legacy = await fileJournal.ReadActiveAsync(CancellationToken.None);
            Assert.Null(legacy!.OwnerSid);

            var journal = new Journal { Active = legacy };
            var network = new Network();
            var controller = Controller(network, journal);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => controller.BeginSafeConnectAsync(OwnerB, CancellationToken.None));
            Assert.Null(journal.Active!.OwnerSid);
            Assert.Empty(journal.Writes);
            Assert.Equal(0, network.Captures);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MalformedPersistedOwnerFailsClosedBeforeCleanup()
    {
        var legacy = Transaction("not-a-sid") with { State = ConnectionState.RollbackRequired };
        var journal = new Journal { Active = legacy };
        var engine = new Engine();
        var controller = Controller(new Network(), journal, engine);

        await Assert.ThrowsAsync<ArgumentException>(
            () => controller.RollbackAsync("controlled test", CancellationToken.None));

        Assert.Same(legacy, journal.Active);
        Assert.Empty(journal.Writes);
        Assert.Equal(0, engine.Stops);
    }

    [Fact]
    public async Task ExactOwnerSidAuthorizesStatusAndRollback()
    {
        var network = new Network();
        var journal = new Journal();
        var engine = new Engine();
        var controller = Controller(network, journal, engine);
        var caller = Caller(OwnerA);

        var created = await controller.BeginSafeConnectAsync(OwnerA, CancellationToken.None);

        var statusAuth = await controller.GetStatusAuthorizedAsync(caller, CancellationToken.None);
        Assert.Equal(SessionAuthorizationDecision.OwnerAuthorized, statusAuth.Decision);
        Assert.Equal(ConnectionState.SnapshotCommitted, statusAuth.State);
        Assert.Equal(created.SessionId, statusAuth.SessionId);

        var rollbackAuth = await controller.RollbackAuthorizedAsync(caller, "user requested", CancellationToken.None);
        Assert.Equal(SessionAuthorizationDecision.OwnerAuthorized, rollbackAuth.Decision);
        Assert.Equal(ConnectionState.Disconnected, controller.State);
        Assert.Equal(1, engine.Stops);
        Assert.Equal(1, journal.Clears);
    }

    [Fact]
    public async Task SeparateCallerObjectWithSameSidIsSameOwner()
    {
        var network = new Network();
        var journal = new Journal();
        var engine = new Engine();
        var controller = Controller(network, journal, engine);

        var created = await controller.BeginSafeConnectAsync(OwnerA, CancellationToken.None);

        var caller1 = new ControlCallerIdentity(OwnerA, new[] { "S-1-5-32-545" }, true, false, false, false);
        var caller2 = new ControlCallerIdentity(OwnerA, new[] { "S-1-5-11" }, true, false, false, false);

        var status1 = await controller.GetStatusAuthorizedAsync(caller1, CancellationToken.None);
        var status2 = await controller.GetStatusAuthorizedAsync(caller2, CancellationToken.None);
        Assert.Equal(SessionAuthorizationDecision.OwnerAuthorized, status1.Decision);
        Assert.Equal(SessionAuthorizationDecision.OwnerAuthorized, status2.Decision);

        var rollback = await controller.RollbackAuthorizedAsync(caller2, "user requested", CancellationToken.None);
        Assert.Equal(SessionAuthorizationDecision.OwnerAuthorized, rollback.Decision);
        Assert.Equal(ConnectionState.Disconnected, controller.State);
        Assert.Equal(1, engine.Stops);
    }

    [Fact]
    public async Task AdminMatchingOwnerIsClassifiedAsOwnerAuthorizedNotOverride()
    {
        var network = new Network();
        var journal = new Journal();
        var engine = new Engine();
        var controller = Controller(network, journal, engine);
        var adminOwnerCaller = Caller(OwnerA, administrator: true);

        await controller.BeginSafeConnectAsync(OwnerA, CancellationToken.None);

        var statusAuth = await controller.GetStatusAuthorizedAsync(adminOwnerCaller, CancellationToken.None);
        Assert.Equal(SessionAuthorizationDecision.OwnerAuthorized, statusAuth.Decision);

        var rollbackAuth = await controller.RollbackAuthorizedAsync(adminOwnerCaller, "admin owner disconnect", CancellationToken.None);
        Assert.Equal(SessionAuthorizationDecision.OwnerAuthorized, rollbackAuth.Decision);
    }

    [Fact]
    public async Task StatusAndDisconnectOnNoJournalReturnNoActiveSessionDisconnected()
    {
        var network = new Network();
        var journal = new Journal();
        var engine = new Engine();
        var controller = Controller(network, journal, engine);
        var caller = Caller(OwnerA);

        var statusAuth = await controller.GetStatusAuthorizedAsync(caller, CancellationToken.None);
        Assert.Equal(SessionAuthorizationDecision.NoActiveSession, statusAuth.Decision);
        Assert.Equal(ConnectionState.Disconnected, statusAuth.State);
        Assert.Null(statusAuth.SessionId);

        var rollbackAuth = await controller.RollbackAuthorizedAsync(caller, "disconnect empty", CancellationToken.None);
        Assert.Equal(SessionAuthorizationDecision.NoActiveSession, rollbackAuth.Decision);
        Assert.Equal(ConnectionState.Disconnected, rollbackAuth.State);
        Assert.Empty(journal.Writes);
        Assert.Equal(0, engine.Stops);
        Assert.Equal(0, journal.Clears);
    }

    [Fact]
    public async Task JournalOwnerAuthorizesEvenWhenActiveIsNullAfterSimulatedRestart()
    {
        var existingTx = Transaction(OwnerA) with { State = ConnectionState.Connected };
        var journal = new Journal { Active = existingTx };
        var engine = new Engine();
        var controller = Controller(new Network(), journal, engine);
        var ownerCaller = Caller(OwnerA);

        var statusAuth = await controller.GetStatusAuthorizedAsync(ownerCaller, CancellationToken.None);
        Assert.Equal(SessionAuthorizationDecision.OwnerAuthorized, statusAuth.Decision);
        Assert.Equal(ConnectionState.Connected, statusAuth.State);
        Assert.Equal(existingTx.SessionId, statusAuth.SessionId);

        var rollbackAuth = await controller.RollbackAuthorizedAsync(ownerCaller, "restart cleanup", CancellationToken.None);
        Assert.Equal(SessionAuthorizationDecision.OwnerAuthorized, rollbackAuth.Decision);
        Assert.Equal(1, engine.Stops);
        Assert.Equal(1, journal.Clears);
        Assert.Null(journal.Active);
    }

    [Fact]
    public async Task AuthorizedRestartDisconnectRollsBackExactTransactionFromSingleJournalRead()
    {
        var snapshotA = Snapshot() with { MachineName = "authorized-a" };
        var snapshotB = Snapshot() with { MachineName = "unauthorized-b" };
        var transactionA = Transaction(OwnerA) with { Snapshot = snapshotA };
        var transactionB = Transaction(OwnerB) with { Snapshot = snapshotB };
        var journal = new SequencedJournal(transactionA, transactionB);
        var network = new Network();
        var engine = new Engine();
        var controller = new ConnectionController(network, journal, engine);

        var result = await controller.RollbackAuthorizedAsync(
            Caller(OwnerA), "authorized restart cleanup", CancellationToken.None);

        Assert.Equal(SessionAuthorizationDecision.OwnerAuthorized, result.Decision);
        Assert.Equal(1, journal.Reads);
        Assert.Equal(1, engine.Stops);
        Assert.Same(snapshotA, Assert.Single(network.VerifiedSnapshots));
        Assert.Equal(
            new[] { ConnectionState.RollingBack, ConnectionState.Disconnected },
            journal.Writes.Select(write => write.State));
        Assert.All(journal.Writes, write => Assert.Equal(transactionA.SessionId, write.SessionId));
        Assert.Equal(transactionA.SessionId, Assert.Single(journal.ClearedSessionIds));
        Assert.DoesNotContain(journal.Writes, write => write.SessionId == transactionB.SessionId);
    }

    [Fact]
    public async Task ForeignCallerIsDeniedStatusAndDisconnectWithZeroSideEffects()
    {
        var network = new Network();
        var journal = new Journal();
        var engine = new Engine();
        var controller = Controller(network, journal, engine);
        var created = await controller.BeginSafeConnectAsync(OwnerA, CancellationToken.None);
        var foreignCaller = Caller(OwnerB, administrator: false);

        var statusAuth = await controller.GetStatusAuthorizedAsync(foreignCaller, CancellationToken.None);
        Assert.Equal(SessionAuthorizationDecision.ForeignOwnerDenied, statusAuth.Decision);
        Assert.Equal(ConnectionState.SnapshotCommitted, statusAuth.State);

        var rollbackAuth = await controller.RollbackAuthorizedAsync(foreignCaller, "foreign attempt", CancellationToken.None);
        Assert.Equal(SessionAuthorizationDecision.ForeignOwnerDenied, rollbackAuth.Decision);

        Assert.Single(journal.Writes);
        Assert.Same(created, journal.Active);
        Assert.Equal(0, engine.Stops);
        Assert.Equal(0, journal.Clears);
        Assert.Equal(ConnectionState.SnapshotCommitted, controller.State);
    }

    [Fact]
    public async Task LegacyOwnerDeniedForOrdinaryCallerAndOverrideForAdmin()
    {
        var legacyTx = Transaction(ownerSid: null) with { State = ConnectionState.SnapshotCommitted };
        var journal = new Journal { Active = legacyTx };
        var engine = new Engine();
        var controller = Controller(new Network(), journal, engine);

        var ordinaryCaller = Caller(OwnerA, administrator: false);
        var adminCaller = Caller(OwnerB, administrator: true);

        var ordinaryStatus = await controller.GetStatusAuthorizedAsync(ordinaryCaller, CancellationToken.None);
        Assert.Equal(SessionAuthorizationDecision.LegacyOwnerDenied, ordinaryStatus.Decision);

        var ordinaryRollback = await controller.RollbackAuthorizedAsync(ordinaryCaller, "ordinary attempt", CancellationToken.None);
        Assert.Equal(SessionAuthorizationDecision.LegacyOwnerDenied, ordinaryRollback.Decision);
        Assert.Empty(journal.Writes);
        Assert.Equal(0, engine.Stops);
        Assert.Equal(0, journal.Clears);

        var adminStatus = await controller.GetStatusAuthorizedAsync(adminCaller, CancellationToken.None);
        Assert.Equal(SessionAuthorizationDecision.OperationalOverrideAuthorized, adminStatus.Decision);
        Assert.Equal(ConnectionState.SnapshotCommitted, adminStatus.State);

        var adminRollback = await controller.RollbackAuthorizedAsync(adminCaller, "admin cleanup", CancellationToken.None);
        Assert.Equal(SessionAuthorizationDecision.OperationalOverrideAuthorized, adminRollback.Decision);
        Assert.Equal(1, engine.Stops);
        Assert.Equal(1, journal.Clears);

        Assert.Equal(2, journal.Writes.Count);
        Assert.All(journal.Writes, write => Assert.Null(write.OwnerSid));
    }

    [Fact]
    public async Task InvalidOwnerDeniedForOrdinaryCallerAndOverrideForAdminPreservingInvalidSidText()
    {
        const string invalidOwnerSid = "not-a-valid-sid";
        var invalidTx = Transaction(invalidOwnerSid) with { State = ConnectionState.Connected };
        var journal = new Journal { Active = invalidTx };
        var engine = new Engine();
        var controller = Controller(new Network(), journal, engine);

        var ordinaryCaller = Caller(OwnerA, administrator: false);
        var adminCaller = Caller(OwnerB, administrator: true);

        var ordinaryStatus = await controller.GetStatusAuthorizedAsync(ordinaryCaller, CancellationToken.None);
        Assert.Equal(SessionAuthorizationDecision.InvalidOwnerDenied, ordinaryStatus.Decision);

        var ordinaryRollback = await controller.RollbackAuthorizedAsync(ordinaryCaller, "ordinary invalid attempt", CancellationToken.None);
        Assert.Equal(SessionAuthorizationDecision.InvalidOwnerDenied, ordinaryRollback.Decision);
        Assert.Empty(journal.Writes);
        Assert.Equal(0, engine.Stops);

        var adminRollback = await controller.RollbackAuthorizedAsync(adminCaller, "admin cleanup invalid", CancellationToken.None);
        Assert.Equal(SessionAuthorizationDecision.OperationalOverrideAuthorized, adminRollback.Decision);
        Assert.Equal(1, engine.Stops);
        Assert.Equal(1, journal.Clears);

        Assert.Equal(2, journal.Writes.Count);
        Assert.All(journal.Writes, write => Assert.Equal(invalidOwnerSid, write.OwnerSid));
    }

    [Fact]
    public async Task LocalSystemOverrideCanInspectAndCleanupForeignSession()
    {
        var network = new Network();
        var journal = new Journal();
        var engine = new Engine();
        var controller = Controller(network, journal, engine);
        await controller.BeginSafeConnectAsync(OwnerA, CancellationToken.None);

        var systemCaller = Caller("S-1-5-18", localSystem: true);

        var status = await controller.GetStatusAuthorizedAsync(systemCaller, CancellationToken.None);
        Assert.Equal(SessionAuthorizationDecision.OperationalOverrideAuthorized, status.Decision);

        var rollback = await controller.RollbackAuthorizedAsync(systemCaller, "system override", CancellationToken.None);
        Assert.Equal(SessionAuthorizationDecision.OperationalOverrideAuthorized, rollback.Decision);
        Assert.Equal(1, engine.Stops);
        Assert.Equal(1, journal.Clears);
    }

    [Fact]
    public async Task ActiveAndJournalDisagreementFailsClosed()
    {
        // Case A: Active present but journal is null
        var networkA = new Network();
        var journalA = new Journal();
        var controllerA = Controller(networkA, journalA);
        await controllerA.BeginSafeConnectAsync(OwnerA, CancellationToken.None);
        journalA.Active = null;

        var statusA = await controllerA.GetStatusAuthorizedAsync(Caller(OwnerA), CancellationToken.None);
        Assert.Equal(SessionAuthorizationDecision.InconsistentStateDenied, statusA.Decision);

        var rollbackA = await controllerA.RollbackAuthorizedAsync(Caller(OwnerA), "test", CancellationToken.None);
        Assert.Equal(SessionAuthorizationDecision.InconsistentStateDenied, rollbackA.Decision);

        // Case B: SessionId mismatch
        var networkB = new Network();
        var journalB = new Journal();
        var controllerB = Controller(networkB, journalB);
        await controllerB.BeginSafeConnectAsync(OwnerA, CancellationToken.None);
        journalB.Active = Transaction(OwnerA);

        var statusB = await controllerB.GetStatusAuthorizedAsync(Caller(OwnerA), CancellationToken.None);
        Assert.Equal(SessionAuthorizationDecision.InconsistentStateDenied, statusB.Decision);

        var rollbackB = await controllerB.RollbackAuthorizedAsync(Caller(OwnerA), "test", CancellationToken.None);
        Assert.Equal(SessionAuthorizationDecision.InconsistentStateDenied, rollbackB.Decision);

        // Case C: OwnerSid mismatch
        var networkC = new Network();
        var journalC = new Journal();
        var controllerC = Controller(networkC, journalC);
        var txC = await controllerC.BeginSafeConnectAsync(OwnerA, CancellationToken.None);
        journalC.Active = txC with { OwnerSid = OwnerB };

        var statusC = await controllerC.GetStatusAuthorizedAsync(Caller(OwnerA), CancellationToken.None);
        Assert.Equal(SessionAuthorizationDecision.InconsistentStateDenied, statusC.Decision);

        var rollbackC = await controllerC.RollbackAuthorizedAsync(Caller(OwnerA), "test", CancellationToken.None);
        Assert.Equal(SessionAuthorizationDecision.InconsistentStateDenied, rollbackC.Decision);
    }

    [Fact]
    public async Task AtomicDisconnectHoldsLockAndSecondDisconnectWaitsForCleanCompletion()
    {
        var network = new Network();
        var journal = new Journal();
        var engine = new Engine();
        var controller = Controller(network, journal, engine);
        await controller.BeginSafeConnectAsync(OwnerA, CancellationToken.None);

        var caller = Caller(OwnerA);
        var rollback1Task = controller.RollbackAuthorizedAsync(caller, "first", CancellationToken.None);
        var rollback2Task = controller.RollbackAuthorizedAsync(caller, "second", CancellationToken.None);

        var results = await Task.WhenAll(rollback1Task, rollback2Task);

        Assert.Contains(results, r => r.Decision == SessionAuthorizationDecision.OwnerAuthorized);
        Assert.Contains(results, r => r.Decision == SessionAuthorizationDecision.NoActiveSession);
        Assert.Equal(1, engine.Stops);
        Assert.Equal(1, journal.Clears);
        Assert.Equal(ConnectionState.Disconnected, controller.State);
    }

    [Fact]
    public async Task ConcurrentConnectAndForeignDisconnectLeavesWinningSessionIntact()
    {
        var journal = new Journal();
        var network = new Network();
        var engine = new Engine();
        var controller = Controller(network, journal, engine);

        var connectTask = controller.BeginSafeConnectAsync(OwnerA, CancellationToken.None);
        var foreignDisconnectTask = controller.RollbackAuthorizedAsync(Caller(OwnerB), "attack disconnect", CancellationToken.None);

        await Task.WhenAll(connectTask, foreignDisconnectTask);

        var connectTx = await connectTask;
        var disconnectResult = await foreignDisconnectTask;

        Assert.NotNull(connectTx);
        Assert.Equal(OwnerA, connectTx.OwnerSid);
        Assert.True(
            disconnectResult.Decision == SessionAuthorizationDecision.NoActiveSession ||
            disconnectResult.Decision == SessionAuthorizationDecision.ForeignOwnerDenied);

        if (disconnectResult.Decision == SessionAuthorizationDecision.ForeignOwnerDenied)
        {
            Assert.Same(connectTx, journal.Active);
            Assert.Equal(ConnectionState.SnapshotCommitted, controller.State);
            Assert.Equal(0, engine.Stops);
        }
    }

    [Fact]
    public void OwnershipSurfaceRemainsInternalAndIsolated()
    {
        Assert.DoesNotContain(
            typeof(NetworkTransaction).GetProperties(),
            property => property.Name.Contains("ChangeOwner", StringComparison.Ordinal));

        var root = FindRepositoryRoot();
        var protocol = string.Join('\n', Directory.GetFiles(
            Path.Combine(root, "src", "LibertyRoute.ControlProtocol"), "*.cs").Select(File.ReadAllText));
        var desktop = string.Join('\n', Directory.GetFiles(
            Path.Combine(root, "src", "LibertyRoute.Desktop"), "*.cs").Select(File.ReadAllText));
        var ledger = File.ReadAllText(Path.Combine(root, "src", "LibertyRoute.Restoration", "OwnershipLedgerModel.cs"));
        var serviceProject = File.ReadAllText(Path.Combine(root, "src", "LibertyRoute.Service", "LibertyRoute.Service.csproj"));
        var commands = string.Join(',', Enum.GetNames<ControlCommand>());
        var serviceSources = string.Join('\n', Directory.GetFiles(
            Path.Combine(root, "src", "LibertyRoute.Service"), "*.cs").Select(File.ReadAllText));

        Assert.DoesNotContain("OwnerSid", protocol, StringComparison.Ordinal);
        Assert.DoesNotContain("OwnerSid", desktop, StringComparison.Ordinal);
        Assert.DoesNotContain("OwnerSid", ledger, StringComparison.Ordinal);
        Assert.Contains("Restoration.Windows", serviceProject, StringComparison.Ordinal);
        Assert.DoesNotContain("Recovery", commands, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Restoration", commands, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(4, Enum.GetValues<ControlCommand>().Length);
        foreach (var forbidden in new[]
                 {
                     "ControlledRecoveryApproval", "ControlledRecoveryRestorationWorkflow",
                     "ControlledRestorationActivationGrant", "RestorationExecutionCapability",
                     "RouteMutationProviderFactory", "WindowsRouteMutationNative"
                 })
            Assert.DoesNotContain(forbidden, serviceSources, StringComparison.Ordinal);
    }

    private static ControlCallerIdentity Caller(
        string sid,
        bool authenticated = true,
        bool administrator = false,
        bool network = false,
        bool localSystem = false)
        => new(sid, Array.Empty<string>(), authenticated, administrator, network, localSystem);

    private static async Task<(NetworkTransaction? Transaction, Exception? Exception)> Capture(
        Func<Task<NetworkTransaction>> action)
    {
        try { return (await action(), null); }
        catch (Exception exception) { return (null, exception); }
    }

    private static ConnectionController Controller(Network network, ITransactionJournal journal, Engine? engine = null)
        => new(network, journal, engine ?? new Engine());

    private static NetworkTransaction Transaction(string? ownerSid)
        => new(Guid.NewGuid(), ConnectionState.SnapshotCommitted, DateTimeOffset.UtcNow,
            Snapshot(), Array.Empty<OwnedNetworkChange>(), "test", null, ownerSid);

    private static NetworkStateSnapshot Snapshot()
        => new(DateTimeOffset.UtcNow, "machine", Array.Empty<AdapterState>());

    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"LibertyRoute.SessionOwnership.{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "LibertyRoute.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private sealed class Network : INetworkStateManager
    {
        public int Captures { get; private set; }
        public List<NetworkStateSnapshot> VerifiedSnapshots { get; } = new();
        public Exception? CaptureException { get; init; }
        public Func<CancellationToken, Task>? BeforeCapture { get; init; }
        public Action? AfterCapture { get; init; }
        public List<CancellationToken> VerifyTokens { get; } = new();
        public async Task<NetworkStateSnapshot> CaptureStateAsync(CancellationToken cancellationToken)
        {
            Captures++;
            cancellationToken.ThrowIfCancellationRequested();
            if (BeforeCapture is not null) await BeforeCapture(cancellationToken);
            if (CaptureException is not null) throw CaptureException;
            AfterCapture?.Invoke();
            return Snapshot();
        }
        public Task VerifyRestorationAsync(NetworkStateSnapshot original, CancellationToken cancellationToken)
        {
            VerifiedSnapshots.Add(original);
            VerifyTokens.Add(cancellationToken);
            return Task.CompletedTask;
        }
    }

    private sealed class SequencedJournal(params NetworkTransaction?[] reads) : ITransactionJournal
    {
        private readonly Queue<NetworkTransaction?> _reads = new(reads);
        public int Reads { get; private set; }
        public List<NetworkTransaction> Writes { get; } = new();
        public List<CancellationToken> WriteTokens { get; } = new();
        public List<Guid> ClearedSessionIds { get; } = new();
        public string JournalPath => "sequenced-test";

        public Task<NetworkTransaction?> ReadActiveAsync(CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult(_reads.Count > 0 ? _reads.Dequeue() : null);
        }

        public Task WriteAsync(NetworkTransaction transaction, CancellationToken cancellationToken)
        {
            Writes.Add(transaction);
            return Task.CompletedTask;
        }

        public Task ClearAsync(Guid expectedSessionId, CancellationToken cancellationToken)
        {
            ClearedSessionIds.Add(expectedSessionId);
            return Task.CompletedTask;
        }
    }

    private sealed class Journal : ITransactionJournal
    {
        public NetworkTransaction? Active { get; set; }
        public List<NetworkTransaction> Writes { get; } = new();
        public List<CancellationToken> WriteTokens { get; } = new();
        public int WriteAttempts { get; private set; }
        public int Clears { get; private set; }
        public Exception? WriteException { get; init; }
        public Func<NetworkTransaction, Task>? OnWrite { get; init; }
        public string JournalPath => "controlled-test";
        public async Task WriteAsync(NetworkTransaction transaction, CancellationToken cancellationToken)
        {
            WriteAttempts++;
            WriteTokens.Add(cancellationToken);
            if (WriteException is not null) throw WriteException;
            if (OnWrite is not null) await OnWrite(transaction);
            Writes.Add(transaction);
            Active = transaction;
        }
        public Task<NetworkTransaction?> ReadActiveAsync(CancellationToken cancellationToken)
            => Task.FromResult(Active);
        public Task ClearAsync(Guid expectedSessionId, CancellationToken cancellationToken)
        {
            Clears++;
            Assert.Equal(expectedSessionId, Active?.SessionId);
            Active = null;
            return Task.CompletedTask;
        }
    }

    private sealed class Engine : IConnectionEngine
    {
        public string Id => "controlled-test";
        public int Stops { get; private set; }
        public Exception? StopException { get; init; }
        public List<CancellationToken> StopTokens { get; } = new();
        public Task StartAsync(VpnServerConfig server, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Live engine start is forbidden.");
        public Task StopAsync(CancellationToken cancellationToken)
        {
            Stops++;
            StopTokens.Add(cancellationToken);
            if (StopException is not null) throw StopException;
            return Task.CompletedTask;
        }
        public Task<bool> IsHealthyAsync(CancellationToken cancellationToken) => Task.FromResult(false);
    }
}
