using System.Windows;
using LibertyRoute.ControlProtocol;

namespace LibertyRoute.Desktop;

public partial class MainWindow : Window
{
    private readonly ControlClient _controlClient = new();
    private bool _transactionActive;
    private bool _operationInFlight;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await RunExclusiveAsync(RefreshStatusCoreAsync, showDialog: false);
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        await RunExclusiveAsync(async () =>
        {
            if (_transactionActive)
            {
                var result = await _controlClient.DisconnectAsync();
                ApplyState(result.State);
                DetailText.Text = "The service completed the rollback request.";
            }
            else
            {
                var result = await _controlClient.ConnectAsync();
                ApplyState(result.State);
                DetailText.Text = result.State == ControlConnectionState.SnapshotCommitted
                    ? "Rollback snapshot committed. VPN transport is not enabled."
                    : "The service completed connection preparation.";
            }
        }, showDialog: true);
    }

    private async void DiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        await RunExclusiveAsync(async () =>
        {
            StateText.Text = "Requesting network snapshot...";
            DetailText.Text = "Requesting network snapshot...";
            var result = await _controlClient.GetSnapshotAsync();
            var path = await ControlSnapshotExporter.ExportAsync(result);
            DetailText.Text = $"Snapshot exported to {path}";
            StateText.Text = StateTextForCurrentTransaction();
        }, showDialog: true, failureDetail: "Snapshot export failed.");
    }

    private async Task RefreshStatusCoreAsync()
    {
        var result = await _controlClient.GetStatusAsync();
        ApplyState(result.State);
        DetailText.Text = "Service status refreshed.";
    }

    private async Task RunExclusiveAsync(
        Func<Task> operation,
        bool showDialog,
        string failureDetail = "The operation could not be completed.")
    {
        if (_operationInFlight)
            return;

        _operationInFlight = true;
        SetControlsEnabled(false);
        try
        {
            await operation();
        }
        catch (ControlClientException exception)
        {
            ApplyClientFailure(exception, showDialog);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[{DateTime.UtcNow:O}] Desktop operation failed ({exception.GetType().FullName}).");
            if (showDialog)
                MessageBox.Show(failureDetail, "LibertyRoute", MessageBoxButton.OK, MessageBoxImage.Error);
            DetailText.Text = failureDetail;
            StateText.Text = StateTextForCurrentTransaction();
        }
        finally
        {
            _operationInFlight = false;
            SetControlsEnabled(true);
        }
    }

    private void ApplyClientFailure(ControlClientException exception, bool showDialog)
    {
        Console.Error.WriteLine($"[{DateTime.UtcNow:O}] Desktop control operation failed ({exception.Error}).");
        if (showDialog)
            MessageBox.Show(exception.Message, "LibertyRoute", MessageBoxButton.OK, MessageBoxImage.Warning);
        StateText.Text = exception.Error switch
        {
            ControlClientError.AuthorizationRequired => "Authorization required",
            ControlClientError.ServiceUnavailable => "Service unavailable",
            ControlClientError.IndeterminateMutationOutcome => "Operation status unknown",
            _ => StateTextForCurrentTransaction()
        };
        DetailText.Text = exception.Message;
    }

    private void ApplyState(ControlConnectionState state)
    {
        _transactionActive = state != ControlConnectionState.Disconnected;
        StateText.Text = state switch
        {
            ControlConnectionState.Disconnected => "Disconnected",
            ControlConnectionState.CapturingState => "Capturing rollback state",
            ControlConnectionState.SnapshotCommitted => "Safe snapshot ready",
            ControlConnectionState.Connecting => "Connection preparation in progress",
            ControlConnectionState.Connected => "Service reports connected",
            ControlConnectionState.RollbackRequired => "Rollback required",
            ControlConnectionState.RollingBack => "Rolling back",
            ControlConnectionState.Verifying => "Verifying rollback",
            ControlConnectionState.RestorationFailed => "Restoration failed",
            _ => "Unknown service state"
        };
        ConnectButton.Content = _transactionActive ? "ROLL BACK" : "CONNECT";
    }

    private string StateTextForCurrentTransaction()
        => _transactionActive ? "Recovery snapshot active" : "Disconnected";

    private void SetControlsEnabled(bool enabled)
    {
        ConnectButton.IsEnabled = enabled;
        DiagnosticsButton.IsEnabled = enabled;
    }
}
