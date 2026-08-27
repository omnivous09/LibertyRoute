namespace LibertyRoute.Service;

internal sealed class LibertyRouteWorker : BackgroundService
{
    private const string PipeName = "LibertyRoute.Network.v2";
    private readonly ConnectionController _controller;
    private readonly SecureControlPipeFactory _pipeFactory;
    private readonly SecureControlConnectionHandler _handler;
    private readonly ILogger<LibertyRouteWorker> _logger;

    public LibertyRouteWorker(
        ConnectionController controller,
        SecureControlPipeFactory pipeFactory,
        SecureControlConnectionHandler handler,
        ILogger<LibertyRouteWorker> logger)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _pipeFactory = pipeFactory ?? throw new ArgumentNullException(nameof(pipeFactory));
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
            await using var pipe = CreateListener();
            try
            {
                await pipe.WaitForConnectionAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogCritical(exception, "The secure control listener failed.");
                throw;
            }

            try
            {
                await _handler.HandleAsync(pipe, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Secure control client handling failed.");
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _controller.RollbackAsync("Windows service stopping.", cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Rollback during service stop failed; recovery journal was retained.");
        }

        await base.StopAsync(cancellationToken);
    }

    private System.IO.Pipes.NamedPipeServerStream CreateListener()
    {
        try
        {
            return _pipeFactory.Create(PipeName);
        }
        catch (Exception exception)
        {
            _logger.LogCritical(exception, "The secure control listener could not be created.");
            throw;
        }
    }
}
