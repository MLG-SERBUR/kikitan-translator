// FORK (feed-overlay): feed channel reader. Mirrors FeedPipeWriter.cs in
// KikitanTranslator.WinForms (keep the JSON shape in sync). JSON over MMF so
// the schema can grow without breaking native struct layouts.

using System.IO.MemoryMappedFiles;
using System.Text;
using Newtonsoft.Json;

namespace KikitanTranslator.Overlay;

public sealed class FeedPipeEntry
{
    [JsonProperty("ts")] public DateTime Timestamp { get; set; }
    [JsonProperty("desktop")] public bool FromDesktop { get; set; }
    [JsonProperty("original")] public string Original { get; set; } = "";
    [JsonProperty("translation")] public string Translation { get; set; } = "";
}

public sealed class FeedPipeConfig
{
    [JsonProperty("maxEntries")] public int MaxEntries { get; set; } = 6;
    [JsonProperty("fontSize")] public int FontSize { get; set; } = 64;
    [JsonProperty("opacity")] public int Opacity { get; set; } = 85;
    [JsonProperty("anchor")] public int Anchor { get; set; } = 0;
    [JsonProperty("widthCm")] public int WidthCm { get; set; } = 90;
    [JsonProperty("showTimestamps")] public bool ShowTimestamps { get; set; } = true;
    [JsonProperty("desktopOverlay")] public bool DesktopOverlay { get; set; } = true;
}

public sealed class FeedPipeState
{
    [JsonProperty("v")] public int Version { get; set; }
    [JsonProperty("counter")] public int Counter { get; set; }
    [JsonProperty("config")] public FeedPipeConfig Config { get; set; } = new();
    [JsonProperty("entries")] public List<FeedPipeEntry> Entries { get; set; } = [];
}

public sealed class FeedPipeReader : IDisposable
{
    private const string PipeName = "kikitan-feed";

    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _accessor;
    private int _lastCounter = -1;
    private bool _disposed;

    private bool EnsureOpen()
    {
        if (_accessor != null) return true;
        try
        {
            _mmf = MemoryMappedFile.OpenExisting(PipeName);
            _accessor = _mmf.CreateViewAccessor();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void Reset()
    {
        _accessor?.Dispose();
        _mmf?.Dispose();
        _accessor = null;
        _mmf = null;
        _lastCounter = -1;
    }

    /// <summary>Returns the new state, or null when unchanged / unavailable.</summary>
    public FeedPipeState? Read()
    {
        if (_disposed) return null;
        if (!EnsureOpen()) return null;

        try
        {
            int len = _accessor!.ReadInt32(0);
            if (len <= 0 || len > 60000) return null;

            var bytes = new byte[len];
            _accessor.ReadArray(4, bytes, 0, len);
            var state = JsonConvert.DeserializeObject<FeedPipeState>(Encoding.UTF8.GetString(bytes));
            if (state == null || state.Counter == _lastCounter) return null;

            _lastCounter = state.Counter;
            return state;
        }
        catch
        {
            // Writer restarted (or torn down); reopen on next poll.
            Reset();
            return null;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        Reset();
    }
}
