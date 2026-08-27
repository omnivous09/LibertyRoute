using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LibertyRoute.ControlProtocol;
using LibertyRoute.Core;
using LibertyRoute.Engine;
using LibertyRoute.Networking;
using LibertyRoute.Recovery;
using LibertyRoute.Restoration;
using LibertyRoute.Service;

namespace LibertyRoute.Restoration.Tests;

public sealed class SessionOwnershipTests
{
    private const string OwnerA = "S-1-5-21-1001";
    private const string OwnerB = "S-1-5-21-1002";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-sid")]
    public async Task InvalidOwnerFailsBeforeSnapshotCapture(string? ownerSid)
    {
        var network = new Network();
        var journal = new Journal();
        var controller = Controller(network, journal);

        await Assert.ThrowsAsync<ArgumentException>(
            () => controller.BeginSafeConnectAsync(ownerSid, CancellationToken.None));

        Assert.Equal(0, network.Captures);
        Assert.Empty(journal.Writes);
        Assert.Equal(ConnectionState.Disconnected, controller.State);
    }

    [Fact]
    public async Task CanonicalOwnerAndGeneratedSessionAreInFirstWriteBeforePublication()
    {
        var network = new Network();
        ConnectionController? controller = null;
        var journal = new Journal
        {
            OnWrite = transaction =>
            {
                Assert.NotEqual(Guid.Empty, transaction.SessionId);
                Assert.Equal(OwnerA, transaction.OwnerSid);
                Assert.Equal(ConnectionState.Disconnected, controller!.State);
                return Task.CompletedTask;
            }
        };
        controller = Controller(network, journal);

        var transaction = await controller.BeginSafeConnectAsync(OwnerA, CancellationToken.None);

        var first = Assert.Single(journal.Writes);
        Assert.Same(transaction, first);
        Assert.Equal(transaction.SessionId, first.SessionId);
        Assert.Equal(OwnerA, first.OwnerSid);
        Assert.Equal(ConnectionState.SnapshotCommitted, controller.State);
    }

    [Fact]
    public async Task CaptureFailureWritesNoSession()
    {
        var network = new Network { CaptureException = new InvalidOperationException("capture failed") };
        var journal = new Journal();
        var controller = Controller(network, journal);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => controller.BeginSafeConnectAsync(OwnerA, CancellationToken.None));

        Assert.Empty(journal.Writes);
        Assert.Null(journal.Active);
        Assert.Equal(ConnectionState.Disconnected, controller.State);
    }

    [Fact]
    public async Task CancelledOrFailedJournalWriteDoesNotPublishActiveSession()
    {
        foreach (var failure in new Exception[]
                 {
                     new OperationCanceledException(),
                     new IOException("write failed")
                 })
        {
            var journal = new Journal { WriteException = failure };
            var controller = Controller(new Network(), journal);

            await Assert.ThrowsAnyAsync<Exception>(
                () => controller.BeginSafeConnectAsync(OwnerA, CancellationToken.None));

            Assert.Null(journal.Active);
            Assert.Equal(ConnectionState.Disconnected, controller.State);
            Assert.Equal(1, journal.WriteAttempts);
        }
    }

    [Fact]
    public async Task ConcurrentCreationHasOneWinnerAndCannotReplaceOwner()
    {
        var journal = new Journal();
        var controller = Controller(new Network(), journal);
        var attempts = new[]
        {
            Capture(() => controller.BeginSafeConnectAsync(OwnerA, CancellationToken.None)),
            Capture(() => controller.BeginSafeConnectAsync(OwnerB, CancellationToken.None))
        };

        var outcomes = await Task.WhenAll(attempts);

        var winner = Assert.Single(outcomes, outcome => outcome.Transaction is not null).Transaction!;
        Assert.Single(outcomes, outcome => outcome.Exception is InvalidOperationException);
        Assert.Single(journal.Writes);
        Assert.Equal(winner.OwnerSid, journal.Active!.OwnerSid);
        Assert.Contains(winner.OwnerSid, new[] { OwnerA, OwnerB });
    }

    [Fact]
    public async Task RollbackStateRewritesPreserveOwnerExactly()
    {
        var journal = new Journal();
        var engine = new Engine();
        var controller = Controller(new Network(), journal, engine);
        var created = await controller.BeginSafeConnectAsync(OwnerA, CancellationToken.None);

        await controller.RollbackAsync("controlled test", CancellationToken.None);

        Assert.Equal(3, journal.Writes.Count);
        Assert.All(journal.Writes, transaction =>
        {
            Assert.Equal(created.SessionId, transaction.SessionId);
            Assert.Equal(OwnerA, transaction.OwnerSid);
        });
        Assert.Equal(new[]
        {
            ConnectionState.SnapshotCommitted,
            ConnectionState.RollingBack,
            ConnectionState.Disconnected
        }, journal.Writes.Select(transaction => transaction.State));
        Assert.Equal(1, engine.Stops);
    }

    [Fact]
    public async Task JournalRoundTripPreservesOwnerAndExplicitNull()
    {
        var root = TempDirectory();
        try
        {
            var journal = new FileTransactionJournal(Path.Combine(root, "active.lrj"));
            var owned = Transaction(OwnerA);
            await journal.WriteAsync(owned, CancellationToken.None);
            Assert.Equal(OwnerA, (await journal.ReadActiveAsync(CancellationToken.None))!.OwnerSid);

            var unknown = Transaction(ownerSid: null);
            await journal.WriteAsync(unknown, CancellationToken.None);
            Assert.Null((await journal.ReadActiveAsync(CancellationToken.None))!.OwnerSid);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LegacyMissingOwnerRemainsUnknownAndCannotBeAdopted()
    {
        var root = TempDirectory();
        try
        {
            var path = Path.Combine(root, "active.lrj");
            var transaction = Transaction(ownerSid: null);
            var payload = JsonSerializer.Serialize(transaction, CamelCase)
                .Replace(",\"ownerSid\":null", string.Empty, StringComparison.Ordinal);
            var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(
                new { payload, sha256 = checksum }, CamelCase));

            var fileJournal = new FileTransactionJournal(path);
            var legacy = await fileJournal.ReadActiveAsync(CancellationToken.None);
            Assert.Null(legacy!.OwnerSid);

            var journal = new Journal { Active = legacy };
            var network = new Network();
            var controller = Controller(network, journal);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => controller.BeginSafeConnectAsync(OwnerB, CancellationToken.None));
            Assert.Null(journal.Active!.OwnerSid);
            Assert.Empty(journal.Writes);
            Assert.Equal(0, network.Captures);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MalformedPersistedOwnerFailsClosedBeforeCleanup()
    {
        var legacy = Transaction("not-a-sid") with { State = ConnectionState.RollbackRequired };
        var journal = new Journal { Active = legacy };
        var engine = new Engine();
        var controller = Controller(new Network(), journal, engine);

        await Assert.ThrowsAsync<ArgumentException>(
            () => controller.RollbackAsync("controlled test", CancellationToken.None));

        Assert.Same(legacy, journal.Active);
        Assert.Empty(journal.Writes);
        Assert.Equal(0, engine.Stops);
    }

    [Fact]
    public void OwnershipSurfaceRemainsInternalAndIsolated()
    {
        Assert.DoesNotContain(
            typeof(NetworkTransaction).GetProperties(),
            property => property.Name.Contains("ChangeOwner", StringComparison.Ordinal));

        var root = FindRepositoryRoot();
        var protocol = string.Join('\n', Directory.GetFiles(
            Path.Combine(root, "src", "LibertyRoute.ControlProtocol"), "*.cs").Select(File.ReadAllText));
        var desktop = string.Join('\n', Directory.GetFiles(
            Path.Combine(root, "src", "LibertyRoute.Desktop"), "*.cs").Select(File.ReadAllText));
        var ledger = File.ReadAllText(Path.Combine(root, "src", "LibertyRoute.Restoration", "OwnershipLedgerModel.cs"));
        var serviceProject = File.ReadAllText(Path.Combine(root, "src", "LibertyRoute.Service", "LibertyRoute.Service.csproj"));
        var commands = string.Join(',', Enum.GetNames<ControlCommand>());
        var serviceSources = string.Join('\n', Directory.GetFiles(
            Path.Combine(root, "src", "LibertyRoute.Service"), "*.cs").Select(File.ReadAllText));

        Assert.DoesNotContain("OwnerSid", protocol, StringComparison.Ordinal);
        Assert.DoesNotContain("OwnerSid", desktop, StringComparison.Ordinal);
        Assert.DoesNotContain("OwnerSid", ledger, StringComparison.Ordinal);
        Assert.DoesNotContain("Restoration.Windows", serviceProject, StringComparison.Ordinal);
        Assert.DoesNotContain("Recovery", commands, StringComparison.OrdinalIgnoreCase);
        foreach (var forbidden in new[]
                 {
                     "ControlledRecoveryApproval", "ControlledRecoveryRestorationWorkflow",
                     "ControlledRestorationActivationGrant", "RestorationExecutionCapability",
                     "RouteMutationProviderFactory", "WindowsRouteMutationNative"
                 })
            Assert.DoesNotContain(forbidden, serviceSources, StringComparison.Ordinal);
    }

    private static async Task<(NetworkTransaction? Transaction, Exception? Exception)> Capture(
        Func<Task<NetworkTransaction>> action)
    {
        try { return (await action(), null); }
        catch (Exception exception) { return (null, exception); }
    }

    private static ConnectionController Controller(Network network, Journal journal, Engine? engine = null)
        => new(network, journal, engine ?? new Engine());

    private static NetworkTransaction Transaction(string? ownerSid)
        => new(Guid.NewGuid(), ConnectionState.SnapshotCommitted, DateTimeOffset.UtcNow,
            Snapshot(), Array.Empty<OwnedNetworkChange>(), "test", null, ownerSid);

    private static NetworkStateSnapshot Snapshot()
        => new(DateTimeOffset.UtcNow, "machine", Array.Empty<AdapterState>());

    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"LibertyRoute.SessionOwnership.{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "LibertyRoute.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private sealed class Network : INetworkStateManager
    {
        public int Captures { get; private set; }
        public Exception? CaptureException { get; init; }
        public Task<NetworkStateSnapshot> CaptureStateAsync(CancellationToken cancellationToken)
        {
            Captures++;
            cancellationToken.ThrowIfCancellationRequested();
            if (CaptureException is not null) throw CaptureException;
            return Task.FromResult(Snapshot());
        }
        public Task VerifyRestorationAsync(NetworkStateSnapshot original, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class Journal : ITransactionJournal
    {
        public NetworkTransaction? Active { get; set; }
        public List<NetworkTransaction> Writes { get; } = new();
        public int WriteAttempts { get; private set; }
        public Exception? WriteException { get; init; }
        public Func<NetworkTransaction, Task>? OnWrite { get; init; }
        public string JournalPath => "controlled-test";
        public async Task WriteAsync(NetworkTransaction transaction, CancellationToken cancellationToken)
        {
            WriteAttempts++;
            if (WriteException is not null) throw WriteException;
            if (OnWrite is not null) await OnWrite(transaction);
            Writes.Add(transaction);
            Active = transaction;
        }
        public Task<NetworkTransaction?> ReadActiveAsync(CancellationToken cancellationToken)
            => Task.FromResult(Active);
        public Task ClearAsync(Guid expectedSessionId, CancellationToken cancellationToken)
        {
            Assert.Equal(expectedSessionId, Active?.SessionId);
            Active = null;
            return Task.CompletedTask;
        }
    }

    private sealed class Engine : IConnectionEngine
    {
        public string Id => "controlled-test";
        public int Stops { get; private set; }
        public Task StartAsync(VpnServerConfig server, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Live engine start is forbidden.");
        public Task StopAsync(CancellationToken cancellationToken)
        {
            Stops++;
            return Task.CompletedTask;
        }
        public Task<bool> IsHealthyAsync(CancellationToken cancellationToken) => Task.FromResult(false);
    }
}
