using System.IO.Pipes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LibertyRoute.Service;

internal interface IControlPipeConnection : IAsyncDisposable
{
    Task WaitForConnectionAsync(CancellationToken cancellationToken);
}

internal sealed class BoundedControlPipeServer
{
    internal const int ActiveClientLimit = SecureControlPipeFactory.MaximumActiveClients;

    private readonly Func<bool, IControlPipeConnection> _createConnection;
    private readonly Func<IControlPipeConnection, CancellationToken, Task> _handleConnection;
    private readonly ILogger _logger;
    private readonly Action<int>? _activeTaskCountChanged;
    private readonly SemaphoreSlim _admission = new(ActiveClientLimit, ActiveClientLimit);
    private readonly object _tasksSync = new();
    private readonly HashSet<Task> _activeTasks = new();
    private bool _firstInstance = true;

    internal BoundedControlPipeServer(
        string pipeName,
        SecureControlPipeFactory pipeFactory,
        SecureControlConnectionHandler handler,
        ILogger? logger = null)
        : this(
            firstInstance => new NamedPipeConnection(pipeFactory.Create(pipeName, firstInstance)),
            (connection, cancellationToken) => handler.HandleAsync(
                ((NamedPipeConnection)connection).Pipe,
                cancellationToken),
            logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentNullException.ThrowIfNull(pipeFactory);
        ArgumentNullException.ThrowIfNull(handler);
    }

    internal BoundedControlPipeServer(
        Func<bool, IControlPipeConnection> createConnection,
        Func<IControlPipeConnection, CancellationToken, Task> handleConnection,
        ILogger? logger = null,
        Action<int>? activeTaskCountChanged = null)
    {
        _createConnection = createConnection ?? throw new ArgumentNullException(nameof(createConnection));
        _handleConnection = handleConnection ?? throw new ArgumentNullException(nameof(handleConnection));
        _logger = logger ?? NullLogger.Instance;
        _activeTaskCountChanged = activeTaskCountChanged;
    }

    internal int ActiveTaskCount
    {
        get
        {
            lock (_tasksSync)
                return _activeTasks.Count;
        }
    }

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _admission.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            IControlPipeConnection? connection = null;
            try
            {
                connection = _createConnection(_firstInstance);
                _firstInstance = false;
                await connection.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (connection is not null)
                    await connection.DisposeAsync().ConfigureAwait(false);
                _admission.Release();
                break;
            }
            catch
            {
                if (connection is not null)
                    await connection.DisposeAsync().ConfigureAwait(false);
                _admission.Release();
                throw;
            }

            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task? clientTask = null;
            clientTask = TrackClientAsync(connection, cancellationToken, start.Task, () => clientTask!);
            int activeCount;
            lock (_tasksSync)
            {
                _activeTasks.Add(clientTask);
                activeCount = _activeTasks.Count;
                _activeTaskCountChanged?.Invoke(activeCount);
            }
            start.SetResult();
        }
    }

    private async Task TrackClientAsync(
        IControlPipeConnection connection,
        CancellationToken cancellationToken,
        Task start,
        Func<Task> self)
    {
        try
        {
            await start.ConfigureAwait(false);
            await using (connection.ConfigureAwait(false))
                await _handleConnection(connection, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Secure control client handling failed.");
        }
        finally
        {
            int activeCount;
            lock (_tasksSync)
            {
                _activeTasks.Remove(self());
                activeCount = _activeTasks.Count;
                _activeTaskCountChanged?.Invoke(activeCount);
            }
            _admission.Release();
        }
    }

    private sealed class NamedPipeConnection(NamedPipeServerStream pipe) : IControlPipeConnection
    {
        internal NamedPipeServerStream Pipe { get; } = pipe;

        public Task WaitForConnectionAsync(CancellationToken cancellationToken)
            => Pipe.WaitForConnectionAsync(cancellationToken);

        public ValueTask DisposeAsync() => Pipe.DisposeAsync();
    }
}
