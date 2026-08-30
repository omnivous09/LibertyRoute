using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LibertyRoute.Core;

public enum ConnectionState
{
    Disconnected,
    CapturingState,
    SnapshotCommitted,
    Connecting,
    Connected,
    RollbackRequired,
    RollingBack,
    Verifying,
    RestorationFailed
}

public enum DnsConfigurationSource
{
    Unknown,
    Automatic,
    Static,
    Mixed
}

public sealed record DnsInterfaceState(
    string InterfaceId,
    string InterfaceName,
    bool IsUp,
    IReadOnlyList<string> DnsServers,
    IReadOnlyList<string>? IPv4DnsServers = null,
    IReadOnlyList<string>? IPv6DnsServers = null,
    DnsConfigurationSource IPv4ConfigurationSource = DnsConfigurationSource.Unknown,
    DnsConfigurationSource IPv6ConfigurationSource = DnsConfigurationSource.Unknown,
    IReadOnlyList<string>? IPv4StaticDnsServers = null,
    IReadOnlyList<string>? IPv4DhcpDnsServers = null,
    IReadOnlyList<string>? IPv6StaticDnsServers = null,
    IReadOnlyList<string>? IPv6DhcpDnsServers = null);

public sealed record GatewayState(string InterfaceId, string Address);

public sealed record AdapterState(
    string Id,
    string Name,
    string Description,
    string NetworkInterfaceType,
    string OperationalStatus,
    IReadOnlyList<string> UnicastAddresses,
    IReadOnlyList<string> Gateways,
    IReadOnlyList<string> DnsServers);

public sealed record NetworkStateSnapshot(
    DateTimeOffset CapturedAtUtc,
    string MachineName,
    IReadOnlyList<AdapterState> Adapters,
    IReadOnlyList<RouteState>? Routes = null,
    IReadOnlyList<DnsInterfaceState>? DnsInterfaces = null);

public sealed record OwnedNetworkChange(
    Guid ChangeId,
    string Kind,
    string Target,
    string? BeforeJson,
    string? AfterJson,
    DateTimeOffset RecordedAtUtc);

public enum RecoveryPhase
{
    IntentRecorded,
    Prepared,
    ExecutionStarted,
    ExecutionCompleted,
    BaselineVerified,
    LedgerFinalizing,
    LedgerFinalized,
    TerminalCommitted,
    ManualRecoveryRequired
}

public sealed record RecoveryEvidenceBinding(
    Guid EvidenceId,
    string EvidenceIdentity,
    string EvidenceFingerprint)
{
    public static RecoveryEvidenceBinding Create(Guid evidenceId, string evidenceIdentity, string evidenceFingerprint)
    {
        if (evidenceId == Guid.Empty)
            throw new ArgumentException("Evidence id is required.", nameof(evidenceId));
        if (string.IsNullOrWhiteSpace(evidenceIdentity))
            throw new ArgumentException("Evidence identity is required.", nameof(evidenceIdentity));
        if (string.IsNullOrWhiteSpace(evidenceFingerprint))
            throw new ArgumentException("Evidence fingerprint is required.", nameof(evidenceFingerprint));
        return new RecoveryEvidenceBinding(evidenceId, evidenceIdentity, evidenceFingerprint);
    }
}

public sealed record RecoveryManifest(
    Guid RecoveryAttemptId,
    Guid SessionId,
    string? OwnerSid,
    string AuthorizedTransactionFingerprint,
    IReadOnlyList<RecoveryEvidenceBinding> OriginalEvidenceBindings,
    Guid RecoveryOwnershipChangeId,
    string OperationIdentity,
    string OperationCategory,
    string TargetIdentity,
    string OriginalValue,
    string AppliedValue,
    int SequenceOrder,
    string? PreparationFingerprint = null)
{
    public static RecoveryManifest Create(
        Guid recoveryAttemptId,
        Guid sessionId,
        string? ownerSid,
        string authorizedTransactionFingerprint,
        IEnumerable<RecoveryEvidenceBinding> originalEvidenceBindings,
        Guid recoveryOwnershipChangeId,
        string operationIdentity,
        string operationCategory,
        string targetIdentity,
        string originalValue,
        string appliedValue,
        int sequenceOrder,
        string? preparationFingerprint = null)
    {
        if (recoveryAttemptId == Guid.Empty)
            throw new ArgumentException("Recovery attempt id is required.", nameof(recoveryAttemptId));
        if (sessionId == Guid.Empty)
            throw new ArgumentException("Session id is required.", nameof(sessionId));
        if (string.IsNullOrWhiteSpace(authorizedTransactionFingerprint))
            throw new ArgumentException("Authorized transaction fingerprint is required.", nameof(authorizedTransactionFingerprint));
        if (string.IsNullOrWhiteSpace(operationIdentity))
            throw new ArgumentException("Operation identity is required.", nameof(operationIdentity));
        if (string.IsNullOrWhiteSpace(operationCategory))
            throw new ArgumentException("Operation category is required.", nameof(operationCategory));
        if (string.IsNullOrWhiteSpace(targetIdentity))
            throw new ArgumentException("Target identity is required.", nameof(targetIdentity));
        if (string.IsNullOrWhiteSpace(originalValue))
            throw new ArgumentException("Original value is required.", nameof(originalValue));
        if (string.IsNullOrWhiteSpace(appliedValue))
            throw new ArgumentException("Applied value is required.", nameof(appliedValue));

        var bindings = (originalEvidenceBindings ?? Array.Empty<RecoveryEvidenceBinding>())
            .OrderBy(binding => binding.EvidenceId)
            .ThenBy(binding => binding.EvidenceIdentity, StringComparer.Ordinal)
            .ToArray();
        if (bindings.Length == 0)
            throw new ArgumentException("At least one original evidence binding is required.", nameof(originalEvidenceBindings));
        if (string.IsNullOrWhiteSpace(preparationFingerprint))
            throw new ArgumentException("Preparation fingerprint is required.", nameof(preparationFingerprint));

        return new RecoveryManifest(
            recoveryAttemptId,
            sessionId,
            ownerSid,
            authorizedTransactionFingerprint,
            bindings,
            recoveryOwnershipChangeId,
            operationIdentity,
            operationCategory,
            targetIdentity,
            originalValue,
            appliedValue,
            sequenceOrder,
            preparationFingerprint);
    }

    public static string FormatCanonicalEvidenceBindings(IEnumerable<RecoveryEvidenceBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);

        return string.Join("|", bindings
            .OrderBy(binding => binding.EvidenceId)
            .ThenBy(binding => binding.EvidenceIdentity, StringComparer.Ordinal)
            .Select(binding => $"{binding.EvidenceId:D}:{binding.EvidenceIdentity}:{binding.EvidenceFingerprint}"));
    }
}

public sealed record RecoveryCompletion(
    Guid RecoveryAttemptId,
    RecoveryPhase Phase,
    string AuthorizedTransactionFingerprint,
    string RecoveryManifestFingerprint,
    string OriginalEvidenceBindings,
    string? PreparationFingerprint = null,
    DateTimeOffset? IntentRecordedAtUtc = null,
    DateTimeOffset? PreparedAtUtc = null,
    DateTimeOffset? ExecutionStartedAtUtc = null,
    DateTimeOffset? ExecutionCompletedAtUtc = null,
    DateTimeOffset? BaselineVerifiedAtUtc = null,
    DateTimeOffset? LedgerFinalizingAtUtc = null,
    DateTimeOffset? LedgerFinalizedAtUtc = null,
    DateTimeOffset? TerminalCommittedAtUtc = null,
    string? FailureReason = null,
    string? ManualRecoveryNote = null)
{
    public RecoveryManifest? Manifest { get; init; }
    public RecoveryPhase? ManualRecoveryOriginPhase { get; init; }
    public string ManifestIdentity => RecoveryManifestFingerprint;
    public string RecoveryManifestIdentity => RecoveryManifestFingerprint;
    public bool IsTerminal => Phase == RecoveryPhase.TerminalCommitted || Phase == RecoveryPhase.ManualRecoveryRequired;

    public static bool IsValidTransition(RecoveryPhase current, RecoveryPhase next)
    {
        if (current == next)
            return false;

        return (current, next) switch
        {
            (RecoveryPhase.IntentRecorded, RecoveryPhase.Prepared) => true,
            (RecoveryPhase.IntentRecorded, RecoveryPhase.ManualRecoveryRequired) => true,
            (RecoveryPhase.Prepared, RecoveryPhase.ExecutionStarted) => true,
            (RecoveryPhase.Prepared, RecoveryPhase.ManualRecoveryRequired) => true,
            (RecoveryPhase.ExecutionStarted, RecoveryPhase.ExecutionCompleted) => true,
            (RecoveryPhase.ExecutionStarted, RecoveryPhase.ManualRecoveryRequired) => true,
            (RecoveryPhase.ExecutionCompleted, RecoveryPhase.BaselineVerified) => true,
            (RecoveryPhase.ExecutionCompleted, RecoveryPhase.ManualRecoveryRequired) => true,
            (RecoveryPhase.BaselineVerified, RecoveryPhase.LedgerFinalizing) => true,
            (RecoveryPhase.BaselineVerified, RecoveryPhase.ManualRecoveryRequired) => true,
            (RecoveryPhase.LedgerFinalizing, RecoveryPhase.LedgerFinalized) => true,
            (RecoveryPhase.LedgerFinalizing, RecoveryPhase.ManualRecoveryRequired) => true,
            (RecoveryPhase.LedgerFinalized, RecoveryPhase.TerminalCommitted) => true,
            (RecoveryPhase.LedgerFinalized, RecoveryPhase.ManualRecoveryRequired) => true,
            _ => false
        };
    }

    public static string Validate(RecoveryCompletion completion)
    {
        ArgumentNullException.ThrowIfNull(completion);

        if (completion.RecoveryAttemptId == Guid.Empty)
            return "Recovery attempt id is required.";
        if (!Enum.IsDefined(completion.Phase))
            return "Recovery phase is invalid.";
        if (string.IsNullOrWhiteSpace(completion.AuthorizedTransactionFingerprint))
            return "Authorized transaction fingerprint is required.";
        if (string.IsNullOrWhiteSpace(completion.RecoveryManifestFingerprint))
            return "Recovery manifest fingerprint is required.";
        if (string.IsNullOrWhiteSpace(completion.OriginalEvidenceBindings))
            return "Original evidence bindings are required.";

        if (completion.Phase == RecoveryPhase.ManualRecoveryRequired)
        {
            if (!completion.ManualRecoveryOriginPhase.HasValue || !Enum.IsDefined(completion.ManualRecoveryOriginPhase.Value))
                return "Manual recovery requires an explicit origin phase.";
            if (!IsValidManualRecoveryOrigin(completion.ManualRecoveryOriginPhase.Value))
                return "Manual recovery origin phase is invalid.";
            if (string.IsNullOrWhiteSpace(completion.FailureReason) && string.IsNullOrWhiteSpace(completion.ManualRecoveryNote))
                return "Manual recovery requires failure or recovery note information.";
        }

        var reachedPhase = completion.Phase == RecoveryPhase.ManualRecoveryRequired
            ? completion.ManualRecoveryOriginPhase!.Value
            : completion.Phase;

        if (string.IsNullOrWhiteSpace(completion.PreparationFingerprint))
            return "Recovery requires a preparation fingerprint.";

        if (!completion.IntentRecordedAtUtc.HasValue)
            return "Recovery requires an intent-recorded timestamp.";
        if (RequiresPreparedTimestamp(reachedPhase) && !completion.PreparedAtUtc.HasValue)
            return "Prepared or later recovery requires a prepared timestamp.";
        if (RequiresExecutionStarted(reachedPhase) && !completion.ExecutionStartedAtUtc.HasValue)
            return "Execution started requires an execution-started timestamp.";
        if (RequiresExecutionCompleted(reachedPhase) && !completion.ExecutionCompletedAtUtc.HasValue)
            return "Execution completed requires an execution-completed timestamp.";
        if (RequiresBaselineVerified(reachedPhase) && !completion.BaselineVerifiedAtUtc.HasValue)
            return "Baseline verification requires a baseline-verification timestamp.";
        if (RequiresLedgerFinalizing(reachedPhase) && !completion.LedgerFinalizingAtUtc.HasValue)
            return "Ledger finalizing requires a ledger-finalizing timestamp.";
        if (RequiresLedgerFinalized(reachedPhase) && !completion.LedgerFinalizedAtUtc.HasValue)
            return "Ledger finalized requires a ledger-finalized timestamp.";
        if (reachedPhase == RecoveryPhase.TerminalCommitted && !completion.TerminalCommittedAtUtc.HasValue)
            return "Terminal commit requires terminal-committed timestamp.";

        if (HasTimestampsAfter(completion, reachedPhase))
            return completion.Phase == RecoveryPhase.ManualRecoveryRequired
                ? "Manual recovery contains timestamps for phases after its origin phase."
                : "Recovery contains timestamps for phases after its declared phase.";

        if (completion.ManualRecoveryOriginPhase.HasValue && completion.Phase != RecoveryPhase.ManualRecoveryRequired)
            return "Manual recovery origin phase is only valid for ManualRecoveryRequired states.";

        if (completion.IntentRecordedAtUtc.HasValue && completion.PreparedAtUtc.HasValue && completion.PreparedAtUtc.Value < completion.IntentRecordedAtUtc.Value)
            return "Prepared timestamp must not precede intent-recorded timestamp.";
        if (completion.PreparedAtUtc.HasValue && completion.ExecutionStartedAtUtc.HasValue && completion.ExecutionStartedAtUtc.Value < completion.PreparedAtUtc.Value)
            return "Execution-started timestamp must not precede prepared timestamp.";
        if (completion.ExecutionStartedAtUtc.HasValue && completion.ExecutionCompletedAtUtc.HasValue && completion.ExecutionCompletedAtUtc.Value < completion.ExecutionStartedAtUtc.Value)
            return "Execution-completed timestamp must not precede execution-started timestamp.";
        if (completion.ExecutionCompletedAtUtc.HasValue && completion.BaselineVerifiedAtUtc.HasValue && completion.BaselineVerifiedAtUtc.Value < completion.ExecutionCompletedAtUtc.Value)
            return "Baseline verification timestamp must not precede execution-completed timestamp.";
        if (completion.BaselineVerifiedAtUtc.HasValue && completion.LedgerFinalizingAtUtc.HasValue && completion.LedgerFinalizingAtUtc.Value < completion.BaselineVerifiedAtUtc.Value)
            return "Ledger-finalizing timestamp must not precede baseline verification timestamp.";
        if (completion.LedgerFinalizingAtUtc.HasValue && completion.LedgerFinalizedAtUtc.HasValue && completion.LedgerFinalizedAtUtc.Value < completion.LedgerFinalizingAtUtc.Value)
            return "Ledger-finalized timestamp must not precede ledger-finalizing timestamp.";
        if (completion.LedgerFinalizedAtUtc.HasValue && completion.TerminalCommittedAtUtc.HasValue && completion.TerminalCommittedAtUtc.Value < completion.LedgerFinalizedAtUtc.Value)
            return "Terminal-committed timestamp must not precede ledger-finalized timestamp.";

        return string.Empty;
    }

    private static bool IsValidManualRecoveryOrigin(RecoveryPhase phase) => phase switch
    {
        RecoveryPhase.IntentRecorded or
        RecoveryPhase.Prepared or
        RecoveryPhase.ExecutionStarted or
        RecoveryPhase.ExecutionCompleted or
        RecoveryPhase.BaselineVerified or
        RecoveryPhase.LedgerFinalizing or
        RecoveryPhase.LedgerFinalized => true,
        _ => false
    };

    private static bool RequiresPreparedTimestamp(RecoveryPhase phase) => phase is not RecoveryPhase.IntentRecorded;
    private static bool RequiresExecutionStarted(RecoveryPhase phase) => phase is RecoveryPhase.ExecutionStarted or RecoveryPhase.ExecutionCompleted or RecoveryPhase.BaselineVerified or RecoveryPhase.LedgerFinalizing or RecoveryPhase.LedgerFinalized or RecoveryPhase.TerminalCommitted;
    private static bool RequiresExecutionCompleted(RecoveryPhase phase) => phase is RecoveryPhase.ExecutionCompleted or RecoveryPhase.BaselineVerified or RecoveryPhase.LedgerFinalizing or RecoveryPhase.LedgerFinalized or RecoveryPhase.TerminalCommitted;
    private static bool RequiresBaselineVerified(RecoveryPhase phase) => phase is RecoveryPhase.BaselineVerified or RecoveryPhase.LedgerFinalizing or RecoveryPhase.LedgerFinalized or RecoveryPhase.TerminalCommitted;
    private static bool RequiresLedgerFinalizing(RecoveryPhase phase) => phase is RecoveryPhase.LedgerFinalizing or RecoveryPhase.LedgerFinalized or RecoveryPhase.TerminalCommitted;
    private static bool RequiresLedgerFinalized(RecoveryPhase phase) => phase is RecoveryPhase.LedgerFinalized or RecoveryPhase.TerminalCommitted;

    private static bool HasTimestampsAfter(RecoveryCompletion completion, RecoveryPhase phase) => phase switch
    {
        RecoveryPhase.IntentRecorded => completion.PreparedAtUtc.HasValue || completion.ExecutionStartedAtUtc.HasValue || completion.ExecutionCompletedAtUtc.HasValue || completion.BaselineVerifiedAtUtc.HasValue || completion.LedgerFinalizingAtUtc.HasValue || completion.LedgerFinalizedAtUtc.HasValue || completion.TerminalCommittedAtUtc.HasValue,
        RecoveryPhase.Prepared => completion.ExecutionStartedAtUtc.HasValue || completion.ExecutionCompletedAtUtc.HasValue || completion.BaselineVerifiedAtUtc.HasValue || completion.LedgerFinalizingAtUtc.HasValue || completion.LedgerFinalizedAtUtc.HasValue || completion.TerminalCommittedAtUtc.HasValue,
        RecoveryPhase.ExecutionStarted => completion.ExecutionCompletedAtUtc.HasValue || completion.BaselineVerifiedAtUtc.HasValue || completion.LedgerFinalizingAtUtc.HasValue || completion.LedgerFinalizedAtUtc.HasValue || completion.TerminalCommittedAtUtc.HasValue,
        RecoveryPhase.ExecutionCompleted => completion.BaselineVerifiedAtUtc.HasValue || completion.LedgerFinalizingAtUtc.HasValue || completion.LedgerFinalizedAtUtc.HasValue || completion.TerminalCommittedAtUtc.HasValue,
        RecoveryPhase.BaselineVerified => completion.LedgerFinalizingAtUtc.HasValue || completion.LedgerFinalizedAtUtc.HasValue || completion.TerminalCommittedAtUtc.HasValue,
        RecoveryPhase.LedgerFinalizing => completion.LedgerFinalizedAtUtc.HasValue || completion.TerminalCommittedAtUtc.HasValue,
        RecoveryPhase.LedgerFinalized => completion.TerminalCommittedAtUtc.HasValue,
        RecoveryPhase.TerminalCommitted => false,
        _ => true
    };

    public static string ValidateDurableManifest(RecoveryCompletion completion, NetworkTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(completion);
        ArgumentNullException.ThrowIfNull(transaction);

        if (completion.Manifest is null)
            return "Durable recovery manifest is required.";

        var manifest = completion.Manifest;

        var structuralFailure = ValidateManifestStructure(manifest);
        if (!string.IsNullOrEmpty(structuralFailure))
            return structuralFailure;

        if (manifest.RecoveryAttemptId != completion.RecoveryAttemptId)
            return "Manifest recovery attempt ID does not match completion.";

        if (manifest.SessionId != transaction.SessionId)
            return "Manifest session ID does not match transaction.";

        if (!StringComparer.Ordinal.Equals(manifest.OwnerSid, transaction.OwnerSid))
            return "Manifest owner SID does not match transaction.";

        if (!StringComparer.Ordinal.Equals(manifest.AuthorizedTransactionFingerprint, completion.AuthorizedTransactionFingerprint))
            return "Manifest authorized transaction fingerprint does not match completion.";

        if (!StringComparer.Ordinal.Equals(manifest.PreparationFingerprint, completion.PreparationFingerprint))
            return "Manifest preparation fingerprint does not match completion.";

        var canonicalEvidenceBindings = RecoveryManifest.FormatCanonicalEvidenceBindings(manifest.OriginalEvidenceBindings);
        if (!StringComparer.Ordinal.Equals(canonicalEvidenceBindings, completion.OriginalEvidenceBindings))
            return "Manifest original evidence bindings do not match completion.";

        string recomputedManifestFingerprint;
        try
        {
            recomputedManifestFingerprint = RecoveryFingerprinting.ComputeRecoveryManifestFingerprint(manifest);
        }
        catch (Exception ex)
        {
            return $"Failed to recompute manifest fingerprint: {ex.Message}";
        }

        if (!StringComparer.Ordinal.Equals(recomputedManifestFingerprint, completion.RecoveryManifestFingerprint))
            return "Manifest fingerprint does not match recomputed fingerprint.";

        string recomputedAuthorizedFingerprint;
        try
        {
            recomputedAuthorizedFingerprint = RecoveryFingerprinting.ComputeAuthorizedTransactionFingerprint(transaction);
        }
        catch (Exception ex)
        {
            return $"Failed to recompute authorized fingerprint: {ex.Message}";
        }

        if (!StringComparer.Ordinal.Equals(recomputedAuthorizedFingerprint, completion.AuthorizedTransactionFingerprint))
            return "Authorized transaction fingerprint does not match recomputed fingerprint.";

        return string.Empty;
    }

    private static string ValidateManifestStructure(RecoveryManifest manifest)
    {
        if (manifest.RecoveryAttemptId == Guid.Empty)
            return "Manifest recovery attempt id is required.";
        if (manifest.SessionId == Guid.Empty)
            return "Manifest session id is required.";
        if (string.IsNullOrWhiteSpace(manifest.AuthorizedTransactionFingerprint))
            return "Manifest authorized transaction fingerprint is required.";
        if (manifest.OriginalEvidenceBindings is null || manifest.OriginalEvidenceBindings.Count == 0)
            return "Manifest requires at least one original evidence binding.";
        if (manifest.RecoveryOwnershipChangeId == Guid.Empty)
            return "Manifest recovery ownership change id is required.";
        if (string.IsNullOrWhiteSpace(manifest.OperationIdentity))
            return "Manifest operation identity is required.";
        if (string.IsNullOrWhiteSpace(manifest.OperationCategory))
            return "Manifest operation category is required.";
        if (string.IsNullOrWhiteSpace(manifest.TargetIdentity))
            return "Manifest target identity is required.";
        if (string.IsNullOrWhiteSpace(manifest.OriginalValue))
            return "Manifest original value is required.";
        if (string.IsNullOrWhiteSpace(manifest.AppliedValue))
            return "Manifest applied value is required.";
        if (manifest.SequenceOrder < 0)
            return "Manifest sequence order must not be negative.";
        if (string.IsNullOrWhiteSpace(manifest.PreparationFingerprint))
            return "Manifest preparation fingerprint is required.";

        for (var index = 0; index < manifest.OriginalEvidenceBindings.Count; index++)
        {
            var binding = manifest.OriginalEvidenceBindings[index];
            if (binding is null)
                return $"Manifest evidence binding at index {index} is required.";
            if (binding.EvidenceId == Guid.Empty)
                return $"Manifest evidence binding at index {index} requires an evidence id.";
            if (string.IsNullOrWhiteSpace(binding.EvidenceIdentity))
                return $"Manifest evidence binding at index {index} requires an evidence identity.";
            if (string.IsNullOrWhiteSpace(binding.EvidenceFingerprint))
                return $"Manifest evidence binding at index {index} requires an evidence fingerprint.";
        }

        var canonicalOrder = manifest.OriginalEvidenceBindings
            .OrderBy(binding => binding.EvidenceId)
            .ThenBy(binding => binding.EvidenceIdentity, StringComparer.Ordinal)
            .ToArray();
        if (!manifest.OriginalEvidenceBindings.SequenceEqual(canonicalOrder))
            return "Manifest original evidence bindings are not in canonical order.";

        return string.Empty;
    }

    public RecoveryCompletion WithPhase(
        RecoveryPhase newPhase,
        DateTimeOffset? atUtc = null,
        string? failureReason = null,
        string? manualRecoveryNote = null)
    {
        var currentFailure = Validate(this);
        if (!string.IsNullOrEmpty(currentFailure))
            throw new InvalidOperationException($"Current recovery completion is invalid: {currentFailure}");
        if (!IsValidTransition(Phase, newPhase))
            throw new InvalidOperationException($"Recovery phase transition {Phase} -> {newPhase} is invalid.");
        if (Phase == newPhase)
            throw new InvalidOperationException("Recovery phase transitions must advance to a different phase.");

        var result = new RecoveryCompletion(
            RecoveryAttemptId,
            newPhase,
            AuthorizedTransactionFingerprint,
            RecoveryManifestFingerprint,
            OriginalEvidenceBindings,
            PreparationFingerprint,
            IntentRecordedAtUtc,
            newPhase == RecoveryPhase.Prepared ? atUtc ?? DateTimeOffset.UtcNow : PreparedAtUtc,
            newPhase == RecoveryPhase.ExecutionStarted ? atUtc ?? DateTimeOffset.UtcNow : ExecutionStartedAtUtc,
            newPhase == RecoveryPhase.ExecutionCompleted ? atUtc ?? DateTimeOffset.UtcNow : ExecutionCompletedAtUtc,
            newPhase == RecoveryPhase.BaselineVerified ? atUtc ?? DateTimeOffset.UtcNow : BaselineVerifiedAtUtc,
            newPhase == RecoveryPhase.LedgerFinalizing ? atUtc ?? DateTimeOffset.UtcNow : LedgerFinalizingAtUtc,
            newPhase == RecoveryPhase.LedgerFinalized ? atUtc ?? DateTimeOffset.UtcNow : LedgerFinalizedAtUtc,
            newPhase == RecoveryPhase.TerminalCommitted ? atUtc ?? DateTimeOffset.UtcNow : TerminalCommittedAtUtc,
            newPhase == RecoveryPhase.ManualRecoveryRequired ? failureReason : FailureReason,
            newPhase == RecoveryPhase.ManualRecoveryRequired ? manualRecoveryNote : ManualRecoveryNote)
        {
            Manifest = Manifest,
            ManualRecoveryOriginPhase = newPhase == RecoveryPhase.ManualRecoveryRequired ? ManualRecoveryOriginPhase ?? Phase : null
        };

        var resultFailure = Validate(result);
        if (!string.IsNullOrEmpty(resultFailure))
            throw new InvalidOperationException($"Resulting recovery completion is invalid: {resultFailure}");
        return result;
    }

    public static string ValidateImmutableIdentity(NetworkTransaction current, NetworkTransaction proposed)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(proposed);
        var left = current.RecoveryCompletion;
        var right = proposed.RecoveryCompletion;
        if (left is null || right is null || left.Manifest is null || right.Manifest is null)
            return "Both recovery transactions require durable recovery manifests.";
        if (current.SessionId != proposed.SessionId || !StringComparer.Ordinal.Equals(current.OwnerSid, proposed.OwnerSid))
            return "Recovery transaction session or owner identity changed.";
        if (left.RecoveryAttemptId != right.RecoveryAttemptId ||
            !StringComparer.Ordinal.Equals(left.AuthorizedTransactionFingerprint, right.AuthorizedTransactionFingerprint) ||
            !StringComparer.Ordinal.Equals(left.RecoveryManifestFingerprint, right.RecoveryManifestFingerprint) ||
            !StringComparer.Ordinal.Equals(left.PreparationFingerprint, right.PreparationFingerprint) ||
            !StringComparer.Ordinal.Equals(left.OriginalEvidenceBindings, right.OriginalEvidenceBindings))
            return "Recovery completion immutable identity changed.";

        var a = left.Manifest;
        var b = right.Manifest;
        if (a.RecoveryAttemptId != b.RecoveryAttemptId || a.SessionId != b.SessionId ||
            !StringComparer.Ordinal.Equals(a.OwnerSid, b.OwnerSid) ||
            !StringComparer.Ordinal.Equals(a.AuthorizedTransactionFingerprint, b.AuthorizedTransactionFingerprint) ||
            a.RecoveryOwnershipChangeId != b.RecoveryOwnershipChangeId ||
            !StringComparer.Ordinal.Equals(a.OperationIdentity, b.OperationIdentity) ||
            !StringComparer.Ordinal.Equals(a.OperationCategory, b.OperationCategory) ||
            !StringComparer.Ordinal.Equals(a.TargetIdentity, b.TargetIdentity) ||
            !StringComparer.Ordinal.Equals(a.OriginalValue, b.OriginalValue) ||
            !StringComparer.Ordinal.Equals(a.AppliedValue, b.AppliedValue) ||
            a.SequenceOrder != b.SequenceOrder ||
            !StringComparer.Ordinal.Equals(a.PreparationFingerprint, b.PreparationFingerprint) ||
            a.OriginalEvidenceBindings.Count != b.OriginalEvidenceBindings.Count)
            return "Typed recovery manifest immutable identity changed.";

        for (var index = 0; index < a.OriginalEvidenceBindings.Count; index++)
        {
            var x = a.OriginalEvidenceBindings[index];
            var y = b.OriginalEvidenceBindings[index];
            if (x.EvidenceId != y.EvidenceId ||
                !StringComparer.Ordinal.Equals(x.EvidenceIdentity, y.EvidenceIdentity) ||
                !StringComparer.Ordinal.Equals(x.EvidenceFingerprint, y.EvidenceFingerprint))
                return "Typed recovery evidence binding immutable identity changed.";
        }

        return string.Empty;
    }
}

public sealed record NetworkTransaction(
    Guid SessionId,
    ConnectionState State,
    DateTimeOffset StartedAtUtc,
    NetworkStateSnapshot Snapshot,
    IReadOnlyList<OwnedNetworkChange> Changes,
    string? EngineId,
    string? LastError,
    string? OwnerSid = null,
    RecoveryCompletion? RecoveryCompletion = null);

public static class OwnershipIdentity
{
    public static Guid DeriveChangeId(Guid sessionId, string operationIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationIdentity);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sessionId.ToString("D") + "\n" + operationIdentity));
        return new Guid(hash.AsSpan(0, 16));
    }
}

public static class RecoveryFingerprinting
{
    public static string ComputeAuthorizedTransactionFingerprint(NetworkTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        var payload = new
        {
            transaction.SessionId,
            transaction.State,
            transaction.StartedAtUtc,
            transaction.EngineId,
            transaction.OwnerSid,
            snapshot = new
            {
                transaction.Snapshot.CapturedAtUtc,
                transaction.Snapshot.MachineName,
                adapters = transaction.Snapshot.Adapters
                    .OrderBy(adapter => adapter.Id, StringComparer.Ordinal)
                    .Select(adapter => new
                    {
                        adapter.Id,
                        adapter.Name,
                        adapter.Description,
                        adapter.NetworkInterfaceType,
                        adapter.OperationalStatus,
                        unicastAddresses = adapter.UnicastAddresses.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                        gateways = adapter.Gateways.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                        dnsServers = adapter.DnsServers.OrderBy(value => value, StringComparer.Ordinal).ToArray()
                    })
                    .ToArray(),
                routes = (transaction.Snapshot.Routes ?? Array.Empty<RouteState>())
                    .OrderBy(route => route.Destination, StringComparer.Ordinal)
                    .ThenBy(route => route.NextHop, StringComparer.Ordinal)
                    .ThenBy(route => route.InterfaceIndex)
                    .ThenBy(route => route.AddressFamily, StringComparer.Ordinal)
                    .Select(route => new
                    {
                        route.Destination,
                        route.NextHop,
                        route.InterfaceIndex,
                        route.Metric,
                        route.AddressFamily
                    })
                    .ToArray(),
                dnsInterfaces = (transaction.Snapshot.DnsInterfaces ?? Array.Empty<DnsInterfaceState>())
                    .OrderBy(dns => dns.InterfaceId, StringComparer.Ordinal)
                    .Select(dns => new
                    {
                        dns.InterfaceId,
                        dns.InterfaceName,
                        dns.IsUp,
                        dnsServers = dns.DnsServers.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                        ipv4DnsServers = dns.IPv4DnsServers is null ? null : dns.IPv4DnsServers.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                        ipv6DnsServers = dns.IPv6DnsServers is null ? null : dns.IPv6DnsServers.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                        dns.IPv4ConfigurationSource,
                        dns.IPv6ConfigurationSource,
                        ipv4StaticDnsServers = dns.IPv4StaticDnsServers is null ? null : dns.IPv4StaticDnsServers.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                        ipv4DhcpDnsServers = dns.IPv4DhcpDnsServers is null ? null : dns.IPv4DhcpDnsServers.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                        ipv6StaticDnsServers = dns.IPv6StaticDnsServers is null ? null : dns.IPv6StaticDnsServers.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                        ipv6DhcpDnsServers = dns.IPv6DhcpDnsServers is null ? null : dns.IPv6DhcpDnsServers.OrderBy(value => value, StringComparer.Ordinal).ToArray()
                    })
                    .ToArray()
            },
            changes = transaction.Changes
                .OrderBy(change => change.ChangeId)
                .ThenBy(change => change.RecordedAtUtc)
                .Select(change => new
                {
                    change.ChangeId,
                    change.Kind,
                    change.Target,
                    change.BeforeJson,
                    change.AfterJson,
                    change.RecordedAtUtc
                })
                .ToArray()
        };

        return ComputeCanonicalHash(payload);
    }

    public static string ComputeRecoveryManifestFingerprint(NetworkTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        if (transaction.RecoveryCompletion is null)
            throw new InvalidOperationException("A recovery completion is required to compute a recovery manifest fingerprint.");
        if (transaction.RecoveryCompletion.Manifest is null)
            throw new InvalidOperationException("A durable recovery manifest is required; cannot synthesize manifest identity.");

        return ComputeRecoveryManifestFingerprint(transaction.RecoveryCompletion.Manifest);
    }

    public static string ComputeRecoveryManifestFingerprint(RecoveryManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var payload = new
        {
            manifest.RecoveryAttemptId,
            manifest.SessionId,
            manifest.OwnerSid,
            manifest.AuthorizedTransactionFingerprint,
            originalEvidenceBindings = manifest.OriginalEvidenceBindings
                .OrderBy(binding => binding.EvidenceId)
                .ThenBy(binding => binding.EvidenceIdentity, StringComparer.Ordinal)
                .Select(binding => new
                {
                    binding.EvidenceId,
                    binding.EvidenceIdentity,
                    binding.EvidenceFingerprint
                })
                .ToArray(),
            manifest.RecoveryOwnershipChangeId,
            manifest.OperationIdentity,
            manifest.OperationCategory,
            manifest.TargetIdentity,
            manifest.OriginalValue,
            manifest.AppliedValue,
            manifest.SequenceOrder,
            manifest.PreparationFingerprint
        };

        return ComputeCanonicalHash(payload);
    }

    public static string ComputeRecoveryManifestFingerprint(RecoveryCompletion completion)
    {
        ArgumentNullException.ThrowIfNull(completion);
        if (completion.Manifest is null)
            throw new InvalidOperationException("A recovery manifest is required to compute a recovery manifest fingerprint.");

        return ComputeRecoveryManifestFingerprint(completion.Manifest);
    }

    public static string ComputeRecoveryManifestFingerprint(RecoveryCompletion completion, string authorizedTransactionFingerprint)
    {
        ArgumentNullException.ThrowIfNull(completion);
        ArgumentException.ThrowIfNullOrWhiteSpace(authorizedTransactionFingerprint);
        if (completion.Manifest is null)
            throw new InvalidOperationException("A durable recovery manifest is required; cannot synthesize manifest identity.");
        if (!StringComparer.Ordinal.Equals(authorizedTransactionFingerprint, completion.Manifest.AuthorizedTransactionFingerprint))
            throw new InvalidOperationException("Authorized transaction fingerprint does not match the durable recovery manifest.");

        return ComputeRecoveryManifestFingerprint(completion.Manifest);
    }

    private static string ComputeCanonicalHash(object payload)
    {
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(hash).ToUpperInvariant();
    }
}

public sealed record VpnServerConfig(
    string Id,
    string DisplayName,
    string CountryCode,
    string Host,
    int Port,
    string ServerPublicKey,
    string ClientAddress,
    IReadOnlyList<string> AllowedIps,
    string? DnsServer,
    int? Mtu);
