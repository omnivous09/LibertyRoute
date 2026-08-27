using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using LibertyRoute.ControlProtocol;

namespace LibertyRoute.Desktop;

internal static class ControlSnapshotExporter
{
    private static readonly JsonSerializerOptions ExportOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = true
    };

    internal static async Task<string> ExportAsync(
        ControlSnapshotResult result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        var directory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"LibertyRoute-NetworkSnapshot-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.json");
        var json = JsonSerializer.Serialize(result.Snapshot, ExportOptions);
        await File.WriteAllTextAsync(path, json, cancellationToken);
        return path;
    }
}
