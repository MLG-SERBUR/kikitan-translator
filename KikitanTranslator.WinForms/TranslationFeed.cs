// FORK (feed-overlay): in-memory translation feed, mirroring the message log
// concept from VRCT. No persistence by design (matches VRCT behavior).

namespace KikitanTranslator.WinForms;

public sealed class FeedEntry
{
    public DateTime Timestamp { get; init; }
    public bool FromDesktop { get; init; }
    public string Original { get; init; } = "";
    public string Translation { get; init; } = "";

    public string DisplayText(bool showTimestamps, bool showTranscription)
    {
        // FORK: transcription and translation on separate lines.
        var body = showTranscription && Translation.Length > 0
            ? $"{Bidi(Original)}\n{Bidi(Translation)}"
            : Bidi(Translation.Length > 0 ? Translation : Original);
        return showTimestamps ? $"[{Timestamp:HH:mm:ss}] {body}" : body;
    }

    // Wrap RTL text (Arabic, Hebrew, ...) in directional isolates so it renders
    // in the correct order inside an LTR log line.
    private static string Bidi(string s)
    {
        foreach (var c in s)
        {
            if (c is >= '\u0590' and <= '\u08FF')
                return "\u2066" + s + "\u2069";
        }
        return s;
    }
}

public sealed class TranslationFeed
{
    private readonly List<FeedEntry> _entries = [];
    private readonly object _lock = new();
    private const int MaxEntries = 200;

    public event Action<FeedEntry>? EntryAdded;

    public void Add(bool fromDesktop, string original, string translation)
    {
        if (string.IsNullOrWhiteSpace(original) && string.IsNullOrWhiteSpace(translation)) return;

        var entry = new FeedEntry
        {
            Timestamp = DateTime.Now,
            FromDesktop = fromDesktop,
            Original = original,
            Translation = translation,
        };

        lock (_lock)
        {
            _entries.Add(entry);
            while (_entries.Count > MaxEntries) _entries.RemoveAt(0);
        }

        EntryAdded?.Invoke(entry);
    }

    public FeedEntry[] GetSnapshot()
    {
        lock (_lock) return _entries.ToArray();
    }

    public void Clear()
    {
        lock (_lock) _entries.Clear();
    }
}
