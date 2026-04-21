namespace Loupedeck.PomoDeckPlugin
{
    using System;
    using SkiaSharp;

    /// <summary>
    /// Liquid glass renderer — all paints are local per frame. No static mutable state.
    /// Thread-safe via lock. No memory leaks.
    /// </summary>
    public static class LiquidRenderer
    {
        private static Int64 _frame;
        private static readonly DateTime _startTime = DateTime.UtcNow;
        private static readonly Object _lock = new();

        // Bubble constants — static to avoid per-frame allocation
        private static readonly Single[] BubbleCx = { 18, 38, 62, 82, 102 };
        private static readonly Single[] BubbleCr = { 2, 1.5f, 2.5f, 1.8f, 1.3f };
        private static readonly Single[] BubbleCo = { 0, 2.1f, 4.5f, 1.3f, 3.7f };

        private const Single V = 120f;

        public static BitmapImage Render(PomoDeckPlugin pomo, PluginImageSize imageSize)
        {
            lock (_lock) { return RenderFrame(pomo, imageSize); }
        }

        private static BitmapImage RenderFrame(PomoDeckPlugin pomo, PluginImageSize imageSize)
        {
            System.Threading.Interlocked.Increment(ref _frame);
            var tc = ThemeHelper.Resolve(pomo);
            var size = ThemeHelper.RenderSize(imageSize);
            var progress = pomo?.GetProgress() ?? 0.0;
            var secs = pomo?.GetRemainingSecs() ?? 0;
            var phase = pomo?.GetPhaseDisplay() ?? "FOCUS";
            var paused = pomo?.IsPaused() ?? false;
            var stopped = pomo?.IsStopped() ?? true;
            var running = pomo?.IsRunning() ?? false;
            var t = (Single)(DateTime.UtcNow - _startTime).TotalSeconds;

            using var bmp = new SKBitmap(size, size);
            using var c = new SKCanvas(bmp);
            c.Scale(size / V);
            c.Clear(tc.Bg);

            // Timer text — all local paints
            var timeStr = $"{secs / 60}:{secs % 60:D2}";
            var timeColor = paused ? tc.Dim : tc.Text;

            using var timeP = new SKPaint { IsAntialias = true, TextAlign = SKTextAlign.Center, SubpixelText = true, Color = timeColor, TextSize = 32, Typeface = ThemeHelper.Typeface };
            var fm = timeP.FontMetrics;
            var ty = 60f - 2 - (fm.Ascent + fm.Descent) / 2f;
            c.DrawText(timeStr, 60f, ty, timeP);

            using var phaseP = new SKPaint { IsAntialias = true, TextAlign = SKTextAlign.Center, SubpixelText = true, Color = tc.PhaseLabel, TextSize = 11, Typeface = ThemeHelper.Typeface };
            c.DrawText(phase, 60f, 82f, phaseP);

            if (paused)
            {
                using var bp = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = tc.Dim };
                c.DrawRoundRect(54, 32, 4, 10, 1, 1, bp);
                c.DrawRoundRect(62, 32, 4, 10, 1, 1, bp);
            }
            if (stopped)
            {
                using var sp = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = tc.Phase.WithAlpha(180) };
                using var tri = new SKPath();
                tri.MoveTo(56, 34); tri.LineTo(56, 46); tri.LineTo(66, 40); tri.Close();
                c.DrawPath(tri, sp);
            }

            // Water
            var wl = V - (Single)(progress * V);
            if (progress > 0.001)
            {
                var ws = running ? t * 1.2f : t * 0.4f;
                var wa = running ? 3.5f : 1.5f;

                using var wp = new SKPath();
                for (var x = 0f; x <= V; x += 2f) { var y = wl + Wv(x, ws, wa); if (x < 1) wp.MoveTo(x, y); else wp.LineTo(x, y); }
                wp.LineTo(V, V); wp.LineTo(0, V); wp.Close();

                var bgLum = (tc.Bg.Red * 0.299 + tc.Bg.Green * 0.587 + tc.Bg.Blue * 0.114) / 255.0;
                var isLight = bgLum > 0.5;
                var aTop = isLight ? (Byte)80 : (Byte)40;
                var aBot = isLight ? (Byte)140 : (Byte)75;

                using var shader = SKShader.CreateLinearGradient(new SKPoint(0, wl), new SKPoint(0, V), new[] { tc.Phase.WithAlpha(aTop), tc.Phase.WithAlpha(aBot) }, null, SKShaderTileMode.Clamp);
                using var waterP = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Shader = shader };
                c.DrawPath(wp, waterP);

                // Bubbles
                c.Save(); c.ClipPath(wp);
                var wH = V - wl;
                if (wH > 8)
                {
                    var bubAlpha = isLight ? (Byte)120 : (Byte)80;
                    var hlAlpha = isLight ? (Byte)80 : (Byte)40;
                    using var bubP = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 0.8f, Color = new SKColor(255, 255, 255, bubAlpha) };
                    using var hlP = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = new SKColor(255, 255, 255, hlAlpha) };
                    Single[] cx = BubbleCx; Single[] cr = BubbleCr; Single[] co = BubbleCo;
                    var spd = running ? 0.4f : 0.15f;
                    for (var i = 0; i < 5; i++)
                    {
                        var rawY = V - ((t * spd * 15f + co[i] * 20f) % V);
                        var yp = wl + (rawY / V) * wH;
                        if (yp < wl + cr[i] + 2 || yp > V - cr[i] - 1) continue;
                        var xp = cx[i] + (Single)Math.Sin(t * 0.8f + co[i]) * 2f;
                        c.DrawCircle(xp, yp, cr[i], bubP);
                        c.DrawCircle(xp - cr[i] * 0.3f, yp - cr[i] * 0.3f, cr[i] * 0.35f, hlP);
                    }
                }
                c.Restore();

                // Surface line
                var surfAlpha = isLight ? (Byte)90 : (Byte)55;
                using var surfP = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f, Color = tc.Phase.WithAlpha(surfAlpha) };
                using var sp2 = new SKPath();
                for (var x = 0f; x <= V; x += 2f) { var y = wl + Wv(x, ws, wa) * 0.85f; if (x < 1) sp2.MoveTo(x, y); else sp2.LineTo(x, y); }
                c.DrawPath(sp2, surfP);

                // Refracted text
                if (wl < 80)
                {
                    var rx = (Single)Math.Sin(t * 0.9f) * 1.8f; var ry = (Single)Math.Cos(t * 0.7f) * 0.8f;
                    c.Save(); c.ClipRect(new SKRect(0, wl, V, V));
                    using var blurT = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 1.2f);
                    using var rtP = new SKPaint { IsAntialias = true, TextAlign = SKTextAlign.Center, SubpixelText = true, Color = Blend(timeColor, tc.Phase, 0.35f), TextSize = 32, Typeface = ThemeHelper.Typeface, MaskFilter = blurT };
                    c.DrawText(timeStr, 60f + rx, ty + ry, rtP);
                    using var blurS = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 0.8f);
                    using var rpP = new SKPaint { IsAntialias = true, TextAlign = SKTextAlign.Center, SubpixelText = true, Color = Blend(tc.PhaseLabel, tc.Phase, 0.3f), TextSize = 11, Typeface = ThemeHelper.Typeface, MaskFilter = blurS };
                    c.DrawText(phase, 60f + rx, 82f + ry, rpP);
                    c.Restore();
                }
            }

            if (pomo?.IsRemote == true)
            {
                using var rp = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = tc.Accent };
                c.DrawCircle(112, 8, 4, rp);
            }

            using var img = SKImage.FromBitmap(bmp);
            using var data = img.Encode(SKEncodedImageFormat.Jpeg, 85);
            return BitmapImage.FromArray(data.ToArray());
        }

        private static Single Wv(Single x, Single s, Single a) =>
            (Single)Math.Sin(x * 0.05f + s) * a + (Single)Math.Sin(x * 0.08f + s * 1.3f) * a * 0.5f + (Single)Math.Sin(x * 0.13f + s * 0.7f) * a * 0.25f;

        private static SKColor Blend(SKColor a, SKColor b, Single t) => new(
            (Byte)(a.Red + (b.Red - a.Red) * t), (Byte)(a.Green + (b.Green - a.Green) * t), (Byte)(a.Blue + (b.Blue - a.Blue) * t), a.Alpha);
    }
}
