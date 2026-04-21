namespace Loupedeck.PomoDeckPlugin
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Timers;
    using SkiaSharp;

    /// <summary>
    /// Pomo Jar — physics-based tomato collection. Thread-safe, no deadlocks.
    /// Uses volatile snapshot array for lock-free rendering.
    /// </summary>
    public class PomoJarWidget : PluginDynamicCommand
    {
        private PomoDeckPlugin Pomo => this.Plugin as PomoDeckPlugin;
        private readonly Timer _pollTimer;
        private readonly Timer _physTimer;
        private Int32 _prevCount;
        private volatile Boolean _simulating;
        private Int32 _simFrame;
        private const Int32 MaxFrames = 45;
        private Byte[] _cachedBytes;
        private Byte[] _disconnectedBytes; // cached "app required" screen

        // Physics state — only modified on physics thread
        private Single[] _bx, _by, _br, _bvx, _bvy;
        private UInt32[] _bcolor;
        private Int32 _ballCount;
        private Int32 _frozenCount; // balls below this index don't move

        // Snapshot for rendering — volatile swap, no lock needed
        private volatile Single[] _snapX, _snapY, _snapR;
        private volatile UInt32[] _snapColor;
        private volatile Int32 _snapCount;

        private const Single JL = 4f, JR = 76f, JT = 4f, JB = 76f;

        public PomoJarWidget() : base("2. Pomo Jar", "Collects a tomato for each completed focus session. Bigger = longer session. Color shows flow quality — green for deep focus, red for distracted. Press to view streak", "3. Flow & Stats (App Required)")
        {
            this.IsWidget = true;
            _bx = _by = _br = _bvx = _bvy = Array.Empty<Single>();
            _bcolor = Array.Empty<UInt32>();
            _snapX = _snapY = _snapR = Array.Empty<Single>();
            _snapColor = Array.Empty<UInt32>();

            _physTimer = new Timer(33) { AutoReset = true };
            _physTimer.Elapsed += (_, _) =>
            {
                try
                {
                    if (!_simulating) { _physTimer.Stop(); return; }
                    _simFrame++;
                    StepPhysics();
                    PublishSnapshot();
                    if (_simFrame >= MaxFrames)
                    {
                        _simulating = false;
                        _physTimer.Stop();
                        _cachedBytes = null; // force one final render to cache
                        RenderGate.Request("PomoJar", () => { try { this.ActionImageChanged(); } catch { } });
                        return; // don't render again
                    }
                    RenderGate.Request("PomoJar", () => { try { this.ActionImageChanged(); } catch { } });
                }
                catch { _simulating = false; _physTimer.Stop(); }
            };

            _pollTimer = new Timer(30000) { AutoReset = true }; // 30s fallback — app broadcasts on session complete
            _pollTimer.Elapsed += (_, _) => { try { RequestJar(); } catch { } };
            _pollTimer.Start();
        }

        private volatile Boolean _bridgeWasConnected;

        protected override Boolean OnLoad()
        {
            Pomo?.RegisterJarListener(UpdateJar);
            Pomo?.RegisterThemeListener(() => {
                _cachedBytes = null;
                _disconnectedBytes = null;
                RenderGate.Request("PomoJar", () => { try { this.ActionImageChanged(); } catch { } });
            });
            // Only request jar once when bridge first connects (not on every state update)
            Pomo?.RegisterSettingsListener(() => {
                var connected = Pomo?.Bridge?.IsConnected == true;
                if (connected && !_bridgeWasConnected) { _bridgeWasConnected = true; RequestJar(); }
                else if (!connected) { _bridgeWasConnected = false; }
            });
            RequestJar();
            return true;
        }

        protected override Boolean OnUnload()
        {
            _pollTimer?.Stop(); _pollTimer?.Dispose();
            _physTimer?.Stop(); _physTimer?.Dispose();
            return true;
        }

        private void RequestJar()
        {
            var b = Pomo?.Bridge;
            if (b != null && b.IsConnected) b.Send("{\"type\":\"getJar\"}");
        }

        internal void UpdateJar(List<JarTomato> tomatoes)
        {
            var newCount = tomatoes?.Count ?? 0;
            // Same count as last time — do nothing, keep settled positions
            if (newCount == _prevCount) return;

            if (newCount > _prevCount && _ballCount > 0)
            {
                // Add new tomatoes — existing balls are frozen
                var oldCount = _ballCount;
                _frozenCount = oldCount;
                var total = oldCount + (newCount - _prevCount);
                Array.Resize(ref _bx, total);
                Array.Resize(ref _by, total);
                Array.Resize(ref _br, total);
                Array.Resize(ref _bvx, total);
                Array.Resize(ref _bvy, total);
                Array.Resize(ref _bcolor, total);

                var rng = new Random(newCount);
                for (var i = oldCount; i < total; i++)
                {
                    var idx = _prevCount + (i - oldCount);
                    if (idx >= newCount) break;
                    var t = tomatoes[idx];
                    var r = TomatoR(t.DurationSecs);
                    _bx[i] = JL + r + (Single)(rng.NextDouble() * (JR - JL - r * 2));
                    _by[i] = JT - r;
                    _br[i] = r;
                    _bvx[i] = 0; _bvy[i] = 0;
                    _bcolor[i] = FlowColor(t.FlowScore);
                }
                _ballCount = total;
            }
            else
            {
                // Full rebuild
                _ballCount = newCount;
                _frozenCount = 0;
                _bx = new Single[newCount]; _by = new Single[newCount];
                _br = new Single[newCount]; _bvx = new Single[newCount];
                _bvy = new Single[newCount]; _bcolor = new UInt32[newCount];

                var rng = new Random(42);
                for (var i = 0; i < newCount; i++)
                {
                    var t = tomatoes[i];
                    var r = TomatoR(t.DurationSecs);
                    _bx[i] = JL + r + (Single)(rng.NextDouble() * (JR - JL - r * 2));
                    _by[i] = JT + (Single)(rng.NextDouble() * (JB * 0.3f));
                    _br[i] = r; _bvx[i] = 0; _bvy[i] = 0;
                    _bcolor[i] = FlowColor(t.FlowScore);
                }
            }

            _prevCount = newCount;
            _cachedBytes = null; // invalidate cache for new data
            _simFrame = 0;
            _simulating = true;
            PublishSnapshot();
            _physTimer.Start();
            RenderGate.Request("PomoJar", () => { try { this.ActionImageChanged(); } catch { } }); // immediate first frame
        }

        private void StepPhysics()
        {
            var n = _ballCount;
            var frozen = _frozenCount;
            if (n == 0) return;

            // Apply gravity and velocity ONLY to non-frozen balls
            for (var i = frozen; i < n; i++)
            {
                _bvy[i] += 0.4f;
                _bvx[i] *= 0.88f;
                _bvy[i] *= 0.92f;
                _bx[i] += _bvx[i];
                _by[i] += _bvy[i];
            }

            // Collision passes
            for (var pass = 0; pass < 8; pass++)
            {
                // Wall collisions only for non-frozen
                for (var i = frozen; i < n; i++)
                {
                    var ri = _br[i];
                    if (_by[i] + ri > JB) { _by[i] = JB - ri; _bvy[i] = -Math.Abs(_bvy[i]) * 0.12f; if (Math.Abs(_bvy[i]) < 0.2f) _bvy[i] = 0; }
                    if (_bx[i] - ri < JL) { _bx[i] = JL + ri; _bvx[i] = Math.Abs(_bvx[i]) * 0.12f; }
                    if (_bx[i] + ri > JR) { _bx[i] = JR - ri; _bvx[i] = -Math.Abs(_bvx[i]) * 0.12f; }
                    if (_by[i] - ri < JT) { _by[i] = JT + ri; _bvy[i] = Math.Abs(_bvy[i]) * 0.12f; }
                }

                // Ball-ball collisions — frozen balls act as immovable walls
                for (var i = 0; i < n; i++)
                {
                    for (var j = i + 1; j < n; j++)
                    {
                        // Skip if both are frozen
                        if (i < frozen && j < frozen) continue;

                        var dx = _bx[j] - _bx[i]; var dy = _by[j] - _by[i];
                        var dSq = dx * dx + dy * dy;
                        var minD = _br[i] + _br[j];
                        if (dSq < minD * minD && dSq > 0.001f)
                        {
                            var d = (Single)Math.Sqrt(dSq);
                            var nx = dx / d; var ny = dy / d;
                            var ov = minD - d;

                            if (i < frozen)
                            {
                                // i is frozen — only push j
                                _bx[j] += nx * ov; _by[j] += ny * ov;
                                if (pass == 0) { _bvx[j] += nx * 0.3f; _bvy[j] += ny * 0.3f; }
                            }
                            else if (j < frozen)
                            {
                                // j is frozen — only push i
                                _bx[i] -= nx * ov; _by[i] -= ny * ov;
                                if (pass == 0) { _bvx[i] -= nx * 0.3f; _bvy[i] -= ny * 0.3f; }
                            }
                            else
                            {
                                // Both movable
                                _bx[i] -= nx * ov * 0.55f; _by[i] -= ny * ov * 0.55f;
                                _bx[j] += nx * ov * 0.55f; _by[j] += ny * ov * 0.55f;
                                if (pass == 0)
                                {
                                    var rv = (_bvx[i] - _bvx[j]) * nx + (_bvy[i] - _bvy[j]) * ny;
                                    if (rv > 0) { _bvx[i] -= rv * nx * 0.3f; _bvy[i] -= rv * ny * 0.3f; _bvx[j] += rv * nx * 0.3f; _bvy[j] += rv * ny * 0.3f; }
                                }
                            }
                        }
                    }
                }
            }

            if (_simFrame >= MaxFrames)
                for (var i = 0; i < n; i++) { _bvx[i] = 0; _bvy[i] = 0; }
        }

        /// <summary>Copy physics state to snapshot arrays — volatile swap, no lock.</summary>
        private void PublishSnapshot()
        {
            var n = _ballCount;
            var sx = new Single[n]; var sy = new Single[n]; var sr = new Single[n]; var sc = new UInt32[n];
            Array.Copy(_bx, sx, n); Array.Copy(_by, sy, n); Array.Copy(_br, sr, n); Array.Copy(_bcolor, sc, n);
            // Write arrays BEFORE count — reader checks count first, gets consistent view
            _snapX = sx; _snapY = sy; _snapR = sr; _snapColor = sc;
            System.Threading.Thread.MemoryBarrier();
            _snapCount = n;
        }

        protected override void RunCommand(String ap)
        {
            var p = Pomo;
            if (p?.Bridge != null && p.Bridge.IsConnected)
                p.Bridge.Send("{\"type\":\"toggleStats\"}");
            else
                PomodoroApplication.Launch();
        }

        protected override String GetCommandDisplayName(String a, PluginImageSize s) => null;

        protected override BitmapImage GetCommandImage(String ap, PluginImageSize imageSize)
        {
            // Show "app required" when not connected — cached to avoid re-render
            var pomo = Pomo;
            if (pomo == null || !pomo.IsRemote)
            {
                if (_disconnectedBytes != null) return BitmapImage.FromArray(_disconnectedBytes);
                using var b = new SKBitmap(ThemeHelper.RenderSize(imageSize), ThemeHelper.RenderSize(imageSize));
                using var cv = new SKCanvas(b);
                var tc = ThemeHelper.Resolve(pomo);
                cv.Scale(ThemeHelper.RenderSize(imageSize) / 80f);
                cv.Clear(tc.Bg);
                using var lp = new SKPaint { IsAntialias = true, Color = tc.Dim, TextSize = 9, TextAlign = SKTextAlign.Center, Typeface = ThemeHelper.Typeface, SubpixelText = true };
                cv.DrawText("POMO JAR", 40, 32, lp);
                using var sp = new SKPaint { IsAntialias = true, Color = tc.Track, TextSize = 8, TextAlign = SKTextAlign.Center, Typeface = ThemeHelper.Typeface, SubpixelText = true };
                cv.DrawText("APP REQUIRED", 40, 48, sp);
                using var img2 = SKImage.FromBitmap(b);
                using var data2 = img2.Encode(SKEncodedImageFormat.Jpeg, 85);
                _disconnectedBytes = data2.ToArray();
                return BitmapImage.FromArray(_disconnectedBytes);
            }
            // Clear disconnected cache when connected (theme might change)
            _disconnectedBytes = null;

            // Bulletproof cache: return stored bytes directly, never re-render settled jar
            if (!_simulating && _cachedBytes != null)
                return BitmapImage.FromArray(_cachedBytes);

            try
            {
                var tc = ThemeHelper.Resolve(Pomo);
                var size = ThemeHelper.RenderSize(imageSize);
                var n = _snapCount;
                var sx = _snapX; var sy = _snapY; var sr = _snapR; var sc = _snapColor;

                using var bmp = new SKBitmap(size, size);
                using var c = new SKCanvas(bmp);
                c.Scale(size / 80f);
                c.Clear(tc.Bg);

                {
                    var bgLum = (tc.Bg.Red * 0.299 + tc.Bg.Green * 0.587 + tc.Bg.Blue * 0.114) / 255.0;
                    var countAlpha = bgLum > 0.5 ? (Byte)30 : (Byte)18;
                    using var cp = new SKPaint { IsAntialias = true, Color = tc.Dim.WithAlpha(countAlpha), TextSize = 36, TextAlign = SKTextAlign.Center, Typeface = ThemeHelper.Typeface, SubpixelText = true };
                    var fm = cp.FontMetrics;
                    c.DrawText($"{n}", 40, 40 - (fm.Ascent + fm.Descent) / 2f, cp);
                }

                for (var i = 0; i < n && i < sx.Length; i++)
                {
                    using var bp = new SKPaint { IsAntialias = true, Color = new SKColor(sc[i]), Style = SKPaintStyle.Fill };
                    c.DrawCircle(sx[i], sy[i], sr[i], bp);
                }

                using var img = SKImage.FromBitmap(bmp);
                using var data = img.Encode(SKEncodedImageFormat.Jpeg, 85);
                var bytes = data.ToArray();

                // Cache the final settled frame as raw bytes
                if (!_simulating) { _cachedBytes = bytes; }
                return BitmapImage.FromArray(bytes);
            }
            catch
            {
                if (_cachedBytes != null) return BitmapImage.FromArray(_cachedBytes);
                using var b2 = new BitmapBuilder(imageSize);
                b2.Clear(new BitmapColor(26, 29, 35));
                return b2.ToImage();
            }
        }

        private static Single TomatoR(Int64 durSecs) => 2f + (Single)Math.Sqrt(Math.Min(Math.Max(durSecs, 60), 7200) / 7200.0) * 6f;

        private static UInt32 FlowColor(Int32 score)
        {
            // High score = ripe red (hue 0), low score = unripe green (hue 120)
            var hue = (1f - Math.Clamp(score / 100f, 0f, 1f)) * 120f;
            var c = SKColor.FromHsl(hue, 75f, 48f);
            return (UInt32)c;
        }
    }

    public class JarTomato
    {
        [JsonPropertyName("duration_secs")] public Int64 DurationSecs { get; set; }
        [JsonPropertyName("flow_score")] public Int32 FlowScore { get; set; }
        [JsonPropertyName("hour")] public Int32 Hour { get; set; }
        [JsonPropertyName("completed")] public Boolean Completed { get; set; }
        [JsonPropertyName("task_title")] public String TaskTitle { get; set; } = "";
        [JsonPropertyName("task_color")] public String TaskColor { get; set; } = "";
    }
}
