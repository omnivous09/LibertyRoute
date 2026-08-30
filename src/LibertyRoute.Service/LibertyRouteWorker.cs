namespace LibertyRoute.Service;

using LibertyRoute.Restoration.Windows;

internal sealed class LibertyRouteWorker : BackgroundService
{
    private const string PipeName = "LibertyRoute.Network.v2";
    private readonly ConnectionController _controller;
    private readonly SecureControlPipeFactory _pipeFactory;
    private readonly SecureControlConnectionHandler _handler;
    private readonly ILogger<LibertyRouteWorker> _logger;
    private readonly IRecoveryStartupReconciler _startupReconciler;

    public LibertyRouteWorker(
        ConnectionController controller,
        SecureControlPipeFactory pipeFactory,
        SecureControlConnectionHandler handler,
        IRecoveryStartupReconciler startupReconciler,
        ILogger<LibertyRouteWorker> logger)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _pipeFactory = pipeFactory ?? throw new ArgumentNullException(nameof(pipeFactory));
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _startupReconciler = startupReconciler ?? throw new ArgumentNullException(nameof(startupReconciler));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
            var server = new BoundedControlPipeServer(
                PipeName,
                _pipeFactory,
                _handler,
                _logger);
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

}
