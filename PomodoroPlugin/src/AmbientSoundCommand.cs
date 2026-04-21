namespace Loupedeck.PomoDeckPlugin
{
    using System;
    using SkiaSharp;

    /// <summary>
    /// Ambient tick toggle. Controls both plugin's local tick and app's tick.
    /// </summary>
    public class AmbientSoundCommand : PluginDynamicCommand
    {
        private PomoDeckPlugin Pomo => this.Plugin as PomoDeckPlugin;
        private readonly PressAnimation _anim;
        private Byte[] _cache;
        private String _cacheKey = "";

        public AmbientSoundCommand()
            : base("1. Sound", "Toggle the focus tick sound on or off. Starts enabled to match the app", "4. Preferences")
        {
            this.IsWidget = true;
            _anim = new PressAnimation(() => { RenderGate.Request("AmbientSound", () => { try { this.ActionImageChanged(); } catch { } }); });
        }

        protected override Boolean OnLoad()
        {
            Pomo?.RegisterThemeListener(() => { _cache = null; RenderGate.Request("AmbientSound", () => { try { this.ActionImageChanged(); } catch { } }); });
            Pomo?.RegisterSettingsListener(() => {
                // Only redraw if visual state actually changed
                var pomo = Pomo;
                var tc = ThemeHelper.Resolve(pomo);
                var enabled = pomo?.TickEnabled ?? true;
                var running = pomo?.IsRunning() ?? false;
                var key = $"{enabled}:{running}:{false}:{tc.Bg}";
                if (key != _cacheKey) { _cache = null; RenderGate.Request("AmbientSound", () => { try { this.ActionImageChanged(); } catch { } }); }
            });
            return true;
        }

        protected override void RunCommand(String actionParameter)
        {
            var pomo = Pomo;
            if (pomo == null) return;

            pomo.TickEnabled = !pomo.TickEnabled;

            if (pomo.IsRemote)
            {
                pomo.Bridge.SendSetting("tick_sounds_work", pomo.TickEnabled ? "true" : "false");
                pomo.Bridge.SendSetting("tick_sounds_break", pomo.TickEnabled ? "true" : "false");
            }

            if (!pomo.TickEnabled) AmbientTick.Stop();

            pomo.RaiseHaptic("phase_change");
            _anim.Kick();
        }

        protected override String GetCommandDisplayName(String actionParameter, PluginImageSize imageSize) => null;

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize)
        {
            var pomo = Pomo;
            var tc = ThemeHelper.Resolve(pomo);
            var size = ThemeHelper.RenderSize(imageSize);
            var enabled = pomo?.TickEnabled ?? true;
            var running = pomo?.IsRunning() ?? false;
            var color = enabled ? tc.Phase : tc.Dim;

            // Cache: return stored bytes if visual state unchanged
            var key = $"{enabled}:{running}:{_anim.IsActive}:{tc.Bg}";
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

            // Speaker icon — 30×28 centered at (40,35)
            using var ip = new SKPaint { IsAntialias = true, Color = color, Style = SKPaintStyle.Fill, PathEffect = SKPathEffect.CreateCorner(3) };
            using var body = new SKPath();
            body.MoveTo(24, 28); body.LineTo(30, 28); body.LineTo(37, 21);
            body.LineTo(37, 49); body.LineTo(30, 42); body.LineTo(24, 42); body.Close();
            c.DrawPath(body, ip);

            if (enabled)
            {
                using var wp = new SKPaint { IsAntialias = true, Color = tc.Phase, Style = SKPaintStyle.Stroke, StrokeWidth = 3.5f, StrokeCap = SKStrokeCap.Round };
                using var w1 = new SKPath(); w1.AddArc(new SKRect(40, 26, 48, 44), -40, 80); c.DrawPath(w1, wp);
                wp.Color = tc.Phase.WithAlpha(100);
                using var w2 = new SKPath(); w2.AddArc(new SKRect(44, 22, 55, 48), -40, 80); c.DrawPath(w2, wp);
            }
            else
            {
                // Mute X — compact square proportions
                using var mp = new SKPaint { IsAntialias = true, Color = tc.Dim.WithAlpha(180), Style = SKPaintStyle.Stroke, StrokeWidth = 3f, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
                using var xp = new SKPath();
                xp.MoveTo(44, 30); xp.LineTo(51, 40); xp.MoveTo(51, 30); xp.LineTo(44, 40);
                c.DrawPath(xp, mp);
            }

            c.Restore();

            // Label
            var label = enabled ? (running ? "TICK ●" : "TICK") : "MUTED";
            using var lp = new SKPaint { IsAntialias = true, Color = enabled ? tc.Text : tc.Dim, TextSize = 9, TextAlign = SKTextAlign.Center, Typeface = ThemeHelper.Typeface, SubpixelText = true };
            c.DrawText(label, 40, 68, lp);

            using var img = SKImage.FromBitmap(bmp);
            using var data = img.Encode(SKEncodedImageFormat.Jpeg, 85);
            var bytes = data.ToArray();
            if (!_anim.IsActive) { _cache = bytes; _cacheKey = key; }
            return BitmapImage.FromArray(bytes);
        }
    }

    /// <summary>MCI tick playback — isolated DllImport.</summary>
    internal static class AmbientTick
    {
        [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "mciSendStringW", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern Int32 Mci(String cmd, IntPtr buf, Int32 sz, IntPtr cb);

        private const String Alias = "pdTick";
        private static String _path;
        private static volatile Boolean _ready;

        public static void EnsureReady()
        {
            if (_ready) return;
            try
            {
                var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PomoDeck");
                System.IO.Directory.CreateDirectory(dir);
                var path = System.IO.Path.Combine(dir, "tick.wav");
                using var stream = System.Reflection.Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream("Loupedeck.PomoDeckPlugin.audio.tick.wav");
                if (stream == null) return;
                using var fs = System.IO.File.Create(path);
                stream.CopyTo(fs);
                _path = path;
                _ready = true;
            }
            catch { }
        }

        public static void Start()
        {
            if (!_ready || _path == null) return;
            Mci($"close {Alias}", IntPtr.Zero, 0, IntPtr.Zero);
            if (Mci($"open \"{_path}\" type waveaudio alias {Alias}", IntPtr.Zero, 0, IntPtr.Zero) != 0) return;
            Mci($"play {Alias} repeat", IntPtr.Zero, 0, IntPtr.Zero);
        }

        public static void Stop()
        {
            Mci($"close {Alias}", IntPtr.Zero, 0, IntPtr.Zero);
        }

        public static void PlayOnce()
        {
            if (!_ready || _path == null) return;
            Mci($"seek {Alias} to start", IntPtr.Zero, 0, IntPtr.Zero);
            var r = Mci($"play {Alias}", IntPtr.Zero, 0, IntPtr.Zero);
            if (r != 0)
            {
                Mci($"close {Alias}", IntPtr.Zero, 0, IntPtr.Zero);
                if (Mci($"open \"{_path}\" type waveaudio alias {Alias}", IntPtr.Zero, 0, IntPtr.Zero) != 0) return;
                Mci($"play {Alias}", IntPtr.Zero, 0, IntPtr.Zero);
            }
        }
    }
}
