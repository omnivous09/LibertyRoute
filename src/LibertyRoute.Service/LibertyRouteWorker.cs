using System.Diagnostics;
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
            Console.Error.WriteLine($"[{DateTime.UtcNow:O}] LibertyRouteWorker: waiting for pipe client");
            await using var pipe = new NamedPipeServerStream(
                PipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            try
            {
                await pipe.WaitForConnectionAsync(stoppingToken);
                Console.Error.WriteLine($"[{DateTime.UtcNow:O}] LibertyRouteWorker: client connected");
                await HandleClientAsync(pipe, stoppingToken);
                Console.Error.WriteLine($"[{DateTime.UtcNow:O}] LibertyRouteWorker: client request handling completed, returning to WaitForConnectionAsync");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                Console.Error.WriteLine($"[{DateTime.UtcNow:O}] LibertyRouteWorker: shutdown cancellation requested");
                break;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[{DateTime.UtcNow:O}] LibertyRouteWorker: pipe handling exception {ex.GetType().FullName}: {ex.Message}");
                _logger.LogError(ex, "Named-pipe client handling failed; continuing to accept clients.");
            }
            finally
            {
                if (pipe.IsConnected)
                    pipe.Disconnect();
            }
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
        Console.Error.WriteLine($"[{DateTime.UtcNow:O}] LibertyRouteWorker: HandleClientAsync entered");

        Console.Error.WriteLine($"[{DateTime.UtcNow:O}] LibertyRouteWorker: creating StreamReader");
        using var reader = new StreamReader(stream, new UTF8Encoding(false), leaveOpen: true);
        Console.Error.WriteLine($"[{DateTime.UtcNow:O}] LibertyRouteWorker: StreamReader created");

        Console.Error.WriteLine($"[{DateTime.UtcNow:O}] LibertyRouteWorker: ReadLineAsync starting");
        var request = await reader.ReadLineAsync(cancellationToken);
        Console.Error.WriteLine($"[{DateTime.UtcNow:O}] LibertyRouteWorker: ReadLineAsync completed requestWasNull={request is null}");
        if (string.IsNullOrWhiteSpace(request))
            return;

        var normalizedRequest = request.Trim().ToUpperInvariant();
        Console.Error.WriteLine($"[{DateTime.UtcNow:O}] LibertyRouteWorker: command received '{normalizedRequest}'");

        object response;
        try
        {
            Console.Error.WriteLine($"[{DateTime.UtcNow:O}] LibertyRouteWorker: handling command '{normalizedRequest}'");
            response = normalizedRequest switch
            {
                "STATUS" => HandleStatusCommand(),
                "SNAPSHOT" => await CaptureSnapshotAsync(cancellationToken),
                "CONNECT" => await BeginConnectAsync(cancellationToken),
                "DISCONNECT" => await DisconnectAsync(cancellationToken),
                _ => new { ok = false, error = "Unknown command." }
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[{DateTime.UtcNow:O}] LibertyRouteWorker: command exception {ex.GetType().FullName}: {ex.Message}");
            response = new { ok = false, error = ex.Message, state = _controller.State.ToString() };
        }

        var responseJson = JsonSerializer.Serialize(response);
        Console.Error.WriteLine($"[{DateTime.UtcNow:O}] LibertyRouteWorker: starting JSON serialization (length={responseJson.Length})");
        var serialized = JsonSerializer.Serialize(response);
        Console.Error.WriteLine($"[{DateTime.UtcNow:O}] LibertyRouteWorker: serialization completed (length={serialized.Length})");

        Console.Error.WriteLine($"[{DateTime.UtcNow:O}] LibertyRouteWorker: creating StreamWriter");
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
        Console.Error.WriteLine($"[{DateTime.UtcNow:O}] LibertyRouteWorker: StreamWriter created");
        Console.Error.WriteLine($"[{DateTime.UtcNow:O}] LibertyRouteWorker: before WriteLineAsync(responseLength={serialized.Length})");
        await writer.WriteLineAsync(serialized);
        await writer.FlushAsync();
        Console.Error.WriteLine($"[{DateTime.UtcNow:O}] LibertyRouteWorker: after WriteLineAsync(responseLength={serialized.Length})");
    }

    private object HandleStatusCommand()
    {
        Console.Error.WriteLine($"[{DateTime.UtcNow:O}] LibertyRouteWorker: STATUS handling start");
        var result = new { ok = true, state = _controller.State.ToString() };
        Console.Error.WriteLine($"[{DateTime.UtcNow:O}] LibertyRouteWorker: STATUS handling end");
        return result;
    }

    private async Task<object> CaptureSnapshotAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        Console.Error.WriteLine($"[{DateTime.UtcNow:O}] LibertyRouteWorker: starting CaptureSnapshotAsync");
        var snapshot = await _controller.CaptureDiagnosticSnapshotAsync(cancellationToken);
        Console.Error.WriteLine($"[{DateTime.UtcNow:O}] LibertyRouteWorker: CaptureSnapshotAsync completed in {stopwatch.ElapsedMilliseconds}ms");
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
