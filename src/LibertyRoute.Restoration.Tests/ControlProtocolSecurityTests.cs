using System.Buffers.Binary;
using System.Text;
using LibertyRoute.ControlProtocol;

namespace LibertyRoute.Restoration.Tests;

public sealed class ControlProtocolSecurityTests
{
    private static ControlRequestEnvelope Request(ControlCommand command = ControlCommand.Status) => new(
        ControlProtocolConstants.Version,
        Guid.NewGuid(),
        Guid.NewGuid(),
        DateTimeOffset.Parse("2026-01-02T03:04:05+00:00"),
        command,
        new ControlRequestPayload());

    private static ControlResponseEnvelope Response(
        ControlResponseResult? result = null,
        ControlCommand command = ControlCommand.Status,
        ControlOutcome outcome = ControlOutcome.Succeeded,
        ControlErrorCode error = ControlErrorCode.None) => new(
        ControlProtocolConstants.Version,
        Guid.NewGuid(),
        Guid.NewGuid(),
        command,
        outcome,
        error,
        result ?? (outcome == ControlOutcome.Succeeded ? new ControlStatusResult(ControlConnectionState.Disconnected) : null));

    [Fact]
    public async Task ValidRequestRoundTrips()
    {
        var expected = Request(ControlCommand.Connect);
        await using var stream = new MemoryStream();

        await LengthPrefixedJsonProtocol.WriteRequestAsync(stream, expected);
        stream.Position = 0;

        Assert.Equal(expected, await LengthPrefixedJsonProtocol.ReadRequestAsync(stream));
    }

    [Fact]
    public async Task ValidTypedResponseRoundTrips()
    {
        var expected = Response(new ControlStatusResult(ControlConnectionState.Connected));
        await using var stream = new MemoryStream();

        await LengthPrefixedJsonProtocol.WriteResponseAsync(stream, expected);
        stream.Position = 0;

        Assert.Equal(expected, await LengthPrefixedJsonProtocol.ReadResponseAsync(stream));
    }

    [Theory]
    [InlineData(ControlCommand.Status, "STATUS")]
    [InlineData(ControlCommand.Snapshot, "SNAPSHOT")]
    [InlineData(ControlCommand.Connect, "CONNECT")]
    [InlineData(ControlCommand.Disconnect, "DISCONNECT")]
    public async Task ExactlyFourCommandsHaveStableWireNames(ControlCommand command, string wireName)
    {
        Assert.Equal(4, Enum.GetValues<ControlCommand>().Length);
        await using var stream = new MemoryStream();
        await LengthPrefixedJsonProtocol.WriteRequestAsync(stream, Request(command));
        var json = Encoding.UTF8.GetString(stream.ToArray()[ControlProtocolConstants.LengthPrefixSize..]);
        Assert.Contains($"\"command\":\"{wireName}\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnsupportedVersionIsRejected()
    {
        var request = Request() with { ProtocolVersion = 1 };
        var exception = await Assert.ThrowsAsync<ControlProtocolException>(
            () => LengthPrefixedJsonProtocol.WriteRequestAsync(new MemoryStream(), request));
        Assert.Equal(ControlProtocolError.UnsupportedVersion, exception.Error);
    }

    [Fact]
    public async Task GreetingRoundTripsAndRejectsVersionOneOrEmptyInstance()
    {
        var expected = new ControlServerGreeting(ControlProtocolConstants.Version, Guid.NewGuid());
        await using var stream = new MemoryStream();
        await LengthPrefixedJsonProtocol.WriteGreetingAsync(stream, expected);
        stream.Position = 0;
        Assert.Equal(expected, await LengthPrefixedJsonProtocol.ReadGreetingAsync(stream));
        foreach (var invalid in new[] { expected with { ProtocolVersion = 1 }, expected with { ServiceInstanceId = Guid.Empty } })
            await Assert.ThrowsAsync<ControlProtocolException>(() => LengthPrefixedJsonProtocol.WriteGreetingAsync(new MemoryStream(), invalid));
    }

    [Fact]
    public async Task EveryCommandRequiresItsExactClosedResultType()
    {
        var snapshot = EmptySnapshot("machine");
        var valid = new (ControlCommand Command, ControlResponseResult Result)[]
        {
            (ControlCommand.Status, new ControlStatusResult(ControlConnectionState.Disconnected)),
            (ControlCommand.Snapshot, new ControlSnapshotResult(snapshot)),
            (ControlCommand.Connect, new ControlConnectResult(ControlConnectionState.Connected)),
            (ControlCommand.Disconnect, new ControlDisconnectResult(ControlConnectionState.Disconnected))
        };
        foreach (var item in valid)
        {
            await using var stream = new MemoryStream();
            var expected = Response(item.Result, item.Command);
            await LengthPrefixedJsonProtocol.WriteResponseAsync(stream, expected);
            stream.Position = 0;
            var actual = await LengthPrefixedJsonProtocol.ReadResponseAsync(stream);
            Assert.Equal(expected.ProtocolVersion, actual.ProtocolVersion);
            Assert.Equal(expected.ServiceInstanceId, actual.ServiceInstanceId);
            Assert.Equal(expected.RequestId, actual.RequestId);
            Assert.Equal(expected.Command, actual.Command);
            Assert.Equal(expected.Outcome, actual.Outcome);
            Assert.Equal(expected.ErrorCode, actual.ErrorCode);
            Assert.IsType(item.Result.GetType(), actual.Result);
            if (actual.Result is ControlSnapshotResult actualSnapshot)
            {
                Assert.Equal(snapshot.CapturedAtUtc, actualSnapshot.Snapshot.CapturedAtUtc);
                Assert.Equal(snapshot.MachineName, actualSnapshot.Snapshot.MachineName);
                Assert.Empty(actualSnapshot.Snapshot.Adapters);
                Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<ControlRouteState>>(actualSnapshot.Snapshot.Routes));
                Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<ControlDnsInterfaceState>>(actualSnapshot.Snapshot.DnsInterfaces));
            }
            else
            {
                Assert.Equal(item.Result, actual.Result);
            }
        }
        await Assert.ThrowsAsync<ControlProtocolException>(() => LengthPrefixedJsonProtocol.WriteResponseAsync(
            new MemoryStream(), Response(new ControlSnapshotResult(snapshot), ControlCommand.Status)));
    }

    [Fact]
    public async Task FailedResponseRequiresNullResultAndResponseTooLargeIsStableAndSmall()
    {
        var failure = Response(null, ControlCommand.Snapshot, ControlOutcome.Failed, ControlErrorCode.ResponseTooLarge);
        await using var stream = new MemoryStream();
        await LengthPrefixedJsonProtocol.WriteResponseAsync(stream, failure);
        Assert.InRange(stream.Length, 1, ControlProtocolConstants.MaximumResponseSize + ControlProtocolConstants.LengthPrefixSize);
        await Assert.ThrowsAsync<ControlProtocolException>(() => LengthPrefixedJsonProtocol.WriteResponseAsync(
            new MemoryStream(), failure with { Result = new ControlSnapshotResult(EmptySnapshot("machine")) }));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(2147483647u)]
    [InlineData(2147483648u)]
    [InlineData(uint.MaxValue)]
    public async Task RouteMetricPreservesFullUnsignedRange(uint metric)
    {
        var snapshot = EmptySnapshot("machine") with
        {
            Routes = new[] { new ControlRouteState("destination", "next-hop", 7, metric, "family") }
        };
        var actual = await RoundTripSnapshotAsync(snapshot);
        Assert.Equal(metric, Assert.Single(actual.Routes!).Metric);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("\"7\"")]
    public async Task NegativeOrWrongTypeRouteMetricJsonIsRejected(string replacement)
    {
        var snapshot = EmptySnapshot("machine") with
        {
            Routes = new[] { new ControlRouteState("destination", "next-hop", 7, 1, "family") }
        };
        var json = (await ResponseJsonAsync(Response(new ControlSnapshotResult(snapshot), ControlCommand.Snapshot)))
            .ReplacePropertyValue("metric", replacement);
        Assert.Equal(ControlProtocolError.MalformedJson, (await ReadResponseFailureAsync(json)).Error);
    }

    [Fact]
    public async Task NullableSnapshotAndDnsCollectionsPreserveNullVersusEmpty()
    {
        var nullDns = new ControlDnsInterfaceState("id", "name", true, Array.Empty<string>(),
            null, null, ControlDnsConfigurationSource.Unknown, ControlDnsConfigurationSource.Unknown,
            null, null, null, null);
        var emptyDns = new ControlDnsInterfaceState("id", "name", true, Array.Empty<string>(),
            Array.Empty<string>(), Array.Empty<string>(), ControlDnsConfigurationSource.Unknown, ControlDnsConfigurationSource.Unknown,
            Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());

        var nullSnapshot = await RoundTripSnapshotAsync(new ControlNetworkSnapshot(
            DateTimeOffset.Parse("2026-01-02T03:04:05+00:00"), "machine", Array.Empty<ControlAdapterState>(), null, null));
        Assert.Null(nullSnapshot.Routes);
        Assert.Null(nullSnapshot.DnsInterfaces);

        var emptySnapshot = await RoundTripSnapshotAsync(new ControlNetworkSnapshot(
            DateTimeOffset.Parse("2026-01-02T03:04:05+00:00"), "machine", Array.Empty<ControlAdapterState>(),
            Array.Empty<ControlRouteState>(), new[] { nullDns, emptyDns }));
        Assert.Empty(emptySnapshot.Routes!);
        var dns = emptySnapshot.DnsInterfaces!;
        Assert.All(new[] { dns[0].IPv4DnsServers, dns[0].IPv6DnsServers, dns[0].IPv4StaticDnsServers,
            dns[0].IPv4DhcpDnsServers, dns[0].IPv6StaticDnsServers, dns[0].IPv6DhcpDnsServers }, Assert.Null);
        Assert.All(new[] { dns[1].IPv4DnsServers, dns[1].IPv6DnsServers, dns[1].IPv4StaticDnsServers,
            dns[1].IPv4DhcpDnsServers, dns[1].IPv6StaticDnsServers, dns[1].IPv6DhcpDnsServers }, value => Assert.Empty(value!));
    }

    [Theory]
    [InlineData("routes")]
    [InlineData("dnsInterfaces")]
    [InlineData("iPv4DnsServers")]
    [InlineData("iPv6DnsServers")]
    [InlineData("iPv4StaticDnsServers")]
    [InlineData("iPv4DhcpDnsServers")]
    [InlineData("iPv6StaticDnsServers")]
    [InlineData("iPv6DhcpDnsServers")]
    public async Task NullableCollectionsRemainRequiredAndRejectWrongJsonTypes(string property)
    {
        var dns = new ControlDnsInterfaceState("id", "name", true, Array.Empty<string>(),
            Array.Empty<string>(), Array.Empty<string>(), ControlDnsConfigurationSource.Unknown, ControlDnsConfigurationSource.Unknown,
            Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
        var snapshot = EmptySnapshot("machine") with { DnsInterfaces = new[] { dns } };
        var json = await ResponseJsonAsync(Response(new ControlSnapshotResult(snapshot), ControlCommand.Snapshot));
        Assert.Equal(ControlProtocolError.MalformedJson, (await ReadResponseFailureAsync(RemoveProperty(json, property))).Error);
        Assert.Equal(ControlProtocolError.MalformedJson, (await ReadResponseFailureAsync(json.ReplacePropertyValue(property, "{}"))).Error);
    }

    [Theory]
    [InlineData("FUTURE")]
    [InlineData("status")]
    [InlineData("0")]
    public async Task UnknownOrInexactCommandIsRejected(string command)
    {
        var json = ValidRequestJson().Replace("\"STATUS\"", $"\"{command}\"", StringComparison.Ordinal);
        var exception = await ReadRequestFailureAsync(json);
        Assert.Equal(ControlProtocolError.UnknownCommand, exception.Error);
    }

    [Theory]
    [InlineData("serviceInstanceId")]
    [InlineData("requestId")]
    public async Task EmptyIdentifiersAreRejected(string property)
    {
        var json = ValidRequestJson().ReplacePropertyValue(property, "\"00000000-0000-0000-0000-000000000000\"");
        var exception = await ReadRequestFailureAsync(json);
        Assert.Equal(ControlProtocolError.InvalidContract, exception.Error);
    }

    [Theory]
    [InlineData("protocolVersion")]
    [InlineData("serviceInstanceId")]
    [InlineData("requestId")]
    [InlineData("sentAtUtc")]
    [InlineData("command")]
    [InlineData("payload")]
    public async Task MissingRequiredRequestMemberIsRejected(string property)
    {
        var json = RemoveProperty(ValidRequestJson(), property);
        await Assert.ThrowsAsync<ControlProtocolException>(() => ReadRequestAsync(json));
    }

    [Fact]
    public async Task WrongMemberTypeIsRejected()
    {
        var json = ValidRequestJson().ReplacePropertyValue("requestId", "17");
        var exception = await ReadRequestFailureAsync(json);
        Assert.Equal(ControlProtocolError.MalformedJson, exception.Error);
    }

    [Fact]
    public async Task ExtraAndDuplicateMembersAreRejected()
    {
        var extra = ValidRequestJson().Replace("\"payload\":{}", "\"payload\":{},\"extra\":true", StringComparison.Ordinal);
        var duplicate = ValidRequestJson().Replace("\"payload\":{}", "\"payload\":{},\"requestId\":\"11111111-1111-1111-1111-111111111111\"", StringComparison.Ordinal);

        Assert.Equal(ControlProtocolError.MalformedJson, (await ReadRequestFailureAsync(extra)).Error);
        Assert.Equal(ControlProtocolError.MalformedJson, (await ReadRequestFailureAsync(duplicate)).Error);
    }

    [Fact]
    public async Task EmptyPayloadRejectsArbitraryInstructionMembers()
    {
        var json = ValidRequestJson().Replace("\"payload\":{}", "\"payload\":{\"route\":\"0.0.0.0/0\"}", StringComparison.Ordinal);
        Assert.Equal(ControlProtocolError.MalformedJson, (await ReadRequestFailureAsync(json)).Error);
        Assert.Empty(typeof(ControlRequestPayload).GetProperties());
    }

    [Fact]
    public async Task MalformedJsonIsRejected()
    {
        Assert.Equal(ControlProtocolError.MalformedJson, (await ReadRequestFailureAsync("{")).Error);
    }

    [Fact]
    public async Task PrefixIsFourByteBigEndianAndOneFrameIsReadAtATime()
    {
        await using var first = new MemoryStream();
        await LengthPrefixedJsonProtocol.WriteRequestAsync(first, Request());
        var firstBytes = first.ToArray();
        Assert.Equal(firstBytes.Length - 4, BinaryPrimitives.ReadInt32BigEndian(firstBytes.AsSpan(0, 4)));

        await using var second = new MemoryStream();
        await LengthPrefixedJsonProtocol.WriteRequestAsync(second, Request(ControlCommand.Snapshot));
        var combined = firstBytes.Concat(second.ToArray()).ToArray();
        await using var stream = new MemoryStream(combined);

        Assert.Equal(ControlCommand.Status, (await LengthPrefixedJsonProtocol.ReadRequestAsync(stream)).Command);
        Assert.Equal(firstBytes.Length, stream.Position);
        Assert.Equal(ControlCommand.Snapshot, (await LengthPrefixedJsonProtocol.ReadRequestAsync(stream)).Command);
    }

    [Theory]
    [InlineData(0, ControlProtocolError.ZeroLengthFrame)]
    [InlineData(-1, ControlProtocolError.InvalidLengthPrefix)]
    [InlineData(ControlProtocolConstants.MaximumRequestSize + 1, ControlProtocolError.FrameTooLarge)]
    public async Task InvalidRequestLengthsAreRejectedBeforePayloadRead(int length, ControlProtocolError expected)
    {
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(prefix, length);
        var exception = await Assert.ThrowsAsync<ControlProtocolException>(
            () => LengthPrefixedJsonProtocol.ReadRequestAsync(new MemoryStream(prefix)));
        Assert.Equal(expected, exception.Error);
    }

    [Fact]
    public async Task OversizedResponseWriteProducesNoPartialFrame()
    {
        await using var stream = new MemoryStream();
        var exception = await Assert.ThrowsAsync<ControlProtocolException>(() =>
            LengthPrefixedJsonProtocol.WriteResponseAsync(
                stream,
                SnapshotResponse(new string('x', ControlProtocolConstants.MaximumResponseSize))));

        Assert.Equal(ControlProtocolError.FrameTooLarge, exception.Error);
        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public async Task SnapshotResponseHonorsExactOneMiBBoundaryWithoutTruncationOrMultipleFrames()
    {
        await using var baseline = new MemoryStream();
        await LengthPrefixedJsonProtocol.WriteResponseAsync(baseline, SnapshotResponse(string.Empty));
        var overhead = checked((int)baseline.Length - ControlProtocolConstants.LengthPrefixSize);
        var exactMachineName = new string('x', ControlProtocolConstants.MaximumResponseSize - overhead);
        await using var exact = new MemoryStream();
        await LengthPrefixedJsonProtocol.WriteResponseAsync(exact, SnapshotResponse(exactMachineName));
        Assert.Equal(ControlProtocolConstants.MaximumResponseSize + ControlProtocolConstants.LengthPrefixSize, exact.Length);
        Assert.Equal(ControlProtocolConstants.MaximumResponseSize,
            BinaryPrimitives.ReadInt32BigEndian(exact.ToArray().AsSpan(0, ControlProtocolConstants.LengthPrefixSize)));
        await using var oversized = new MemoryStream();
        var exception = await Assert.ThrowsAsync<ControlProtocolException>(() =>
            LengthPrefixedJsonProtocol.WriteResponseAsync(oversized, SnapshotResponse(exactMachineName + "x")));
        Assert.Equal(ControlProtocolError.FrameTooLarge, exception.Error);
        Assert.Equal(0, oversized.Length);
    }

    [Fact]
    public async Task InvalidSerializableEnvelopeReturnsStableErrorWithoutWriting()
    {
        await using var stream = new MemoryStream();
        var invalid = Response() with { Outcome = (ControlOutcome)int.MaxValue };

        var exception = await Assert.ThrowsAsync<ControlProtocolException>(() =>
            LengthPrefixedJsonProtocol.WriteResponseAsync(stream, invalid));

        Assert.IsNotType<System.Text.Json.JsonException>(exception);
        Assert.Equal(ControlProtocolError.InvalidContract, exception.Error);
        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public async Task TruncatedPrefixAndPayloadAreRejected()
    {
        var prefixException = await Assert.ThrowsAsync<ControlProtocolException>(() =>
            LengthPrefixedJsonProtocol.ReadRequestAsync(new MemoryStream(new byte[3])));

        var frame = new byte[6];
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(0, 4), 10);
        var payloadException = await Assert.ThrowsAsync<ControlProtocolException>(() =>
            LengthPrefixedJsonProtocol.ReadRequestAsync(new MemoryStream(frame)));

        Assert.Equal(ControlProtocolError.TruncatedFrame, prefixException.Error);
        Assert.Equal(ControlProtocolError.TruncatedFrame, payloadException.Error);
    }

    [Fact]
    public async Task PartialStreamReadsAreCombinedExactly()
    {
        await using var framed = new MemoryStream();
        var expected = Request(ControlCommand.Disconnect);
        await LengthPrefixedJsonProtocol.WriteRequestAsync(framed, expected);
        await using var partial = new PartialReadStream(framed.ToArray());

        Assert.Equal(expected, await LengthPrefixedJsonProtocol.ReadRequestAsync(partial));
    }

    [Fact]
    public async Task InvalidUtf8IsRejected()
    {
        var frame = new byte[6];
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(0, 4), 2);
        frame[4] = 0xC3;
        frame[5] = 0x28;

        var exception = await Assert.ThrowsAsync<ControlProtocolException>(() =>
            LengthPrefixedJsonProtocol.ReadRequestAsync(new MemoryStream(frame)));
        Assert.Equal(ControlProtocolError.InvalidUtf8, exception.Error);
    }

    [Fact]
    public void ProtocolProjectIsAPlatformNeutralDependencyLeafAndProductionServiceIsV2Only()
    {
        var root = FindRepositoryRoot();
        var projectDirectory = Path.Combine(root, "src", "LibertyRoute.ControlProtocol");
        var project = File.ReadAllText(Path.Combine(projectDirectory, "LibertyRoute.ControlProtocol.csproj"));
        var protocolSource = string.Join('\n', Directory.GetFiles(projectDirectory, "*.cs").Select(File.ReadAllText));
        var serviceProject = File.ReadAllText(Path.Combine(root, "src", "LibertyRoute.Service", "LibertyRoute.Service.csproj"));
        var desktopProject = File.ReadAllText(Path.Combine(root, "src", "LibertyRoute.Desktop", "LibertyRoute.Desktop.csproj"));
        var worker = File.ReadAllText(Path.Combine(root, "src", "LibertyRoute.Service", "LibertyRouteWorker.cs"));
        var program = File.ReadAllText(Path.Combine(root, "src", "LibertyRoute.Service", "Program.cs"));
        var registration = File.ReadAllText(Path.Combine(root, "src", "LibertyRoute.Service", "ServiceRegistration.cs"));
        var desktop = File.ReadAllText(Path.Combine(root, "src", "LibertyRoute.Desktop", "MainWindow.xaml.cs"));

        Assert.Contains("<TargetFramework>net10.0</TargetFramework>", project, StringComparison.Ordinal);
        Assert.DoesNotContain("ProjectReference", project, StringComparison.Ordinal);
        Assert.Contains("LibertyRoute.ControlProtocol", serviceProject, StringComparison.Ordinal);
        Assert.DoesNotContain("LibertyRoute.ControlProtocol", desktopProject, StringComparison.Ordinal);
        Assert.DoesNotContain("LibertyRoute.Restoration.Windows", serviceProject, StringComparison.Ordinal);
        Assert.Contains("LibertyRoute.Network.v2", worker, StringComparison.Ordinal);
        Assert.DoesNotContain("LibertyRoute.Network.v1", worker, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadLineAsync", worker, StringComparison.Ordinal);
        Assert.DoesNotContain("WriteLineAsync", worker, StringComparison.Ordinal);
        Assert.Contains("WriteLineAsync(command)", desktop, StringComparison.Ordinal);
        Assert.Contains("SecureControlConnectionHandler", worker, StringComparison.Ordinal);
        Assert.DoesNotContain("SecureControl", program, StringComparison.Ordinal);
        Assert.Contains("SecureControlConnectionHandler", registration, StringComparison.Ordinal);
        Assert.DoesNotContain("Recovery", string.Join(',', Enum.GetNames<ControlCommand>()), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Restoration", string.Join(',', Enum.GetNames<ControlCommand>()), StringComparison.OrdinalIgnoreCase);

        var privilegedTypeTerms = new[]
        {
            "Provider", "Native", "Restoration",
            "Approval", "Grant", "Capability", "OwnedNetworkChange"
        };
        var protocolTypes = typeof(ControlRequestEnvelope).Assembly.GetTypes();
        Assert.All(protocolTypes, type =>
            Assert.All(privilegedTypeTerms, term =>
                Assert.DoesNotContain(term, type.Name, StringComparison.OrdinalIgnoreCase)));

        var forbidden = new[]
        {
            "NamedPipeServerStream", "NamedPipeClientStream", "PipeSecurity", "WindowsIdentity",
            "SecurityIdentifier", "RunAsClient", "RestorationExecutionCapability",
            "ControlledRestorationActivationGrant", "ControlledRecoveryApproval",
            "ControlledApprovedRecoveryExecution", "RouteMutationProviderFactory",
            "WindowsRouteMutationNative", "IRouteMutationNative", "RecordRevertedAsync",
            "IServiceProvider", "IConfiguration", "Environment.GetEnvironmentVariable",
            "Process.Start", "powershell", "pwsh", "netsh", "Set-Net", "New-Net",
            "Remove-Net", "Add-Net", "Registry", "private key", "password", "credential", "secret"
        };
        Assert.All(forbidden, term => Assert.DoesNotContain(term, protocolSource, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<ControlProtocolException> ReadRequestFailureAsync(string json)
        => await Assert.ThrowsAsync<ControlProtocolException>(() => ReadRequestAsync(json));

    private static Task<ControlRequestEnvelope> ReadRequestAsync(string json)
        => LengthPrefixedJsonProtocol.ReadRequestAsync(Frame(json));

    private static Task<ControlResponseEnvelope> ReadResponseAsync(string json)
        => LengthPrefixedJsonProtocol.ReadResponseAsync(Frame(json));

    private static async Task<ControlProtocolException> ReadResponseFailureAsync(string json)
        => await Assert.ThrowsAsync<ControlProtocolException>(() => ReadResponseAsync(json));

    private static async Task<string> ResponseJsonAsync(ControlResponseEnvelope response)
    {
        await using var stream = new MemoryStream();
        await LengthPrefixedJsonProtocol.WriteResponseAsync(stream, response);
        return Encoding.UTF8.GetString(stream.ToArray()[ControlProtocolConstants.LengthPrefixSize..]);
    }

    private static async Task<ControlNetworkSnapshot> RoundTripSnapshotAsync(ControlNetworkSnapshot snapshot)
    {
        await using var stream = new MemoryStream();
        await LengthPrefixedJsonProtocol.WriteResponseAsync(stream, Response(new ControlSnapshotResult(snapshot), ControlCommand.Snapshot));
        stream.Position = 0;
        return Assert.IsType<ControlSnapshotResult>((await LengthPrefixedJsonProtocol.ReadResponseAsync(stream)).Result).Snapshot;
    }

    private static MemoryStream Frame(string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var frame = new byte[payload.Length + 4];
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(0, 4), payload.Length);
        payload.CopyTo(frame.AsSpan(4));
        return new MemoryStream(frame);
    }

    private static string ValidRequestJson()
        => "{\"protocolVersion\":2,\"serviceInstanceId\":\"11111111-1111-1111-1111-111111111111\",\"requestId\":\"22222222-2222-2222-2222-222222222222\",\"sentAtUtc\":\"2026-01-02T03:04:05+00:00\",\"command\":\"STATUS\",\"payload\":{}}";

    private static ControlResponseEnvelope SnapshotResponse(string machineName)
        => Response(new ControlSnapshotResult(EmptySnapshot(machineName)), ControlCommand.Snapshot);

    private static ControlNetworkSnapshot EmptySnapshot(string machineName)
        => new(DateTimeOffset.Parse("2026-01-02T03:04:05+00:00"), machineName,
            Array.Empty<ControlAdapterState>(), Array.Empty<ControlRouteState>(), Array.Empty<ControlDnsInterfaceState>());

    private static string RemoveProperty(string json, string property)
    {
        var marker = $"\"{property}\":";
        var start = json.IndexOf(marker, StringComparison.Ordinal);
        var end = start + marker.Length;
        var depth = 0;
        var quoted = false;
        while (end < json.Length)
        {
            var character = json[end];
            if (character == '"' && (end == 0 || json[end - 1] != '\\')) quoted = !quoted;
            if (!quoted)
            {
                if (character is '{' or '[') depth++;
                if (character is '}' or ']')
                {
                    if (depth == 0) break;
                    depth--;
                }
                if (character == ',' && depth == 0) break;
            }
            end++;
        }
        if (end < json.Length && json[end] == ',') end++;
        else if (start > 1 && json[start - 1] == ',') start--;
        return json.Remove(start, end - start);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "LibertyRoute.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    private sealed class PartialReadStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
    }
}

internal static class JsonTestExtensions
{
    public static string ReplacePropertyValue(this string json, string property, string replacement)
    {
        var marker = $"\"{property}\":";
        var start = json.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var end = start;
        if (json[start] == '"')
        {
            end++;
            while (json[end] != '"' || json[end - 1] == '\\') end++;
            end++;
        }
        else
        {
            while (end < json.Length && json[end] is not ',' and not '}') end++;
        }
        return json[..start] + replacement + json[end..];
    }
}
