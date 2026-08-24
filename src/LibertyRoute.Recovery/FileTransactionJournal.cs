using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LibertyRoute.Core;

namespace LibertyRoute.Recovery;

public sealed class FileTransactionJournal : ITransactionJournal
{
    private sealed record Envelope(string Payload, string Sha256);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public string JournalPath { get; }

    public FileTransactionJournal()
    {
        var basePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "LibertyRoute",
            "transactions");

        Directory.CreateDirectory(basePath);
        JournalPath = Path.Combine(basePath, "active.lrj");
    }

    public async Task WriteAsync(NetworkTransaction transaction, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(transaction, JsonOptions);
        var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
        var envelope = JsonSerializer.Serialize(new Envelope(payload, checksum), JsonOptions);

        var directory = Path.GetDirectoryName(JournalPath)!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(JournalPath)}.{Guid.NewGuid():N}.tmp");

        await using (var stream = new FileStream(
            temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
            FileOptions.WriteThrough | FileOptions.Asynchronous))
        {
            var bytes = Encoding.UTF8.GetBytes(envelope);
            await stream.WriteAsync(bytes, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, JournalPath, overwrite: true);
    }

    public async Task<NetworkTransaction?> ReadActiveAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(JournalPath))
            return null;

        var envelopeJson = await File.ReadAllTextAsync(JournalPath, cancellationToken);
        var envelope = JsonSerializer.Deserialize<Envelope>(envelopeJson, JsonOptions)
                       ?? throw new InvalidDataException("Transaction journal envelope is invalid.");

        var calculated = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(envelope.Payload)));

        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(calculated),
                Convert.FromHexString(envelope.Sha256)))
        {
            throw new InvalidDataException("Transaction journal checksum mismatch.");
        }

        return JsonSerializer.Deserialize<NetworkTransaction>(envelope.Payload, JsonOptions)
               ?? throw new InvalidDataException("Transaction payload is invalid.");
    }

    public async Task ClearAsync(Guid expectedSessionId, CancellationToken cancellationToken)
    {
        var active = await ReadActiveAsync(cancellationToken);
        if (active is null)
            return;

        if (active.SessionId != expectedSessionId)
            throw new InvalidOperationException("Refusing to clear a journal owned by another session.");

        File.Delete(JournalPath);
    }
}
