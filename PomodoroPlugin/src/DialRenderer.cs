namespace Loupedeck.PomoDeckPlugin
{
    using System;
    using SkiaSharp;

    /// <summary>
    /// SkiaSharp dial renderer. Thread-safe — all paints are local.
    /// </summary>
    public static class DialRenderer
    {
        private const Int32 VSize = 120;
        private const Single VCenter = VSize / 2f;
        private const Single ArcRadius = 50f;
        private const Single ArcStroke = 6f;

        private static readonly SKColor WorkColor = new(0, 212, 170);
        private static readonly SKColor ShortColor = new(0, 229, 160);
        private static readonly SKColor LongColor = new(0, 180, 216);
        private static readonly SKColor BgColor = new(26, 29, 35);
        private static readonly SKColor TrackColor = new(34, 38, 46);
        private static readonly SKColor TextColor = new(232, 234, 237);
        private static readonly SKColor DimColor = new(139, 149, 165);

        private static SKTypeface _typeface;
        private static readonly Object _fontLock = new();

        public static BitmapImage Render(PomodoroTimer timer, PluginImageSize imageSize)
        {
            var bw = imageSize.GetButtonWidth();
            var bh = imageSize.GetButtonHeight();
            var renderSize = Math.Max(Math.Max(bw, bh), 200);

            using var bitmap = new SKBitmap(renderSize, renderSize);
            using var canvas = new SKCanvas(bitmap);
            canvas.Scale((Single)renderSize / VSize, (Single)renderSize / VSize);
            canvas.Clear(BgColor);

            var progress = timer.Progress;
            var phaseColor = GetPhaseColor(timer.Phase);
            var paused = timer.State == PomodoroTimer.TimerState.Paused;
            var stopped = timer.State == PomodoroTimer.TimerState.Stopped;
            var remainingSecs = (Int32)Math.Ceiling(timer.Remaining.TotalSeconds);

            DrawDial(canvas, progress, phaseColor, paused, stopped, remainingSecs,
                     timer.GetPhaseDisplayName(), timer.CompletedSessions,
                     timer.Phase == PomodoroTimer.TimerPhase.Work, false,
                     BgColor, TrackColor, TextColor, DimColor, SKColors.Empty,
                     timer.Phase switch
                     {
                         PomodoroTimer.TimerPhase.ShortBreak => ThemeHelper.BreakLabelColor,
                         PomodoroTimer.TimerPhase.LongBreak => ThemeHelper.LongBreakLabelColor,
                         _ => ThemeHelper.AccentColor
                     });

            using var img = SKImage.FromBitmap(bitmap);
            using var data = img.Encode(SKEncodedImageFormat.Jpeg, 85);
            return BitmapImage.FromArray(data.ToArray());
        }

        /// <summary>Render using the plugin's unified API (works for both local and remote).</summary>
        public static BitmapImage RenderUnified(PomoDeckPlugin pomo, PluginImageSize imageSize)
        {
            var bw = imageSize.GetButtonWidth();
            var bh = imageSize.GetButtonHeight();
            var renderSize = Math.Max(Math.Max(bw, bh), 200);

            var theme = pomo.Bridge?.Theme;
            var bgColor = theme?.GetColor("--color-background", BgColor) ?? BgColor;
            var trackColor = theme?.GetColor("--color-background-light", TrackColor) ?? TrackColor;
            var textColor = theme?.GetColor("--color-foreground", TextColor) ?? TextColor;
            var dimColor = theme?.GetColor("--color-foreground-darker", DimColor) ?? DimColor;
            var workColor = theme?.GetColor("--color-focus-round", WorkColor) ?? WorkColor;
            var shortColor = theme?.GetColor("--color-short-round", ShortColor) ?? ShortColor;
            var longColor = theme?.GetColor("--color-long-round", LongColor) ?? LongColor;
            var accentColor = theme?.GetColor("--color-accent", new SKColor(5, 236, 140)) ?? new SKColor(5, 236, 140);

            var phaseColor = pomo.GetPhase() switch
            {
                PomodoroTimer.TimerPhase.ShortBreak => shortColor,
                PomodoroTimer.TimerPhase.LongBreak => longColor,
                _ => workColor
            };

            using var bitmap = new SKBitmap(renderSize, renderSize);
            using var canvas = new SKCanvas(bitmap);
            canvas.Scale((Single)renderSize / VSize, (Single)renderSize / VSize);
            canvas.Clear(bgColor);

            var progress = pomo.GetProgress();
            var paused = pomo.IsPaused();
            var stopped = pomo.IsStopped();
            var remainingSecs = pomo.GetRemainingSecs();

            DrawDial(canvas, progress, phaseColor, paused, stopped, remainingSecs,
                     pomo.GetPhaseDisplay(), pomo.GetCompletedSessions(),
                     pomo.GetPhase() == PomodoroTimer.TimerPhase.Work,
                     pomo.IsRemote,
                     bgColor, trackColor, textColor, dimColor, accentColor,
                     pomo.GetPhase() switch
                     {
                         PomodoroTimer.TimerPhase.ShortBreak => ThemeHelper.BreakLabelColor,
                         PomodoroTimer.TimerPhase.LongBreak => ThemeHelper.LongBreakLabelColor,
                         _ => accentColor
                     });

            using var img = SKImage.FromBitmap(bitmap);
            using var data = img.Encode(SKEncodedImageFormat.Jpeg, 85);
            return BitmapImage.FromArray(data.ToArray());
        }

        // ── Shared draw logic — all paints are local, fully thread-safe ───

        private static void DrawDial(
            SKCanvas canvas,
            Double progress, SKColor phaseColor,
            Boolean paused, Boolean stopped, Int32 remainingSecs,
            String phaseLabel, Int32 completedSessions, Boolean isWorkPhase,
            Boolean isRemote,
            SKColor bgColor, SKColor trackColor, SKColor textColor, SKColor dimColor, SKColor accentColor,
            SKColor phaseLabelColor)
        {
            // Accent border — makes the main timer stand out
            using (var borderPaint = new SKPaint
            {
                IsAntialias = true, Style = SKPaintStyle.Stroke,
                StrokeWidth = 1.5f, Color = phaseColor.WithAlpha(70)
            })
            {
                canvas.DrawRoundRect(2, 2, VSize - 4, VSize - 4, 8, 8, borderPaint);
            }

            // Slightly brighter background in the center area
            using var glowShader = SKShader.CreateRadialGradient(
                    new SKPoint(VCenter, VCenter), ArcRadius + 10,
                    new[] { trackColor.WithAlpha(50), SKColors.Transparent },
                    null, SKShaderTileMode.Clamp);
            using (var glowPaint = new SKPaint
            {
                IsAntialias = true, Style = SKPaintStyle.Fill,
                Shader = glowShader
            })
            {
                canvas.DrawRect(0, 0, VSize, VSize, glowPaint);
            }

            var arcRect = new SKRect(VCenter - ArcRadius, VCenter - ArcRadius,
                                     VCenter + ArcRadius, VCenter + ArcRadius);

            // Track ring
            using (var arcPaint = new SKPaint
            {
                IsAntialias = true, Style = SKPaintStyle.Stroke,
                StrokeWidth = ArcStroke, StrokeCap = SKStrokeCap.Round,
                Color = trackColor
            })
            using (var p = new SKPath()) { p.AddArc(arcRect, 0, 360); canvas.DrawPath(p, arcPaint); }

            // Progress arc + cap dot
            if (progress > 0.001)
            {
                using var arcPaint = new SKPaint
                {
                    IsAntialias = true, Style = SKPaintStyle.Stroke,
                    StrokeWidth = ArcStroke, StrokeCap = SKStrokeCap.Round,
                    Color = phaseColor
                };
                using (var p = new SKPath()) { p.AddArc(arcRect, -90f, (Single)(progress * 360.0)); canvas.DrawPath(p, arcPaint); }

                var a = (-90.0 + progress * 360.0) * Math.PI / 180.0;
                using var dotPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = SKColors.White };
                canvas.DrawCircle(VCenter + ArcRadius * (Single)Math.Cos(a),
                                  VCenter + ArcRadius * (Single)Math.Sin(a),
                                  ArcStroke / 2f + 1f, dotPaint);
            }

            // Countdown text
            EnsureFont();
            using (var timePaint = new SKPaint
            {
                IsAntialias = true, TextAlign = SKTextAlign.Center, SubpixelText = true,
                Color = paused ? dimColor : textColor,
                TextSize = 28, Typeface = _typeface
            })
            {
                var m = timePaint.FontMetrics;
                canvas.DrawText($"{remainingSecs / 60}:{remainingSecs % 60:D2}",
                    VCenter, VCenter - 2 - (m.Ascent + m.Descent) / 2f, timePaint);
            }

            // Phase label
            using (var labelPaint = new SKPaint
            {
                IsAntialias = true, TextAlign = SKTextAlign.Center, SubpixelText = true,
                Color = phaseLabelColor, TextSize = 11, Typeface = _typeface
            })
            {
                var m = labelPaint.FontMetrics;
                canvas.DrawText(phaseLabel,
                    VCenter, VCenter + 18 - (m.Ascent + m.Descent) / 2f, labelPaint);
            }

            // Round dots
            var dotTotal = Math.Clamp(completedSessions + 1, 1, 4);
            var dotStartX = VCenter - (dotTotal * 12f - 12f + 6f) / 2f + 3f;
            var dotY = VCenter + 30f;
            for (var i = 0; i < dotTotal; i++)
            {
                var filled = i < completedSessions || (i == completedSessions && isWorkPhase);
                using var dp = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = filled ? phaseColor : trackColor };
                canvas.DrawCircle(dotStartX + i * 12f, dotY, 3f, dp);
            }

            // Pause bars
            if (paused)
            {
                using var pp = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = dimColor };
                canvas.DrawRoundRect(VCenter - 6, VCenter - 28, 4, 10, 1, 1, pp);
                canvas.DrawRoundRect(VCenter + 2, VCenter - 28, 4, 10, 1, 1, pp);
            }

            // Play triangle when stopped
            if (stopped && progress < 0.001)
            {
                using var tp = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = phaseColor.WithAlpha(180) };
                using var tri = new SKPath();
                var ty = VCenter - 26;
                tri.MoveTo(VCenter - 4, ty); tri.LineTo(VCenter - 4, ty + 12); tri.LineTo(VCenter + 6, ty + 6); tri.Close();
                canvas.DrawPath(tri, tp);
            }

            // Remote indicator dot
            if (isRemote && accentColor != SKColors.Empty)
            {
                using var rp = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = accentColor };
                canvas.DrawCircle(VSize - 12, 12, 4, rp);
            }
        }

        private static void EnsureFont()
        {
            if (_typeface != null) return;
            lock (_fontLock)
            {
                _typeface ??= SKTypeface.FromFamilyName("Segoe UI",
                    SKFontStyleWeight.Medium, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
                    ?? SKTypeface.Default;
            }
        }

        private static SKColor GetPhaseColor(PomodoroTimer.TimerPhase phase) => phase switch
        {
            PomodoroTimer.TimerPhase.Work => WorkColor,
            PomodoroTimer.TimerPhase.ShortBreak => ShortColor,
            PomodoroTimer.TimerPhase.LongBreak => LongColor,
            _ => WorkColor
        };
    }
}
