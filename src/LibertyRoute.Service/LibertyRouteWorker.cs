namespace LibertyRoute.Service;

using LibertyRoute.Restoration.Windows;

internal sealed class LibertyRouteWorker : BackgroundService
{
    private const string PipeName = "LibertyRoute.Network.v2";
    private readonly ConnectionController _controller;
    private readonly Func<BoundedControlPipeServer> _serverFactory;
    private readonly ILogger<LibertyRouteWorker> _logger;
    private readonly IRecoveryStartupReconciler _startupReconciler;
    private BoundedControlPipeServer? _server;

    public LibertyRouteWorker(
        ConnectionController controller,
        SecureControlPipeFactory pipeFactory,
        SecureControlConnectionHandler handler,
        IRecoveryStartupReconciler startupReconciler,
        ILogger<LibertyRouteWorker> logger)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        ArgumentNullException.ThrowIfNull(pipeFactory);
        ArgumentNullException.ThrowIfNull(handler);
        _startupReconciler = startupReconciler ?? throw new ArgumentNullException(nameof(startupReconciler));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _serverFactory = () => new BoundedControlPipeServer(PipeName, pipeFactory, handler, logger);
    }

    internal LibertyRouteWorker(
        ConnectionController controller,
        IRecoveryStartupReconciler startupReconciler,
        ILogger<LibertyRouteWorker> logger,
        Func<BoundedControlPipeServer> serverFactory)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _startupReconciler = startupReconciler ?? throw new ArgumentNullException(nameof(startupReconciler));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _serverFactory = serverFactory ?? throw new ArgumentNullException(nameof(serverFactory));
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        var reconciliation = await _startupReconciler.ReconcileAsync(cancellationToken);
        switch (reconciliation.Status)
        {
            case RecoveryStartupReconciliationStatus.NoJournal:
            case RecoveryStartupReconciliationStatus.ReconciledAndCleared:
                break;
            case RecoveryStartupReconciliationStatus.LegacyRecoveryRequired:
                await _controller.RecoverOnStartupAsync(cancellationToken);
                break;
            default:
                _logger.LogCritical(
                    "Startup recovery failed closed with {Status}: {Reason}",
                    reconciliation.Status,
                    reconciliation.Reason);
                throw new InvalidOperationException(
                    $"Startup recovery failed closed with {reconciliation.Status}: {reconciliation.Reason}");
        }
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var server = _serverFactory();
            Volatile.Write(ref _server, server);
            await server.RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogCritical(exception, "The secure control listener failed.");
            throw;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        var server = Volatile.Read(ref _server);
        server?.BeginShutdown();
        await base.StopAsync(cancellationToken);

        if (server is not null &&
            (!server.DrainCompletion.IsCompletedSuccessfully || server.State != ControlPipeServerState.Stopped))
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("The secure control server did not complete its client drain.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await _controller.RollbackAsync("Windows service stopping.", cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Rollback during service stop failed; recovery journal was retained.");
        }
    }

}
