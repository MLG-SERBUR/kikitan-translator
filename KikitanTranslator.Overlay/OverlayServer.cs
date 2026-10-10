using System.IO.MemoryMappedFiles;
using KikitanTranslator.Overlay;
using SkiaSharp;


public class OverlayServer
{
    private Form1 _form;
    private OpenVROverlay _openvroverlay;

    private bool _desktop;

    private async Task StartReader()
    {
        // FORK (feed-overlay): the WinForms host does not create the legacy
        // "kikitan-pipe"; tolerate its absence instead of throwing.
        OverlayReader? reader = null;
        try { reader = new OverlayReader(); }
        catch (Exception e)
        {
            Console.WriteLine($"Legacy overlay pipe unavailable, skipping: {e.GetType().Name}");
            return;
        }

        var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));

        while (await timer.WaitForNextTickAsync())
        {
            var state = reader.Read();
            if (state is not null) SendText(state.Value.Text, state.Value.NoLanguageSpace, state.Value.Time);
        }
    }

    // FORK (feed-overlay): polls the "kikitan-feed" pipe for the translation
    // feed + overlay config, and routes rendered frames to both outputs.
    private async Task StartFeedReader()
    {
        var reader = new FeedPipeReader();
        var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));

        while (await timer.WaitForNextTickAsync())
        {
            var state = reader.Read();
            if (state is not null) SendFeed(state);
        }
    }

    public void StartForm(Form1 form)
    {
        Task.Run(StartReader);
        Task.Run(StartFeedReader);

        _form = form;
        _desktop = true;
    }

    public void StartOpenVR(OpenVROverlay openVROverlay)
    {
        Task.Run(StartReader);
        Task.Run(StartFeedReader);

        _openvroverlay = openVROverlay;
        // FORK (feed-overlay): don't block the main thread; the desktop form
        // can now run alongside the VR overlay.
        Task.Run(() => _openvroverlay.ImageLoop());
    }

    public void SendText(string text, bool noLanguageSpace, int time = 5000)
    {
        var img = DrawTextOnCanvas(text, noLanguageSpace, time);
        var ms = new MemoryStream(img);
        if (_desktop)
        {
            Image image = Image.FromStream(ms);
            ms.Close();

            _form.SetImage(image, time);
        }
        else
        {
            _openvroverlay.AddToQueue(ms, time);
        }

        Console.WriteLine("Image received!");
    }

    private byte[] DrawTextOnCanvas(string text, bool noSpaceLanguage, int time)
    {
        const int canvasWidth = 4000;
        const int canvasHeight = 1250;
        const int maxLines = 8;
        const float minTextSize = 150f;
        const float paddingX = 100f;
        const float paddingY = 50f;
        float fontSize = 500f;

        float maxTextWidth = canvasWidth - paddingX * 2;
        float maxTextHeight = canvasHeight - paddingY * 2;

        var imageInfo = new SKImageInfo(canvasWidth, canvasHeight, SKColorType.Rgba8888, SKAlphaType.Premul);

        using var surface = SKSurface.Create(imageInfo);
        var canvas = surface.Canvas;

        canvas.Clear(new SKColor(0x33, 0x33, 0x33));
        
        var paint = new SKPaint
        {
            Color = SKColors.White,
            IsAntialias = true,
            TextAlign = SKTextAlign.Center,
        };
        
        var typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Normal);
        var font = new SKFont(typeface, fontSize);

        string currentText = text;

        while ((MeasureWidthWithFallback(currentText, font.Size, paint) > maxTextWidth || MeasureHeightWithFallback(currentText, font.Size, paint) > maxTextHeight)
               && font.Size > minTextSize)
        {
            font.Size -= 1f;
        }


        if (MeasureWidthWithFallback(currentText, font.Size, paint) > maxTextWidth || MeasureHeightWithFallback(currentText, font.Size, paint) > maxTextHeight)
        {
            IEnumerable<string> tokens = noSpaceLanguage
                ? currentText.Select(c => c.ToString())
                : currentText.Split(' ');

            var lines = new List<string>();
            string newLine = "";

            foreach (var word in tokens)
            {
                string candidate = newLine + word + (noSpaceLanguage ? "" : " ");
                if (MeasureWidthWithFallback(candidate, font.Size, paint) > maxTextWidth)
                {
                    lines.Add(newLine.TrimEnd());
                    newLine = "";
                }

                newLine += word + (noSpaceLanguage ? "" : " ");
            }

            if (newLine.Trim() != "")
                lines.Add(newLine.TrimEnd());

            if (lines.Count > maxLines)
            {
                for (int i = maxLines; i < lines.Count; i++)
                    lines[maxLines - 1] += " " + lines[i];
                lines = lines.Take(maxLines).ToList();
            }
            
            string longestLine = lines.OrderByDescending(l => MeasureWidthWithFallback(l, font.Size, paint)).First();
            while (font.Size > minTextSize)
            {
                float blockHeight = font.Size * 1.2f * lines.Count;
                if (MeasureWidthWithFallback(longestLine, font.Size, paint) <= maxTextWidth && blockHeight <= maxTextHeight)
                    break;
                font.Size -= 1f;
            }

            currentText = string.Join("\n", lines);
        }

        var splitLines = currentText.Split('\n');
        font.GetFontMetrics(out var metrics);

        float lineHeight = font.Size * 1.2f;
        float totalBlockHeight = lineHeight * splitLines.Length;

        float centerX = canvasWidth / 2f;
        float centerY = canvasHeight / 2f;
        
        float startY = centerY - totalBlockHeight / 2f + Math.Abs(metrics.Ascent);

        for (int i = 0; i < splitLines.Length; i++)
        {
            float y = startY + i * lineHeight;
            DrawTextWithFallback(canvas, splitLines[i], centerX, y, paint, font.Size);
        }

        var image = surface.Snapshot();
        var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        
        return data.ToArray();
    }
    
    public void DrawTextWithFallback(SKCanvas canvas, string text, float x, float y, SKPaint paint, float fontSize)
    {
        var fontManager = SKFontManager.Default;
        var style = new SKFontStyle(
            paint.FakeBoldText ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal,
            SKFontStyleWidth.Normal,
            SKFontStyleSlant.Upright);
        
        float totalWidth = 0f;
        var charData = new List<(string ch, SKTypeface typeface)>();

        foreach (var c in text)
        {
            var typeface = fontManager.MatchCharacter(null, style, null, c) ?? SKTypeface.FromFamilyName("Arial Unicode MS");

            var charStr = c.ToString();
            using var measurePaint = paint.Clone();
            measurePaint.Typeface = typeface;
            measurePaint.TextSize = fontSize;
            measurePaint.TextAlign = SKTextAlign.Left;

            totalWidth += measurePaint.MeasureText(charStr);
            charData.Add((charStr, typeface));
        }
        
        float cursorX = x - totalWidth / 2f;

        foreach (var (charStr, typeface) in charData)
        {
            using var charPaint = paint.Clone();
            charPaint.Typeface = typeface;
            charPaint.TextSize = fontSize;
            charPaint.TextAlign = SKTextAlign.Left;

            canvas.DrawText(charStr, cursorX, y, charPaint);
            cursorX += charPaint.MeasureText(charStr);
        }
    }

    private float MeasureWidthWithFallback(string text, float fontSize, SKPaint paint)
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

    private float MeasureHeightWithFallback(string text, float fontSize, SKPaint paint)
    {
        var fontManager = SKFontManager.Default;
        var style = new SKFontStyle(
            paint.FakeBoldText ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal,
            SKFontStyleWidth.Normal,
            SKFontStyleSlant.Upright);

        float maxAscent = 0f, maxDescent = 0f;
        var seen = new HashSet<SKTypeface>();

        foreach (var c in text)
        {
            var typeface = fontManager.MatchCharacter(null, style, null, c)
                           ?? SKTypeface.FromFamilyName("Arial Unicode MS");
            if (!seen.Add(typeface)) continue;

            using var f = new SKFont(typeface, fontSize);
            f.GetFontMetrics(out var m);
            maxAscent = Math.Max(maxAscent, Math.Abs(m.Ascent));
            maxDescent = Math.Max(maxDescent, Math.Abs(m.Descent));
        }

        return maxAscent + maxDescent;
    }

    // FORK (feed-overlay): multi-line feed rendering. The font size is fixed
    // from config; the box grows with content (up to a cap) instead of
    // shrinking the text, and the oldest lines are trimmed past the cap.

    private sealed class FeedLine
    {
        public string Ts = "";
        public string Body = "";
        public SKColor Color;
        public bool FirstOfEntry;
    }

    private void SendFeed(FeedPipeState state)
    {
        var png = DrawFeedOnCanvas(state);
        if (_desktop)
        {
            if (_form == null) return;
            if (state.Config.DesktopOverlay)
            {
                using var ms = new MemoryStream(png);
                using var image = Image.FromStream(ms);
                _form.SetFeedImage(new Bitmap(image));
            }
            else
            {
                _form.ClearImage();
            }
        }
        else
        {
            _openvroverlay?.ShowFeed(new MemoryStream(png), state.Config);
        }

        Console.WriteLine("Feed frame received!");
    }

    private void DrawTextLeftWithFallback(SKCanvas canvas, string text, float x, float y, SKPaint paint, float fontSize)
    {
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

    private List<(string ts, string body)> WrapFeedLine(string ts, string body, float maxWidth, float fontSize, SKPaint paint)
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

    private byte[] DrawFeedOnCanvas(FeedPipeState state)
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
            var body = e.Translation.Length > 0 ? $"{e.Original} → {e.Translation}" : e.Original;
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
}