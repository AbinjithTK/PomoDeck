namespace Loupedeck.PomoDeckPlugin
{
    using System;
    using System.Diagnostics;
    using System.Timers;
    using SkiaSharp;

    /// <summary>
    /// Animated "Done" button.
    ///
    /// IDLE  → big "Done" label centered.
    /// PRESS → label slides down + shrinks, circle stroke draws in (trim-path),
    ///         tick draws as trim-path, holds, then fades back to IDLE.
    ///
    /// Animation is frame-driven at ~30 fps during the active phase,
    /// then drops back to 1 fps poll for phase-color sync.
    /// </summary>
    public class CompleteTaskCommand : PluginDynamicCommand
    {
        private PomoDeckPlugin Pomo => this.Plugin as PomoDeckPlugin;

        // ── colours resolved via ThemeHelper ──────────────────────────────

        // ── animation state ───────────────────────────────────────────────
        private enum AnimState { Idle, Animating, Hold }
        private AnimState _state = AnimState.Idle;
        private readonly Stopwatch _animClock = new();

        // Durations (ms)
        private const Int32 LabelSlideDur = 200;   // label moves down + shrinks
        private const Int32 CircleDur     = 300;    // circle stroke draws in
        private const Int32 TickDur       = 300;    // tick trim-path
        private const Int32 HoldDur       = 900;    // hold the completed state
        private const Int32 FadeOutDur    = 250;    // fade back to idle

        // Total animation length
        private const Int32 TotalAnimDur = LabelSlideDur + CircleDur + TickDur + HoldDur + FadeOutDur;

        // ── timers ────────────────────────────────────────────────────────
        private readonly Timer _pollTimer;   // 1 fps idle poll
        private readonly Timer _frameTimer;  // ~30 fps animation driver
        private String _lastPhase = "";

        // ── layout constants (in 80×80 virtual space) ─────────────────────
        private const Single Cx = 40f;
        private const Single Cy = 35f;
        private const Single CircleR = 24f;

        public CompleteTaskCommand()
            : base("2. Complete Task", "Mark your current task as done. Shows a checkmark animation. Requires PomoDeck app", "2. Tasks (App Required)")
        {
            this.IsWidget = true;

            _pollTimer = new Timer(2000) { AutoReset = true };
            _pollTimer.Elapsed += (_, _) =>
            {
                if (_state != AnimState.Idle) return;
                try
                {
                    var phase = Pomo?.GetPhaseDisplay() ?? "";
                    if (phase != _lastPhase) { _lastPhase = phase; RenderGate.Request("CompleteTask", () => { try { this.ActionImageChanged(); } catch { } }); }
                }
                catch { }
            };
            _pollTimer.Start();

            _frameTimer = new Timer(33) { AutoReset = true }; // ~30 fps, only runs during animation
            _frameTimer.Elapsed += (_, _) =>
            {
                try
                {
                    if (_state == AnimState.Idle) { _frameTimer.Stop(); return; }
                    if (_animClock.ElapsedMilliseconds >= TotalAnimDur)
                    {
                        _state = AnimState.Idle;
                        _frameTimer.Stop();
                        _animClock.Stop();
                    }
                    this.ActionImageChanged();
                }
                catch { }
            };
        }

        protected override Boolean OnLoad()
        {
            Pomo?.RegisterThemeListener(() => { RenderGate.Request("CompleteTask", () => { try { this.ActionImageChanged(); } catch { } }); });
            return true;
        }

        protected override Boolean OnUnload()
        {
            _pollTimer?.Stop(); _pollTimer?.Dispose();
            _frameTimer?.Stop(); _frameTimer?.Dispose();
            return true;
        }

        protected override void RunCommand(String actionParameter)
        {
            var pomo = Pomo;
            if (pomo == null) return;

            // Task completion requires app connection
            if (pomo.IsRemote)
                pomo.CompleteActiveTask();

            // Always play sound and haptic
            SoundAlert.PlayTaskDone();
            pomo.RaiseHaptic("timer_complete");

            // Kick animation
            _state = AnimState.Animating;
            _animClock.Restart();
            _frameTimer.Start();
            RenderGate.Request("CompleteTask", () => { try { this.ActionImageChanged(); } catch { } });
        }

        protected override String GetCommandDisplayName(String actionParameter, PluginImageSize imageSize) => null;

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize)
        {
            var pomo = Pomo;
            var tc = ThemeHelper.Resolve(pomo);

            var size = ThemeHelper.RenderSize(imageSize);
            using var bmp = new SKBitmap(size, size);
            using var c = new SKCanvas(bmp);
            var scale = size / 80f;
            c.Scale(scale);
            c.Clear(tc.Bg);

            // Show "app required" when not connected
            if (pomo == null || !pomo.IsRemote)
            {
                using var lp = new SKPaint
                {
                    IsAntialias = true, Color = tc.Dim, TextSize = 9,
                    TextAlign = SKTextAlign.Center, Typeface = ThemeHelper.Typeface,
                    SubpixelText = true
                };
                c.DrawText("DONE", Cx, 32, lp);
                using var sp = new SKPaint
                {
                    IsAntialias = true, Color = tc.Track, TextSize = 8,
                    TextAlign = SKTextAlign.Center, Typeface = ThemeHelper.Typeface,
                    SubpixelText = true
                };
                c.DrawText("APP REQUIRED", Cx, 48, sp);

                using var img2 = SKImage.FromBitmap(bmp);
                using var data2 = img2.Encode(SKEncodedImageFormat.Jpeg, 85);
                return BitmapImage.FromArray(data2.ToArray());
            }

            var t = _state != AnimState.Idle ? (Single)_animClock.ElapsedMilliseconds : 0f;

            if (_state == AnimState.Idle)
            {
                DrawIdle(c, tc.Phase, tc.Text);
            }
            else
            {
                DrawAnimated(c, t, tc.Phase, tc.Dim, tc.Bg);
            }

            using var img = SKImage.FromBitmap(bmp);
            using var data = img.Encode(SKEncodedImageFormat.Jpeg, 85);
            return BitmapImage.FromArray(data.ToArray());
        }

        // ── IDLE: big "Done" label ────────────────────────────────────────
        private static void DrawIdle(SKCanvas c, SKColor phaseColor, SKColor dimColor)
        {
            using var hintPaint = new SKPaint
            {
                IsAntialias = true, Color = phaseColor.WithAlpha(25),
                Style = SKPaintStyle.Stroke, StrokeWidth = 2f
            };
            c.DrawCircle(Cx, Cy, CircleR, hintPaint);

            using var labelPaint = new SKPaint
            {
                IsAntialias = true, Color = dimColor,
                TextSize = 11, TextAlign = SKTextAlign.Center,
                Typeface = ThemeHelper.Typeface, SubpixelText = true
            };
            var m = labelPaint.FontMetrics;
            c.DrawText("DONE", Cx, Cy - (m.Ascent + m.Descent) / 2f, labelPaint);
        }

        // ── ANIMATED: label slide → circle draw → tick draw → hold → fade ─
        private static void DrawAnimated(SKCanvas c, Single t, SKColor phaseColor, SKColor dimColor, SKColor bgColor)
        {
            // Phase 1: Label slides down + shrinks (0 → LabelSlideDur)
            var labelT = Clamp01(t / LabelSlideDur);
            var labelEased = EaseOutCubic(labelT);

            // Label: starts at center (Cy), ends at 72
            var labelY = Lerp(Cy + 5f, 72f, labelEased);
            var labelSize = Lerp(16f, 9f, labelEased);
            var labelAlpha = (Byte)(255 * (1f - labelEased * 0.3f)); // slight fade

            // Fade-out phase: everything fades back
            var fadeStart = LabelSlideDur + CircleDur + TickDur + HoldDur;
            var fadeT = t > fadeStart ? Clamp01((t - fadeStart) / FadeOutDur) : 0f;
            var fadeAlpha = 1f - EaseInCubic(fadeT);

            // Draw label (always visible, just moves)
            using var labelPaint = new SKPaint
            {
                IsAntialias = true,
                Color = dimColor.WithAlpha((Byte)(labelAlpha * fadeAlpha)),
                TextSize = labelSize, TextAlign = SKTextAlign.Center,
                Typeface = ThemeHelper.Typeface, SubpixelText = true
            };
            var fm = labelPaint.FontMetrics;
            c.DrawText("DONE", Cx, labelY - (fm.Ascent + fm.Descent) / 2f, labelPaint);

            // Phase 2: Circle draws in (LabelSlideDur → LabelSlideDur + CircleDur)
            var circleStart = LabelSlideDur * 0.5f; // overlap slightly with label
            var circleT = Clamp01((t - circleStart) / CircleDur);
            var circleEased = EaseOutCubic(circleT);

            if (circleEased > 0.001f)
            {
                var sweepAngle = circleEased * 360f;

                // Filled circle background — fades in
                using var fillPaint = new SKPaint
                {
                    IsAntialias = true,
                    Color = phaseColor.WithAlpha((Byte)(30 * circleEased * fadeAlpha)),
                    Style = SKPaintStyle.Fill
                };
                c.DrawCircle(Cx, Cy, CircleR, fillPaint);

                // Circle stroke trim-path
                using var strokePaint = new SKPaint
                {
                    IsAntialias = true,
                    Color = phaseColor.WithAlpha((Byte)(255 * fadeAlpha)),
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = 2.5f,
                    StrokeCap = SKStrokeCap.Round
                };
                var arcRect = new SKRect(Cx - CircleR, Cy - CircleR, Cx + CircleR, Cy + CircleR);
                using var arcPath = new SKPath();
                arcPath.AddArc(arcRect, -90f, sweepAngle);
                c.DrawPath(arcPath, strokePaint);
            }

            // Phase 3: Tick draws as trim-path (LabelSlideDur + CircleDur → ... + TickDur)
            var tickStart = LabelSlideDur + CircleDur * 0.7f; // overlap with circle end
            var tickT = Clamp01((t - tickStart) / TickDur);
            var tickEased = EaseOutCubic(tickT);

            if (tickEased > 0.001f)
            {
                DrawTickTrimPath(c, tickEased, phaseColor, fadeAlpha);
            }
        }

        /// <summary>
        /// Draw the tick mark as a trim-path: the stroke progressively reveals
        /// from the start point through the corner to the end.
        /// </summary>
        private static void DrawTickTrimPath(SKCanvas c, Single progress, SKColor color, Single alpha)
        {
            // Tick geometry: 3 points
            var p1 = new SKPoint(28, 36);  // start (left)
            var p2 = new SKPoint(37, 45);  // corner (bottom)
            var p3 = new SKPoint(52, 27);  // end (right-top)

            // Total path length: seg1 + seg2
            var seg1Len = Distance(p1, p2);
            var seg2Len = Distance(p2, p3);
            var totalLen = seg1Len + seg2Len;

            var drawLen = progress * totalLen;

            using var tickPaint = new SKPaint
            {
                IsAntialias = true,
                Color = color.WithAlpha((Byte)(255 * alpha)),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 5f,
                StrokeCap = SKStrokeCap.Round,
                StrokeJoin = SKStrokeJoin.Round
            };

            using var path = new SKPath();
            path.MoveTo(p1);

            if (drawLen <= seg1Len)
            {
                // Still on first segment
                var frac = drawLen / seg1Len;
                path.LineTo(Lerp(p1.X, p2.X, frac), Lerp(p1.Y, p2.Y, frac));
            }
            else
            {
                // First segment complete, drawing second
                path.LineTo(p2);
                var remaining = drawLen - seg1Len;
                var frac = remaining / seg2Len;
                path.LineTo(Lerp(p2.X, p3.X, frac), Lerp(p2.Y, p3.Y, frac));
            }

            c.DrawPath(path, tickPaint);
        }

        // ── helpers ───────────────────────────────────────────────────────

        private static Single Clamp01(Single v) => v < 0f ? 0f : v > 1f ? 1f : v;
        private static Single Lerp(Single a, Single b, Single t) => a + (b - a) * t;
        private static Single EaseOutCubic(Single t) { var f = 1f - t; return 1f - f * f * f; }
        private static Single EaseInCubic(Single t) => t * t * t;
        private static Single Distance(SKPoint a, SKPoint b)
        {
            var dx = b.X - a.X;
            var dy = b.Y - a.Y;
            return (Single)Math.Sqrt(dx * dx + dy * dy);
        }
    }
}
