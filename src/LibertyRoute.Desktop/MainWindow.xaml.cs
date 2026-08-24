using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace LibertyRoute.Desktop;

public partial class MainWindow : Window
{
    private const string PipeName = "LibertyRoute.Network.v1";
    private bool _transactionActive;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await RefreshStatusAsync();
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        ConnectButton.IsEnabled = false;
        try
        {
            if (_transactionActive)
            {
                var response = await SendCommandAsync("DISCONNECT");
                _transactionActive = false;
                StateText.Text = "Disconnected";
                ConnectButton.Content = "CONNECT";
                DetailText.Text = response;
            }
            else
            {
                var response = await SendCommandAsync("CONNECT");
                // Phase 1 intentionally stops after durable snapshot commit.
                _transactionActive = response.Contains("SnapshotCommitted", StringComparison.OrdinalIgnoreCase);
                StateText.Text = _transactionActive ? "Safe snapshot ready" : "Disconnected";
                ConnectButton.Content = _transactionActive ? "ROLL BACK" : "CONNECT";
                DetailText.Text = response;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "LibertyRoute", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ConnectButton.IsEnabled = true;
        }
    }

    private async Task RefreshStatusAsync()
    {
        try
        {
            var response = await SendCommandAsync("STATUS");
            _transactionActive = response.Contains("SnapshotCommitted", StringComparison.OrdinalIgnoreCase);
            StateText.Text = _transactionActive ? "Recovery snapshot active" : "Disconnected";
            ConnectButton.Content = _transactionActive ? "ROLL BACK" : "CONNECT";
            DetailText.Text = response;
        }
        catch
        {
            StateText.Text = "Service unavailable";
            DetailText.Text = "Install/start the LibertyRoute Network Service.";
        }
    }

    private static async Task<string> SendCommandAsync(string command)
    {
        await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await pipe.ConnectAsync(timeout.Token);

        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        using var writer = new StreamWriter(pipe, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync(command);
        return await reader.ReadLineAsync(timeout.Token) ?? "{}";
    }
}
