using System.Text.Json.Serialization;

namespace LibertyRoute.ControlProtocol;

public static class ControlProtocolConstants
{
    public const int Version = 1;
    public const int LengthPrefixSize = 4;
    public const int MaximumRequestSize = 16 * 1024;
    public const int MaximumResponseSize = 1024 * 1024;
}

public enum ControlCommand
{
    Status,
    Snapshot,
    Connect,
    Disconnect
}

public enum ControlOutcome
{
    Succeeded,
    Failed
}

public enum ControlErrorCode
{
    None,
    InvalidRequest,
    UnsupportedVersion,
    Unauthorized,
    InternalError
}

public enum ControlProtocolError
{
    InvalidLengthPrefix,
    ZeroLengthFrame,
    FrameTooLarge,
    TruncatedFrame,
    InvalidUtf8,
    MalformedJson,
    InvalidContract,
    UnsupportedVersion,
    UnknownCommand
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ControlRequestPayload;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ControlRequestEnvelope(
    [property: JsonRequired] int ProtocolVersion,
    [property: JsonRequired] Guid ServiceInstanceId,
    [property: JsonRequired] Guid RequestId,
    [property: JsonRequired] DateTimeOffset SentAtUtc,
    [property: JsonRequired] ControlCommand Command,
    [property: JsonRequired] ControlRequestPayload Payload);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ControlResponseEnvelope(
    [property: JsonRequired] int ProtocolVersion,
    [property: JsonRequired] Guid ServiceInstanceId,
    [property: JsonRequired] Guid RequestId,
    [property: JsonRequired] ControlOutcome Outcome,
    [property: JsonRequired] ControlErrorCode ErrorCode,
    // Transitional, bounded response data for 4K-1. Phase 4K-3 must replace this
    // with command-specific result contracts when production IPC is migrated.
    [property: JsonRequired] string? Result);

public sealed class ControlProtocolException : Exception
{
    public ControlProtocolException(ControlProtocolError error)
        : base(MessageFor(error))
    {
        Error = error;
    }

    public ControlProtocolError Error { get; }

    private static string MessageFor(ControlProtocolError error) => error switch
    {
        ControlProtocolError.InvalidLengthPrefix => "The frame length prefix is invalid.",
        ControlProtocolError.ZeroLengthFrame => "The frame payload is empty.",
        ControlProtocolError.FrameTooLarge => "The frame exceeds the permitted size.",
        ControlProtocolError.TruncatedFrame => "The frame ended before it was complete.",
        ControlProtocolError.InvalidUtf8 => "The frame payload is not valid UTF-8.",
        ControlProtocolError.MalformedJson => "The frame payload is not valid protocol JSON.",
        ControlProtocolError.InvalidContract => "The protocol contract is invalid.",
        ControlProtocolError.UnsupportedVersion => "The protocol version is not supported.",
        ControlProtocolError.UnknownCommand => "The protocol command is not supported.",
        _ => "The protocol input is invalid."
    };
}
