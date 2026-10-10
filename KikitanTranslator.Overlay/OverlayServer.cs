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

    // FORK (feed-overlay): routes rendered feed frames (see FeedRenderer) to the
    // desktop form / VR overlay.

    private void SendFeed(FeedPipeState state)
    {
        var png = FeedRenderer.DrawFeedOnCanvas(state);
        if (_desktop)
        {
            if (_form == null) return;
            if (state.Config.DesktopOverlay)
            {
                using var ms = new MemoryStream(png);
                using var image = Image.FromStream(ms);
                _form.SetFeedImage(new Bitmap(image), state.Config.DisplayDurationSec);
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



}