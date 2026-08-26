using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using LibertyRoute.Restoration;

namespace LibertyRoute.Restoration.Windows;

internal enum ControlledRestorationTriggerStatus
{
    Authorized,
    Denied
}

internal sealed record ControlledRestorationTriggerDecision(
    ControlledRestorationTriggerStatus Status,
    string Reason);

internal interface IControlledRestorationActivationTrigger
{
    ControlledRestorationTriggerDecision Evaluate(
        RestorationOrchestrationPreparation preparation);
}

internal sealed class UnavailableControlledRestorationActivationTrigger
    : IControlledRestorationActivationTrigger
{
    public ControlledRestorationTriggerDecision Evaluate(
        RestorationOrchestrationPreparation preparation)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        return new ControlledRestorationTriggerDecision(
            ControlledRestorationTriggerStatus.Denied,
            "No production restoration activation trigger is available.");
    }
}

internal enum ControlledRestorationActivationStatus
{
    Authorized,
    DeniedInvalidPreparation,
    DeniedTriggerUnavailable,
    DeniedOutstandingGrant
}

internal sealed record ControlledRestorationActivationDecision(
    ControlledRestorationActivationStatus Status,
    string Reason,
    string? PreparationFingerprint,
    ControlledRestorationActivationGrant? Grant);

internal enum ControlledRestorationGrantConsumptionStatus
{
    Consumed,
    RejectedAlreadyTerminal,
    RejectedPreparationMismatch
}

internal sealed record ControlledRestorationGrantConsumption(
    ControlledRestorationGrantConsumptionStatus Status,
    string Reason)
{
    public bool IsConsumed => Status == ControlledRestorationGrantConsumptionStatus.Consumed;
}

internal interface IControlledRestorationActivationAuthority
{
    Task<ControlledRestorationActivationDecision> AuthorizeAsync(
        RestorationOrchestrationPreparation preparation,
        CancellationToken cancellationToken);
}

internal readonly record struct ControlledRestorationActivationKey(
    Guid ActiveSessionId,
    string PreparationFingerprint);

internal sealed class ControlledRestorationActivationReservation
{
}

internal static class ControlledRestorationPreparationFingerprint
{
    private const string Domain = "LibertyRoute.ControlledRestorationActivation.v1";

    internal static bool TryCreate(
        RestorationOrchestrationPreparation? preparation,
        out string fingerprint,
        out string failureReason)
    {
        fingerprint = string.Empty;
        failureReason = Validate(preparation);
        if (!string.IsNullOrEmpty(failureReason))
            return false;

        var requests = preparation!.ExecutionPreparation.AuthorizedRequests;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, Domain);
        Append(hash, preparation.ActiveSessionId.ToString("D"));
        Append(hash, requests.Count.ToString(CultureInfo.InvariantCulture));

        foreach (var request in requests)
        {
            // TransactionId binds this exact Phase 3D execution envelope. SessionId
            // remains the canonical ownership identity and registry scope.
            Append(hash, request.TransactionId.ToString("D"));
            Append(hash, request.SessionId.ToString("D"));
            Append(hash, request.OperationIdentity);
            Append(hash, ((int)request.Category).ToString(CultureInfo.InvariantCulture));
            Append(hash, ((int)request.Action).ToString(CultureInfo.InvariantCulture));
            Append(hash, request.TargetIdentity);
            Append(hash, request.OriginalValue);
            Append(hash, request.CurrentValue);
            Append(hash, request.IntendedRestorationValue);
            Append(hash, request.ExecutionOrder.ToString(CultureInfo.InvariantCulture));
            Append(hash, request.AuthorizationEvidenceId.ToString("D"));
            Append(hash, request.AutomaticallyExecutable ? "1" : "0");
        }

        fingerprint = Convert.ToHexString(hash.GetHashAndReset());
        return true;
    }

    private static string Validate(RestorationOrchestrationPreparation? preparation)
    {
        if (preparation is null)
            return "Restoration preparation is required.";
        if (preparation.ActiveSessionId == Guid.Empty)
            return "The active session id is required.";

        var executionPreparation = preparation.ExecutionPreparation;
        if (executionPreparation is null)
            return "The execution preparation is required.";
        if (!executionPreparation.CanExecuteAutomatically)
            return "The complete preparation is not automatically executable.";
        if (executionPreparation.RejectedOperations is null || executionPreparation.RejectedOperations.Count != 0)
            return "The preparation contains rejected operations.";
        if (executionPreparation.BlockingReasons is null || executionPreparation.BlockingReasons.Count != 0)
            return "The preparation contains blocking reasons.";
        if (executionPreparation.AuthorizedRequests is null || executionPreparation.AuthorizedRequests.Count == 0)
            return "The preparation contains no authorized requests.";

        var identities = new HashSet<string>(StringComparer.Ordinal);
        var orders = new HashSet<int>();
        Guid? transactionId = null;
        var previousOrder = 0;

        foreach (var request in executionPreparation.AuthorizedRequests)
        {
            if (request is null)
                return "The preparation contains a missing authorized request.";
            if (request.SessionId != preparation.ActiveSessionId)
                return "An authorized request does not match the active session.";
            if (!request.AutomaticallyExecutable)
                return "An authorized request is not automatically executable.";
            if (request.TransactionId == Guid.Empty || request.SessionId == Guid.Empty ||
                request.AuthorizationEvidenceId == Guid.Empty ||
                string.IsNullOrWhiteSpace(request.OperationIdentity) ||
                string.IsNullOrWhiteSpace(request.TargetIdentity) ||
                string.IsNullOrWhiteSpace(request.OriginalValue) ||
                string.IsNullOrWhiteSpace(request.CurrentValue) ||
                string.IsNullOrWhiteSpace(request.IntendedRestorationValue) ||
                string.IsNullOrWhiteSpace(request.AuthorizationReason) ||
                !Enum.IsDefined(request.Category) || !Enum.IsDefined(request.Action))
            {
                return "An authorized request contains malformed required metadata.";
            }
            if (request.ExecutionOrder <= 0)
                return "Execution order must be positive.";
            if (!identities.Add(request.OperationIdentity))
                return "The preparation contains a duplicate operation identity.";
            if (!orders.Add(request.ExecutionOrder))
                return "The preparation contains a duplicate execution order.";
            if (request.ExecutionOrder <= previousOrder)
                return "Authorized requests are not in deterministic execution order.";

            previousOrder = request.ExecutionOrder;
            transactionId ??= request.TransactionId;
            if (transactionId.Value != request.TransactionId)
                return "Authorized requests do not share one execution transaction id.";
        }

        return string.Empty;
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}

internal sealed class ControlledRestorationActivationGrant : IDisposable
{
    private readonly ControlledRestorationActivationKey _key;
    private readonly ControlledRestorationActivationReservation _reservation;
    private readonly Action<ControlledRestorationActivationKey, ControlledRestorationActivationReservation> _release;
    private int _terminal;

    internal ControlledRestorationActivationGrant(
        ControlledRestorationActivationKey key,
        ControlledRestorationActivationReservation reservation,
        Action<ControlledRestorationActivationKey, ControlledRestorationActivationReservation> release)
    {
        _key = key;
        _reservation = reservation;
        _release = release;
    }

    internal Guid ActiveSessionId => _key.ActiveSessionId;
    internal string PreparationFingerprint => _key.PreparationFingerprint;
    internal bool IsTerminal => Volatile.Read(ref _terminal) != 0;

    internal ControlledRestorationGrantConsumption Consume(
        RestorationOrchestrationPreparation preparation,
        CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _terminal, 1, 0) != 0)
        {
            return new ControlledRestorationGrantConsumption(
                ControlledRestorationGrantConsumptionStatus.RejectedAlreadyTerminal,
                "The activation grant has already been consumed or revoked.");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ControlledRestorationPreparationFingerprint.TryCreate(
                    preparation,
                    out var fingerprint,
                    out var failureReason) ||
                preparation.ActiveSessionId != _key.ActiveSessionId ||
                !StringComparer.Ordinal.Equals(fingerprint, _key.PreparationFingerprint))
            {
                return new ControlledRestorationGrantConsumption(
                    ControlledRestorationGrantConsumptionStatus.RejectedPreparationMismatch,
                    string.IsNullOrEmpty(failureReason)
                        ? "The activation grant is bound to a different session or preparation."
                        : $"The supplied preparation is invalid: {failureReason}");
            }

            return new ControlledRestorationGrantConsumption(
                ControlledRestorationGrantConsumptionStatus.Consumed,
                "The exact preparation-bound activation grant was consumed once.");
        }
        finally
        {
            _release(_key, _reservation);
        }
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _terminal, 1, 0) == 0)
            _release(_key, _reservation);
    }
}

internal sealed class ControlledRestorationActivationAuthority
    : IControlledRestorationActivationAuthority
{
    private static readonly ConcurrentDictionary<
        ControlledRestorationActivationKey,
        ControlledRestorationActivationReservation> Outstanding = new();

    private readonly IControlledRestorationActivationTrigger _trigger;
    private readonly Action? _onGrantReserved;

    internal ControlledRestorationActivationAuthority()
        : this(new UnavailableControlledRestorationActivationTrigger())
    {
    }

    internal ControlledRestorationActivationAuthority(
        IControlledRestorationActivationTrigger trigger,
        Action? onGrantReserved = null)
    {
        _trigger = trigger ?? throw new ArgumentNullException(nameof(trigger));
        _onGrantReserved = onGrantReserved;
    }

    public Task<ControlledRestorationActivationDecision> AuthorizeAsync(
        RestorationOrchestrationPreparation preparation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!ControlledRestorationPreparationFingerprint.TryCreate(
                preparation,
                out var fingerprint,
                out var failureReason))
        {
            return Task.FromResult(new ControlledRestorationActivationDecision(
                ControlledRestorationActivationStatus.DeniedInvalidPreparation,
                failureReason,
                null,
                null));
        }

        var triggerDecision = _trigger.Evaluate(preparation);
        if (triggerDecision.Status != ControlledRestorationTriggerStatus.Authorized)
        {
            return Task.FromResult(new ControlledRestorationActivationDecision(
                ControlledRestorationActivationStatus.DeniedTriggerUnavailable,
                triggerDecision.Reason,
                fingerprint,
                null));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var key = new ControlledRestorationActivationKey(preparation.ActiveSessionId, fingerprint);
        var reservation = new ControlledRestorationActivationReservation();
        if (!Outstanding.TryAdd(key, reservation))
        {
            return Task.FromResult(new ControlledRestorationActivationDecision(
                ControlledRestorationActivationStatus.DeniedOutstandingGrant,
                "An activation grant for this exact session and preparation is already outstanding.",
                fingerprint,
                null));
        }

        var grant = new ControlledRestorationActivationGrant(key, reservation, Release);
        try
        {
            _onGrantReserved?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch
        {
            grant.Dispose();
            throw;
        }

        return Task.FromResult(new ControlledRestorationActivationDecision(
            ControlledRestorationActivationStatus.Authorized,
            "The controlled trigger authorized one outstanding grant for the exact preparation.",
            fingerprint,
            grant));
    }

    private static void Release(
        ControlledRestorationActivationKey key,
        ControlledRestorationActivationReservation reservation)
    {
        var pair = new KeyValuePair<
            ControlledRestorationActivationKey,
            ControlledRestorationActivationReservation>(key, reservation);
        ((ICollection<KeyValuePair<
            ControlledRestorationActivationKey,
            ControlledRestorationActivationReservation>>)Outstanding).Remove(pair);
    }
}
