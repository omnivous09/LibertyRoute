using System.IO.Pipes;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LibertyRoute.Service;

internal interface IControlPipeConnection : IAsyncDisposable
{
    Task WaitForConnectionAsync(CancellationToken cancellationToken);
}

internal interface IControlCommandAdmission
{
    bool TryAdmit();
}

internal enum ControlPipeServerState { Running, StoppingAdmissions, Draining, Stopped, Faulted }

internal sealed class BoundedControlPipeServer
{
    internal const int ActiveClientLimit = SecureControlPipeFactory.MaximumActiveClients;
    private readonly Func<bool, IControlPipeConnection> _createConnection;
    private readonly Func<IControlPipeConnection, IControlCommandAdmission, CancellationToken, Task> _handleConnection;
    private readonly ILogger _logger;
    private readonly Action<int>? _activeTaskCountChanged;
    private readonly Action? _admissionSlotReleased;
    private readonly Func<Task>? _beforeRegistration;
    private readonly SemaphoreSlim _admission = new(ActiveClientLimit, ActiveClientLimit);
    private readonly object _lifecycleSync = new();
    private readonly HashSet<Task> _activeTasks = new();
    private readonly CancellationTokenSource _transportStop = new();
    private readonly TaskCompletionSource _shutdownStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ControlPipeServerState _state = ControlPipeServerState.Running;
    private bool _firstInstance = true;

    internal BoundedControlPipeServer(string pipeName, SecureControlPipeFactory pipeFactory,
        SecureControlConnectionHandler handler, ILogger? logger = null)
        : this(first => new NamedPipeConnection(pipeFactory.Create(pipeName, first)),
            (connection, admission, token) => handler.HandleAsync(
                ((NamedPipeConnection)connection).Pipe, admission, token), logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentNullException.ThrowIfNull(pipeFactory);
        ArgumentNullException.ThrowIfNull(handler);
    }

    internal BoundedControlPipeServer(Func<bool, IControlPipeConnection> createConnection,
        Func<IControlPipeConnection, IControlCommandAdmission, CancellationToken, Task> handleConnection,
        ILogger? logger = null, Action<int>? activeTaskCountChanged = null,
        Func<Task>? beforeRegistration = null, Action? admissionSlotReleased = null)
    {
        _createConnection = createConnection ?? throw new ArgumentNullException(nameof(createConnection));
        _handleConnection = handleConnection ?? throw new ArgumentNullException(nameof(handleConnection));
        _logger = logger ?? NullLogger.Instance;
        _activeTaskCountChanged = activeTaskCountChanged;
        _beforeRegistration = beforeRegistration;
        _admissionSlotReleased = admissionSlotReleased;
    }

    internal int ActiveTaskCount { get { lock (_lifecycleSync) return _activeTasks.Count; } }
    internal ControlPipeServerState State { get { lock (_lifecycleSync) return _state; } }
    internal Task DrainCompletion => _drained.Task;
    internal Task ShutdownStarted => _shutdownStarted.Task;

    internal void BeginShutdown()
    {
        var cancel = false;
        lock (_lifecycleSync)
        {
            if (_state == ControlPipeServerState.Running)
            {
                _state = ControlPipeServerState.StoppingAdmissions;
                cancel = true;
            }
        }
        if (cancel)
        {
            _shutdownStarted.TrySetResult();
            _transportStop.Cancel();
        }
    }

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(BeginShutdown);
        Exception? failure = null;
        try { await AcceptLoopAsync().ConfigureAwait(false); }
        catch (Exception exception)
        {
            failure = exception;
            BeginShutdown();
            lock (_lifecycleSync) _state = ControlPipeServerState.Faulted;
        }

        BeginShutdown();
        Task[] active;
        lock (_lifecycleSync)
        {
            if (_state != ControlPipeServerState.Faulted) _state = ControlPipeServerState.Draining;
            active = _activeTasks.ToArray();
        }
        await Task.WhenAll(active).ConfigureAwait(false);
        lock (_lifecycleSync)
            if (failure is null) _state = ControlPipeServerState.Stopped;
        _drained.TrySetResult();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private async Task AcceptLoopAsync()
    {
        while (State == ControlPipeServerState.Running)
        {
            try { await _admission.WaitAsync(_transportStop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException exception) when (
                exception.CancellationToken == _transportStop.Token && _transportStop.IsCancellationRequested)
            {
                return;
            }
            var transferred = false;
            IControlPipeConnection? connection = null;
            try
            {
                connection = _createConnection(_firstInstance);
                _firstInstance = false;
                try { await connection.WaitForConnectionAsync(_transportStop.Token).ConfigureAwait(false); }
                catch (Exception acceptFailure)
                {
                    var disposalFailure = await TryDisposeAsync(connection).ConfigureAwait(false);
                    connection = null;
                    if (acceptFailure is OperationCanceledException cancellation &&
                        cancellation.CancellationToken == _transportStop.Token &&
                        _transportStop.IsCancellationRequested && disposalFailure is null)
                        return;
                    if (disposalFailure is not null) throw new AggregateException(acceptFailure, disposalFailure);
                    ExceptionDispatchInfo.Capture(acceptFailure).Throw();
                    throw;
                }

                if (_beforeRegistration is not null)
                    await _beforeRegistration().ConfigureAwait(false);

                var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Task? clientTask = null;
                var commandAdmission = new ClientCommandAdmission(this);
                lock (_lifecycleSync)
                {
                    if (_state == ControlPipeServerState.Running)
                    {
                        clientTask = TrackClientAsync(connection, commandAdmission, start.Task, () => clientTask!);
                        _activeTasks.Add(clientTask);
                        transferred = true;
                        _activeTaskCountChanged?.Invoke(_activeTasks.Count);
                    }
                }
                if (!transferred)
                {
                    var disposalFailure = await TryDisposeAsync(connection).ConfigureAwait(false);
                    connection = null;
                    if (disposalFailure is not null) ExceptionDispatchInfo.Capture(disposalFailure).Throw();
                    return;
                }
                connection = null;
                start.SetResult();
            }
            finally
            {
                if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false);
                if (!transferred) ReleaseAdmission();
            }
        }
    }

    private async Task TrackClientAsync(IControlPipeConnection connection,
        IControlCommandAdmission commandAdmission, Task start, Func<Task> self)
    {
        try
        {
            await start.ConfigureAwait(false);
            try
            {
                await _handleConnection(connection, commandAdmission, _transportStop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (
                exception.CancellationToken == _transportStop.Token && _transportStop.IsCancellationRequested)
            {
            }
            catch (IOException exception)
            {
                _logger.LogDebug(
                    "Secure control client transport terminated with fixed exception type {TransportExceptionType}.",
                    exception.GetType().Name);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Secure control client handling failed.");
            }

            try { await connection.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Secure control client disposal failed.");
            }
        }
        finally
        {
            lock (_lifecycleSync)
            {
                _activeTasks.Remove(self());
                _activeTaskCountChanged?.Invoke(_activeTasks.Count);
            }
            ReleaseAdmission();
        }
    }

    private void ReleaseAdmission()
    {
        _admission.Release();
        _admissionSlotReleased?.Invoke();
    }

    private static async Task<Exception?> TryDisposeAsync(IControlPipeConnection connection)
    {
        try { await connection.DisposeAsync().ConfigureAwait(false); return null; }
        catch (Exception exception) { return exception; }
    }

    private bool TryAdmitCommand() { lock (_lifecycleSync) return _state == ControlPipeServerState.Running; }

    private sealed class ClientCommandAdmission(BoundedControlPipeServer owner) : IControlCommandAdmission
    {
        private int _attempted;
        public bool TryAdmit() => Interlocked.Exchange(ref _attempted, 1) == 0 && owner.TryAdmitCommand();
    }

    internal sealed class AlwaysOpenCommandAdmission : IControlCommandAdmission
    {
        internal static readonly AlwaysOpenCommandAdmission Instance = new();
        public bool TryAdmit() => true;
    }

    private sealed class NamedPipeConnection(NamedPipeServerStream pipe) : IControlPipeConnection
    {
        internal NamedPipeServerStream Pipe { get; } = pipe;
        public Task WaitForConnectionAsync(CancellationToken cancellationToken) => Pipe.WaitForConnectionAsync(cancellationToken);
        public ValueTask DisposeAsync() => Pipe.DisposeAsync();
    }
}
