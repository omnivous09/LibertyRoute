using LibertyRoute.Service;
using Microsoft.Extensions.Logging.Abstractions;

namespace LibertyRoute.Restoration.Tests;

public sealed class ControlRuntimeHardeningTests
{
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

    private sealed class Harness
    {
        private readonly object _sync = new();
        private readonly int _acceptImmediatelyThrough;
        private readonly Dictionary<int, FakeConnection> _connections = new();
        private readonly Dictionary<int, TaskCompletionSource> _releases = new();
        private readonly Dictionary<int, int> _handleCounts = new();
        private readonly Dictionary<int, TaskCompletionSource> _acceptedSignals = new();
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

        internal Harness(int acceptImmediatelyThrough)
        {
            _acceptImmediatelyThrough = acceptImmediatelyThrough;
            _idle.TrySetResult();
        }

        internal int CreatedCount { get { lock (_sync) return _created; } }
        internal int AcceptedCount { get { lock (_sync) return _accepted; } }
        internal int HandledCount { get { lock (_sync) return _handled; } }
        internal int MaximumObservedActiveCount { get; private set; }
        internal IReadOnlyList<FakeConnection> AcceptedConnections
        {
            get { lock (_sync) return _connections.Values.Where(item => item.Accepted).ToArray(); }
        }

        internal BoundedControlPipeServer Server(
            Func<FakeConnection, CancellationToken, Task> handler)
            => new(
                Create,
                (connection, token) => handler((FakeConnection)connection, token),
                NullLogger.Instance,
                ActiveCountChanged);

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
                return ValueTask.CompletedTask;
            }
        }
    }
}
