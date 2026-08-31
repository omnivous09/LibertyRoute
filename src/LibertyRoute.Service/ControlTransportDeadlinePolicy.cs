namespace LibertyRoute.Service;

internal enum ControlTransportStage
{
    Greeting,
    Request,
    Response
}

internal sealed class ControlTransportDeadlineException(
    ControlTransportStage stage,
    Exception? innerException = null) : OperationCanceledException(
        $"The {stage} control transport deadline expired.", innerException)
{
    internal ControlTransportStage Stage { get; } = stage;
}

internal sealed class ControlTransportDeadlinePolicy(TimeProvider timeProvider)
{
    internal static readonly TimeSpan GreetingTimeout = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(10);

    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    internal Task ExecuteGreetingAsync(Func<CancellationToken, Task> operation, CancellationToken callerToken)
        => ExecuteAsync(ControlTransportStage.Greeting, GreetingTimeout, operation, callerToken);

    internal Task<T> ExecuteRequestAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken callerToken)
        => ExecuteAsync(ControlTransportStage.Request, RequestTimeout, operation, callerToken);

    internal Task ExecuteResponseAsync(Func<CancellationToken, Task> operation, CancellationToken callerToken)
        => ExecuteAsync(ControlTransportStage.Response, ResponseTimeout, operation, callerToken);

    private async Task ExecuteAsync(
        ControlTransportStage stage,
        TimeSpan timeout,
        Func<CancellationToken, Task> operation,
        CancellationToken callerToken)
    {
        await ExecuteAsync<object?>(async token =>
        {
            await operation(token);
            return null;
        }, stage, timeout, callerToken);
    }

    private async Task<T> ExecuteAsync<T>(
        ControlTransportStage stage,
        TimeSpan timeout,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken callerToken)
        => await ExecuteAsync(operation, stage, timeout, callerToken);

    private async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        ControlTransportStage stage,
        TimeSpan timeout,
        CancellationToken callerToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        callerToken.ThrowIfCancellationRequested();
        using var deadline = new CancellationTokenSource(timeout, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(callerToken, deadline.Token);
        try
        {
            var result = await operation(linked.Token);
            callerToken.ThrowIfCancellationRequested();
            if (deadline.IsCancellationRequested)
                throw new ControlTransportDeadlineException(stage);
            return result;
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(callerToken);
        }
        catch (OperationCanceledException exception) when (
            deadline.IsCancellationRequested)
        {
            throw new ControlTransportDeadlineException(stage, exception);
        }
    }
}
