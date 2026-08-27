using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using LibertyRoute.ControlProtocol;
using LibertyRoute.Desktop;

namespace LibertyRoute.Restoration.Tests;

public sealed class ControlClientTests
{
    private static readonly Guid InstanceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-06-07T08:09:10+00:00");

    [Fact]
    public async Task GreetingPrecedesRequestAndExactMetadataIsBoundAndCorrelated()
    {
        var pipeName = UniquePipeName();
        ControlRequestEnvelope? observed = null;
        var server = RunServerAsync(pipeName, async pipe =>
        {
            await LengthPrefixedJsonProtocol.WriteGreetingAsync(pipe, new(ControlProtocolConstants.Version, InstanceId));
            observed = await LengthPrefixedJsonProtocol.ReadRequestAsync(pipe);
            await LengthPrefixedJsonProtocol.WriteResponseAsync(pipe, Success(
                observed, new ControlStatusResult(ControlConnectionState.SnapshotCommitted)));
        });

        var result = await Client(pipeName).GetStatusAsync();
        await server;

        Assert.Equal(ControlConnectionState.SnapshotCommitted, result.State);
        Assert.NotNull(observed);
        Assert.Equal(InstanceId, observed.ServiceInstanceId);
        Assert.NotEqual(Guid.Empty, observed.RequestId);
        Assert.Equal(Now, observed.SentAtUtc);
        Assert.Equal(ControlCommand.Status, observed.Command);
        Assert.Empty(typeof(ControlRequestPayload).GetProperties());
    }

    [Fact]
    public async Task EveryExplicitOperationUsesFreshRequestIdAndConnection()
    {
        var pipeName = UniquePipeName();
        var requestIds = new List<Guid>();
        for (var index = 0; index < 2; index++)
        {
            var server = RunServerAsync(pipeName, async pipe =>
            {
                await LengthPrefixedJsonProtocol.WriteGreetingAsync(pipe, new(ControlProtocolConstants.Version, InstanceId));
                var request = await LengthPrefixedJsonProtocol.ReadRequestAsync(pipe);
                requestIds.Add(request.RequestId);
                await LengthPrefixedJsonProtocol.WriteResponseAsync(pipe, Success(
                    request, new ControlStatusResult(ControlConnectionState.Disconnected)));
            });
            await Client(pipeName).GetStatusAsync();
            await server;
        }
        Assert.Equal(2, requestIds.Distinct().Count());
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"protocolVersion\":1,\"serviceInstanceId\":\"11111111-1111-1111-1111-111111111111\"}")]
    [InlineData("{\"protocolVersion\":2,\"serviceInstanceId\":\"00000000-0000-0000-0000-000000000000\"}")]
    public async Task MalformedWrongVersionOrEmptyInstanceGreetingIsRejected(string json)
    {
        var pipeName = UniquePipeName();
        var server = RunServerAsync(pipeName, pipe => WriteRawFrameAsync(pipe, json));
        var exception = await Assert.ThrowsAsync<ControlClientException>(() => Client(pipeName).GetStatusAsync());
        await server;
        Assert.Equal(ControlClientError.ProtocolError, exception.Error);
    }

    [Theory]
    [InlineData("instance")]
    [InlineData("request")]
    [InlineData("command")]
    public async Task ResponseCorrelationMustMatchExactly(string mismatch)
    {
        var pipeName = UniquePipeName();
        var server = RunServerAsync(pipeName, async pipe =>
        {
            await LengthPrefixedJsonProtocol.WriteGreetingAsync(pipe, new(ControlProtocolConstants.Version, InstanceId));
            var request = await LengthPrefixedJsonProtocol.ReadRequestAsync(pipe);
            var response = mismatch switch
            {
                "instance" => Success(request, new ControlStatusResult(ControlConnectionState.Disconnected)) with { ServiceInstanceId = Guid.NewGuid() },
                "request" => Success(request, new ControlStatusResult(ControlConnectionState.Disconnected)) with { RequestId = Guid.NewGuid() },
                _ => new ControlResponseEnvelope(ControlProtocolConstants.Version, request.ServiceInstanceId, request.RequestId,
                    ControlCommand.Snapshot, ControlOutcome.Succeeded, ControlErrorCode.None,
                    new ControlSnapshotResult(EmptySnapshot()))
            };
            await LengthPrefixedJsonProtocol.WriteResponseAsync(pipe, response);
        });
        var exception = await Assert.ThrowsAsync<ControlClientException>(() => Client(pipeName).GetStatusAsync());
        await server;
        Assert.Equal(ControlClientError.ProtocolError, exception.Error);
    }

    [Fact]
    public async Task WrongResponseVersionAndResultShapeAreRejected()
    {
        foreach (var change in new[] { "version", "result" })
        {
            var pipeName = UniquePipeName();
            var server = RunServerAsync(pipeName, async pipe =>
            {
                await LengthPrefixedJsonProtocol.WriteGreetingAsync(pipe, new(ControlProtocolConstants.Version, InstanceId));
                var request = await LengthPrefixedJsonProtocol.ReadRequestAsync(pipe);
                await using var framed = new MemoryStream();
                await LengthPrefixedJsonProtocol.WriteResponseAsync(framed, Success(
                    request, new ControlStatusResult(ControlConnectionState.Disconnected)));
                var json = Encoding.UTF8.GetString(framed.ToArray()[ControlProtocolConstants.LengthPrefixSize..]);
                json = change == "version"
                    ? json.Replace("\"protocolVersion\":2", "\"protocolVersion\":1", StringComparison.Ordinal)
                    : json.Replace("\"$resultType\":\"STATUS\"", "\"$resultType\":\"SNAPSHOT\"", StringComparison.Ordinal);
                await WriteRawFrameAsync(pipe, json);
            });
            var exception = await Assert.ThrowsAsync<ControlClientException>(() => Client(pipeName).GetStatusAsync());
            await server;
            Assert.Equal(ControlClientError.ProtocolError, exception.Error);
        }
    }

    [Fact]
    public async Task SnapshotPreservesUnsignedMetricAndNullEmptyDistinctions()
    {
        var pipeName = UniquePipeName();
        var snapshot = new ControlNetworkSnapshot(Now, "machine", Array.Empty<ControlAdapterState>(),
            new[] { new ControlRouteState("destination", "next", 1, uint.MaxValue, "family") },
            new[] { new ControlDnsInterfaceState("id", "name", true, Array.Empty<string>(), null, Array.Empty<string>(),
                ControlDnsConfigurationSource.Unknown, ControlDnsConfigurationSource.Static, null, Array.Empty<string>(), null, Array.Empty<string>()) });
        var server = RunServerAsync(pipeName, async pipe =>
        {
            await LengthPrefixedJsonProtocol.WriteGreetingAsync(pipe, new(ControlProtocolConstants.Version, InstanceId));
            var request = await LengthPrefixedJsonProtocol.ReadRequestAsync(pipe);
            await LengthPrefixedJsonProtocol.WriteResponseAsync(pipe, Success(request, new ControlSnapshotResult(snapshot)));
        });
        var result = await Client(pipeName).GetSnapshotAsync();
        await server;
        Assert.Equal(uint.MaxValue, Assert.Single(result.Snapshot.Routes!).Metric);
        var dns = Assert.Single(result.Snapshot.DnsInterfaces!);
        Assert.Null(dns.IPv4DnsServers);
        Assert.Empty(dns.IPv6DnsServers!);
        Assert.Null(dns.IPv4StaticDnsServers);
        Assert.Empty(dns.IPv4DhcpDnsServers!);
    }

    [Fact]
    public async Task ResponseTooLargeIsSanitizedWithoutRetryOrPartialResult()
    {
        var pipeName = UniquePipeName();
        var connections = 0;
        var server = RunServerAsync(pipeName, async pipe =>
        {
            connections++;
            await LengthPrefixedJsonProtocol.WriteGreetingAsync(pipe, new(ControlProtocolConstants.Version, InstanceId));
            var request = await LengthPrefixedJsonProtocol.ReadRequestAsync(pipe);
            await LengthPrefixedJsonProtocol.WriteResponseAsync(pipe, new(ControlProtocolConstants.Version,
                request.ServiceInstanceId, request.RequestId, request.Command, ControlOutcome.Failed,
                ControlErrorCode.ResponseTooLarge, null));
        });
        var exception = await Assert.ThrowsAsync<ControlClientException>(() => Client(pipeName).GetSnapshotAsync());
        await server;
        Assert.Equal(ControlClientError.ResponseTooLarge, exception.Error);
        Assert.Equal(1, connections);
    }

    [Fact]
    public async Task AuthorizationAndUnavailableFailuresAreSanitized()
    {
        var authorization = Client((_, _) => Task.FromException<Stream>(new UnauthorizedAccessException("sensitive")));
        var unavailable = Client((_, _) => Task.FromException<Stream>(new IOException("sensitive")));
        var denied = await Assert.ThrowsAsync<ControlClientException>(() => authorization.GetStatusAsync());
        var missing = await Assert.ThrowsAsync<ControlClientException>(() => unavailable.GetStatusAsync());
        Assert.Equal(ControlClientError.AuthorizationRequired, denied.Error);
        Assert.Equal(ControlClientError.ServiceUnavailable, missing.Error);
        Assert.DoesNotContain("sensitive", denied.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive", missing.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ControlCommand.Connect)]
    [InlineData(ControlCommand.Disconnect)]
    public async Task LostMutationResponseIsIndeterminateAndNeverRetried(ControlCommand command)
    {
        var pipeName = UniquePipeName();
        var connections = 0;
        var requests = 0;
        var server = RunServerAsync(pipeName, async pipe =>
        {
            connections++;
            await LengthPrefixedJsonProtocol.WriteGreetingAsync(pipe, new(ControlProtocolConstants.Version, InstanceId));
            var request = await LengthPrefixedJsonProtocol.ReadRequestAsync(pipe);
            Assert.Equal(command, request.Command);
            requests++;
        });
        var client = Client(pipeName);
        var exception = command == ControlCommand.Connect
            ? await Assert.ThrowsAsync<ControlClientException>(() => client.ConnectAsync())
            : await Assert.ThrowsAsync<ControlClientException>(() => client.DisconnectAsync());
        await server;
        Assert.Equal(ControlClientError.IndeterminateMutationOutcome, exception.Error);
        Assert.Equal(1, connections);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task CancellationBeforeConnectionIsNotIndeterminate()
    {
        var calls = 0;
        var client = Client((_, _) => { calls++; return Task.FromResult<Stream>(new MemoryStream()); });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var exception = await Assert.ThrowsAsync<ControlClientException>(() => client.ConnectAsync(cancellation.Token));
        Assert.Equal(ControlClientError.OperationCancelled, exception.Error);
        Assert.Equal(0, calls);
    }

    private static ControlClient Client(string pipeName)
        => new(pipeName, new FixedTimeProvider(Now), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), null);

    private static ControlClient Client(Func<string, CancellationToken, Task<Stream>> connector)
        => new("controlled-test", new FixedTimeProvider(Now), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), connector);

    private static async Task RunServerAsync(string pipeName, Func<NamedPipeServerStream, Task> action)
    {
        await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await server.WaitForConnectionAsync();
        await action(server);
    }

    private static ControlResponseEnvelope Success(ControlRequestEnvelope request, ControlResponseResult result)
        => new(ControlProtocolConstants.Version, request.ServiceInstanceId, request.RequestId, request.Command,
            ControlOutcome.Succeeded, ControlErrorCode.None, result);

    private static ControlNetworkSnapshot EmptySnapshot()
        => new(Now, "machine", Array.Empty<ControlAdapterState>(), null, null);

    private static async Task WriteRawFrameAsync(Stream stream, string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var prefix = new byte[ControlProtocolConstants.LengthPrefixSize];
        BinaryPrimitives.WriteInt32BigEndian(prefix, payload.Length);
        await stream.WriteAsync(prefix);
        await stream.WriteAsync(payload);
    }

    private static string UniquePipeName() => $"LibertyRoute.ControlClientTest.{Guid.NewGuid():N}";

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
