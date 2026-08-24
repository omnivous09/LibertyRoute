using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace LibertyRoute.Desktop;

public partial class MainWindow : Window
{
    private const string PipeName = "LibertyRoute.Network.v1";
    private static readonly JsonSerializerOptions SnapshotJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private bool _transactionActive;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            Console.Error.WriteLine($"[{DateTime.UtcNow:O}] MainWindow: Loaded event fired");
            await RefreshStatusAsync();
            Console.Error.WriteLine($"[{DateTime.UtcNow:O}] MainWindow: Loaded/RefreshStatusAsync completed");
        };
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

    private async void DiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        DiagnosticsButton.IsEnabled = false;
        var phase = "starting export";
        var directory = string.Empty;
        var path = string.Empty;

        try
        {
            phase = "connecting to service / requesting SNAPSHOT";
            StateText.Text = "Requesting network snapshot...";
            DetailText.Text = "Requesting network snapshot...";

            var response = await SendCommandAsync("SNAPSHOT");

            phase = "parsing response";
            using var document = JsonDocument.Parse(response);
            if (!document.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
                throw new InvalidOperationException(document.RootElement.GetProperty("error").GetString());

            var snapshot = document.RootElement.GetProperty("snapshot").Deserialize<object>(SnapshotJsonOptions);
            if (snapshot is null)
                throw new InvalidOperationException("The service returned an empty network snapshot.");

            var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
            phase = "resolving Documents path";
            directory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

            phase = "creating export directory";
            Directory.CreateDirectory(directory);

            path = Path.Combine(directory, $"LibertyRoute-NetworkSnapshot-{timestamp}.json");
            phase = "writing JSON file";
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(snapshot, SnapshotJsonOptions));
            DetailText.Text = $"Snapshot exported to {path}";
            StateText.Text = _transactionActive ? "Recovery snapshot active" : "Disconnected";
        }
        catch (Exception ex)
        {
            var details = $"Phase: {phase}\n" +
                $"Exception type: {ex.GetType().FullName}\n" +
                $"Message: {ex.Message}\n" +
                $"Final path: {(string.IsNullOrEmpty(path) ? "(not resolved)" : path)}\n\n" +
                $"Full exception:\n{ex}";
            MessageBox.Show(details, "LibertyRoute", MessageBoxButton.OK, MessageBoxImage.Error);
            StateText.Text = _transactionActive ? "Recovery snapshot active" : "Disconnected";
            DetailText.Text = "Snapshot export failed.";
        }
        finally
        {
            DiagnosticsButton.IsEnabled = true;
        }
    }

    private async Task RefreshStatusAsync()
    {
        try
        {
            Console.Error.WriteLine($"[{DateTime.UtcNow:O}] MainWindow: sending STATUS");
            var response = await SendCommandAsync("STATUS");
            Console.Error.WriteLine($"[{DateTime.UtcNow:O}] MainWindow: STATUS response received ({response.Length} chars)");
            _transactionActive = response.Contains("SnapshotCommitted", StringComparison.OrdinalIgnoreCase);
            StateText.Text = _transactionActive ? "Recovery snapshot active" : "Disconnected";
            ConnectButton.Content = _transactionActive ? "ROLL BACK" : "CONNECT";
            DetailText.Text = response;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[{DateTime.UtcNow:O}] MainWindow: STATUS failed with {ex.GetType().FullName}: {ex.Message}");
            StateText.Text = "Service unavailable";
            DetailText.Text = "Install/start the LibertyRoute Network Service.";
        }
    }

    private static async Task<string> SendCommandAsync(string command)
    {
        const int connectTimeoutSeconds = 1;
        const int responseTimeoutSeconds = 8;

        NamedPipeClientStream? pipe = null;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Console.Error.WriteLine($"[{DateTime.UtcNow:O}] MainWindow: attempting named-pipe connect (attempt {attempt + 1})");
                pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(connectTimeoutSeconds));
                await pipe.ConnectAsync(connectTimeout.Token);
                Console.Error.WriteLine($"[{DateTime.UtcNow:O}] MainWindow: pipe connection succeeded");
                break;
            }
            catch (OperationCanceledException) when (attempt < 2)
            {
                Console.Error.WriteLine($"[{DateTime.UtcNow:O}] MainWindow: connect timed out on attempt {attempt + 1}");
                pipe?.Dispose();
                pipe = null;
                await Task.Delay(200);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[{DateTime.UtcNow:O}] MainWindow: connect exception {ex.GetType().FullName}: {ex.Message}");
                pipe?.Dispose();
                pipe = null;
                throw;
            }
        }

        if (pipe is null)
            throw new TimeoutException("The LibertyRoute service did not respond on the named pipe.");

        try
        {
            var utf8NoBom = new UTF8Encoding(false);

            Console.Error.WriteLine($"[{DateTime.UtcNow:O}] MainWindow: creating request StreamWriter");
            using var writer = new StreamWriter(pipe, utf8NoBom, leaveOpen: true);
            Console.Error.WriteLine($"[{DateTime.UtcNow:O}] MainWindow: request StreamWriter created");

            Console.Error.WriteLine($"[{DateTime.UtcNow:O}] MainWindow: before WriteLineAsync(command='{command}')");
            await writer.WriteLineAsync(command);
            await writer.FlushAsync();
            Console.Error.WriteLine($"[{DateTime.UtcNow:O}] MainWindow: after WriteLineAsync(command='{command}')");

            Console.Error.WriteLine($"[{DateTime.UtcNow:O}] MainWindow: creating response StreamReader");
            using var reader = new StreamReader(pipe, utf8NoBom, leaveOpen: true);
            Console.Error.WriteLine($"[{DateTime.UtcNow:O}] MainWindow: response StreamReader created");

            using var responseTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(responseTimeoutSeconds));
            try
            {
                Console.Error.WriteLine($"[{DateTime.UtcNow:O}] MainWindow: before ReadLineAsync(command='{command}')");
                var response = await reader.ReadLineAsync(responseTimeout.Token);
                Console.Error.WriteLine($"[{DateTime.UtcNow:O}] MainWindow: after ReadLineAsync(command='{command}') responseWasNull={response is null}");
                if (response is null)
                    throw new TimeoutException("The LibertyRoute service connected but did not return a response in time.");

                Console.Error.WriteLine($"[{DateTime.UtcNow:O}] MainWindow: response received ({response.Length} chars)");
                return response;
            }
            catch (OperationCanceledException ex)
            {
                Console.Error.WriteLine($"[{DateTime.UtcNow:O}] MainWindow: response read timed out: {ex.GetType().FullName}: {ex.Message}");
                throw new TimeoutException("The LibertyRoute service connected but did not return a response in time.");
            }
        }
        finally
        {
            pipe.Dispose();
        }
    }
}
