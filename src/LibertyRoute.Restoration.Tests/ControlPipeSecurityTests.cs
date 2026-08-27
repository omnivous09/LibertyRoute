using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using LibertyRoute.ControlProtocol;
using LibertyRoute.Core;
using LibertyRoute.Engine;
using LibertyRoute.Networking;
using LibertyRoute.Recovery;
using LibertyRoute.Service;
using Microsoft.Extensions.Logging.Abstractions;

namespace LibertyRoute.Restoration.Tests;

public sealed class ControlPipeSecurityTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-02-03T04:05:06+00:00");
    private static readonly Guid InstanceId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void DaclIsProtectedExplicitAndNarrow()
    {
        using var current = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var serviceSid = current.User!;
        var security = new SecureControlPipeFactory(serviceSid).CreateSecurity();
        var rules = security.GetAccessRules(true, false, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .ToArray();

        Assert.True(security.AreAccessRulesProtected);
        Assert.All(rules, rule => Assert.False(rule.IsInherited));
        AssertRule(rules, WellKnownSidType.NetworkSid, AccessControlType.Deny);
        AssertRule(rules, WellKnownSidType.LocalSystemSid, AccessControlType.Allow);
        AssertRule(rules, WellKnownSidType.BuiltinAdministratorsSid, AccessControlType.Allow);
        Assert.Contains(rules, rule =>
            ((SecurityIdentifier)rule.IdentityReference).Equals(serviceSid) &&
            rule.AccessControlType == AccessControlType.Allow &&
            HasRequiredDuplexRights(rule.PipeAccessRights));

        AssertNoRule(rules, WellKnownSidType.WorldSid);
        AssertNoRule(rules, WellKnownSidType.AuthenticatedUserSid);
        AssertNoRule(rules, WellKnownSidType.BuiltinUsersSid);
        Assert.All(rules, rule =>
        {
            Assert.True(HasRequiredDuplexRights(rule.PipeAccessRights));
            Assert.NotEqual(PipeAccessRights.FullControl, rule.PipeAccessRights);
            Assert.Equal((PipeAccessRights)0, rule.PipeAccessRights & PipeAccessRights.ChangePermissions);
            Assert.Equal((PipeAccessRights)0, rule.PipeAccessRights & PipeAccessRights.TakeOwnership);
            Assert.Equal((PipeAccessRights)0, rule.PipeAccessRights & PipeAccessRights.Delete);
            Assert.Equal((PipeAccessRights)0, rule.PipeAccessRights & PipeAccessRights.AccessSystemSecurity);
        });
    }

    [Fact]
    public async Task FirstInstanceFailsClosedAndCallerIsCapturedFromImpersonatedToken()
    {
        using var current = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var pipeName = $"LibertyRoute.SecurityTest.{Guid.NewGuid():N}";
        var factory = new SecureControlPipeFactory(current.User!);
        await using var server = factory.Create(pipeName);
        NamedPipeServerStream? secondServer = null;
        var secondCreationFailure = Record.Exception(() => secondServer = factory.Create(pipeName));
        Assert.Null(secondServer);
        Assert.True(
            secondCreationFailure is IOException or UnauthorizedAccessException,
            $"Unexpected first-instance failure: {secondCreationFailure?.GetType().FullName ?? "(none)"}");

        await using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Impersonation);
        var accept = server.WaitForConnectionAsync();
        await client.ConnectAsync();
        await accept;

        var caller = WindowsControlCallerIdentityCapture.Capture(server);
        var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        using var principalIdentity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(principalIdentity);
        var adminSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var networkSid = new SecurityIdentifier(WellKnownSidType.NetworkSid, null);

        Assert.Equal(current.User!.Value, caller.UserSid);
        Assert.Equal(current.User.Equals(systemSid), caller.IsLocalSystem);
        Assert.Equal(principal.IsInRole(adminSid), caller.IsBuiltinAdministrator);
        Assert.Equal(principal.IsInRole(networkSid), caller.HasNetworkLogonSid);
        Assert.DoesNotContain(
            typeof(ControlCallerIdentity).GetProperties(),
            property => typeof(WindowsIdentity).IsAssignableFrom(property.PropertyType) ||
                        property.PropertyType == typeof(IntPtr));
    }

    [Theory]
    [InlineData(false, false, false, false, "Unauthenticated")]
    [InlineData(true, true, true, false, "NetworkLogonDenied")]
    [InlineData(true, false, false, false, "Forbidden")]
    [InlineData(true, true, false, false, "Authorized")]
    [InlineData(true, false, false, true, "Authorized")]
    public void PrincipalPolicyIsFailClosed(
        bool authenticated,
        bool administrator,
        bool network,
        bool localSystem,
        string expected)
    {
        var caller = Caller("S-1-5-21-1", authenticated, administrator, network, localSystem);
        var policy = new ControlCommandAuthorization();
        Assert.Equal(expected, policy.AuthorizePrincipal(caller).ToString());
        foreach (var command in Enum.GetValues<ControlCommand>())
            Assert.Equal(expected, policy.AuthorizeCommand(caller, command).ToString());
    }

    [Fact]
    public void ServiceInstanceIsNonemptyTransientAndDeterministicWhenInjected()
    {
        Assert.Throws<ArgumentException>(() => new ControlServiceInstance(Guid.Empty));
        Assert.Equal(InstanceId, new ControlServiceInstance(InstanceId).Id);
        var first = ControlServiceInstance.CreateTransient().Id;
        var second = ControlServiceInstance.CreateTransient().Id;
        Assert.NotEqual(Guid.Empty, first);
        Assert.NotEqual(Guid.Empty, second);
        Assert.NotEqual(first, second);
    }

    [Theory]
    [InlineData(-120, "Current")]
    [InlineData(15, "Current")]
    [InlineData(-121, "Stale")]
    [InlineData(16, "TooFarInFuture")]
    public void FreshnessBoundariesAreExplicit(int seconds, string expected)
    {
        var guard = new ControlRequestReplayGuard(new MutableTimeProvider(Now));
        Assert.Equal(expected, guard.EvaluateFreshness(Now.AddSeconds(seconds)).ToString());
    }

    [Fact]
    public async Task ReplayReservationClassifiesDuplicateConflictAndDifferentCaller()
    {
        var guard = new ControlRequestReplayGuard(new MutableTimeProvider(Now));
        var firstCaller = Caller("S-1-5-21-1");
        var secondCaller = Caller("S-1-5-21-2");
        var request = Request();

        Assert.Equal(ControlReplayReservationResult.Reserved, await Reserve(guard, firstCaller, request));
        Assert.Equal(ControlReplayReservationResult.Duplicate, await Reserve(guard, firstCaller, request));
        Assert.Equal(
            ControlReplayReservationResult.Conflict,
            await Reserve(guard, firstCaller, request with { Command = ControlCommand.Snapshot }));
        Assert.Equal(ControlReplayReservationResult.Reserved, await Reserve(guard, secondCaller, request));
    }

    [Fact]
    public async Task ProductionDispatcherMapsStatusSnapshotConnectAndDisconnectExactly()
    {
        var snapshot = DiagnosticSnapshot();
        var network = new FakeNetworkStateManager(snapshot);
        var journal = new FakeTransactionJournal();
        var engine = new FakeConnectionEngine();
        var controller = new ConnectionController(network, journal, engine);
        var dispatcher = new ControlCommandDispatcher(controller);
        var caller = Caller("S-1-5-21-1");

        var status = await dispatcher.DispatchAsync(caller, Request(), CancellationToken.None);
        Assert.Equal(ControlConnectionState.Disconnected, Assert.IsType<ControlStatusResult>(status.Result).State);

        var snapshotResult = await dispatcher.DispatchAsync(caller, Request() with { Command = ControlCommand.Snapshot }, CancellationToken.None);
        var mapped = Assert.IsType<ControlSnapshotResult>(snapshotResult.Result).Snapshot;
        Assert.Equal(1, network.Captures);
        Assert.Equal(Now, mapped.CapturedAtUtc);
        Assert.Equal("machine", mapped.MachineName);
        var adapter = Assert.Single(mapped.Adapters);
        Assert.Equal(("id", "name", "description", "Ethernet", "Up"),
            (adapter.Id, adapter.Name, adapter.Description, adapter.NetworkInterfaceType, adapter.OperationalStatus));
        Assert.Equal(new[] { "10.0.0.2" }, adapter.UnicastAddresses);
        Assert.Equal(new[] { "10.0.0.1" }, adapter.Gateways);
        Assert.Equal(new[] { "1.1.1.1" }, adapter.DnsServers);
        var route = Assert.Single(mapped.Routes!);
        Assert.Equal(("0.0.0.0/0", "10.0.0.1", 7, uint.MaxValue, "InterNetwork"),
            (route.Destination, route.NextHop, route.InterfaceIndex, route.Metric, route.AddressFamily));
        var dns = Assert.Single(mapped.DnsInterfaces!);
        Assert.Equal(("id", "name", true), (dns.InterfaceId, dns.InterfaceName, dns.IsUp));
        Assert.Equal(new[] { "1.1.1.1" }, dns.DnsServers);
        Assert.Null(dns.IPv4DnsServers);
        Assert.Empty(dns.IPv6DnsServers!);
        Assert.Equal(ControlDnsConfigurationSource.Unknown, dns.IPv4ConfigurationSource);
        Assert.Equal(ControlDnsConfigurationSource.Unknown, dns.IPv6ConfigurationSource);
        Assert.Null(dns.IPv4StaticDnsServers);
        Assert.Null(dns.IPv4DhcpDnsServers);
        Assert.Null(dns.IPv6StaticDnsServers);
        Assert.Null(dns.IPv6DhcpDnsServers);

        var connect = await dispatcher.DispatchAsync(caller, Request() with { Command = ControlCommand.Connect }, CancellationToken.None);
        Assert.Equal(ControlConnectionState.SnapshotCommitted, Assert.IsType<ControlConnectResult>(connect.Result).State);
        Assert.Equal(2, network.Captures);
        Assert.Equal(1, journal.Writes);

        var disconnect = await dispatcher.DispatchAsync(caller, Request() with { Command = ControlCommand.Disconnect }, CancellationToken.None);
        Assert.Equal(ControlConnectionState.Disconnected, Assert.IsType<ControlDisconnectResult>(disconnect.Result).State);
        Assert.Equal(1, engine.Stops);
        Assert.Equal(1, network.Verifications);
        Assert.Equal(1, journal.Clears);
    }

    [Fact]
    public void ProductionDispatcherStateAndDnsMappingsAreExhaustive()
    {
        foreach (var state in Enum.GetValues<ConnectionState>())
            Assert.Equal(state.ToString(), ControlCommandDispatcher.MapState(state).ToString());
        Assert.Throws<InvalidOperationException>(() => ControlCommandDispatcher.MapState((ConnectionState)int.MaxValue));
        foreach (var source in Enum.GetValues<DnsConfigurationSource>())
            Assert.Equal(source.ToString(), ControlCommandDispatcher.MapDnsSource(source).ToString());
        Assert.Throws<InvalidOperationException>(() => ControlCommandDispatcher.MapDnsSource((DnsConfigurationSource)int.MaxValue));
    }

    [Fact]
    public void ProductionSnapshotMappingRejectsCorruptRequiredRuntimeNulls()
    {
        var invalidMachine = DiagnosticSnapshot() with { MachineName = null! };
        Assert.Throws<InvalidOperationException>(() => ControlCommandDispatcher.MapSnapshot(invalidMachine));

        var invalidElements = DiagnosticSnapshot() with { Adapters = new AdapterState[] { null! } };
        Assert.Throws<InvalidOperationException>(() => ControlCommandDispatcher.MapSnapshot(invalidElements));
    }

    [Fact]
    public async Task ConcurrentReplayReservationHasExactlyOneWinner()
    {
        var guard = new ControlRequestReplayGuard(new MutableTimeProvider(Now));
        var caller = Caller("S-1-5-21-1");
        var request = Request();
        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Reserve(guard, caller, request)));

        Assert.Equal(1, results.Count(result => result == ControlReplayReservationResult.Reserved));
        Assert.Equal(31, results.Count(result => result == ControlReplayReservationResult.Duplicate));
    }

    [Fact]
    public async Task ReplayCacheFailsClosedAtCapacityAndReclaimsExpiredEntries()
    {
        var time = new MutableTimeProvider(Now);
        var guard = new ControlRequestReplayGuard(time, capacity: 2);
        var caller = Caller("S-1-5-21-1");

        Assert.Equal(ControlReplayReservationResult.Reserved, await Reserve(guard, caller, Request()));
        Assert.Equal(ControlReplayReservationResult.Reserved, await Reserve(guard, caller, Request()));
        Assert.Equal(ControlReplayReservationResult.CapacityExceeded, await Reserve(guard, caller, Request()));

        time.UtcNow += ControlRequestReplayGuard.Retention + TimeSpan.FromTicks(1);
        Assert.Equal(ControlReplayReservationResult.Reserved, await Reserve(guard, caller, Request(time.UtcNow)));
    }

    [Fact]
    public async Task UnauthorizedCallerIsRejectedBeforeStreamReadOrDispatch()
    {
        var dispatcher = new FakeDispatcher();
        var handler = Handler(dispatcher);
        await handler.HandleAuthenticatedForTestsAsync(
            new ThrowOnReadStream(),
            Caller("S-1-5-21-1", administrator: false),
            CancellationToken.None);
        Assert.Equal(0, dispatcher.Calls);
    }

    [Fact]
    public async Task MalformedTruncatedRequestDoesNotDispatch()
    {
        var dispatcher = new FakeDispatcher();
        var handler = Handler(dispatcher);
        await handler.HandleAuthenticatedForTestsAsync(
            new DuplexStream(new byte[3]),
            Caller("S-1-5-21-1"),
            CancellationToken.None);
        Assert.Equal(0, dispatcher.Calls);
    }

    [Theory]
    [InlineData("instance", ControlErrorCode.WrongServiceInstance)]
    [InlineData("stale", ControlErrorCode.StaleRequest)]
    public async Task InvalidInstanceOrFreshnessReturnsSanitizedFailureWithoutDispatch(
        string change,
        ControlErrorCode expected)
    {
        var dispatcher = new FakeDispatcher();
        var handler = Handler(dispatcher);
        var request = change == "instance"
            ? Request() with { ServiceInstanceId = Guid.NewGuid() }
            : Request(Now - ControlRequestReplayGuard.MaximumAge - TimeSpan.FromTicks(1));

        var response = await InvokeAsync(handler, request, Caller("S-1-5-21-1"));
        Assert.Equal(expected, response!.ErrorCode);
        Assert.Null(response.Result);
        Assert.Equal(0, dispatcher.Calls);
    }

    [Fact]
    public async Task ValidRequestDispatchesExactlyOnceAndDuplicateDoesNotRedispatch()
    {
        var dispatcher = new FakeDispatcher();
        var handler = Handler(dispatcher);
        var request = Request();

        var first = await InvokeAsync(handler, request, Caller("S-1-5-21-1"));
        var duplicate = await InvokeAsync(handler, request, Caller("S-1-5-21-1"));

        Assert.Equal(ControlOutcome.Succeeded, first!.Outcome);
        Assert.Equal(ControlErrorCode.DuplicateRequest, duplicate!.ErrorCode);
        Assert.Equal(1, dispatcher.Calls);
    }

    [Fact]
    public async Task ForbiddenCommandDoesNotDispatchOrConsumeReplayReservation()
    {
        var replayGuard = new ControlRequestReplayGuard(new MutableTimeProvider(Now), capacity: 1);
        var forbiddenDispatcher = new FakeDispatcher();
        var authorizedDispatcher = new FakeDispatcher();
        var request = Request();
        var caller = Caller("S-1-5-21-1");
        var forbiddenHandler = Handler(
            forbiddenDispatcher,
            replayGuard,
            new ControlCommandAuthorization(new[] { ControlCommand.Status }));
        var authorizedHandler = Handler(
            authorizedDispatcher,
            replayGuard,
            new ControlCommandAuthorization());

        var forbidden = await InvokeAsync(forbiddenHandler, request, caller);
        var authorized = await InvokeAsync(authorizedHandler, request, caller);

        Assert.Equal(ControlErrorCode.ForbiddenCommand, forbidden!.ErrorCode);
        Assert.Equal(0, forbiddenDispatcher.Calls);
        Assert.Equal(ControlOutcome.Succeeded, authorized!.Outcome);
        Assert.Equal(1, authorizedDispatcher.Calls);
    }

    [Fact]
    public async Task DispatcherExceptionIsSanitizedAndReservationRemainsConsumed()
    {
        var dispatcher = new FakeDispatcher { Exception = new InvalidOperationException("sensitive-detail") };
        var handler = Handler(dispatcher);
        var request = Request();

        var failure = await InvokeAsync(handler, request, Caller("S-1-5-21-1"));
        dispatcher.Exception = null;
        var replay = await InvokeAsync(handler, request, Caller("S-1-5-21-1"));

        Assert.Equal(ControlErrorCode.InternalError, failure!.ErrorCode);
        Assert.Null(failure.Result);
        Assert.Equal(ControlErrorCode.DuplicateRequest, replay!.ErrorCode);
        Assert.Equal(1, dispatcher.Calls);
    }

    [Fact]
    public async Task DispatcherResultForWrongCommandIsSanitized()
    {
        var dispatcher = new FakeDispatcher
        {
            Result = new ControlSnapshotResult(new ControlNetworkSnapshot(
                Now, "machine", Array.Empty<ControlAdapterState>(), Array.Empty<ControlRouteState>(),
                Array.Empty<ControlDnsInterfaceState>()))
        };
        var response = await InvokeAsync(Handler(dispatcher), Request(), Caller("S-1-5-21-1"));
        Assert.Equal(ControlOutcome.Failed, response!.Outcome);
        Assert.Equal(ControlErrorCode.InternalError, response.ErrorCode);
        Assert.Null(response.Result);
    }

    [Fact]
    public async Task DispatcherCancellationPropagatesAndReservationRemainsConsumed()
    {
        var dispatcher = new FakeDispatcher { Exception = new OperationCanceledException() };
        var handler = Handler(dispatcher);
        var request = Request();
        var firstStream = await RequestStreamAsync(request);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            handler.HandleAuthenticatedForTestsAsync(firstStream, Caller("S-1-5-21-1"), CancellationToken.None));

        dispatcher.Exception = null;
        var replay = await InvokeAsync(handler, request, Caller("S-1-5-21-1"));
        Assert.Equal(ControlErrorCode.DuplicateRequest, replay!.ErrorCode);
        Assert.Equal(1, dispatcher.Calls);
    }

    [Fact]
    public async Task AuthorizedCallerReceivesGreetingBeforeRequestRead()
    {
        var stream = new DuplexStream(Array.Empty<byte>(), throwOnRead: true);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Handler(new FakeDispatcher()).HandleAuthenticatedForTestsAsync(
                stream, Caller("S-1-5-21-1"), CancellationToken.None));
        await using var output = new MemoryStream(stream.Written);
        var greeting = await LengthPrefixedJsonProtocol.ReadGreetingAsync(output);
        Assert.Equal(ControlProtocolConstants.Version, greeting.ProtocolVersion);
        Assert.Equal(InstanceId, greeting.ServiceInstanceId);
        Assert.Equal(output.Length, output.Position);
    }

    [Fact]
    public async Task OversizedSnapshotFallsBackWithoutPartialFrameAndConsumesReplay()
    {
        var dispatcher = new FakeDispatcher
        {
            Result = new ControlSnapshotResult(new ControlNetworkSnapshot(
                Now, new string('x', ControlProtocolConstants.MaximumResponseSize),
                Array.Empty<ControlAdapterState>(), null, null))
        };
        var handler = Handler(dispatcher);
        var request = Request() with { Command = ControlCommand.Snapshot };
        var first = await InvokeAsync(handler, request, Caller("S-1-5-21-1"));
        dispatcher.Result = new ControlStatusResult(ControlConnectionState.Disconnected);
        var replay = await InvokeAsync(handler, request, Caller("S-1-5-21-1"));

        Assert.Equal(ControlOutcome.Failed, first!.Outcome);
        Assert.Equal(ControlErrorCode.ResponseTooLarge, first.ErrorCode);
        Assert.Equal(request.Command, first.Command);
        Assert.Null(first.Result);
        Assert.Equal(ControlErrorCode.DuplicateRequest, replay!.ErrorCode);
        Assert.Equal(1, dispatcher.Calls);
    }

    [Fact]
    public async Task NonOversizeProtocolFailurePropagatesWithoutSecondResponseAndConsumesReplay()
    {
        var dispatcher = new FakeDispatcher
        {
            Result = new ControlStatusResult((ControlConnectionState)int.MaxValue)
        };
        var handler = Handler(dispatcher);
        var request = Request();
        await using var firstStream = await RequestStreamAsync(request);

        var exception = await Assert.ThrowsAsync<ControlProtocolException>(() =>
            handler.HandleAuthenticatedForTestsAsync(firstStream, Caller("S-1-5-21-1"), CancellationToken.None));
        Assert.Equal(ControlProtocolError.InvalidContract, exception.Error);

        await using var firstOutput = new MemoryStream(firstStream.Written);
        var greeting = await LengthPrefixedJsonProtocol.ReadGreetingAsync(firstOutput);
        Assert.Equal(InstanceId, greeting.ServiceInstanceId);
        Assert.Equal(firstOutput.Length, firstOutput.Position);

        dispatcher.Result = new ControlStatusResult(ControlConnectionState.Disconnected);
        var replay = await InvokeAsync(handler, request, Caller("S-1-5-21-1"));
        Assert.Equal(ControlErrorCode.DuplicateRequest, replay!.ErrorCode);
        Assert.Equal(1, dispatcher.Calls);
    }

    [Fact]
    public void SecureStackIsProductionV2OnlyAndRecoveryRemainsUnreachable()
    {
        var root = FindRepositoryRoot();
        var worker = File.ReadAllText(Path.Combine(root, "src", "LibertyRoute.Service", "LibertyRouteWorker.cs"));
        var program = File.ReadAllText(Path.Combine(root, "src", "LibertyRoute.Service", "Program.cs"));
        var registration = File.ReadAllText(Path.Combine(root, "src", "LibertyRoute.Service", "ServiceRegistration.cs"));
        var serviceProject = File.ReadAllText(Path.Combine(root, "src", "LibertyRoute.Service", "LibertyRoute.Service.csproj"));
        var protocol = File.ReadAllText(Path.Combine(root, "src", "LibertyRoute.ControlProtocol", "ControlProtocolContracts.cs"));

        Assert.Contains("LibertyRoute.Network.v2", worker, StringComparison.Ordinal);
        Assert.Contains("SecureControlPipeFactory", worker, StringComparison.Ordinal);
        Assert.Contains("SecureControlConnectionHandler", worker, StringComparison.Ordinal);
        Assert.DoesNotContain("LibertyRoute.Network.v1", worker, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadLineAsync", worker, StringComparison.Ordinal);
        Assert.DoesNotContain("WriteLineAsync", worker, StringComparison.Ordinal);
        Assert.DoesNotContain("ToUpperInvariant", worker, StringComparison.Ordinal);
        Assert.DoesNotContain("SecureControl", program, StringComparison.Ordinal);
        Assert.Contains("SecureControlConnectionHandler", registration, StringComparison.Ordinal);
        Assert.Contains("LibertyRoute.ControlProtocol", serviceProject, StringComparison.Ordinal);
        Assert.DoesNotContain("LibertyRoute.Restoration.Windows", serviceProject, StringComparison.Ordinal);
        Assert.DoesNotContain("Recovery", string.Join(',', Enum.GetNames<ControlCommand>()), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Restoration", string.Join(',', Enum.GetNames<ControlCommand>()), StringComparison.OrdinalIgnoreCase);

        var forbiddenActivationTerms = new[]
        {
            "ControlledRecoveryApproval", "ControlledApprovedRecoveryExecution",
            "ControlledRecoveryRestorationWorkflow", "RestorationExecutionCapability",
            "ControlledRestorationActivationGrant", "RouteMutationProviderFactory",
            "WindowsRouteMutationNative", "IRouteMutationNative", "RecordRevertedAsync",
            "IServiceProvider", "IConfiguration", "Environment.GetEnvironmentVariable"
        };
        var secureSources = string.Join('\n', new[]
        {
            "ControlSecurityBoundary.cs", "SecureControlPipeFactory.cs",
            "ControlRequestReplayGuard.cs", "SecureControlConnectionHandler.cs"
        }.Select(file => File.ReadAllText(Path.Combine(root, "src", "LibertyRoute.Service", file))));
        Assert.All(forbiddenActivationTerms, term =>
            Assert.DoesNotContain(term, secureSources, StringComparison.Ordinal));
        Assert.DoesNotContain("Recovery", protocol, StringComparison.Ordinal);
    }

    private static void AssertRule(
        IEnumerable<PipeAccessRule> rules,
        WellKnownSidType sidType,
        AccessControlType access)
    {
        var sid = new SecurityIdentifier(sidType, null);
        Assert.Contains(rules, rule =>
            ((SecurityIdentifier)rule.IdentityReference).Equals(sid) &&
            rule.AccessControlType == access &&
            HasRequiredDuplexRights(rule.PipeAccessRights));
    }

    private static void AssertNoRule(IEnumerable<PipeAccessRule> rules, WellKnownSidType sidType)
    {
        var sid = new SecurityIdentifier(sidType, null);
        Assert.DoesNotContain(rules, rule => ((SecurityIdentifier)rule.IdentityReference).Equals(sid));
    }

    private static bool HasRequiredDuplexRights(PipeAccessRights rights)
        => (rights & PipeAccessRights.ReadWrite) == PipeAccessRights.ReadWrite;

    private static ControlCallerIdentity Caller(
        string sid,
        bool authenticated = true,
        bool administrator = true,
        bool network = false,
        bool localSystem = false)
        => new(sid, Array.Empty<string>(), authenticated, administrator, network, localSystem);

    private static ControlRequestEnvelope Request(DateTimeOffset? sentAt = null) => new(
        ControlProtocolConstants.Version,
        InstanceId,
        Guid.NewGuid(),
        sentAt ?? Now,
        ControlCommand.Status,
        new ControlRequestPayload());

    private static Task<ControlReplayReservationResult> Reserve(
        ControlRequestReplayGuard guard,
        ControlCallerIdentity caller,
        ControlRequestEnvelope request)
        => guard.ReserveAsync(caller, request, CancellationToken.None);

    private static SecureControlConnectionHandler Handler(
        FakeDispatcher dispatcher,
        ControlRequestReplayGuard? replayGuard = null,
        ControlCommandAuthorization? authorization = null)
        => new(
            new ControlServiceInstance(InstanceId),
            authorization ?? new ControlCommandAuthorization(),
            replayGuard ?? new ControlRequestReplayGuard(new MutableTimeProvider(Now)),
            dispatcher,
            NullLogger<SecureControlConnectionHandler>.Instance);

    private static async Task<ControlResponseEnvelope?> InvokeAsync(
        SecureControlConnectionHandler handler,
        ControlRequestEnvelope request,
        ControlCallerIdentity caller)
    {
        await using var stream = await RequestStreamAsync(request);
        await handler.HandleAuthenticatedForTestsAsync(stream, caller, CancellationToken.None);
        await using var output = new MemoryStream(stream.Written);
        var greeting = await LengthPrefixedJsonProtocol.ReadGreetingAsync(output);
        Assert.Equal(InstanceId, greeting.ServiceInstanceId);
        if (output.Position == output.Length)
            return null;
        var response = await LengthPrefixedJsonProtocol.ReadResponseAsync(output);
        Assert.Equal(output.Length, output.Position);
        return response;
    }

    private static async Task<DuplexStream> RequestStreamAsync(ControlRequestEnvelope request)
    {
        await using var input = new MemoryStream();
        await LengthPrefixedJsonProtocol.WriteRequestAsync(input, request);
        return new DuplexStream(input.ToArray());
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "LibertyRoute.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    private static NetworkStateSnapshot DiagnosticSnapshot()
        => new(Now, "machine",
            new[] { new AdapterState("id", "name", "description", "Ethernet", "Up", new[] { "10.0.0.2" }, new[] { "10.0.0.1" }, new[] { "1.1.1.1" }) },
            new[] { new RouteState { Destination = "0.0.0.0/0", NextHop = "10.0.0.1", InterfaceIndex = 7, Metric = uint.MaxValue, AddressFamily = "InterNetwork" } },
            new[] { new DnsInterfaceState("id", "name", true, new[] { "1.1.1.1" }, null, Array.Empty<string>()) });

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class FakeDispatcher : IControlCommandDispatcher
    {
        public int Calls { get; private set; }
        public Exception? Exception { get; set; }
        public ControlResponseResult Result { get; set; } = new ControlStatusResult(ControlConnectionState.Disconnected);

        public Task<ControlDispatchResult> DispatchAsync(
            ControlCallerIdentity caller,
            ControlRequestEnvelope request,
            CancellationToken cancellationToken)
        {
            Calls++;
            if (Exception is not null)
                throw Exception;
            return Task.FromResult(new ControlDispatchResult(ControlOutcome.Succeeded, ControlErrorCode.None, Result));
        }
    }

    private sealed class FakeNetworkStateManager(NetworkStateSnapshot snapshot) : INetworkStateManager
    {
        public int Captures { get; private set; }
        public int Verifications { get; private set; }
        public Task<NetworkStateSnapshot> CaptureStateAsync(CancellationToken cancellationToken)
        {
            Captures++;
            return Task.FromResult(snapshot);
        }
        public Task VerifyRestorationAsync(NetworkStateSnapshot original, CancellationToken cancellationToken)
        {
            Verifications++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTransactionJournal : ITransactionJournal
    {
        public NetworkTransaction? Active { get; private set; }
        public int Writes { get; private set; }
        public int Clears { get; private set; }
        public string JournalPath => "controlled-test";
        public Task WriteAsync(NetworkTransaction transaction, CancellationToken cancellationToken)
        {
            Writes++;
            Active = transaction;
            return Task.CompletedTask;
        }
        public Task<NetworkTransaction?> ReadActiveAsync(CancellationToken cancellationToken) => Task.FromResult(Active);
        public Task ClearAsync(Guid expectedSessionId, CancellationToken cancellationToken)
        {
            Clears++;
            Active = null;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeConnectionEngine : IConnectionEngine
    {
        public string Id => "controlled-test";
        public int Stops { get; private set; }
        public Task StartAsync(VpnServerConfig server, CancellationToken cancellationToken) => throw new InvalidOperationException("Start is forbidden in this test.");
        public Task StopAsync(CancellationToken cancellationToken) { Stops++; return Task.CompletedTask; }
        public Task<bool> IsHealthyAsync(CancellationToken cancellationToken) => Task.FromResult(false);
    }

    private sealed class ThrowOnReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Read was forbidden.");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Read was forbidden.");
    }

    private sealed class DuplexStream(byte[] input, bool throwOnRead = false) : Stream
    {
        private readonly MemoryStream _input = new(input, writable: false);
        private readonly MemoryStream _output = new();
        public byte[] Written => _output.ToArray();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => _output.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _output.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count)
            => throwOnRead ? throw new InvalidOperationException("Read was forbidden.") : _input.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => throwOnRead ? throw new InvalidOperationException("Read was forbidden.") : _input.ReadAsync(buffer, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => _output.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => _output.WriteAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
