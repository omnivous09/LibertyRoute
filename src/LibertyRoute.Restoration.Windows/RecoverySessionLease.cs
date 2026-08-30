using System.Collections.Concurrent;
using LibertyRoute.Recovery;

namespace LibertyRoute.Restoration.Windows;

/// <summary>Process-local serialization for all recovery work targeting one journal.</summary>
internal static class RecoverySessionLease
{
    private sealed class Entry
    {
        internal SemaphoreSlim Gate { get; } = new(1, 1);
    }

    private static readonly ConcurrentDictionary<string, Entry> Entries = new(StringComparer.OrdinalIgnoreCase);

    internal static async ValueTask<IDisposable> AcquireAsync(
        ITransactionJournal journal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(journal);
        var key = Canonicalize(journal.JournalPath);
        var entry = Entries.GetOrAdd(key, static _ => new Entry());
        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser(entry.Gate);
    }

    private static string Canonicalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Journal path is required.", nameof(path));
        return Path.GetFullPath(path)
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;
        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}
