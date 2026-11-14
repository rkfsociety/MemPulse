using System.Globalization;

namespace MemPulse.Core;

public sealed record CleanupHistoryEntry(
    DateTimeOffset Timestamp,
    string Reason,
    ulong UsedBeforeBytes,
    ulong UsedAfterBytes,
    bool Success,
    string Details)
{
    public long FreedBytes => UsedBeforeBytes >= UsedAfterBytes
        ? (long)(UsedBeforeBytes - UsedAfterBytes)
        : -(long)(UsedAfterBytes - UsedBeforeBytes);

    public string FreedText
    {
        get
        {
            if (!Success)
                return "ошибка";
            long delta = FreedBytes;
            if (delta > 0)
                return "−" + Format.Bytes((ulong)delta);
            if (delta < 0)
                return "+" + Format.Bytes((ulong)(-delta));
            return "0 Б";
        }
    }

    public string DisplayLine =>
        $"{Timestamp.LocalDateTime:HH:mm:ss} · {Reason} · {FreedText}";
}

/// <summary>Bounded in-memory history of recent memory cleanups.</summary>
public sealed class CleanupHistoryLog
{
    private readonly int _capacity;
    private readonly List<CleanupHistoryEntry> _entries = [];

    public CleanupHistoryLog(int capacity = 10)
    {
        _capacity = Math.Max(1, capacity);
    }

    public IReadOnlyList<CleanupHistoryEntry> Entries => _entries;

    public CleanupHistoryEntry? Latest => _entries.Count > 0 ? _entries[0] : null;

    public void Record(CleanupHistoryEntry entry)
    {
        _entries.Insert(0, entry);
        if (_entries.Count > _capacity)
            _entries.RemoveRange(_capacity, _entries.Count - _capacity);
    }

    public string FormatCompact(int maxLines = 3)
    {
        if (_entries.Count == 0)
            return "Очисток в этой сессии ещё не было.";

        return string.Join(Environment.NewLine, _entries.Take(maxLines).Select(e => e.DisplayLine));
    }
}
