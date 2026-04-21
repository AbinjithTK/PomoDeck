namespace Loupedeck.PomoDeckPlugin
{
    using System;
    using SkiaSharp;

    /// <summary>
    /// Press to toggle between Classic and Liquid widget themes.
    /// For Actions Ring users.
    /// </summary>
    public class ThemeSwitchCommand : PluginDynamicCommand
    {
        private PomoDeckPlugin Pomo => this.Plugin as PomoDeckPlugin;
        private readonly PressAnimation _anim;
        private Byte[] _cache;
        private String _cacheKey = "";

        public ThemeSwitchCommand()
            : base("2. Timer Style", "Switch between Classic (dial) and Liquid (water fill) timer display. Also available via double-tap on the Focus Timer. For Actions Ring", "4. Preferences")
        {
            this.IsWidget = true;
            _anim = new PressAnimation(() => { RenderGate.Request("ThemeSwitch", () => { try { this.ActionImageChanged(); } catch { } }); });
        }

        protected override Boolean OnLoad()
        {
            Pomo?.RegisterThemeListener(() => { _cache = null; RenderGate.Request("ThemeSwitch", () => { try { this.ActionImageChanged(); } catch { } }); });
            if (Pomo?.Skin != null)
                Pomo.Skin.SkinChanged += () => { _cache = null; RenderGate.Request("ThemeSwitch", () => { try { this.ActionImageChanged(); } catch { } }); };
            return true;
        }

        protected override void RunCommand(String actionParameter)
        {
            var pomo = Pomo;
            if (pomo?.Skin == null) return;

            var newId = pomo.Skin.CycleNext();

            if (pomo.IsRemote)
                try { pomo.Bridge.Send($"{{\"type\":\"setSkin\",\"id\":\"{newId}\"}}"); } catch { }
            pomo.RaiseHaptic("phase_change");
            _anim.Kick();
        }

        protected override String GetCommandDisplayName(String actionParameter, PluginImageSize imageSize) => null;

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize)
        {
            var pomo = Pomo;
            var tc = ThemeHelper.Resolve(pomo);
            var size = ThemeHelper.RenderSize(imageSize);
            var isLiquid = (pomo?.Skin?.ActiveTimerWidget ?? "classic") == "liquid";
            var name = pomo?.Skin?.ActiveName ?? "Classic";

            var key = $"{isLiquid}:{name}:{_anim.IsActive}:{tc.Bg}";
            if (key == _cacheKey && _cache != null && !_anim.IsActive)
                return BitmapImage.FromArray(_cache);

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

            if (isLiquid)
            {
                // Filled water shape, ~28px tall centered at (40,35)
                using var wp = new SKPaint { IsAntialias = true, Color = tc.Phase.WithAlpha(100), Style = SKPaintStyle.Fill };
                using var wPath = new SKPath();
                wPath.MoveTo(26, 32);
                for (var x = 26f; x <= 54f; x += 2)
                    wPath.LineTo(x, 28f + (Single)Math.Sin(x * 0.2) * 3f);
                wPath.LineTo(54, 48); wPath.LineTo(26, 48); wPath.Close();
                c.DrawPath(wPath, wp);

                // Surface line
                using var sp = new SKPaint { IsAntialias = true, Color = tc.Phase, Style = SKPaintStyle.Stroke, StrokeWidth = 2.5f, StrokeCap = SKStrokeCap.Round };
                using var sPath = new SKPath();
                sPath.MoveTo(26, 32);
                for (var x = 26f; x <= 54f; x += 2)
                    sPath.LineTo(x, 28f + (Single)Math.Sin(x * 0.2) * 3f);
                c.DrawPath(sPath, sp);
            }
            else
            {
                // Filled donut ring, r=14 centered at (40,35)
                using var tp = new SKPaint { IsAntialias = true, Color = tc.Track, Style = SKPaintStyle.Fill };
                c.DrawCircle(40, 35, 14, tp);

                using var ap = new SKPaint { IsAntialias = true, Color = tc.Phase, Style = SKPaintStyle.Fill };
                using var wedge = new SKPath();
                wedge.MoveTo(40, 35);
                wedge.ArcTo(new SKRect(26, 21, 54, 49), -90, 270, false);
                wedge.Close();
                c.DrawPath(wedge, ap);

                using var inner = new SKPaint { IsAntialias = true, Color = tc.Bg, Style = SKPaintStyle.Fill };
                c.DrawCircle(40, 35, 10, inner);
            }

            c.Restore();

            // Label
            using var lp = new SKPaint { IsAntialias = true, Color = tc.Text, TextSize = 9, TextAlign = SKTextAlign.Center, Typeface = ThemeHelper.Typeface, SubpixelText = true };
            c.DrawText(name.ToUpperInvariant(), 40, 68, lp);

            using var img = SKImage.FromBitmap(bmp);
            using var data = img.Encode(SKEncodedImageFormat.Jpeg, 85);
            var bytes = data.ToArray();
            if (!_anim.IsActive) { _cache = bytes; _cacheKey = key; }
            return BitmapImage.FromArray(bytes);
        }
    }
}
