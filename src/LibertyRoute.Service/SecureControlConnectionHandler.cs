using System.IO.Pipes;
using LibertyRoute.ControlProtocol;

namespace LibertyRoute.Service;

internal sealed record ControlDispatchResult(
    ControlOutcome Outcome,
    ControlErrorCode ErrorCode,
    ControlResponseResult? Result);

internal interface IControlCommandDispatcher
{
    Task<ControlDispatchResult> DispatchAsync(
        ControlCallerIdentity caller,
        ControlRequestEnvelope request,
        CancellationToken cancellationToken);
}

internal sealed class SecureControlConnectionHandler
{
    private readonly ControlServiceInstance _serviceInstance;
    private readonly ControlCommandAuthorization _authorization;
    private readonly ControlRequestReplayGuard _replayGuard;
    private readonly IControlCommandDispatcher _dispatcher;
    private readonly ILogger<SecureControlConnectionHandler> _logger;

    internal SecureControlConnectionHandler(
        ControlServiceInstance serviceInstance,
        ControlCommandAuthorization authorization,
        ControlRequestReplayGuard replayGuard,
        IControlCommandDispatcher dispatcher,
        ILogger<SecureControlConnectionHandler> logger)
    {
        _serviceInstance = serviceInstance ?? throw new ArgumentNullException(nameof(serviceInstance));
        _authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        _replayGuard = replayGuard ?? throw new ArgumentNullException(nameof(replayGuard));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    internal async Task HandleAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);
        cancellationToken.ThrowIfCancellationRequested();
        var caller = WindowsControlCallerIdentityCapture.Capture(server);
        cancellationToken.ThrowIfCancellationRequested();
        await HandleAuthenticatedForTestsAsync(server, caller, cancellationToken);
    }

    internal async Task HandleAuthenticatedForTestsAsync(
        Stream stream,
        ControlCallerIdentity caller,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(caller);

        var principalDecision = _authorization.AuthorizePrincipal(caller);
        if (principalDecision != ControlAuthorizationDecision.Authorized)
        {
            _logger.LogWarning("Control caller {CallerSid} was rejected with {Decision}.", caller.UserSid, principalDecision);
            return;
        }

        ControlRequestEnvelope request;
        try
        {
            request = await LengthPrefixedJsonProtocol.ReadRequestAsync(stream, cancellationToken);
        }
        catch (ControlProtocolException exception)
        {
            _logger.LogWarning("Control request was rejected with {ProtocolError}.", exception.Error);
            return;
        }

        if (request.ServiceInstanceId != _serviceInstance.Id)
        {
            await WriteFailureAsync(stream, request, ControlErrorCode.WrongServiceInstance, cancellationToken);
            return;
        }

        if (_replayGuard.EvaluateFreshness(request.SentAtUtc) != ControlFreshnessDecision.Current)
        {
            await WriteFailureAsync(stream, request, ControlErrorCode.StaleRequest, cancellationToken);
            return;
        }

        if (_authorization.AuthorizeCommand(caller, request.Command) != ControlAuthorizationDecision.Authorized)
        {
            await WriteFailureAsync(stream, request, ControlErrorCode.ForbiddenCommand, cancellationToken);
            return;
        }

        var replay = await _replayGuard.ReserveAsync(caller, request, cancellationToken);
        var replayError = replay switch
        {
            ControlReplayReservationResult.Reserved => ControlErrorCode.None,
            ControlReplayReservationResult.Duplicate => ControlErrorCode.DuplicateRequest,
            ControlReplayReservationResult.Conflict => ControlErrorCode.RequestConflict,
            ControlReplayReservationResult.CapacityExceeded => ControlErrorCode.ReplayCapacityExceeded,
            _ => ControlErrorCode.InternalError
        };
        if (replayError != ControlErrorCode.None)
        {
            await WriteFailureAsync(stream, request, replayError, cancellationToken);
            return;
        }

        ControlDispatchResult result;
        try
        {
            result = await _dispatcher.DispatchAsync(caller, request, cancellationToken);
            if (!Enum.IsDefined(result.Outcome) || !Enum.IsDefined(result.ErrorCode) ||
                ((result.Outcome == ControlOutcome.Succeeded) != (result.ErrorCode == ControlErrorCode.None)) ||
                !IsValidResult(request.Command, result))
                result = new(ControlOutcome.Failed, ControlErrorCode.InternalError, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Control dispatch failed for instance {ServiceInstanceId}, request {RequestId}, command {Command}.",
                _serviceInstance.Id,
                request.RequestId,
                request.Command);
            result = new(ControlOutcome.Failed, ControlErrorCode.InternalError, null);
        }

        var response = new ControlResponseEnvelope(
            ControlProtocolConstants.Version,
            _serviceInstance.Id,
            request.RequestId,
            request.Command,
            result.Outcome,
            result.ErrorCode,
            result.Result);
        await LengthPrefixedJsonProtocol.WriteResponseAsync(stream, response, cancellationToken);
    }

    private Task WriteFailureAsync(
        Stream stream,
        ControlRequestEnvelope request,
        ControlErrorCode error,
        CancellationToken cancellationToken)
        => LengthPrefixedJsonProtocol.WriteResponseAsync(
            stream,
            new ControlResponseEnvelope(
                ControlProtocolConstants.Version,
                _serviceInstance.Id,
                request.RequestId,
                request.Command,
                ControlOutcome.Failed,
                error,
                null),
            cancellationToken);

    private static bool IsValidResult(ControlCommand command, ControlDispatchResult result)
    {
        if (result.Outcome == ControlOutcome.Failed)
            return result.Result is null;
        return command switch
        {
            ControlCommand.Status => result.Result is ControlStatusResult,
            ControlCommand.Snapshot => result.Result is ControlSnapshotResult,
            ControlCommand.Connect => result.Result is ControlConnectResult,
            ControlCommand.Disconnect => result.Result is ControlDisconnectResult,
            _ => false
        };
    }
}
