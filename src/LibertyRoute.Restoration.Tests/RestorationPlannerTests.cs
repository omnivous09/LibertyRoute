using LibertyRoute.Core;
using LibertyRoute.Restoration;

namespace LibertyRoute.Restoration.Tests;

public sealed class RestorationPlannerTests
{
    [Fact]
    public void IdenticalSnapshotsProduceEmptyPlan()
    {
        var snapshot = Snapshot();
        Assert.Empty(RestorationPlanner.CreatePlan(snapshot, snapshot).Differences);
    }

    [Fact]
    public void MissingAdapterIsRestorationCandidate()
    {
        var original = Snapshot(adapter: Adapter("a", addresses: new[] { "192.0.2.1" }));
        var current = Snapshot();
        var difference = Single(original, current, RestorationCategory.Adapter);

        Assert.Equal(DifferenceClassification.Missing, difference.Classification);
        Assert.Equal(RestorationIntent.RestoreBaseline, difference.Intent);
    }

    [Fact]
    public void AddedAdapterIsNeverAutomaticallyDeleted()
    {
        var difference = Single(Snapshot(), Snapshot(adapter: Adapter("a")), RestorationCategory.Adapter);

        Assert.Equal(DifferenceClassification.Added, difference.Classification);
        Assert.Equal(RestorationIntent.NoAutomaticDeletion, difference.Intent);
    }

    [Theory]
    [InlineData("192.0.2.1", RestorationCategory.Address)]
    [InlineData("2001:db8::1", RestorationCategory.Address)]
    public void RemovedAddressIsRestorationCandidate(string address, RestorationCategory category)
    {
        var original = Snapshot(adapter: Adapter("a", addresses: new[] { address }));
        var difference = Single(original, Snapshot(adapter: Adapter("a")), category);

        Assert.Equal(DifferenceClassification.Missing, difference.Classification);
        Assert.Equal(RestorationIntent.RestoreBaseline, difference.Intent);
    }

    [Fact]
    public void AddedAddressIsHandledConservatively()
    {
        var difference = Single(Snapshot(adapter: Adapter("a")), Snapshot(adapter: Adapter("a", addresses: new[] { "192.0.2.2" })), RestorationCategory.Address);

        Assert.Equal(DifferenceClassification.Added, difference.Classification);
        Assert.Equal(RestorationIntent.NoAutomaticDeletion, difference.Intent);
    }

    [Fact]
    public void ChangedGatewayIsReported()
    {
        var original = Snapshot(adapter: Adapter("a", gateways: new[] { "192.0.2.1" }));
        var current = Snapshot(adapter: Adapter("a", gateways: new[] { "192.0.2.254" }));
        var difference = Single(original, current, RestorationCategory.Gateway);

        Assert.Equal(DifferenceClassification.Changed, difference.Classification);
        Assert.Equal("192.0.2.1", difference.OriginalValue);
        Assert.Equal(RestorationIntent.RestoreBaseline, difference.Intent);
    }

    [Fact]
    public void ChangedDnsServerIsReported()
    {
        var original = Snapshot(adapter: Adapter("a", dns: new[] { "192.0.2.53" }));
        var current = Snapshot(adapter: Adapter("a", dns: new[] { "192.0.2.54" }));
        var difference = Single(original, current, RestorationCategory.Dns);

        Assert.Equal(DifferenceClassification.Changed, difference.Classification);
        Assert.Equal(RestorationIntent.RestoreBaseline, difference.Intent);
    }

    [Fact]
    public void RemovedRouteIsRestorationCandidate()
    {
        var route = Route("10.0.0.0/8");
        var difference = Single(Snapshot(routes: new[] { route }), Snapshot(), RestorationCategory.Route);

        Assert.Equal(DifferenceClassification.Missing, difference.Classification);
        Assert.Equal(RestorationIntent.RestoreBaseline, difference.Intent);
    }

    [Fact]
    public void AddedRouteIsNeverAutomaticallyDeleted()
    {
        var difference = Single(Snapshot(), Snapshot(routes: new[] { Route("10.0.0.0/8") }), RestorationCategory.Route);

        Assert.Equal(DifferenceClassification.Added, difference.Classification);
        Assert.Equal(RestorationIntent.NoAutomaticDeletion, difference.Intent);
    }

    [Theory]
    [InlineData("10.0.0.1", "10.0.0.2", 4, 1, 4, "next hop")]
    [InlineData("10.0.0.1", "10.0.0.1", 4, 2, 4, "metric")]
    [InlineData("10.0.0.1", "10.0.0.1", 4, 1, 8, "interface")]
    public void ChangedRouteFieldIsReported(string originalNextHop, string currentNextHop, int originalMetric, int currentMetric, int currentInterface, string reason)
    {
        var original = Snapshot(routes: new[] { Route("10.0.0.0/8", originalNextHop, 4, originalMetric) });
        var current = Snapshot(routes: new[] { Route("10.0.0.0/8", currentNextHop, currentInterface, currentMetric) });
        var difference = Single(original, current, RestorationCategory.Route);

        Assert.Equal(DifferenceClassification.Changed, difference.Classification);
        Assert.Equal(RestorationIntent.RestoreBaseline, difference.Intent);
        Assert.Contains(reason, difference.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IPv4AndIPv6RoutesRemainDistinct()
    {
        var original = Snapshot(routes: new[] { Route("10.0.0.0/8", family: "2"), Route("2001:db8::/32", family: "23") });
        var current = Snapshot(routes: new[] { Route("10.0.0.0/8", family: "23"), Route("2001:db8::/32", family: "2") });

        Assert.Equal(4, RestorationPlanner.CreatePlan(original, current).Differences.Count);
    }

    [Fact]
    public void RouteIdentityDoesNotDependOnColonOrSlashFormatting()
    {
        var original = Snapshot(routes: new[] { Route("10.0.0.0/8") });
        var current = Snapshot(routes: new[] { Route("10.0.0.0:8") });

        Assert.Empty(RestorationPlanner.CreatePlan(original, current).Differences);
    }

    [Fact]
    public void PlanOrderingIsDeterministic()
    {
        var original = Snapshot(
            adapter: Adapter("b", addresses: new[] { "192.0.2.2" }),
            routes: new[] { Route("10.0.0.0/8") });
        var current = Snapshot(adapter: Adapter("a"));

        var first = RestorationPlanner.CreatePlan(original, current).Differences;
        var second = RestorationPlanner.CreatePlan(original, current).Differences;

        Assert.Equal(first, second);
    }

    [Fact]
    public void NullCollectionsAreHandledAsEmpty()
    {
        var original = new NetworkStateSnapshot(DateTimeOffset.UnixEpoch, "test", null!, null, null);
        var current = new NetworkStateSnapshot(DateTimeOffset.UnixEpoch, "test", Array.Empty<AdapterState>(), null, null);

        Assert.Empty(RestorationPlanner.CreatePlan(original, current).Differences);
    }

    [Fact]
    public void PlannerExposesNoMutationSurface()
    {
        var publicMethods = typeof(RestorationPlanner).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Select(method => method.Name)
            .ToArray();

        Assert.Equal(new[] { nameof(RestorationPlanner.CreatePlan) }, publicMethods);
        Assert.Empty(typeof(RestorationPlanner).Assembly.GetTypes()
            .SelectMany(type => type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance))
            .SelectMany(method => method.GetCustomAttributes(typeof(System.Runtime.InteropServices.DllImportAttribute), inherit: false)));
    }

    [Fact]
    public void IdenticalPlanProducesZeroOperations()
    {
        var result = DryRunRestorationExecutor.CreateDryRun(new RestorationPlan(Array.Empty<RestorationDifference>()));

        Assert.Empty(result.Operations);
        Assert.Equal(0, result.Summary.TotalDifferences);
    }

    [Fact]
    public void MissingBaselineAddressProducesNonAutomaticRestoration()
    {
        var plan = RestorationPlanner.CreatePlan(Snapshot(Adapter("a", addresses: new[] { "192.0.2.1" })), Snapshot(Adapter("a")));
        var operation = SingleOperation(plan, DryRunOperationCategory.Address);

        Assert.Equal(DryRunAction.RestoreBaseline, operation.Action);
        Assert.Equal(DryRunSafetyState.SafeToPlan, operation.SafetyState);
        Assert.False(operation.AutomaticExecutionAllowed);
        Assert.True(operation.OwnershipRequired);
    }

    [Fact]
    public void AddedAddressProducesManualReviewAndNoDeletion()
    {
        var plan = RestorationPlanner.CreatePlan(Snapshot(Adapter("a")), Snapshot(Adapter("a", addresses: new[] { "192.0.2.2" })));
        var operation = SingleOperation(plan, DryRunOperationCategory.Address);

        Assert.Equal(DryRunAction.ManualReview, operation.Action);
        Assert.Equal(DryRunSafetyState.ManualReview, operation.SafetyState);
        Assert.Contains("not automatically deleted", operation.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingBaselineRouteProducesRestoration()
    {
        var result = DryRunRestorationExecutor.CreateDryRun(RestorationPlanner.CreatePlan(Snapshot(routes: new[] { Route("10.0.0.0/8") }), Snapshot()));

        Assert.Equal(DryRunAction.RestoreBaseline, Assert.Single(result.Operations).Action);
    }

    [Fact]
    public void AddedBaselineRouteProducesNoDeletion()
    {
        var result = DryRunRestorationExecutor.CreateDryRun(RestorationPlanner.CreatePlan(Snapshot(), Snapshot(routes: new[] { Route("10.0.0.0/8") })));

        Assert.Equal(DryRunAction.ManualReview, Assert.Single(result.Operations).Action);
    }

    [Fact]
    public void ChangedGatewayAndDnsProduceRestorationOperations()
    {
        var original = Snapshot(Adapter("a", gateways: new[] { "192.0.2.1" }, dns: new[] { "192.0.2.53" }));
        var current = Snapshot(Adapter("a", gateways: new[] { "192.0.2.254" }, dns: new[] { "192.0.2.54" }));
        var result = DryRunRestorationExecutor.CreateDryRun(RestorationPlanner.CreatePlan(original, current));

        Assert.Contains(result.Operations, operation => operation.Category == DryRunOperationCategory.Gateway && operation.Action == DryRunAction.RestoreBaseline);
        Assert.Contains(result.Operations, operation => operation.Category == DryRunOperationCategory.Dns && operation.Action == DryRunAction.RestoreBaseline);
    }

    [Fact]
    public void ChangedRouteFieldsProduceRestorationOperations()
    {
        var original = Snapshot(routes: new[] { Route("10.0.0.0/8", "10.0.0.1", 4, 1) });
        var current = Snapshot(routes: new[] { Route("10.0.0.0/8", "10.0.0.2", 8, 2) });
        var result = DryRunRestorationExecutor.CreateDryRun(RestorationPlanner.CreatePlan(original, current));

        var operation = Assert.Single(result.Operations);
        Assert.Equal(DryRunOperationCategory.Route, operation.Category);
        Assert.Equal(DryRunAction.RestoreBaseline, operation.Action);
        Assert.Equal(DryRunSafetyState.SafeToPlan, operation.SafetyState);
    }

    [Fact]
    public void MissingAdapterIsUnsupportedAndNeverAutomatic()
    {
        var result = DryRunRestorationExecutor.CreateDryRun(RestorationPlanner.CreatePlan(Snapshot(Adapter("a")), Snapshot()));
        var operation = SingleOperation(result, DryRunOperationCategory.Adapter);

        Assert.Equal(DryRunSafetyState.Unsupported, operation.SafetyState);
        Assert.False(operation.AutomaticExecutionAllowed);
    }

    [Fact]
    public void ExecutionOrderingAndNumberingAreStable()
    {
        var original = Snapshot(Adapter("a", addresses: new[] { "192.0.2.1" }, gateways: new[] { "192.0.2.1" }, dns: new[] { "192.0.2.53" }), new[] { Route("10.0.0.0/8") });
        var current = Snapshot(Adapter("a", addresses: new[] { "192.0.2.2" }, gateways: new[] { "192.0.2.254" }, dns: new[] { "192.0.2.54" }), new[] { Route("10.0.0.0/8", "10.0.0.1", 8, 2) });
        var first = DryRunRestorationExecutor.CreateDryRun(RestorationPlanner.CreatePlan(original, current));
        var second = DryRunRestorationExecutor.CreateDryRun(RestorationPlanner.CreatePlan(original, current));

        Assert.Equal(first.Operations, second.Operations);
        Assert.Equal(first.Summary.TotalDifferences, second.Summary.TotalDifferences);
        Assert.Equal(first.Summary.TotalOperations, second.Summary.TotalOperations);
        Assert.Equal(first.Summary.SafeOperations, second.Summary.SafeOperations);
        Assert.Equal(first.Summary.ManualReviewOperations, second.Summary.ManualReviewOperations);
        Assert.Equal(first.Summary.UnsupportedOperations, second.Summary.UnsupportedOperations);
        Assert.Equal(first.Summary.IsFullyExecutableInFuture, second.Summary.IsFullyExecutableInFuture);
        Assert.Equal(first.Summary.BlockingReasons, second.Summary.BlockingReasons);
        Assert.Equal(Enumerable.Range(1, first.Operations.Count), first.Operations.Select(operation => operation.ExecutionOrder));
        Assert.Equal(first.Operations.OrderBy(operation => operation.Category switch
        {
            DryRunOperationCategory.Route => 30,
            DryRunOperationCategory.Address => 40,
            DryRunOperationCategory.Gateway => 50,
            DryRunOperationCategory.Dns => 60,
            _ => 80
        }).ToArray(), first.Operations);
    }

    [Fact]
    public void SummaryReportsSafetyBlockers()
    {
        var result = DryRunRestorationExecutor.CreateDryRun(RestorationPlanner.CreatePlan(Snapshot(Adapter("a")), Snapshot(Adapter("a", addresses: new[] { "192.0.2.2" }))));

        Assert.Equal(1, result.Summary.TotalDifferences);
        Assert.Equal(1, result.Summary.TotalOperations);
        Assert.Equal(1, result.Summary.ManualReviewOperations);
        Assert.False(result.Summary.IsFullyExecutableInFuture);
        Assert.NotEmpty(result.Summary.BlockingReasons);
    }

    [Fact]
    public void DryRunDoesNotAlterInputPlan()
    {
        var plan = RestorationPlanner.CreatePlan(Snapshot(Adapter("a")), Snapshot(Adapter("a", addresses: new[] { "192.0.2.2" })));
        var before = plan.Differences.ToArray();

        _ = DryRunRestorationExecutor.CreateDryRun(plan);

        Assert.Equal(before, plan.Differences);
    }

    [Fact]
    public void RestorationAssemblyHasNoWindowsOrMutationSurface()
    {
        var assembly = typeof(DryRunRestorationExecutor).Assembly;
        Assert.DoesNotContain(assembly.GetTypes(), type => type.Namespace?.StartsWith("System.Net.NetworkInformation", StringComparison.Ordinal) == true || type.Namespace?.StartsWith("Microsoft.Win32", StringComparison.Ordinal) == true);
        Assert.Empty(assembly.GetTypes().SelectMany(type => type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance)).SelectMany(method => method.GetCustomAttributes(typeof(System.Runtime.InteropServices.DllImportAttribute), false)));
        Assert.DoesNotContain(typeof(DryRunRestorationExecutor).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static), method => method.Name.Contains("Apply", StringComparison.OrdinalIgnoreCase) || method.Name.Contains("ExecuteMutation", StringComparison.OrdinalIgnoreCase) || method.Name.Contains("Commit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RepeatedDryRunExecutionIsEquivalent()
    {
        var plan = RestorationPlanner.CreatePlan(Snapshot(Adapter("a", addresses: new[] { "192.0.2.1" })), Snapshot(Adapter("a")));

        var first = DryRunRestorationExecutor.CreateDryRun(plan);
        var second = DryRunRestorationExecutor.CreateDryRun(plan);

        Assert.Equal(first.Operations, second.Operations);
        Assert.Equal(first.Summary.TotalDifferences, second.Summary.TotalDifferences);
        Assert.Equal(first.Summary.TotalOperations, second.Summary.TotalOperations);
        Assert.Equal(first.Summary.SafeOperations, second.Summary.SafeOperations);
        Assert.Equal(first.Summary.ManualReviewOperations, second.Summary.ManualReviewOperations);
        Assert.Equal(first.Summary.UnsupportedOperations, second.Summary.UnsupportedOperations);
        Assert.Equal(first.Summary.IsFullyExecutableInFuture, second.Summary.IsFullyExecutableInFuture);
        Assert.True(first.Summary.BlockingReasons.SequenceEqual(second.Summary.BlockingReasons));
    }

    [Fact]
    public void NoOwnershipEvidenceDeniesOperation()
    {
        var operation = MissingAddressOperation();

        var decision = RestorationAuthorizationPolicy.Authorize(operation, Array.Empty<OwnershipEvidence>(), SessionId);

        Assert.Equal(OperationAuthorizationStatus.DeniedNoOwnership, decision.Status);
    }

    [Fact]
    public void ExactOwnershipEvidenceAuthorizesBaselineRestoration()
    {
        var operation = MissingAddressOperation();
        var evidence = Evidence(operation, original: operation.OriginalValue, applied: operation.CurrentValue);

        var decision = RestorationAuthorizationPolicy.Authorize(operation, new[] { evidence }, SessionId);

        Assert.Equal(OperationAuthorizationStatus.Authorized, decision.Status);
        Assert.Equal(evidence, decision.MatchedEvidence);
        Assert.True(decision.FutureAutomaticExecutionAllowed);
    }

    [Theory]
    [InlineData("session")]
    [InlineData("target")]
    [InlineData("applied")]
    public void OwnershipMismatchDeniesOperation(string mismatch)
    {
        var operation = MissingAddressOperation();
        var evidence = Evidence(operation,
            sessionId: mismatch == "session" ? Guid.NewGuid() : SessionId,
            target: mismatch == "target" ? "other" : operation.TargetIdentity,
            applied: mismatch == "applied" ? "different" : operation.CurrentValue);

        var decision = RestorationAuthorizationPolicy.Authorize(operation, new[] { evidence }, SessionId);

        Assert.Equal(mismatch is "session" or "target" ? OperationAuthorizationStatus.DeniedNoOwnership : OperationAuthorizationStatus.DeniedOwnershipMismatch, decision.Status);
    }

    [Fact]
    public void IncompleteEvidenceDeniesOperation()
    {
        var operation = MissingAddressOperation();
        var decision = RestorationAuthorizationPolicy.Authorize(operation, new[] { Evidence(operation, complete: false) }, SessionId);

        Assert.Equal(OperationAuthorizationStatus.DeniedOwnershipMismatch, decision.Status);
    }

    [Fact]
    public void WrongSessionEvidenceDoesNotAuthorizeWhenActiveSessionEvidenceMismatches()
    {
        var operation = MissingAddressOperation();
        var activeMismatch = Evidence(operation, sessionId: SessionId, original: operation.OriginalValue, applied: "different-applied");
        var wrongSessionPerfectMatch = Evidence(operation, sessionId: Guid.NewGuid(), original: operation.OriginalValue, applied: operation.CurrentValue);

        var decision = RestorationAuthorizationPolicy.Authorize(operation, new[] { activeMismatch, wrongSessionPerfectMatch }, SessionId);

        Assert.Equal(OperationAuthorizationStatus.DeniedOwnershipMismatch, decision.Status);
        Assert.NotNull(decision.MatchedEvidence);
        Assert.Equal(SessionId, decision.MatchedEvidence!.SessionId);
        Assert.NotEqual(wrongSessionPerfectMatch, decision.MatchedEvidence);
        Assert.False(decision.Status == OperationAuthorizationStatus.Authorized);
    }

    [Fact]
    public void OnlyWrongSessionPerfectEvidenceIsDenied()
    {
        var operation = MissingAddressOperation();
        var wrongSessionPerfectMatch = Evidence(operation, sessionId: Guid.NewGuid(), original: operation.OriginalValue, applied: operation.CurrentValue);

        var decision = RestorationAuthorizationPolicy.Authorize(operation, new[] { wrongSessionPerfectMatch }, SessionId);

        Assert.Equal(OperationAuthorizationStatus.DeniedNoOwnership, decision.Status);
        Assert.NotNull(decision.MatchedEvidence);
        Assert.Equal(wrongSessionPerfectMatch, decision.MatchedEvidence);
    }

    [Fact]
    public void OwnedAddedRouteCanAuthorizeFutureRemoval()
    {
        var operation = AddedOperation(DryRunOperationCategory.Route, "route-1", "route-value");
        var evidence = Evidence(operation, original: "<absent>", applied: operation.CurrentValue);

        var decision = RestorationAuthorizationPolicy.Authorize(operation, new[] { evidence }, SessionId);

        Assert.Equal(OperationAuthorizationStatus.Authorized, decision.Status);
        Assert.Contains("future removal", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingBaselineRouteRequiresExactProof()
    {
        var operation = new DryRunRestorationOperation(DryRunOperationCategory.Route, DryRunAction.RestoreBaseline, "route-1", "baseline", "<absent>", "test", 1, true, false, DryRunSafetyState.SafeToPlan);
        var denied = RestorationAuthorizationPolicy.Authorize(operation, Array.Empty<OwnershipEvidence>(), SessionId);
        var authorized = RestorationAuthorizationPolicy.Authorize(operation, new[] { Evidence(operation) }, SessionId);

        Assert.Equal(OperationAuthorizationStatus.DeniedNoOwnership, denied.Status);
        Assert.Equal(OperationAuthorizationStatus.Authorized, authorized.Status);
    }

    [Fact]
    public void OwnedDnsAndAddressChangesAuthorizeRestoration()
    {
        foreach (var category in new[] { DryRunOperationCategory.Dns, DryRunOperationCategory.Address })
        {
            var operation = AddedOperation(category, category.ToString(), "current");
            operation = operation with { Action = DryRunAction.RestoreBaseline, OriginalValue = "original" };
            var decision = RestorationAuthorizationPolicy.Authorize(operation, new[] { Evidence(operation) }, SessionId);

            Assert.Equal(OperationAuthorizationStatus.Authorized, decision.Status);
        }
    }

    [Fact]
    public void UnsupportedAndUnverifiableOperationsAreDenied()
    {
        foreach (var state in new[] { DryRunSafetyState.Unsupported, DryRunSafetyState.Unverifiable })
        {
            var operation = MissingAddressOperation() with { SafetyState = state };
            var decision = RestorationAuthorizationPolicy.Authorize(operation, Array.Empty<OwnershipEvidence>(), SessionId);

            Assert.Equal(state == DryRunSafetyState.Unsupported ? OperationAuthorizationStatus.DeniedUnsupported : OperationAuthorizationStatus.DeniedUnverifiable, decision.Status);
        }
    }

    [Fact]
    public void BatchAuthorizationIsOrderedAndConservative()
    {
        var route = MissingAddressOperation() with { Category = DryRunOperationCategory.Route, ExecutionOrder = 2 };
        var address = MissingAddressOperation() with { ExecutionOrder = 1 };
        var result = new DryRunRestorationResult(new[] { route, address }, new DryRunRestorationSummary(2, 2, 2, 0, 0, false, Array.Empty<string>()));

        var batch = RestorationAuthorizationPolicy.AuthorizeBatch(result, new[] { Evidence(address) }, SessionId);

        Assert.Equal(new[] { 1, 2 }, batch.Decisions.Select(decision => decision.Operation.ExecutionOrder));
        Assert.Single(batch.AuthorizedOperations);
        Assert.Single(batch.DeniedOperations);
        Assert.False(batch.FutureAutomaticExecutionAllowed);
    }

    [Fact]
    public void AuthorizationDoesNotAlterInputsOrExposeMutationMethods()
    {
        var operation = MissingAddressOperation();
        var evidence = Evidence(operation);
        var before = evidence with { };

        _ = RestorationAuthorizationPolicy.Authorize(operation, new[] { evidence }, SessionId);

        Assert.Equal(before, evidence);
        Assert.DoesNotContain(typeof(RestorationAuthorizationPolicy).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static), method => method.Name.Contains("Apply", StringComparison.OrdinalIgnoreCase) || method.Name.Contains("Execute", StringComparison.OrdinalIgnoreCase) || method.Name.Contains("Commit", StringComparison.OrdinalIgnoreCase) || method.Name.Contains("Mutate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EvidenceModelContainsNoSecretFields()
    {
        var names = typeof(OwnershipEvidence).GetProperties().Select(property => property.Name).ToArray();

        Assert.DoesNotContain(names, name => name.Contains("password", StringComparison.OrdinalIgnoreCase) || name.Contains("token", StringComparison.OrdinalIgnoreCase) || name.Contains("private", StringComparison.OrdinalIgnoreCase) || name.Contains("credential", StringComparison.OrdinalIgnoreCase) || name.Contains("certificate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AuthorizedOperationCreatesExecutionRequest()
    {
        var operation = MissingAddressOperation();
        var authorization = RestorationAuthorizationPolicy.Authorize(operation, new[] { Evidence(operation) }, SessionId);

        var created = AuthorizedRestorationRequest.Create(operation, authorization, TransactionId, SessionId);

        Assert.Equal(operation.Category, created.Category);
        Assert.Equal(operation.TargetIdentity, created.TargetIdentity);
        Assert.Equal(operation.ExecutionOrder, created.ExecutionOrder);
        Assert.Equal(authorization.MatchedEvidence!.SessionId, created.SessionId);
        Assert.Equal(TransactionId, created.TransactionId);
    }

    [Fact]
    public void DeniedAuthorizationCannotCreateRequest()
    {
        var operation = MissingAddressOperation();
        var authorization = RestorationAuthorizationPolicy.Authorize(operation, Array.Empty<OwnershipEvidence>(), SessionId);

        var created = AuthorizedRestorationRequest.TryCreate(operation, authorization, TransactionId, SessionId, out var request, out var reason);

        Assert.False(created);
        Assert.Null(request);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void ManualReviewCannotCreateRequest()
    {
        var operation = AddedOperation(DryRunOperationCategory.Route, "route-a", "current");
        var authorization = RestorationAuthorizationPolicy.Authorize(operation, new[] { Evidence(operation, original: "<absent>", applied: operation.CurrentValue) }, SessionId);

        var created = AuthorizedRestorationRequest.TryCreate(operation, authorization, TransactionId, SessionId, out _, out var reason);

        Assert.False(created);
        Assert.Contains("manual review", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnsupportedCannotCreateRequest()
    {
        var operation = MissingAddressOperation() with { SafetyState = DryRunSafetyState.Unsupported, Action = DryRunAction.RestoreBaseline };
        var authorization = RestorationAuthorizationPolicy.Authorize(operation, new[] { Evidence(operation) }, SessionId);

        var created = AuthorizedRestorationRequest.TryCreate(operation, authorization, TransactionId, SessionId, out _, out var reason);

        Assert.False(created);
        Assert.Contains("unsupported", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnverifiableCannotCreateRequest()
    {
        var operation = MissingAddressOperation() with { SafetyState = DryRunSafetyState.Unverifiable };
        var authorization = RestorationAuthorizationPolicy.Authorize(operation, new[] { Evidence(operation) }, SessionId);

        var created = AuthorizedRestorationRequest.TryCreate(operation, authorization, TransactionId, SessionId, out _, out var reason);

        Assert.False(created);
        Assert.Contains("unverifiable", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WrongSessionCannotCreateRequest()
    {
        var operation = MissingAddressOperation();
        var authorization = RestorationAuthorizationPolicy.Authorize(operation, new[] { Evidence(operation, sessionId: Guid.NewGuid()) }, SessionId);

        var created = AuthorizedRestorationRequest.TryCreate(operation, authorization, TransactionId, SessionId, out _, out var reason);

        Assert.False(created);
        Assert.Contains("session", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AuthorizationForOperationACannotAuthorizeOperationB()
    {
        var operationA = MissingAddressOperation();
        var operationB = MissingAddressOperation() with { TargetIdentity = "different-target", OriginalValue = "different" };
        var authorization = RestorationAuthorizationPolicy.Authorize(operationA, new[] { Evidence(operationA) }, SessionId);

        var created = AuthorizedRestorationRequest.TryCreate(operationB, authorization, TransactionId, SessionId, out _, out var reason);

        Assert.False(created);
        Assert.Contains("operation", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ModifiedTargetIdentityIsRejected()
    {
        var operation = MissingAddressOperation();
        var authorization = RestorationAuthorizationPolicy.Authorize(operation, new[] { Evidence(operation) }, SessionId);
        var modified = operation with { TargetIdentity = "altered-target" };

        var created = AuthorizedRestorationRequest.TryCreate(modified, authorization, TransactionId, SessionId, out _, out var reason);

        Assert.False(created);
        Assert.Contains("target", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ModifiedActionCategoryValueIsRejected()
    {
        var operation = MissingAddressOperation();
        var authorization = RestorationAuthorizationPolicy.Authorize(operation, new[] { Evidence(operation) }, SessionId);
        var modified = operation with { Action = DryRunAction.ManualReview, OriginalValue = "tampered" };

        var created = AuthorizedRestorationRequest.TryCreate(modified, authorization, TransactionId, SessionId, out _, out var reason);

        Assert.False(created);
        Assert.Contains("original", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IncompleteRequiredValuesAreRejected()
    {
        var operation = MissingAddressOperation() with { TargetIdentity = string.Empty, OriginalValue = string.Empty };
        var authorization = RestorationAuthorizationPolicy.Authorize(operation, new[] { Evidence(operation, target: string.Empty, original: string.Empty) }, SessionId);

        var created = AuthorizedRestorationRequest.TryCreate(operation, authorization, TransactionId, SessionId, out _, out var reason);

        Assert.False(created);
        Assert.Contains("required", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeterministicBatchOrderingIsPreserved()
    {
        var first = MissingAddressOperation() with { ExecutionOrder = 2, TargetIdentity = "adapter-b" };
        var second = MissingAddressOperation() with { ExecutionOrder = 1, TargetIdentity = "adapter-a" };
        var batch = new DryRunRestorationResult(new[] { first, second }, new DryRunRestorationSummary(2, 2, 2, 0, 0, false, Array.Empty<string>()));
        var auth = new[]
        {
            RestorationAuthorizationPolicy.Authorize(first, new[] { Evidence(first, target: first.TargetIdentity) }, SessionId),
            RestorationAuthorizationPolicy.Authorize(second, new[] { Evidence(second, target: second.TargetIdentity) }, SessionId)
        };
        var combined = new BatchAuthorizationResult(auth, auth.Select(d => d.Operation).ToArray(), Array.Empty<DryRunRestorationOperation>(), Array.Empty<DryRunRestorationOperation>(), Array.Empty<string>(), true);

        var preparation = RestorationExecutionPreparation.Prepare(combined, TransactionId, SessionId);

        Assert.Equal(new[] { 1, 2 }, preparation.AuthorizedRequests.Select(request => request.ExecutionOrder));
        Assert.True(preparation.CanExecuteAutomatically);
    }

    [Fact]
    public void DuplicateSequenceIsRejected()
    {
        var first = MissingAddressOperation() with { ExecutionOrder = 1, TargetIdentity = "a" };
        var second = MissingAddressOperation() with { ExecutionOrder = 1, TargetIdentity = "b" };
        var auth = new[]
        {
            RestorationAuthorizationPolicy.Authorize(first, new[] { Evidence(first, target: first.TargetIdentity) }, SessionId),
            RestorationAuthorizationPolicy.Authorize(second, new[] { Evidence(second, target: second.TargetIdentity) }, SessionId)
        };
        var batch = new BatchAuthorizationResult(auth, auth.Select(d => d.Operation).ToArray(), Array.Empty<DryRunRestorationOperation>(), Array.Empty<DryRunRestorationOperation>(), Array.Empty<string>(), true);

        var preparation = RestorationExecutionPreparation.Prepare(batch, TransactionId, SessionId);

        Assert.False(preparation.CanExecuteAutomatically);
        Assert.Contains(preparation.BlockingReasons, reason => reason.Contains("sequence", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DuplicateOperationIdentityIsRejected()
    {
        var first = MissingAddressOperation() with { ExecutionOrder = 1, TargetIdentity = "same" };
        var second = MissingAddressOperation() with { ExecutionOrder = 2, TargetIdentity = "same" };
        var auth = new[]
        {
            RestorationAuthorizationPolicy.Authorize(first, new[] { Evidence(first, target: "same") }, SessionId),
            RestorationAuthorizationPolicy.Authorize(second, new[] { Evidence(second, target: "same") }, SessionId)
        };
        var batch = new BatchAuthorizationResult(auth, auth.Select(d => d.Operation).ToArray(), Array.Empty<DryRunRestorationOperation>(), Array.Empty<DryRunRestorationOperation>(), Array.Empty<string>(), true);

        var preparation = RestorationExecutionPreparation.Prepare(batch, TransactionId, SessionId);

        Assert.False(preparation.CanExecuteAutomatically);
        Assert.Contains(preparation.BlockingReasons, reason => reason.Contains("identity", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BatchWithOneDeniedOperationCanExecuteAutomaticallyFalse()
    {
        var authorized = MissingAddressOperation();
        var denied = MissingAddressOperation() with { TargetIdentity = "denied", ExecutionOrder = 2 };
        var auth = new[]
        {
            RestorationAuthorizationPolicy.Authorize(authorized, new[] { Evidence(authorized) }, SessionId),
            RestorationAuthorizationPolicy.Authorize(denied, Array.Empty<OwnershipEvidence>(), SessionId)
        };
        var batch = new BatchAuthorizationResult(auth, new[] { authorized }, new[] { denied }, Array.Empty<DryRunRestorationOperation>(), new[] { "No ownership evidence matches the active session, category, and target." }, false);

        var preparation = RestorationExecutionPreparation.Prepare(batch, TransactionId, SessionId);

        Assert.False(preparation.CanExecuteAutomatically);
        Assert.Contains(preparation.RejectedOperations, operation => operation.TargetIdentity == "denied");
    }

    [Fact]
    public void BatchWithOneManualReviewOperationFalse()
    {
        var authorized = MissingAddressOperation();
        var manual = AddedOperation(DryRunOperationCategory.Route, "route-a", "current");
        var auth = new[]
        {
            RestorationAuthorizationPolicy.Authorize(authorized, new[] { Evidence(authorized) }, SessionId),
            RestorationAuthorizationPolicy.Authorize(manual, new[] { Evidence(manual, original: "<absent>", applied: manual.CurrentValue) }, SessionId)
        };
        var batch = new BatchAuthorizationResult(auth, new[] { authorized }, Array.Empty<DryRunRestorationOperation>(), new[] { manual }, Array.Empty<string>(), false);

        var preparation = RestorationExecutionPreparation.Prepare(batch, TransactionId, SessionId);

        Assert.False(preparation.CanExecuteAutomatically);
    }

    [Fact]
    public void AllAuthorizedBatchReturnsTrue()
    {
        var operation = MissingAddressOperation();
        var authorization = RestorationAuthorizationPolicy.Authorize(operation, new[] { Evidence(operation) }, SessionId);
        var batch = new BatchAuthorizationResult(new[] { authorization }, new[] { operation }, Array.Empty<DryRunRestorationOperation>(), Array.Empty<DryRunRestorationOperation>(), Array.Empty<string>(), true);

        var preparation = RestorationExecutionPreparation.Prepare(batch, TransactionId, SessionId);

        Assert.True(preparation.CanExecuteAutomatically);
        Assert.Single(preparation.AuthorizedRequests);
    }

    [Fact]
    public void RepeatedPreparationProducesEquivalentOutput()
    {
        var operation = MissingAddressOperation();
        var authorization = RestorationAuthorizationPolicy.Authorize(operation, new[] { Evidence(operation) }, SessionId);
        var batch = new BatchAuthorizationResult(new[] { authorization }, new[] { operation }, Array.Empty<DryRunRestorationOperation>(), Array.Empty<DryRunRestorationOperation>(), Array.Empty<string>(), true);

        var first = RestorationExecutionPreparation.Prepare(batch, TransactionId, SessionId);
        var second = RestorationExecutionPreparation.Prepare(batch, TransactionId, SessionId);

        Assert.Equal(first.AuthorizedRequests, second.AuthorizedRequests);
        Assert.Equal(first.BlockingReasons, second.BlockingReasons);
        Assert.Equal(first.CanExecuteAutomatically, second.CanExecuteAutomatically);
    }

    [Fact]
    public void InputAuthorizationResultsRemainUnchanged()
    {
        var operation = MissingAddressOperation();
        var authorization = RestorationAuthorizationPolicy.Authorize(operation, new[] { Evidence(operation) }, SessionId);
        var before = authorization with { };
        var batch = new BatchAuthorizationResult(new[] { authorization }, new[] { operation }, Array.Empty<DryRunRestorationOperation>(), Array.Empty<DryRunRestorationOperation>(), Array.Empty<string>(), true);

        _ = RestorationExecutionPreparation.Prepare(batch, TransactionId, SessionId);

        Assert.Equal(before, authorization);
    }

    [Fact]
    public void CancellationTokenIsPartOfFutureProviderContract()
    {
        var method = typeof(IRestorationMutationProvider).GetMethod(nameof(IRestorationMutationProvider.ApplyAsync));

        Assert.NotNull(method);
        Assert.Equal(typeof(CancellationToken), method!.GetParameters()[1].ParameterType);
    }

    [Fact]
    public void ProviderAcceptsAuthorizedRestorationRequestOnly()
    {
        var method = typeof(IRestorationMutationProvider).GetMethod(nameof(IRestorationMutationProvider.ApplyAsync));

        Assert.Equal(typeof(AuthorizedRestorationRequest), method!.GetParameters()[0].ParameterType);
    }

    [Fact]
    public void NoConcreteMutationProviderImplementationExists()
    {
        var implementations = typeof(IRestorationMutationProvider).Assembly
            .GetTypes()
            .Where(type => type != typeof(IRestorationMutationProvider) && typeof(IRestorationMutationProvider).IsAssignableFrom(type))
            .ToArray();

        Assert.Empty(implementations);
    }

    [Fact]
    public void AuthorizedRequestContainsNoSecretFields()
    {
        var propertyNames = typeof(AuthorizedRestorationRequest).GetProperties().Select(property => property.Name).ToArray();

        Assert.DoesNotContain(propertyNames, name => name.Contains("password", StringComparison.OrdinalIgnoreCase) || name.Contains("token", StringComparison.OrdinalIgnoreCase) || name.Contains("private", StringComparison.OrdinalIgnoreCase) || name.Contains("credential", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExecutionBoundaryDoesNotWireProductionMutationCode()
    {
        var assembly = typeof(IRestorationMutationProvider).Assembly;
        Assert.DoesNotContain(assembly.GetTypes(), type => type.Namespace is not null && type.Namespace.Contains("LibertyRoute.Service", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(assembly.GetTypes(), type => type.Namespace is not null && type.Namespace.Contains("LibertyRoute.Desktop", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(assembly.GetTypes(), type => type.Namespace is not null && type.Namespace.Contains("LibertyRoute.Networking", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(assembly.GetTypes(), type => type.Namespace is not null && type.Namespace.Contains("LibertyRoute.Recovery", StringComparison.OrdinalIgnoreCase));
    }

    private static readonly Guid TransactionId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static RestorationDifference Single(NetworkStateSnapshot original, NetworkStateSnapshot current, RestorationCategory category)
        => Assert.Single(RestorationPlanner.CreatePlan(original, current).Differences, difference => difference.Category == category);

    private static DryRunRestorationOperation SingleOperation(RestorationPlan plan, DryRunOperationCategory category)
        => Assert.Single(DryRunRestorationExecutor.CreateDryRun(plan).Operations, operation => operation.Category == category);

    private static DryRunRestorationOperation SingleOperation(DryRunRestorationResult result, DryRunOperationCategory category)
        => Assert.Single(result.Operations, operation => operation.Category == category);

    private static readonly Guid SessionId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static DryRunRestorationOperation MissingAddressOperation()
        => AddedOperation(DryRunOperationCategory.Address, "adapter-a", "<absent>") with
        {
            Action = DryRunAction.RestoreBaseline,
            OriginalValue = "192.0.2.1",
            CurrentValue = "<absent>",
            SafetyState = DryRunSafetyState.SafeToPlan,
            ExecutionOrder = 1
        };

    private static DryRunRestorationOperation AddedOperation(DryRunOperationCategory category, string target, string current)
        => new(category, DryRunAction.ManualReview, target, "<absent>", current, "test", 1, true, false, DryRunSafetyState.ManualReview);

    private static OwnershipEvidence Evidence(DryRunRestorationOperation operation, Guid? sessionId = null, string? target = null, string? original = null, string? applied = null, bool complete = true)
        => new(sessionId ?? SessionId, operation.Category, target ?? operation.TargetIdentity, original ?? operation.OriginalValue, applied ?? operation.CurrentValue, Guid.Parse("22222222-2222-2222-2222-222222222222"), DateTimeOffset.UnixEpoch, 1, OwnershipEvidenceSource.TestFixture, complete);

    private static NetworkStateSnapshot Snapshot(AdapterState? adapter = null, IReadOnlyList<RouteState>? routes = null)
        => new(DateTimeOffset.UnixEpoch, "test", adapter is null ? Array.Empty<AdapterState>() : new[] { adapter }, routes, null);

    private static AdapterState Adapter(string id, IReadOnlyList<string>? addresses = null, IReadOnlyList<string>? gateways = null, IReadOnlyList<string>? dns = null)
        => new(id, id, "test", "Ethernet", "Up", addresses ?? Array.Empty<string>(), gateways ?? Array.Empty<string>(), dns ?? Array.Empty<string>());

    private static RouteState Route(string destination, string nextHop = "0.0.0.0", int interfaceIndex = 4, int metric = 1, string family = "2")
        => new() { Destination = destination, NextHop = nextHop, InterfaceIndex = interfaceIndex, Metric = (uint)metric, AddressFamily = family };
}
