using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LibertyRoute.ControlProtocol;

public static class LengthPrefixedJsonProtocol
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public static Task<ControlRequestEnvelope> ReadRequestAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
        => ReadAsync<ControlRequestEnvelope>(
            stream,
            ControlProtocolConstants.MaximumRequestSize,
            ValidateRequest,
            cancellationToken);

    public static Task<ControlResponseEnvelope> ReadResponseAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
        => ReadAsync<ControlResponseEnvelope>(
            stream,
            ControlProtocolConstants.MaximumResponseSize,
            ValidateResponse,
            cancellationToken);

    public static Task WriteRequestAsync(
        Stream stream,
        ControlRequestEnvelope request,
        CancellationToken cancellationToken = default)
        => WriteAsync(
            stream,
            request,
            ControlProtocolConstants.MaximumRequestSize,
            ValidateRequest,
            cancellationToken);

    public static Task WriteResponseAsync(
        Stream stream,
        ControlResponseEnvelope response,
        CancellationToken cancellationToken = default)
        => WriteAsync(
            stream,
            response,
            ControlProtocolConstants.MaximumResponseSize,
            ValidateResponse,
            cancellationToken);

    private static async Task<T> ReadAsync<T>(
        Stream stream,
        int maximumSize,
        Action<T> validate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var prefix = new byte[ControlProtocolConstants.LengthPrefixSize];
        await ReadExactlyAsync(stream, prefix, cancellationToken);
        var length = BinaryPrimitives.ReadInt32BigEndian(prefix);
        if (length == 0)
            throw new ControlProtocolException(ControlProtocolError.ZeroLengthFrame);
        if (length < 0)
            throw new ControlProtocolException(ControlProtocolError.InvalidLengthPrefix);
        if (length > maximumSize)
            throw new ControlProtocolException(ControlProtocolError.FrameTooLarge);

        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload, cancellationToken);

        string json;
        try
        {
            json = StrictUtf8.GetString(payload);
        }
        catch (DecoderFallbackException)
        {
            throw new ControlProtocolException(ControlProtocolError.InvalidUtf8);
        }

        T? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException exception) when (IsUnknownCommand(exception))
        {
            throw new ControlProtocolException(ControlProtocolError.UnknownCommand);
        }
        catch (JsonException)
        {
            throw new ControlProtocolException(ControlProtocolError.MalformedJson);
        }

        if (envelope is null)
            throw new ControlProtocolException(ControlProtocolError.InvalidContract);

        validate(envelope);
        return envelope;
    }

    private static async Task WriteAsync<T>(
        Stream stream,
        T envelope,
        int maximumSize,
        Action<T> validate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(envelope);
        validate(envelope);

        byte[] payload;
        try
        {
            payload = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
        }
        catch (JsonException)
        {
            throw new ControlProtocolException(ControlProtocolError.InvalidContract);
        }

        if (payload.Length > maximumSize)
            throw new ControlProtocolException(ControlProtocolError.FrameTooLarge);

        var prefix = new byte[ControlProtocolConstants.LengthPrefixSize];
        BinaryPrimitives.WriteInt32BigEndian(prefix, payload.Length);
        await stream.WriteAsync(prefix, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken);
            if (read == 0)
                throw new ControlProtocolException(ControlProtocolError.TruncatedFrame);
            offset += read;
        }
    }

    private static void ValidateRequest(ControlRequestEnvelope request)
    {
        ValidateCommon(request.ProtocolVersion, request.ServiceInstanceId, request.RequestId);
        if (request.SentAtUtc == default || request.Payload is null)
            throw new ControlProtocolException(ControlProtocolError.InvalidContract);
        if (!Enum.IsDefined(request.Command))
            throw new ControlProtocolException(ControlProtocolError.UnknownCommand);
    }

    private static void ValidateResponse(ControlResponseEnvelope response)
    {
        ValidateCommon(response.ProtocolVersion, response.ServiceInstanceId, response.RequestId);
        if (!Enum.IsDefined(response.Outcome) || !Enum.IsDefined(response.ErrorCode))
            throw new ControlProtocolException(ControlProtocolError.InvalidContract);
        if ((response.Outcome == ControlOutcome.Succeeded) != (response.ErrorCode == ControlErrorCode.None))
            throw new ControlProtocolException(ControlProtocolError.InvalidContract);
    }

    private static void ValidateCommon(int version, Guid serviceInstanceId, Guid requestId)
    {
        if (version != ControlProtocolConstants.Version)
            throw new ControlProtocolException(ControlProtocolError.UnsupportedVersion);
        if (serviceInstanceId == Guid.Empty || requestId == Guid.Empty)
            throw new ControlProtocolException(ControlProtocolError.InvalidContract);
    }

    private static bool IsUnknownCommand(JsonException exception)
        => exception.Path is not null &&
           exception.Path.EndsWith(".command", StringComparison.Ordinal);

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            AllowDuplicateProperties = false,
            AllowTrailingCommas = false,
            PropertyNameCaseInsensitive = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        options.Converters.Add(new ExactEnumConverter<ControlCommand>());
        options.Converters.Add(new ExactEnumConverter<ControlOutcome>());
        options.Converters.Add(new ExactEnumConverter<ControlErrorCode>());
        return options;
    }

    private sealed class ExactEnumConverter<TEnum> : JsonConverter<TEnum>
        where TEnum : struct, Enum
    {
        public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String)
                throw new JsonException();

            var token = reader.GetString();
            foreach (var value in Enum.GetValues<TEnum>())
            {
                if (string.Equals(token, ToWireValue(value), StringComparison.Ordinal))
                    return value;
            }

            throw new JsonException();
        }

        public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
        {
            if (!Enum.IsDefined(value))
                throw new JsonException();
            writer.WriteStringValue(ToWireValue(value));
        }

        private static string ToWireValue(TEnum value)
            => value.ToString().ToUpperInvariant();
    }
}
