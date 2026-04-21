namespace Loupedeck.PomoDeckPlugin
{
    using System;
    using System.Timers;
    using SkiaSharp;

    public class SkipPhaseCommand : PluginDynamicCommand
    {
        private PomoDeckPlugin Pomo => this.Plugin as PomoDeckPlugin;
        private readonly Timer _pollTimer;
        private readonly PressAnimation _anim;
        private String _lastPhase = "";
        private Byte[] _cache;
        private String _cacheKey = "";

        public SkipPhaseCommand()
            : base("4. Skip Phase", "Jump to the next phase. Skipping a focus session reduces your flow score", "1. Timer")
        {
            this.IsWidget = true;
            _anim = new PressAnimation(() => { RenderGate.Request("SkipPhase", () => { try { this.ActionImageChanged(); } catch { } }); });

            _pollTimer = new Timer(2000) { AutoReset = true };
            _pollTimer.Elapsed += (_, _) =>
            {
                if (_anim.IsActive) return;
                try
                {
                    var phase = Pomo?.GetPhaseDisplay() ?? "";
                    if (phase != _lastPhase) { _lastPhase = phase; RenderGate.Request("SkipPhase", () => { try { this.ActionImageChanged(); } catch { } }); }
                }
                catch { }
            };
            _pollTimer.Start();
        }

        protected override Boolean OnLoad()
        {
            Pomo?.RegisterThemeListener(() => { _cache = null; RenderGate.Request("SkipPhase", () => { try { this.ActionImageChanged(); } catch { } }); });
            return true;
        }

        protected override Boolean OnUnload()
        {
            _pollTimer?.Stop(); _pollTimer?.Dispose();
            return true;
        }

        protected override void RunCommand(String actionParameter)
        {
            var pomo = Pomo;
            if (pomo == null) return;
            pomo.OnUserInteraction();

            // Determine what phase we're skipping TO for the feedback sound
            // (standalone only — when remote, app handles the sound)
            PomodoroTimer.TimerPhase? skipTarget = null;
            if (!pomo.IsRemote)
            {
                skipTarget = pomo.Timer.Phase switch
                {
                    PomodoroTimer.TimerPhase.Work => pomo.Timer.IsLongBreakNext()
                        ? PomodoroTimer.TimerPhase.LongBreak
                        : PomodoroTimer.TimerPhase.ShortBreak,
                    _ => PomodoroTimer.TimerPhase.Work
                };
            }

            SoundAlert.StopAll();
            pomo.SkipPhase();

            // Play skip sound after skip completes
            if (skipTarget.HasValue)
                SoundAlert.PlaySkipForPhase(skipTarget.Value);

            pomo.RaiseHaptic("phase_change");
            _lastPhase = "";
            _anim.Kick();
        }

        protected override String GetCommandDisplayName(String actionParameter, PluginImageSize imageSize) => null;

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize)
        {
            var pomo = Pomo;
            var tc = ThemeHelper.Resolve(pomo);
            var phase = pomo?.GetPhaseDisplay() ?? "FOCUS";

            var key = $"{phase}:{_anim.IsActive}:{tc.Bg}";
            if (key == _cacheKey && _cache != null && !_anim.IsActive)
                return BitmapImage.FromArray(_cache);

            var size = ThemeHelper.RenderSize(imageSize);
            using var bmp = new SKBitmap(size, size);
            using var c = new SKCanvas(bmp);
            c.Scale(size / 80f);
            c.Clear(tc.Bg);

            // Apply press animation
            var s = _anim.Scale;
            c.Save();
            c.Translate(40, 35);
            c.Scale(s, s);
            c.Translate(-40, -35);

            // Skip icon — inside a 24×24 square centered at (40,35)
            // Triangle: 18w × 24h, bar: 4w × 24h, 2px gap
            using var ip = new SKPaint { IsAntialias = true, Color = tc.Phase, Style = SKPaintStyle.Fill, PathEffect = SKPathEffect.CreateCorner(3) };
            using var tri = new SKPath();
            tri.MoveTo(28, 23); tri.LineTo(48, 35); tri.LineTo(28, 47); tri.Close();
            c.DrawPath(tri, ip);
            ip.PathEffect = null;
            c.DrawRoundRect(50, 23, 4, 24, 2, 2, ip);

            c.Restore();

            // Label
            using var lp = new SKPaint { IsAntialias = true, Color = tc.Text, TextSize = 9, TextAlign = SKTextAlign.Center, Typeface = ThemeHelper.Typeface, SubpixelText = true };
            c.DrawText($"SKIP {phase}", 40, 68, lp);

            using var img = SKImage.FromBitmap(bmp);
            using var data = img.Encode(SKEncodedImageFormat.Jpeg, 85);
            var bytes = data.ToArray();
            if (!_anim.IsActive) { _cache = bytes; _cacheKey = key; }
            return BitmapImage.FromArray(bytes);
        }
    }
}
