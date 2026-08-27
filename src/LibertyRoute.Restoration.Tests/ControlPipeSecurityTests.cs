using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using LibertyRoute.ControlProtocol;
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
            new MemoryStream(new byte[3]),
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
    public void SecureStackIsInactiveAndRecoveryRemainsUnreachable()
    {
        var root = FindRepositoryRoot();
        var worker = File.ReadAllText(Path.Combine(root, "src", "LibertyRoute.Service", "LibertyRouteWorker.cs"));
        var program = File.ReadAllText(Path.Combine(root, "src", "LibertyRoute.Service", "Program.cs"));
        var registration = File.ReadAllText(Path.Combine(root, "src", "LibertyRoute.Service", "ServiceRegistration.cs"));
        var serviceProject = File.ReadAllText(Path.Combine(root, "src", "LibertyRoute.Service", "LibertyRoute.Service.csproj"));
        var protocol = File.ReadAllText(Path.Combine(root, "src", "LibertyRoute.ControlProtocol", "ControlProtocolContracts.cs"));

        Assert.Contains("ReadLineAsync", worker, StringComparison.Ordinal);
        Assert.DoesNotContain("SecureControl", worker, StringComparison.Ordinal);
        Assert.DoesNotContain("SecureControl", program, StringComparison.Ordinal);
        Assert.DoesNotContain("SecureControl", registration, StringComparison.Ordinal);
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
        var responseStart = stream.Length;
        await handler.HandleAuthenticatedForTestsAsync(stream, caller, CancellationToken.None);
        if (stream.Length == responseStart)
            return null;
        stream.Position = responseStart;
        return await LengthPrefixedJsonProtocol.ReadResponseAsync(stream);
    }

    private static async Task<MemoryStream> RequestStreamAsync(ControlRequestEnvelope request)
    {
        var stream = new MemoryStream();
        await LengthPrefixedJsonProtocol.WriteRequestAsync(stream, request);
        stream.Position = 0;
        return stream;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "LibertyRoute.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

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
}
