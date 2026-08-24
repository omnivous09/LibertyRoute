using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace LibertyRoute.Service;

public sealed class LibertyRouteWorker : BackgroundService
{
    private const string PipeName = "LibertyRoute.Network.v1";
    private readonly ConnectionController _controller;
    private readonly ILogger<LibertyRouteWorker> _logger;

    public LibertyRouteWorker(ConnectionController controller, ILogger<LibertyRouteWorker> logger)
    {
        _controller = controller;
        _logger = logger;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await _controller.RecoverOnStartupAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(
                PipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            await pipe.WaitForConnectionAsync(stoppingToken);
            await HandleClientAsync(pipe, stoppingToken);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _controller.RollbackAsync("Windows service stopping.", cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Rollback during service stop failed; recovery journal was retained.");
        }

        await base.StopAsync(cancellationToken);
    }

    private async Task HandleClientAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        using var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

        var request = await reader.ReadLineAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(request))
            return;

        object response;
        try
        {
            response = request.Trim().ToUpperInvariant() switch
            {
                "STATUS" => new { ok = true, state = _controller.State.ToString() },
                "SNAPSHOT" => await CaptureSnapshotAsync(cancellationToken),
                "CONNECT" => await BeginConnectAsync(cancellationToken),
                "DISCONNECT" => await DisconnectAsync(cancellationToken),
                _ => new { ok = false, error = "Unknown command." }
            };
        }
        catch (Exception ex)
        {
            response = new { ok = false, error = ex.Message, state = _controller.State.ToString() };
        }

        await writer.WriteLineAsync(JsonSerializer.Serialize(response));
    }

    private async Task<object> CaptureSnapshotAsync(CancellationToken cancellationToken)
    {
        var snapshot = await _controller.CaptureDiagnosticSnapshotAsync(cancellationToken);
        return new { ok = true, snapshot };
    }

    private async Task<object> BeginConnectAsync(CancellationToken cancellationToken)
    {
        var tx = await _controller.BeginSafeConnectAsync(cancellationToken);

        // No actual tunnel is started in Phase 1. This is intentional: a recovery
        // transaction is now proven before WireGuard integration is permitted.
        return new
        {
            ok = false,
            state = tx.State.ToString(),
            sessionId = tx.SessionId,
            error = "Rollback snapshot committed successfully. WireGuard transport is not enabled in Phase 1."
        };
    }

    private async Task<object> DisconnectAsync(CancellationToken cancellationToken)
    {
        await _controller.RollbackAsync("User requested disconnect.", cancellationToken);
        return new { ok = true, state = _controller.State.ToString() };
    }
}
