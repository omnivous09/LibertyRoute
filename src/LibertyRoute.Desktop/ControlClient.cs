using System.IO;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using LibertyRoute.ControlProtocol;

[assembly: InternalsVisibleTo("LibertyRoute.Restoration.Tests")]

namespace LibertyRoute.Desktop;

internal enum ControlClientError
{
    ServiceUnavailable,
    AuthorizationRequired,
    ProtocolError,
    RequestRejected,
    ResponseTooLarge,
    OperationCancelled,
    IndeterminateMutationOutcome,
    Unexpected
}

internal sealed class ControlClientException : Exception
{
    internal ControlClientException(ControlClientError error) : base(MessageFor(error)) => Error = error;
    internal ControlClientError Error { get; }

    private static string MessageFor(ControlClientError error) => error switch
    {
        ControlClientError.ServiceUnavailable => "The LibertyRoute service is unavailable.",
        ControlClientError.AuthorizationRequired => "Administrator authorization is required to control the LibertyRoute service.",
        ControlClientError.ProtocolError => "The LibertyRoute service returned an invalid control response.",
        ControlClientError.RequestRejected => "The LibertyRoute service rejected the request.",
        ControlClientError.ResponseTooLarge => "The network snapshot is too large to return safely.",
        ControlClientError.OperationCancelled => "The operation was cancelled before it could be completed.",
        ControlClientError.IndeterminateMutationOutcome => "The service response was lost; the operation may have been applied. Refresh status before trying again.",
        _ => "The operation could not be completed."
    };
}

internal sealed class ControlClient
{
    internal const string ProductionPipeName = "LibertyRoute.Network.v2";
    internal static readonly TimeSpan ProductionConnectTimeout = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan ProductionExchangeTimeout = TimeSpan.FromSeconds(8);

    private readonly string _pipeName;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan _exchangeTimeout;
    private readonly Func<string, CancellationToken, Task<Stream>> _connectAsync;

    internal ControlClient()
        : this(ProductionPipeName, TimeProvider.System, ProductionConnectTimeout, ProductionExchangeTimeout, null)
    {
    }

    internal ControlClient(
        string pipeName,
        TimeProvider timeProvider,
        TimeSpan connectTimeout,
        TimeSpan exchangeTimeout,
        Func<string, CancellationToken, Task<Stream>>? connectAsync)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        _pipeName = pipeName;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _connectTimeout = Positive(connectTimeout, nameof(connectTimeout));
        _exchangeTimeout = Positive(exchangeTimeout, nameof(exchangeTimeout));
        _connectAsync = connectAsync ?? ConnectPipeAsync;
    }

    internal Task<ControlStatusResult> GetStatusAsync(CancellationToken cancellationToken = default)
        => ExecuteAsync<ControlStatusResult>(ControlCommand.Status, cancellationToken);

    internal Task<ControlSnapshotResult> GetSnapshotAsync(CancellationToken cancellationToken = default)
        => ExecuteAsync<ControlSnapshotResult>(ControlCommand.Snapshot, cancellationToken);

    internal Task<ControlConnectResult> ConnectAsync(CancellationToken cancellationToken = default)
        => ExecuteAsync<ControlConnectResult>(ControlCommand.Connect, cancellationToken);

    internal Task<ControlDisconnectResult> DisconnectAsync(CancellationToken cancellationToken = default)
        => ExecuteAsync<ControlDisconnectResult>(ControlCommand.Disconnect, cancellationToken);

    private async Task<TResult> ExecuteAsync<TResult>(ControlCommand command, CancellationToken cancellationToken)
        where TResult : ControlResponseResult
    {
        var mutation = command is ControlCommand.Connect or ControlCommand.Disconnect;
        var transmissionMayHaveBegun = false;
        Stream stream;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectTimeout.CancelAfter(_connectTimeout);
            stream = await _connectAsync(_pipeName, connectTimeout.Token);
        }
        catch (UnauthorizedAccessException)
        {
            throw new ControlClientException(ControlClientError.AuthorizationRequired);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new ControlClientException(ControlClientError.OperationCancelled);
        }
        catch (OperationCanceledException)
        {
            throw new ControlClientException(ControlClientError.ServiceUnavailable);
        }
        catch (IOException)
        {
            throw new ControlClientException(ControlClientError.ServiceUnavailable);
        }
        catch (Exception)
        {
            throw new ControlClientException(ControlClientError.Unexpected);
        }

        await using (stream)
        {
            try
            {
                using var exchangeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                exchangeTimeout.CancelAfter(_exchangeTimeout);
                var token = exchangeTimeout.Token;
                var greeting = await LengthPrefixedJsonProtocol.ReadGreetingAsync(stream, token);
                var request = new ControlRequestEnvelope(
                    ControlProtocolConstants.Version,
                    greeting.ServiceInstanceId,
                    Guid.NewGuid(),
                    _timeProvider.GetUtcNow(),
                    command,
                    new ControlRequestPayload());

                transmissionMayHaveBegun = true;
                await LengthPrefixedJsonProtocol.WriteRequestAsync(stream, request, token);
                var response = await LengthPrefixedJsonProtocol.ReadResponseAsync(stream, token);
                ValidateCorrelation(response, greeting, request, mutation);

                if (response.Outcome == ControlOutcome.Failed)
                    throw Failure(response.ErrorCode);
                return response.Result is TResult result
                    ? result
                    : throw ProtocolFailure(mutation);
            }
            catch (ControlClientException)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                if (mutation && transmissionMayHaveBegun)
                    throw new ControlClientException(ControlClientError.IndeterminateMutationOutcome);
                throw new ControlClientException(cancellationToken.IsCancellationRequested
                    ? ControlClientError.OperationCancelled
                    : ControlClientError.ServiceUnavailable);
            }
            catch (ControlProtocolException)
            {
                throw ProtocolFailure(mutation && transmissionMayHaveBegun);
            }
            catch (IOException)
            {
                throw new ControlClientException(mutation && transmissionMayHaveBegun
                    ? ControlClientError.IndeterminateMutationOutcome
                    : ControlClientError.ServiceUnavailable);
            }
            catch (Exception)
            {
                throw new ControlClientException(mutation && transmissionMayHaveBegun
                    ? ControlClientError.IndeterminateMutationOutcome
                    : ControlClientError.Unexpected);
            }
        }
    }

    private static void ValidateCorrelation(
        ControlResponseEnvelope response,
        ControlServerGreeting greeting,
        ControlRequestEnvelope request,
        bool mutation)
    {
        if (response.ProtocolVersion != ControlProtocolConstants.Version ||
            response.ServiceInstanceId != greeting.ServiceInstanceId ||
            response.RequestId != request.RequestId ||
            response.Command != request.Command)
            throw ProtocolFailure(mutation);
    }

    private static ControlClientException Failure(ControlErrorCode error) => error switch
    {
        ControlErrorCode.ResponseTooLarge => new(ControlClientError.ResponseTooLarge),
        ControlErrorCode.Unauthorized => new(ControlClientError.AuthorizationRequired),
        _ => new(ControlClientError.RequestRejected)
    };

    private static ControlClientException ProtocolFailure(bool indeterminateMutation)
        => new(indeterminateMutation ? ControlClientError.IndeterminateMutationOutcome : ControlClientError.ProtocolError);

    private static async Task<Stream> ConnectPipeAsync(string pipeName, CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(cancellationToken);
            return pipe;
        }
        catch
        {
            await pipe.DisposeAsync();
            throw;
        }
    }

    private static TimeSpan Positive(TimeSpan value, string name)
        => value > TimeSpan.Zero ? value : throw new ArgumentOutOfRangeException(name);
}
