using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using LibertyRoute.Core;

namespace LibertyRoute.Restoration;

public static class RecoveryOwnershipEvidenceBinding
{
    public static RecoveryEvidenceBinding Create(PersistedOwnedChange record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var failure = PersistedOwnedChange.Validate(record);
        if (!string.IsNullOrEmpty(failure))
            throw new InvalidOperationException($"Ownership evidence is invalid: {failure}");

        var identity = $"{record.SessionId:D}:{record.ChangeId:D}";
        var fields = new[]
        {
            "LibertyRoute.RecoveryOwnershipEvidence.v1", record.SessionId.ToString("D"),
            record.ChangeId.ToString("D"), record.Category.ToString(), record.TargetIdentity,
            record.OriginalValue, record.AppliedValue, record.RecordedAtUtc.ToString("O"),
            record.SequenceNumber?.ToString(CultureInfo.InvariantCulture) ?? string.Empty, record.EvidenceSource.ToString(),
            record.Purpose.ToString(), record.RecoveryAttemptId?.ToString("D") ?? string.Empty,
            record.AuthorizationEvidenceId?.ToString("D") ?? string.Empty
        };
        var canonical = string.Concat(fields.Select(value =>
            Encoding.UTF8.GetByteCount(value).ToString(CultureInfo.InvariantCulture) + ":" + value));
        return RecoveryEvidenceBinding.Create(record.ChangeId, identity,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))));
    }
}
