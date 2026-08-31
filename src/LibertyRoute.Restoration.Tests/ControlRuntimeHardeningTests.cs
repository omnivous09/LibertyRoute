using LibertyRoute.Core;
using LibertyRoute.Engine;
using LibertyRoute.Networking;
using LibertyRoute.Recovery;
using LibertyRoute.Service;
using LibertyRoute.Restoration.Windows;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LibertyRoute.Restoration.Tests;

public sealed class ControlRuntimeHardeningTests
{
    [Fact]
    public async Task WorkerRollbackStartsOnlyAfterRegisteredCommandDrains()
    {
        var harness = new Harness(acceptImmediatelyThrough: 1);
        var commandEntered = NewSignal();
        var releaseCommand = NewSignal();
        var server = harness.Server(async (_, _) =>
        {
            commandEntered.TrySetResult();
            await releaseCommand.Task;
        });
        var engine = new WorkerEngine();
        var worker = Worker(server, engine);
        await worker.StartAsync(CancellationToken.None);
        await commandEntered.Task;

        var stop = worker.StopAsync(CancellationToken.None);
        await server.ShutdownStarted;
        Assert.False(engine.StopStarted.Task.IsCompleted);
        Assert.False(stop.IsCompleted);

        releaseCommand.TrySetResult();
        await stop;
        Assert.True(engine.StopStarted.Task.IsCompletedSuccessfully);
        Assert.Equal(ControlPipeServerState.Stopped, server.State);
    }

    [Fact]
    public async Task WorkerDrainBudgetExpiryNeverFallsThroughToRollback()
    {
        var harness = new Harness(acceptImmediatelyThrough: 1);
        var commandEntered = NewSignal();
        var releaseCommand = NewSignal();
        var server = harness.Server(async (_, _) =>
        {
            commandEntered.TrySetResult();
            await releaseCommand.Task;
        });
        var engine = new WorkerEngine();
        var worker = Worker(server, engine);
        await worker.StartAsync(CancellationToken.None);
        await commandEntered.Task;
        using var budget = new CancellationTokenSource();

        var stop = worker.StopAsync(budget.Token);
        await server.ShutdownStarted;
        budget.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stop);
        Assert.False(engine.StopStarted.Task.IsCompleted);

        releaseCommand.TrySetResult();
        await server.DrainCompletion;
        Assert.False(engine.StopStarted.Task.IsCompleted);
    }

    [Fact]
    public async Task ShutdownWithNoClientsStopsAndDrains()
    {
        var harness = new Harness(acceptImmediatelyThrough: 0);
        var server = harness.Server((_, _) => Task.CompletedTask);
        using var cancellation = new CancellationTokenSource();
        var run = server.RunAsync(cancellation.Token);

        await harness.WaitForCreatedAsync(1);
        cancellation.Cancel();
        await run;

        Assert.Equal(ControlPipeServerState.Stopped, server.State);
        Assert.True(server.DrainCompletion.IsCompletedSuccessfully);
        Assert.Equal(0, server.ActiveTaskCount);
        Assert.Equal(1, harness.Connection(1).DisposeCount);
    }

    [Fact]
    public async Task ShutdownWinningBeforeRegistrationRejectsAcceptedConnection()
    {
        var harness = new Harness(acceptImmediatelyThrough: 1);
        var registrationReached = NewSignal();
        var resumeRegistration = NewSignal();
        var handled = 0;
        var server = harness.ServerWithAdmission((_, _, _) => { handled++; return Task.CompletedTask; }, async () =>
        {
            registrationReached.TrySetResult();
            await resumeRegistration.Task;
        });
        using var cancellation = new CancellationTokenSource();
        var run = server.RunAsync(cancellation.Token);

        await registrationReached.Task;
        cancellation.Cancel();
        resumeRegistration.TrySetResult();
        await run;

        Assert.Equal(0, handled);
        Assert.Equal(0, server.ActiveTaskCount);
        Assert.Equal(1, harness.Connection(1).DisposeCount);
        Assert.Equal(ControlPipeServerState.Stopped, server.State);
    }

    [Fact]
    public async Task RegisteredClientCannotAdmitCommandAfterShutdownLinearizes()
    {
        var harness = new Harness(acceptImmediatelyThrough: 1);
        var handlerEntered = NewSignal();
        var attemptAdmission = NewSignal();
        var admitted = true;
        var server = harness.ServerWithAdmission(async (_, admission, _) =>
        {
            handlerEntered.TrySetResult();
            await attemptAdmission.Task;
            admitted = admission.TryAdmit();
        });
        using var cancellation = new CancellationTokenSource();
        var run = server.RunAsync(cancellation.Token);

        await handlerEntered.Task;
        cancellation.Cancel();
        attemptAdmission.TrySetResult();
        await run;

        Assert.False(admitted);
        Assert.Equal(1, harness.Connection(1).DisposeCount);
        Assert.Equal(0, server.ActiveTaskCount);
    }

    [Fact]
    public async Task AdmittedReadOnlyCommandObservesTransportShutdownAndDrains()
    {
        var harness = new Harness(acceptImmediatelyThrough: 1);
        var admitted = NewSignal();
        var cancellationObserved = NewSignal();
        var server = harness.ServerWithAdmission(async (_, admission, token) =>
        {
            Assert.True(admission.TryAdmit());
            admitted.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                cancellationObserved.TrySetResult();
                throw;
            }
        });
        using var cancellation = new CancellationTokenSource();
        var run = server.RunAsync(cancellation.Token);
        await admitted.Task;

        cancellation.Cancel();
        await cancellationObserved.Task;
        await run;

        Assert.Equal(0, server.ActiveTaskCount);
        Assert.Equal(ControlPipeServerState.Stopped, server.State);
    }

    [Fact]
    public async Task ShutdownDrainsRegisteredHandlerBeforeRunCompletes()
    {
        var harness = new Harness(acceptImmediatelyThrough: 1);
        var entered = NewSignal();
        var release = NewSignal();
        var server = harness.Server(async (_, _) => { entered.TrySetResult(); await release.Task; });
        using var cancellation = new CancellationTokenSource();
        var run = server.RunAsync(cancellation.Token);

        await entered.Task;
        cancellation.Cancel();
        Assert.False(run.IsCompleted);
        release.TrySetResult();
        await run;

        Assert.Equal(0, server.ActiveTaskCount);
        Assert.Equal(ControlPipeServerState.Stopped, server.State);
    }

    [Fact]
    public async Task ListenerDisposalFailureDuringCancellationFaultsAfterCleanup()
    {
        var harness = new Harness(acceptImmediatelyThrough: 0);
        var server = harness.Server((_, _) => Task.CompletedTask);
        using var cancellation = new CancellationTokenSource();
        var run = server.RunAsync(cancellation.Token);
        await harness.WaitForCreatedAsync(1);
        harness.Connection(1).DisposeException = new IOException("dispose failure");

        cancellation.Cancel();
        var exception = await Assert.ThrowsAsync<AggregateException>(() => run);

        Assert.Contains(exception.InnerExceptions, item => item is OperationCanceledException);
        Assert.Contains(exception.InnerExceptions, item => item is IOException);
        Assert.Equal(1, harness.Connection(1).DisposeCount);
        Assert.Equal(0, server.ActiveTaskCount);
        Assert.Equal(ControlPipeServerState.Faulted, server.State);
    }

    [Fact]
    public async Task RejectedAcceptedClientDisposalCancellationFaultsServer()
    {
        var harness = new Harness(acceptImmediatelyThrough: 1);
        var registrationReached = NewSignal();
        var resumeRegistration = NewSignal();
        var handled = 0;
        var server = harness.ServerWithAdmission((_, _, _) => { handled++; return Task.CompletedTask; }, async () =>
        {
            registrationReached.TrySetResult();
            await resumeRegistration.Task;
        });
        using var cancellation = new CancellationTokenSource();
        var run = server.RunAsync(cancellation.Token);
        await registrationReached.Task;
        harness.Connection(1).DisposeException = new OperationCanceledException("disposal cancellation");

        cancellation.Cancel();
        resumeRegistration.TrySetResult();
        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => run);

        Assert.Equal("disposal cancellation", exception.Message);
        Assert.Equal(0, handled);
        Assert.Equal(1, harness.Connection(1).DisposeCount);
        Assert.Equal(1, harness.AdmissionReleaseCount);
        Assert.Equal(ControlPipeServerState.Faulted, server.State);
        Assert.Equal(1, harness.CreatedCount);
    }

    [Fact]
    public async Task RegisteredClientDisposalCancellationIsLoggedAndCleanupCompletes()
    {
        var harness = new Harness(acceptImmediatelyThrough: 2);
        var logger = new RecordingLogger();
        var firstEntered = NewSignal();
        var secondCompleted = NewSignal();
        var server = harness.ServerWithAdmission(async (connection, _, token) =>
        {
            if (connection.Id == 1)
            {
                connection.DisposeException = new OperationCanceledException("registered disposal cancellation");
                firstEntered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            else
            {
                secondCompleted.TrySetResult();
            }
        }, logger: logger);
        using var cancellation = new CancellationTokenSource();
        var run = server.RunAsync(cancellation.Token);

        await firstEntered.Task;
        await secondCompleted.Task;
        await harness.WaitForCreatedAsync(3);
        cancellation.Cancel();
        await run;

        Assert.Contains(logger.Exceptions, exception =>
            exception is OperationCanceledException { Message: "registered disposal cancellation" });
        var disposalLog = Assert.Single(logger.Entries,
            entry => entry.Exception is OperationCanceledException { Message: "registered disposal cancellation" });
        Assert.Equal(LogLevel.Error, disposalLog.Level);
        Assert.Equal(1, harness.Connection(1).DisposeCount);
        Assert.Equal(harness.CreatedCount, harness.AdmissionReleaseCount);
        Assert.Equal(0, server.ActiveTaskCount);
        Assert.Equal(ControlPipeServerState.Stopped, server.State);
    }

    [Fact]
    public async Task SlowClientDoesNotMonopolizeListener()
    {
        var harness = new Harness(acceptImmediatelyThrough: 2);
        var firstEntered = NewSignal();
        var releaseFirst = NewSignal();
        var secondCompleted = NewSignal();
        var server = harness.Server(async (connection, _) =>
        {
            if (connection.Id == 1)
            {
                firstEntered.TrySetResult();
                await releaseFirst.Task;
            }
            else if (connection.Id == 2)
            {
                secondCompleted.TrySetResult();
            }
        });
        using var cancellation = new CancellationTokenSource();
        var run = server.RunAsync(cancellation.Token);

        await firstEntered.Task;
        await secondCompleted.Task;

        Assert.False(releaseFirst.Task.IsCompleted);
        Assert.Equal(2, harness.AcceptedCount);

        releaseFirst.TrySetResult();
        cancellation.Cancel();
        await run;
        await harness.WaitForIdleAsync();
    }

    [Fact]
    public async Task ActiveClientCountIsBoundedAndReleasedCapacityIsReused()
    {
        var harness = new Harness(acceptImmediatelyThrough: int.MaxValue);
        var server = harness.Server((connection, token) => harness.BlockAsync(connection.Id, token));
        using var cancellation = new CancellationTokenSource();
        var run = server.RunAsync(cancellation.Token);

        await harness.WaitForAcceptedAsync(BoundedControlPipeServer.ActiveClientLimit);
        await harness.WaitForBlockedAsync(BoundedControlPipeServer.ActiveClientLimit);
        Assert.Equal(BoundedControlPipeServer.ActiveClientLimit, server.ActiveTaskCount);
        Assert.Equal(BoundedControlPipeServer.ActiveClientLimit, harness.CreatedCount);
        Assert.Equal(BoundedControlPipeServer.ActiveClientLimit, harness.MaximumObservedActiveCount);

        harness.Release(1);
        await harness.WaitForAcceptedAsync(BoundedControlPipeServer.ActiveClientLimit + 1);
        await harness.WaitForTrackedAsync(BoundedControlPipeServer.ActiveClientLimit + 1);
        await harness.WaitForBlockedAsync(BoundedControlPipeServer.ActiveClientLimit + 1);

        Assert.Equal(BoundedControlPipeServer.ActiveClientLimit, server.ActiveTaskCount);
        Assert.Equal(BoundedControlPipeServer.ActiveClientLimit + 1, harness.CreatedCount);
        Assert.Equal(BoundedControlPipeServer.ActiveClientLimit, harness.MaximumObservedActiveCount);

        cancellation.Cancel();
        harness.ReleaseAll();
        await run;
        await harness.WaitForIdleAsync();
    }

    [Fact]
    public async Task PerClientExceptionDoesNotKillListener()
    {
        var harness = new Harness(acceptImmediatelyThrough: 2);
        var secondCompleted = NewSignal();
        var server = harness.Server((connection, _) =>
        {
            if (connection.Id == 1)
                throw new InvalidOperationException("client failure");
            secondCompleted.TrySetResult();
            return Task.CompletedTask;
        });
        using var cancellation = new CancellationTokenSource();
        var run = server.RunAsync(cancellation.Token);

        await secondCompleted.Task;
        Assert.False(run.IsCompleted);
        Assert.Equal(2, harness.AcceptedCount);

        cancellation.Cancel();
        await run;
        await harness.WaitForIdleAsync();
        Assert.Equal(1, harness.Connection(1).DisposeCount);
        Assert.Equal(1, harness.Connection(2).DisposeCount);
    }

    [Fact]
    public async Task RoutinePeerIOExceptionIsDebugWithoutExceptionOrStackFormatting()
    {
        var harness = new Harness(acceptImmediatelyThrough: 1);
        var logger = new RecordingLogger();
        var server = harness.ServerWithAdmission((_, _, _) =>
        {
            harness.RecordHandled();
            throw new IOException("attacker-controlled-transport-detail");
        }, logger: logger);
        using var cancellation = new CancellationTokenSource();
        var run = server.RunAsync(cancellation.Token);

        await harness.WaitForHandledAsync(1);
        await harness.WaitForIdleAsync();
        cancellation.Cancel();
        await run;

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Debug, entry.Level);
        Assert.Null(entry.Exception);
        Assert.Contains(nameof(IOException), entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("attacker-controlled-transport-detail", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompletedTasksAreRemovedWithoutAccumulation()
    {
        const int clients = 32;
        var harness = new Harness(acceptImmediatelyThrough: clients);
        var server = harness.Server((_, _) =>
        {
            harness.RecordHandled();
            return Task.CompletedTask;
        });
        using var cancellation = new CancellationTokenSource();
        var run = server.RunAsync(cancellation.Token);

        await harness.WaitForHandledAsync(clients);
        await harness.WaitForIdleAsync();

        Assert.Equal(0, server.ActiveTaskCount);
        Assert.Equal(clients, harness.HandledCount);
        Assert.True(harness.MaximumObservedActiveCount <= BoundedControlPipeServer.ActiveClientLimit);

        cancellation.Cancel();
        await run;
    }

    [Fact]
    public async Task EachAcceptedPipeIsHandledOnceAndDisposedOnce()
    {
        const int clients = 12;
        var harness = new Harness(acceptImmediatelyThrough: clients);
        var server = harness.Server((connection, _) =>
        {
            harness.RecordHandled(connection.Id);
            if (connection.Id % 3 == 0)
                throw new IOException("simulated disconnect");
            return Task.CompletedTask;
        });
        using var cancellation = new CancellationTokenSource();
        var run = server.RunAsync(cancellation.Token);

        await harness.WaitForHandledAsync(clients);
        await harness.WaitForIdleAsync();

        foreach (var connection in harness.AcceptedConnections)
        {
            Assert.Equal(1, harness.HandleCount(connection.Id));
            Assert.Equal(1, connection.DisposeCount);
        }

        cancellation.Cancel();
        await run;
    }

    private static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static LibertyRouteWorker Worker(BoundedControlPipeServer server, WorkerEngine engine)
    {
        var transaction = new NetworkTransaction(
            Guid.NewGuid(), ConnectionState.SnapshotCommitted, DateTimeOffset.UtcNow,
            new NetworkStateSnapshot(DateTimeOffset.UtcNow, "machine", Array.Empty<AdapterState>()),
            Array.Empty<OwnedNetworkChange>(), engine.Id, null, "S-1-5-21-1001");
        var controller = new ConnectionController(new WorkerNetwork(), new WorkerJournal(transaction), engine);
        return new LibertyRouteWorker(
            controller, new NoRecoveryReconciler(), NullLogger<LibertyRouteWorker>.Instance, () => server);
    }

    private sealed class NoRecoveryReconciler : IRecoveryStartupReconciler
    {
        public Task<RecoveryStartupReconciliationResult> ReconcileAsync(CancellationToken cancellationToken)
            => Task.FromResult(new RecoveryStartupReconciliationResult(
                RecoveryStartupReconciliationStatus.NoJournal, null, null, null, false, "test"));
    }

    private sealed class WorkerNetwork : INetworkStateManager
    {
        public Task<NetworkStateSnapshot> CaptureStateAsync(CancellationToken cancellationToken)
            => throw new InvalidOperationException("Capture is not expected.");
        public Task VerifyRestorationAsync(NetworkStateSnapshot original, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class WorkerJournal(NetworkTransaction active) : ITransactionJournal
    {
        private NetworkTransaction? _active = active;
        public string JournalPath => "worker-test";
        public Task<NetworkTransaction?> ReadActiveAsync(CancellationToken cancellationToken)
            => Task.FromResult(_active);
        public Task WriteAsync(NetworkTransaction transaction, CancellationToken cancellationToken)
        {
            _active = transaction;
            return Task.CompletedTask;
        }
        public Task ClearAsync(Guid expectedSessionId, CancellationToken cancellationToken)
        {
            _active = null;
            return Task.CompletedTask;
        }
    }

    private sealed class WorkerEngine : IConnectionEngine
    {
        public string Id => "worker-test";
        public TaskCompletionSource StopStarted { get; } = NewSignal();
        public Task StartAsync(VpnServerConfig server, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Live engine start is forbidden.");
        public Task StopAsync(CancellationToken cancellationToken)
        {
            StopStarted.TrySetResult();
            return Task.CompletedTask;
        }
        public Task<bool> IsHealthyAsync(CancellationToken cancellationToken) => Task.FromResult(false);
    }

    private sealed class Harness
    {
        private readonly object _sync = new();
        private readonly int _acceptImmediatelyThrough;
        private readonly Dictionary<int, FakeConnection> _connections = new();
        private readonly Dictionary<int, TaskCompletionSource> _releases = new();
        private readonly Dictionary<int, int> _handleCounts = new();
        private readonly Dictionary<int, TaskCompletionSource> _acceptedSignals = new();
        private readonly Dictionary<int, TaskCompletionSource> _createdSignals = new();
        private readonly Dictionary<int, TaskCompletionSource> _handledSignals = new();
        private readonly Dictionary<int, TaskCompletionSource> _trackedSignals = new();
        private readonly Dictionary<int, TaskCompletionSource> _blockedSignals = new();
        private TaskCompletionSource _idle = NewSignal();
        private int _created;
        private int _accepted;
        private int _handled;
        private int _active;
        private int _trackedAdmissions;
        private int _blocked;
        private int _admissionReleaseCount;

        internal Harness(int acceptImmediatelyThrough)
        {
            _acceptImmediatelyThrough = acceptImmediatelyThrough;
            _idle.TrySetResult();
        }

        internal int CreatedCount { get { lock (_sync) return _created; } }
        internal int AcceptedCount { get { lock (_sync) return _accepted; } }
        internal int HandledCount { get { lock (_sync) return _handled; } }
        internal int AdmissionReleaseCount { get { lock (_sync) return _admissionReleaseCount; } }
        internal int MaximumObservedActiveCount { get; private set; }
        internal IReadOnlyList<FakeConnection> AcceptedConnections
        {
            get { lock (_sync) return _connections.Values.Where(item => item.Accepted).ToArray(); }
        }

        internal BoundedControlPipeServer Server(
            Func<FakeConnection, CancellationToken, Task> handler)
            => ServerWithAdmission((connection, _, token) => handler(connection, token));

        internal BoundedControlPipeServer ServerWithAdmission(
            Func<FakeConnection, IControlCommandAdmission, CancellationToken, Task> handler,
            Func<Task>? beforeRegistration = null,
            ILogger? logger = null)
            => new(
                Create,
                (connection, admission, token) => handler((FakeConnection)connection, admission, token),
                logger ?? NullLogger.Instance,
                ActiveCountChanged,
                beforeRegistration,
                AdmissionReleased);

        internal FakeConnection Connection(int id)
        {
            lock (_sync) return _connections[id];
        }

        internal int HandleCount(int id)
        {
            lock (_sync) return _handleCounts.GetValueOrDefault(id);
        }

        internal Task BlockAsync(int id, CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                if (!_releases.TryGetValue(id, out var release))
                {
                    _releases.Add(id, release = NewSignal());
                    _blocked++;
                    Signal(_blockedSignals, _blocked);
                }
                return release.Task.WaitAsync(cancellationToken);
            }
        }

        internal void Release(int id)
        {
            lock (_sync)
                if (_releases.TryGetValue(id, out var release))
                    release.TrySetResult();
        }

        internal void ReleaseAll()
        {
            lock (_sync)
                foreach (var release in _releases.Values)
                    release.TrySetResult();
        }

        internal void RecordHandled(int? id = null)
        {
            lock (_sync)
            {
                _handled++;
                if (id.HasValue)
                    _handleCounts[id.Value] = _handleCounts.GetValueOrDefault(id.Value) + 1;
                Signal(_handledSignals, _handled);
            }
        }

        internal Task WaitForAcceptedAsync(int count)
        {
            lock (_sync)
                return _accepted >= count ? Task.CompletedTask : GetSignal(_acceptedSignals, count).Task;
        }

        internal Task WaitForCreatedAsync(int count)
        {
            lock (_sync)
                return _created >= count ? Task.CompletedTask : GetSignal(_createdSignals, count).Task;
        }

        internal Task WaitForHandledAsync(int count)
        {
            lock (_sync)
                return _handled >= count ? Task.CompletedTask : GetSignal(_handledSignals, count).Task;
        }

        internal Task WaitForTrackedAsync(int count)
        {
            lock (_sync)
                return _trackedAdmissions >= count ? Task.CompletedTask : GetSignal(_trackedSignals, count).Task;
        }

        internal Task WaitForBlockedAsync(int count)
        {
            lock (_sync)
                return _blocked >= count ? Task.CompletedTask : GetSignal(_blockedSignals, count).Task;
        }

        internal Task WaitForIdleAsync()
        {
            lock (_sync) return _active == 0 ? Task.CompletedTask : _idle.Task;
        }

        private IControlPipeConnection Create(bool firstInstance)
        {
            lock (_sync)
            {
                var id = ++_created;
                var connection = new FakeConnection(id, firstInstance, this);
                _connections.Add(id, connection);
                Signal(_createdSignals, id);
                return connection;
            }
        }

        private void Accepted(FakeConnection connection)
        {
            lock (_sync)
            {
                connection.Accepted = true;
                _accepted++;
                Signal(_acceptedSignals, _accepted);
            }
        }

        private void ActiveCountChanged(int count)
        {
            lock (_sync)
            {
                if (count > _active)
                {
                    _trackedAdmissions++;
                    Signal(_trackedSignals, _trackedAdmissions);
                }
                _active = count;
                MaximumObservedActiveCount = Math.Max(MaximumObservedActiveCount, count);
                if (count == 0)
                    _idle.TrySetResult();
                else if (_idle.Task.IsCompleted)
                    _idle = NewSignal();
            }
        }

        private void AdmissionReleased()
        {
            lock (_sync) _admissionReleaseCount++;
        }

        private static TaskCompletionSource GetSignal(
            Dictionary<int, TaskCompletionSource> signals,
            int count)
        {
            if (!signals.TryGetValue(count, out var signal))
                signals.Add(count, signal = NewSignal());
            return signal;
        }

        private static void Signal(Dictionary<int, TaskCompletionSource> signals, int count)
        {
            if (signals.TryGetValue(count, out var signal))
                signal.TrySetResult();
        }

        internal sealed class FakeConnection(
            int id,
            bool firstInstance,
            Harness owner) : IControlPipeConnection
        {
            private int _disposeCount;
            internal Exception? DisposeException { get; set; }
            internal int Id { get; } = id;
            internal bool FirstInstance { get; } = firstInstance;
            internal bool Accepted { get; set; }
            internal int DisposeCount => Volatile.Read(ref _disposeCount);

            public async Task WaitForConnectionAsync(CancellationToken cancellationToken)
            {
                if (Id > owner._acceptImmediatelyThrough)
                {
                    var wait = NewSignal();
                    await wait.Task.WaitAsync(cancellationToken);
                }
                owner.Accepted(this);
            }

            public ValueTask DisposeAsync()
            {
                Interlocked.Increment(ref _disposeCount);
                if (DisposeException is not null) throw DisposeException;
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class RecordingLogger : ILogger
    {
        private readonly object _sync = new();
        private readonly List<(LogLevel Level, string Message, Exception? Exception)> _entries = new();
        internal IReadOnlyList<(LogLevel Level, string Message, Exception? Exception)> Entries
        { get { lock (_sync) return _entries.ToArray(); } }
        internal IReadOnlyList<Exception> Exceptions
        { get { lock (_sync) return _entries.Where(entry => entry.Exception is not null).Select(entry => entry.Exception!).ToArray(); } }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_sync) _entries.Add((logLevel, formatter(state, exception), exception));
        }
    }
}
