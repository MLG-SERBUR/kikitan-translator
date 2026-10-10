// FORK (feed-overlay): Linux-runnable verification. Exercises the real
// FeedPipeWriter -> FeedPipeReader round-trip and renders real feed frames
// with FeedRenderer.DrawFeedOnCanvas for visual inspection.

using KikitanTranslator.Overlay;
using KikitanTranslator.WinForms;

var failures = new List<string>();
void Check(bool cond, string name)
{
    Console.WriteLine((cond ? "PASS " : "FAIL ") + name);
    if (!cond) failures.Add(name);
}

// ---------- 1. MMF pipe round-trip (Windows-only; named maps unsupported on Linux) ----------
try
{
    using var writer = new FeedPipeWriter();
    using var reader = new FeedPipeReader();

    var entries = new[]
    {
        new FeedEntry { Timestamp = new DateTime(2026, 10, 10, 12, 0, 1), FromDesktop = false, Original = "Hello everyone", Translation = "みなさんこんにちは" },
        new FeedEntry { Timestamp = new DateTime(2026, 10, 10, 12, 0, 5), FromDesktop = true, Original = "こんにちは", Translation = "Hello" },
        new FeedEntry { Timestamp = new DateTime(2026, 10, 10, 12, 0, 9), FromDesktop = false, Original = "How is the quest performance today", Translation = "" },
    };
    var config = new FeedPipeConfigDto { MaxEntries = 6, FontSize = 64, Opacity = 85, Anchor = 1, WidthCm = 90, ShowTimestamps = true, DesktopOverlay = true };

    writer.Write(entries, config);
    var s1 = reader.Read();
    Check(s1 != null, "pipe: first read returns state");
    Check(s1?.Entries.Count == 3, "pipe: 3 entries round-trip");
    Check(s1?.Entries[0].Original == "Hello everyone" && s1?.Entries[0].Translation == "みなさんこんにちは", "pipe: CJK content intact");
    Check(s1?.Entries[1].FromDesktop == true, "pipe: direction flag intact");
    Check(s1?.Config.Anchor == 1 && s1?.Config.WidthCm == 90 && s1?.Config.DesktopOverlay == true, "pipe: config intact");

    var s2 = reader.Read();
    Check(s2 == null, "pipe: no-change read returns null");

    config.Opacity = 50;
    writer.Write(entries, config);
    var s3 = reader.Read();
    Check(s3 != null && s3.Config.Opacity == 50 && s3.Counter > s1!.Counter, "pipe: update detected via counter");
}
catch (PlatformNotSupportedException)
{
    Console.WriteLine("SKIP pipe tests: named memory maps are Windows-only");
}

// ---------- 1b. JSON wire-format contract (no MMF needed) ----------
{
    var dto = new FeedPipeStateDto
    {
        Counter = 42,
        Config = new FeedPipeConfigDto { MaxEntries = 6, FontSize = 64, Opacity = 85, Anchor = 1, WidthCm = 90, ShowTimestamps = true, DesktopOverlay = true, DisplayDurationSec = 10, FadeoutDurationSec = 3, PosX = 0.1, PosY = -0.2, PosZ = 0.3, ShowOriginal = false },
        Entries = [new FeedPipeEntryDto { Timestamp = new DateTime(2026, 10, 10, 12, 0, 1), FromDesktop = true, Original = "orig", Translation = "trans" }],
    };
    // Mirror FeedPipeWriter.Write's serialization:
    var json = Newtonsoft.Json.JsonConvert.SerializeObject(dto);
    var back = Newtonsoft.Json.JsonConvert.DeserializeObject<FeedPipeState>(json);
    Check(back != null && back.Counter == 42, "wire: counter survives");
    Check(back?.Entries.Count == 1 && back.Entries[0].Original == "orig" && back.Entries[0].FromDesktop, "wire: entry survives");
    Check(back?.Config.Anchor == 1 && back.Config.ShowOriginal == false && back.Config.DisplayDurationSec == 10
        && Math.Abs(back.Config.PosY - (-0.2)) < 1e-9, "wire: config survives");
}

// ---------- 2. Renderer scenarios ----------
Directory.CreateDirectory("test-output");

void Render(string name, FeedPipeState state)
{
    var png = FeedRenderer.DrawFeedOnCanvas(state);
    File.WriteAllBytes(Path.Combine("test-output", name + ".png"), png);
    using var ms = new MemoryStream(png);
    using var bmp = SkiaSharp.SKBitmap.Decode(ms);
    Console.WriteLine($"RENDER {name}: {bmp.Width}x{bmp.Height} ({png.Length} bytes)");
    Check(png.Length > 1000, $"render {name}: non-trivial output");
}

var baseConfig = new FeedPipeConfig { MaxEntries = 6, FontSize = 64, Opacity = 85, Anchor = 0, WidthCm = 90, ShowTimestamps = true, DesktopOverlay = true };

Render("empty", new FeedPipeState { Counter = 1, Config = baseConfig, Entries = [] });

Render("mixed", new FeedPipeState
{
    Counter = 2,
    Config = baseConfig,
    Entries =
    [
        new() { Timestamp = new DateTime(2026, 10, 10, 12, 0, 1), FromDesktop = false, Original = "Hello everyone, how are you doing today?", Translation = "みなさんこんにちは、今日はお元気ですか？" },
        new() { Timestamp = new DateTime(2026, 10, 10, 12, 0, 20), FromDesktop = true, Original = "元気ですよ！クエストのパフォーマンスはどう？", Translation = "I'm good! How is the quest performance?" },
    ],
});

Render("long", new FeedPipeState
{
    Counter = 3,
    Config = baseConfig,
    Entries =
    [
        new() { Timestamp = new DateTime(2026, 10, 10, 12, 1, 0), FromDesktop = false, Original = "This is a deliberately very long English sentence designed to exercise the word-wrapping logic across multiple visual lines in the overlay renderer", Translation = "これはオーバーレイの折り返し処理をテストするための意図的に長い日本語の文章です複数行にわたって正しく表示されるか確認します" },
        new() { Timestamp = new DateTime(2026, 10, 10, 12, 1, 30), FromDesktop = true, Original = "短い", Translation = "short" },
    ],
});

Render("overflow", new FeedPipeState
{
    Counter = 4,
    Config = baseConfig,
    Entries = Enumerable.Range(1, 10).Select(i => new FeedPipeEntry
    {
        Timestamp = new DateTime(2026, 10, 10, 12, 2, i),
        FromDesktop = i % 2 == 0,
        Original = $"Message number {i} from the {(i % 2 == 0 ? "desktop" : "microphone")} pipeline",
        Translation = $"メッセージ {i}",
    }).ToList(),
});

Render("stt-only", new FeedPipeState
{
    Counter = 5,
    Config = baseConfig,
    Entries =
    [
        new() { Timestamp = new DateTime(2026, 10, 10, 12, 3, 0), FromDesktop = false, Original = "Transcription only mode, no translation available", Translation = "" },
    ],
});

Render("no-timestamps", new FeedPipeState
{
    Counter = 6,
    Config = new FeedPipeConfig { MaxEntries = 4, FontSize = 96, Opacity = 100, Anchor = 2, WidthCm = 70, ShowTimestamps = false, DesktopOverlay = true },
    Entries =
    [
        new() { Timestamp = new DateTime(2026, 10, 10, 12, 4, 0), FromDesktop = true, Original = "Bigger font test", Translation = "大きいフォントのテスト" },
    ],
});

Console.WriteLine(failures.Count == 0 ? "ALL PASS" : $"{failures.Count} FAILURES");
return failures.Count;
