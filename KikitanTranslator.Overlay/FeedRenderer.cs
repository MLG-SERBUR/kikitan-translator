// FORK (feed-overlay): feed frame renderer, extracted from OverlayServer
// into a static class so the rendering logic can be tested in isolation
// (it depends only on SkiaSharp + the feed DTOs).

using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace KikitanTranslator.Overlay;

internal static class FeedRenderer
{
    private sealed class FeedLine
    {
        public string Ts = "";
        public string Body = "";
        public SKColor Color;
        public bool FirstOfEntry;
    }
    private static bool ContainsRtl(string s)
    {
        foreach (var c in s)
            if (c is >= '\u0590' and <= '\u08FF') return true;
        return false;
    }

    // FORK: HarfBuzz shaping for RTL text (Arabic/Hebrew). The char-by-char
    // path below breaks Arabic joining and ordering; HarfBuzz does proper
    // complex-text shaping, bidi, and glyph selection.
    private static void DrawTextShaped(SKCanvas canvas, string text, float x, float y, SKPaint paint, float fontSize)
    {
        var style = new SKFontStyle(SKFontStyleWeight.Normal, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
        var typeface = SKFontManager.Default.MatchCharacter(null, style, null, 'ا')
                       ?? SKTypeface.FromFamilyName("Arial");
        using var font = new SKFont(typeface, fontSize);
        using var shaper = new SKShaper(typeface);
        using var dp = paint.Clone();
        dp.TextAlign = SKTextAlign.Left;
        canvas.DrawShapedText(shaper, text, x, y, SKTextAlign.Left, font, dp);
    }

    private static void DrawTextLeftWithFallback(SKCanvas canvas, string text, float x, float y, SKPaint paint, float fontSize)
    {
        if (ContainsRtl(text))
        {
            DrawTextShaped(canvas, text, x, y, paint, fontSize);
            return;
        }

        var fontManager = SKFontManager.Default;
        var style = new SKFontStyle(
            paint.FakeBoldText ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal,
            SKFontStyleWidth.Normal,
            SKFontStyleSlant.Upright);

        float cursorX = x;
        foreach (var c in text)
        {
            var typeface = fontManager.MatchCharacter(null, style, null, c)
                           ?? SKTypeface.FromFamilyName("Arial Unicode MS");
            var charStr = c.ToString();
            using var charPaint = paint.Clone();
            charPaint.Typeface = typeface;
            charPaint.TextSize = fontSize;
            charPaint.TextAlign = SKTextAlign.Left;

            canvas.DrawText(charStr, cursorX, y, charPaint);
            cursorX += charPaint.MeasureText(charStr);
        }
    }
    private static List<(string ts, string body)> WrapFeedLine(string ts, string body, float maxWidth, float fontSize, SKPaint paint)
    {
        var lines = new List<(string ts, string body)>();
        bool charWrap = !body.Contains(' ');
        string sep = charWrap ? "" : " ";
        var words = charWrap ? body.Select(c => c.ToString()).ToArray() : body.Split(' ');

        var current = "";
        bool first = true;

        float TsWidth(string t) => t.Length > 0 ? MeasureWidthWithFallback(t, fontSize, paint) : 0;

        void Flush()
        {
            if (current.Length > 0) lines.Add((first ? ts : "", current));
            current = "";
            first = false;
        }

        foreach (var w in words)
        {
            var candidate = current.Length == 0 ? w : current + sep + w;
            float avail = first ? maxWidth - TsWidth(ts) : maxWidth;

            if (MeasureWidthWithFallback(candidate, fontSize, paint) <= avail)
            {
                current = candidate;
                continue;
            }

            // Doesn't fit: emit the current line, then hard-split the word.
            // The timestamp goes on exactly one line: the first emitted.
            string pendingTs = first ? ts : "";
            if (current.Length > 0)
            {
                lines.Add((pendingTs, current));
                current = "";
                pendingTs = "";
            }
            first = false;

            avail = maxWidth - TsWidth(pendingTs);
            candidate = w;
            bool firstChunk = true;
            while (MeasureWidthWithFallback(candidate, fontSize, paint) > avail && candidate.Length > 1)
            {
                int lo = 1, hi = candidate.Length;
                while (lo < hi)
                {
                    int mid = (lo + hi + 1) / 2;
                    if (MeasureWidthWithFallback(candidate[..mid], fontSize, paint) <= avail) lo = mid;
                    else hi = mid - 1;
                }
                lines.Add((firstChunk ? pendingTs : "", candidate[..lo]));
                firstChunk = false;
                avail = maxWidth;
                candidate = candidate[lo..];
            }
            current = candidate;
        }

        Flush();
        return lines;
    }
    internal static byte[] DrawFeedOnCanvas(FeedPipeState state)
    {
        const int canvasWidth = 2000;
        const int maxCanvasHeight = 1500;
        const int padding = 70;

        float fontSize = Math.Clamp(state.Config.FontSize, 24, 160);
        float lineHeight = fontSize * 1.42f;
        float entryGap = fontSize * 0.5f;
        float maxTextWidth = canvasWidth - padding * 2f;

        var paint = new SKPaint { IsAntialias = true };
        var tsPaint = new SKPaint { Color = new SKColor(0x99, 0x99, 0x99), IsAntialias = true };

        var lines = new List<FeedLine>();
        var entries = state.Entries.TakeLast(Math.Max(1, state.Config.MaxEntries)).ToList();
        foreach (var e in entries)
        {
            var ts = state.Config.ShowTimestamps ? $"[{e.Timestamp:HH:mm:ss}] " : "";
            var body = state.Config.ShowOriginal || e.Translation.Length == 0
                ? (e.Translation.Length > 0 ? $"{e.Original} → {e.Translation}" : e.Original)
                : e.Translation;
            if (body.Trim().Length == 0) continue;
            var color = e.FromDesktop ? new SKColor(0x9E, 0xCB, 0xFF) : SKColors.White;

            bool firstLine = true;
            foreach (var (lts, lbody) in WrapFeedLine(ts, body, maxTextWidth, fontSize, paint))
            {
                lines.Add(new FeedLine { Ts = lts, Body = lbody, Color = color, FirstOfEntry = firstLine });
                firstLine = false;
            }
        }

        if (lines.Count == 0)
            lines.Add(new FeedLine { Body = "Waiting for speech…", Color = new SKColor(0x88, 0x88, 0x88), FirstOfEntry = true });

        float TotalHeight()
        {
            int entryCount = lines.Count(l => l.FirstOfEntry);
            return padding * 2 + lines.Count * lineHeight + Math.Max(0, entryCount - 1) * entryGap;
        }

        while (TotalHeight() > maxCanvasHeight && lines.Count > 1)
        {
            lines.RemoveAt(0);
            if (lines.Count > 0) lines[0].FirstOfEntry = true;
        }

        int canvasHeight = Math.Max(240, (int)Math.Ceiling(TotalHeight()));

        var imageInfo = new SKImageInfo(canvasWidth, canvasHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(imageInfo);
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        using var bgPaint = new SKPaint { Color = new SKColor(0x14, 0x14, 0x14, 235), IsAntialias = true };
        canvas.DrawRoundRect(new SKRoundRect(new SKRect(0, 0, canvasWidth, canvasHeight), 40, 40), bgPaint);

        using var sepPaint = new SKPaint { Color = new SKColor(0x3A, 0x3A, 0x3A), IsAntialias = true, StrokeWidth = 2 };

        using var font = new SKFont(SKTypeface.FromFamilyName("Arial"), fontSize);
        font.GetFontMetrics(out var metrics);
        float ascent = Math.Abs(metrics.Ascent);

        float y = padding + ascent;
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.FirstOfEntry && i > 0)
            {
                float sepY = y - ascent - entryGap * 0.5f;
                canvas.DrawLine(padding, sepY, canvasWidth - padding, sepY, sepPaint);
            }

            float x = padding;
            if (line.Ts.Length > 0)
            {
                DrawTextLeftWithFallback(canvas, line.Ts, x, y, tsPaint, fontSize);
                x += MeasureWidthWithFallback(line.Ts, fontSize, paint);
            }

            using var bodyPaint = new SKPaint { Color = line.Color, IsAntialias = true };
            DrawTextLeftWithFallback(canvas, line.Body, x, y, bodyPaint, fontSize);

            y += lineHeight;
            bool entryEnds = i + 1 >= lines.Count || lines[i + 1].FirstOfEntry;
            if (entryEnds && i + 1 < lines.Count) y += entryGap;
        }

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    // Duplicated from OverlayServer (upstream) so this class stays self-contained.
    private static float MeasureWidthWithFallback(string text, float fontSize, SKPaint paint)
    {
        var fontManager = SKFontManager.Default;
        var style = new SKFontStyle(
            paint.FakeBoldText ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal,
            SKFontStyleWidth.Normal,
            SKFontStyleSlant.Upright);

        float totalWidth = 0f;
        foreach (var c in text)
        {
            var typeface = fontManager.MatchCharacter(null, style, null, c)
                           ?? SKTypeface.FromFamilyName("Arial Unicode MS");

            using var measurePaint = paint.Clone();
            measurePaint.Typeface = typeface;
            measurePaint.TextSize = fontSize;
            measurePaint.TextAlign = SKTextAlign.Left;

            totalWidth += measurePaint.MeasureText(c.ToString());
        }
        return totalWidth;
    }
}
