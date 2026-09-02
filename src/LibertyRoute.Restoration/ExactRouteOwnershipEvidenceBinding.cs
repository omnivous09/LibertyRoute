using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace LibertyRoute.Restoration;

/// <summary>Canonical, non-authorizing fingerprint binding for exact-route ownership evidence.</summary>
public static class ExactRouteOwnershipEvidenceBinding
{
    private const string Domain = "LibertyRoute/ExactRouteOwnershipEvidence/v1";

    public static string ComputeFingerprint(PersistedOwnedChange record)
    {
        ArgumentNullException.ThrowIfNull(record);
        ValidateShapeForEncoding(record);
        return Convert.ToHexString(SHA256.HashData(GetCanonicalBytes(record)));
    }

    internal static bool FingerprintMatches(PersistedOwnedChange record)
    {
        var expected = SHA256.HashData(GetCanonicalBytes(record));
        var actual = Convert.FromHexString(record.ExactRouteEvidenceFingerprint!);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    internal static bool IsCanonicalFingerprint(string value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'A' and <= 'F');

    internal static byte[] GetCanonicalBytes(PersistedOwnedChange record)
    {
        var writer = new ArrayBufferWriter<byte>();
        WriteRaw(writer, Encoding.ASCII.GetBytes(Domain));
        WriteByte(writer, 0);
        WriteInt32(writer, record.ExactRouteEvidenceVersion!.Value);
        WriteGuid(writer, record.SessionId);
        WriteGuid(writer, record.ChangeId);
        WriteGuid(writer, record.MutationAttemptId!.Value);
        WriteInt32(writer, (int)record.Purpose);
        WriteInt32(writer, (int)record.Category);
        WriteInt32(writer, (int)record.EvidenceSource);
        WriteString(writer, record.TargetIdentity);
        WriteString(writer, record.OriginalValue);
        WriteString(writer, record.AppliedValue);
        WriteInt64(writer, record.RecordedAtUtc.UtcTicks);
        WriteByte(writer, record.SequenceNumber.HasValue ? (byte)1 : (byte)0);
        if (record.SequenceNumber.HasValue)
            WriteInt32(writer, record.SequenceNumber.Value);

        var identity = record.ExactRouteIdentity!;
        var key = identity.Key;
        var profile = identity.Profile;
        WriteInt32(writer, identity.SchemaVersion);
        WriteUInt16(writer, (ushort)key.AddressFamily);
        WriteRaw(writer, key.GetDestinationAddressBytes());
        WriteByte(writer, key.DestinationPrefixLength);
        WriteRaw(writer, key.GetNextHopAddressBytes());
        WriteUInt32(writer, key.NextHopScopeId);
        WriteUInt64(writer, key.InterfaceLuid);
        WriteByte(writer, profile.SitePrefixLength);
        WriteUInt32(writer, profile.InitialValidLifetime);
        WriteUInt32(writer, profile.InitialPreferredLifetime);
        WriteUInt32(writer, profile.Metric);
        WriteUInt32(writer, profile.Protocol);
        WriteBool(writer, profile.Loopback);
        WriteBool(writer, profile.AutoconfigureAddress);
        WriteBool(writer, profile.Publish);
        WriteBool(writer, profile.Immortal);
        return writer.WrittenSpan.ToArray();
    }

    private static void ValidateShapeForEncoding(PersistedOwnedChange record)
    {
        if (record.ExactRouteEvidenceVersion != PersistedOwnedChange.CurrentExactRouteEvidenceVersion ||
            record.ExactRouteIdentity is null || !record.MutationAttemptId.HasValue ||
            record.MutationAttemptId == Guid.Empty)
            throw new InvalidOperationException("Complete supported exact-route ownership evidence is required.");
        if (record.Purpose != RecordPurpose.SessionMutation)
            throw new InvalidOperationException("Exact-route ownership evidence is permitted only for session mutations.");
        if (record.SessionId == Guid.Empty || record.ChangeId == Guid.Empty ||
            string.IsNullOrWhiteSpace(record.TargetIdentity) || string.IsNullOrWhiteSpace(record.OriginalValue) ||
            string.IsNullOrWhiteSpace(record.AppliedValue) || record.RecordedAtUtc == default ||
            record.RecordedAtUtc.Offset != TimeSpan.Zero || record.SequenceNumber is <= 0 ||
            !Enum.IsDefined(record.Category) || !Enum.IsDefined(record.EvidenceSource))
            throw new InvalidOperationException("The exact-route ownership evidence contains invalid bound fields.");
        record.ExactRouteIdentity.Profile.ValidateFor(record.ExactRouteIdentity.Key);
    }

    private static void WriteString(IBufferWriter<byte> writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteUInt32(writer, checked((uint)bytes.Length));
        WriteRaw(writer, bytes);
    }

    private static void WriteGuid(IBufferWriter<byte> writer, Guid value)
    {
        var span = writer.GetSpan(16);
        value.TryWriteBytes(span, bigEndian: true, out _);
        writer.Advance(16);
    }

    private static void WriteBool(IBufferWriter<byte> writer, bool value) => WriteByte(writer, value ? (byte)1 : (byte)0);
    private static void WriteByte(IBufferWriter<byte> writer, byte value) { writer.GetSpan(1)[0] = value; writer.Advance(1); }
    private static void WriteUInt16(IBufferWriter<byte> writer, ushort value) { BinaryPrimitives.WriteUInt16BigEndian(writer.GetSpan(2), value); writer.Advance(2); }
    private static void WriteInt32(IBufferWriter<byte> writer, int value) { BinaryPrimitives.WriteInt32BigEndian(writer.GetSpan(4), value); writer.Advance(4); }
    private static void WriteUInt32(IBufferWriter<byte> writer, uint value) { BinaryPrimitives.WriteUInt32BigEndian(writer.GetSpan(4), value); writer.Advance(4); }
    private static void WriteInt64(IBufferWriter<byte> writer, long value) { BinaryPrimitives.WriteInt64BigEndian(writer.GetSpan(8), value); writer.Advance(8); }
    private static void WriteUInt64(IBufferWriter<byte> writer, ulong value) { BinaryPrimitives.WriteUInt64BigEndian(writer.GetSpan(8), value); writer.Advance(8); }
    private static void WriteRaw(IBufferWriter<byte> writer, ReadOnlySpan<byte> value) { value.CopyTo(writer.GetSpan(value.Length)); writer.Advance(value.Length); }
}
