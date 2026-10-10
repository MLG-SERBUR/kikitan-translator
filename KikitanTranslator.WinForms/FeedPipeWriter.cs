// FORK (feed-overlay): named-pipe (MMF) writer for the overlay feed.
// A second channel alongside upstream's "kikitan-pipe"; JSON payload so the
// schema can grow without breaking the struct layout. Mirrored by
// FeedPipe.cs in KikitanTranslator.Overlay (keep the JSON shape in sync).

using System.IO.MemoryMappedFiles;
using System.Text;
using Newtonsoft.Json;

namespace KikitanTranslator.WinForms;

public sealed class FeedPipeEntryDto
{
    [JsonProperty("ts")] public DateTime Timestamp { get; set; }
    [JsonProperty("desktop")] public bool FromDesktop { get; set; }
    [JsonProperty("original")] public string Original { get; set; } = "";
    [JsonProperty("translation")] public string Translation { get; set; } = "";
}

public sealed class FeedPipeConfigDto
{
    [JsonProperty("maxEntries")] public int MaxEntries { get; set; } = 6;
    [JsonProperty("fontSize")] public int FontSize { get; set; } = 64;
    [JsonProperty("opacity")] public int Opacity { get; set; } = 85;
    [JsonProperty("anchor")] public int Anchor { get; set; } = 0;
    [JsonProperty("widthCm")] public int WidthCm { get; set; } = 90;
    [JsonProperty("showTimestamps")] public bool ShowTimestamps { get; set; } = true;
    [JsonProperty("desktopOverlay")] public bool DesktopOverlay { get; set; } = true;
}

public sealed class FeedPipeStateDto
{
    [JsonProperty("v")] public int Version { get; set; } = 1;
    [JsonProperty("counter")] public int Counter { get; set; }
    [JsonProperty("config")] public FeedPipeConfigDto Config { get; set; } = new();
    [JsonProperty("entries")] public List<FeedPipeEntryDto> Entries { get; set; } = [];
}

public sealed class FeedPipeWriter : IDisposable
{
    private const string PipeName = "kikitan-feed";
    private const int Capacity = 65536;

    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _accessor;
    private int _counter;

    public FeedPipeWriter()
    {
        _mmf = MemoryMappedFile.CreateNew(PipeName, Capacity);
        _accessor = _mmf.CreateViewAccessor();
    }

    public void Write(FeedEntry[] entries, FeedPipeConfigDto config)
    {
        var state = new FeedPipeStateDto
        {
            Counter = Interlocked.Increment(ref _counter),
            Config = config,
            Entries = entries.Select(e => new FeedPipeEntryDto
            {
                Timestamp = e.Timestamp,
                FromDesktop = e.FromDesktop,
                Original = e.Original,
                Translation = e.Translation,
            }).ToList(),
        };

        var json = JsonConvert.SerializeObject(state);
        var bytes = Encoding.UTF8.GetBytes(json);
        if (bytes.Length + 4 > Capacity) return; // drop oversized frame, keep pipe valid

        _accessor.Write(0, bytes.Length);
        _accessor.WriteArray(4, bytes, 0, bytes.Length);
    }

    public void Dispose()
    {
        _accessor.Dispose();
        _mmf.Dispose();
    }
}
