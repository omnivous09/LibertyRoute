using System.Reflection;
using LibertyRoute.Restoration;
using LibertyRoute.Restoration.Windows;

namespace LibertyRoute.Restoration.Tests;

public sealed class RestorationExecutionGateTests
{
    private static readonly Guid TransactionId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid SessionId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void NoCapabilityDisablesWithoutConstructingProvider()
    {
        var constructionCount = new ConstructionCounter();
        var factory = TestFactory(constructionCount);

        var result = factory.Create(PreparedBatch(), SessionId, null);

        Assert.Equal(RestorationExecutionGateStatus.Disabled, result.Status);
        Assert.Null(result.Provider);
        Assert.Equal(0, constructionCount.Value);
    }

    [Fact]
    public void InvalidCapabilityIsRejectedWithoutConstructingProvider()
    {
        var constructionCount = new ConstructionCounter();
        var result = TestFactory(constructionCount).Create(
            PreparedBatch(), SessionId, RestorationExecutionCapability.CreateInvalidForControlledTest());

        Assert.Equal(RestorationExecutionGateStatus.InvalidCapability, result.Status);
        Assert.Null(result.Provider);
        Assert.Equal(0, constructionCount.Value);
    }

    [Fact]
    public void BlockedBatchNeverConstructsProvider()
    {
        var constructionCount = new ConstructionCounter();
        var result = TestFactory(constructionCount).Create(
            BlockedBatch(), SessionId, RestorationExecutionCapability.CreateForControlledTest());

        Assert.Equal(RestorationExecutionGateStatus.BlockedByBatch, result.Status);
        Assert.Null(result.Provider);
        Assert.Equal(0, constructionCount.Value);
    }

    [Fact]
    public void CapabilityAloneCannotEnableEmptyOrUnauthorizedBatch()
    {
        var constructionCount = new ConstructionCounter();
        var result = TestFactory(constructionCount).Create(
            EmptyPreparation(), SessionId, RestorationExecutionCapability.CreateForControlledTest());

        Assert.Equal(RestorationExecutionGateStatus.BlockedByAuthorization, result.Status);
        Assert.Null(result.Provider);
        Assert.Equal(0, constructionCount.Value);
    }

    [Fact]
    public void AuthorizedBatchWithoutCapabilityRemainsDisabled()
    {
        var constructionCount = new ConstructionCounter();
        var result = TestFactory(constructionCount).Create(PreparedBatch(), SessionId, null);

        Assert.Equal(RestorationExecutionGateStatus.Disabled, result.Status);
        Assert.Equal(0, constructionCount.Value);
    }

    [Fact]
    public void CapabilityAndAuthorizedBatchMayConstructProvider()
    {
        var constructionCount = new ConstructionCounter();
        var result = TestFactory(constructionCount).Create(
            PreparedBatch(), SessionId, RestorationExecutionCapability.CreateForControlledTest());

        Assert.Equal(RestorationExecutionGateStatus.Enabled, result.Status);
        Assert.NotNull(result.Provider);
        Assert.Equal(1, constructionCount.Value);
    }

    [Fact]
    public void WrongSessionDoesNotConstructProvider()
    {
        var constructionCount = new ConstructionCounter();
        var result = TestFactory(constructionCount).Create(
            PreparedBatch(), Guid.NewGuid(), RestorationExecutionCapability.CreateForControlledTest());

        Assert.Equal(RestorationExecutionGateStatus.SessionMismatch, result.Status);
        Assert.Null(result.Provider);
        Assert.Equal(0, constructionCount.Value);
    }

    [Fact]
    public void DuplicateRequestsBlockBeforeProviderConstruction()
    {
        var operation = Operation("route-a", 1);
        var authorization = Authorize(operation);
        var duplicate = new RestorationExecutionPreparationForTests();
        var preparation = duplicate.Create(new[] { Request(operation, authorization), Request(operation, authorization) }, true);
        var constructionCount = new ConstructionCounter();

        var result = TestFactory(constructionCount).Create(
            preparation, SessionId, RestorationExecutionCapability.CreateForControlledTest());

        Assert.Equal(RestorationExecutionGateStatus.BlockedByAuthorization, result.Status);
        Assert.Null(result.Provider);
        Assert.Equal(0, constructionCount.Value);
    }

    [Fact]
    public void RepeatedGateEvaluationIsDeterministic()
    {
        var constructionCount = new ConstructionCounter();
        var capability = RestorationExecutionCapability.CreateForControlledTest();
        var factory = TestFactory(constructionCount);

        var first = factory.Create(PreparedBatch(), SessionId, capability);
        var second = factory.Create(PreparedBatch(), SessionId, capability);

        Assert.Equal(first.Status, second.Status);
        Assert.Equal(first.Reason, second.Reason);
        Assert.Equal(2, constructionCount.Value);
    }

    [Fact]
    public void CapabilityHasNoPublicConstructorOrStringBooleanInput()
    {
        var type = typeof(RestorationExecutionCapability);

        Assert.Empty(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.DoesNotContain(type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance), field => field.FieldType == typeof(string) || field.FieldType == typeof(bool));
    }

    [Fact]
    public void RealAdapterAndNativeApiRemainInternalAndConstructorsNonPublic()
    {
        Assert.False(typeof(WindowsRouteMutationNative).IsPublic);
        Assert.False(typeof(WindowsRouteNativeApi).IsPublic);
        Assert.Empty(typeof(WindowsRouteMutationNative).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.Empty(typeof(WindowsRouteNativeApi).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
    }

    [Fact]
    public void ProductionAssembliesDoNotReferenceLiveExecutionTypes()
    {
        var productionAssemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => assembly.GetName().Name is "LibertyRoute.Service" or "LibertyRoute.Desktop" or "LibertyRoute.Recovery");

        Assert.All(productionAssemblies, assembly => Assert.DoesNotContain(assembly.GetTypes(), type => type.Name.Contains("WindowsRouteMutationNative", StringComparison.Ordinal) || type.Name.Contains("RestorationExecutionCapability", StringComparison.Ordinal) || type.Name.Contains("RouteMutationProviderFactory", StringComparison.Ordinal)));
    }

    private static RouteMutationProviderFactory TestFactory(ConstructionCounter constructionCount)
        => new(() =>
        {
            constructionCount.Value++;
            return new NoOpProvider();
        });

    private sealed class ConstructionCounter
    {
        public int Value { get; set; }
    }

    private static RestorationExecutionPreparation PreparedBatch()
    {
        var operation = Operation("route-a", 1);
        var authorization = Authorize(operation);
        return RestorationExecutionPreparation.Prepare(
            new BatchAuthorizationResult(new[] { authorization }, new[] { operation }, Array.Empty<DryRunRestorationOperation>(), Array.Empty<DryRunRestorationOperation>(), Array.Empty<string>(), true),
            TransactionId,
            SessionId);
    }

    private static RestorationExecutionPreparation BlockedBatch()
    {
        var authorized = Operation("route-a", 1);
        var denied = Operation("route-b", 2);
        var authorizedDecision = Authorize(authorized);
        var deniedDecision = RestorationAuthorizationPolicy.Authorize(denied, Array.Empty<OwnershipEvidence>(), SessionId);
        return RestorationExecutionPreparation.Prepare(
            new BatchAuthorizationResult(new[] { authorizedDecision, deniedDecision }, new[] { authorized }, new[] { denied }, Array.Empty<DryRunRestorationOperation>(), new[] { deniedDecision.Reason }, false),
            TransactionId,
            SessionId);
    }

    private static RestorationExecutionPreparation EmptyPreparation()
        => RestorationExecutionPreparation.Prepare(
            new BatchAuthorizationResult(Array.Empty<OperationAuthorization>(), Array.Empty<DryRunRestorationOperation>(), Array.Empty<DryRunRestorationOperation>(), Array.Empty<DryRunRestorationOperation>(), Array.Empty<string>(), true),
            TransactionId,
            SessionId);

    private static DryRunRestorationOperation Operation(string target, int order)
        => new(DryRunOperationCategory.Route, DryRunAction.RestoreBaseline, target, "destination=10.0.0.0/24;nextHop=10.0.0.1;interfaceIndex=4;metric=1", "<absent>", "test", order, true, true, DryRunSafetyState.SafeToPlan);

    private static OperationAuthorization Authorize(DryRunRestorationOperation operation)
    {
        var evidence = new OwnershipEvidence(SessionId, operation.Category, operation.TargetIdentity, operation.OriginalValue, operation.CurrentValue, Guid.NewGuid(), DateTimeOffset.UnixEpoch, operation.ExecutionOrder, OwnershipEvidenceSource.TestFixture, true);
        return RestorationAuthorizationPolicy.Authorize(operation, new[] { evidence }, SessionId);
    }

    private static AuthorizedRestorationRequest Request(DryRunRestorationOperation operation, OperationAuthorization authorization)
        => AuthorizedRestorationRequest.Create(operation, authorization, TransactionId, SessionId);

    private sealed class NoOpProvider : IRestorationMutationProvider
    {
        public Task<RestorationMutationResult> ApplyAsync(AuthorizedRestorationRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new RestorationMutationResult(request.OperationIdentity, RestorationMutationState.Skipped, "Test-only provider.", false));
    }

    private sealed class RestorationExecutionPreparationForTests
    {
        public RestorationExecutionPreparation Create(IReadOnlyList<AuthorizedRestorationRequest> requests, bool canExecute)
        {
            var type = typeof(RestorationExecutionPreparation);
            var constructor = type.GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single();
            return (RestorationExecutionPreparation)constructor.Invoke(new object[]
            {
                requests,
                Array.Empty<DryRunRestorationOperation>(),
                Array.Empty<string>(),
                canExecute
            });
        }
    }
}
