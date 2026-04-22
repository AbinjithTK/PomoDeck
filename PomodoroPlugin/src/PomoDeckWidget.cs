namespace Loupedeck.PomoDeckPlugin
{
    using System;
    using System.Threading;

    public class PomoDeckWidget : PluginDynamicCommand
    {
        private PomoDeckPlugin Pomo => this.Plugin as PomoDeckPlugin;
        private BitmapImage _cachedImage;
        private Int32 _lastSec = -1;
        private Boolean _isLiquid;
        private volatile Boolean _shutdown;

        // Dedicated render thread — immune to ThreadPool starvation and GC pauses
        // on worker threads. Uses Thread.Sleep for timing (not Timer.Elapsed).
        private Thread _renderThread;

        private const Int32 ClassicMs = 1000;
        private const Int32 LiquidMs = 200;
        private const Int32 IdleMs = 3000;

        public PomoDeckWidget()
            : base("1. Focus Timer", "Your main timer. Tap to start or pause. Double-tap to switch style. Shows countdown, phase, and progress", "1. Timer")
        {
            this.IsWidget = true;
        }

        protected override Boolean OnLoad()
        {
            _shutdown = false;
            _renderThread = new Thread(RenderLoop)
            {
                Name = "PomoDeck-Render",
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal
            };
            _renderThread.Start();

            Pomo?.RegisterSettingsListener(() =>
            {
                _cachedImage = null;
                _lastSec = -1;
                RenderGate.Request("PomoDeckWidget", () => { try { this.ActionImageChanged(); } catch { } });
            });
            Pomo?.RegisterThemeListener(() =>
            {
                _cachedImage = null;
                _lastSec = -1;
                if (!_isLiquid) RenderGate.Request("PomoDeckWidget", () => { try { this.ActionImageChanged(); } catch { } });
            });
            return true;
        }

        protected override Boolean OnUnload()
        {
            _shutdown = true;
            _renderThread?.Join(2000);
            return true;
        }

        private void RenderLoop()
        {
            while (!_shutdown)
            {
                try
                {
                    var pomo = Pomo;
                    if (pomo == null || pomo._unloading || _shutdown)
                    {
                        Thread.Sleep(500);
                        continue;
                    }

                    // Determine current mode and sleep interval
                    _isLiquid = (pomo.Skin?.ActiveTimerWidget ?? "classic") == "liquid";
                    var running = pomo.IsRunning();
                    var paused = pomo.IsPaused();

                    Int32 sleepMs;
                    if (running)
                        sleepMs = _isLiquid ? LiquidMs : ClassicMs;
                    else if (paused && _isLiquid)
                        sleepMs = IdleMs;
                    else if (!running && !paused)
                        sleepMs = IdleMs;
                    else
                        sleepMs = ClassicMs;

                    // Check if visual state changed
                    var sec = pomo.GetRemainingSecs();
                    var needsRedraw = false;

                    if (_isLiquid && (running || paused))
                    {
                        needsRedraw = true;
                    }
                    else if (sec != _lastSec)
                    {
                        _lastSec = sec;
                        needsRedraw = true;
                    }

                    if (needsRedraw)
                    {
                        _cachedImage = null;
                        RenderGate.Request("PomoDeckWidget", () => { try { this.ActionImageChanged(); } catch { } });

                        // Mini GC after render — frees the previous frame's SKBitmap/SKCanvas
                        // native wrappers immediately instead of letting them accumulate.
                        // Gen0 only — takes <1ms, prevents finalizer queue buildup.
                        GC.Collect(0, GCCollectionMode.Optimized, false, false);
                    }

                    Thread.Sleep(sleepMs);
                }
                catch
                {
                    if (!_shutdown) Thread.Sleep(1000);
                }
            }
        }

        private DateTime _lastPress = DateTime.MinValue;
        private const Int32 DoubleTapMs = 350;
        private volatile Boolean _pendingToggle;

        protected override void RunCommand(String actionParameter)
        {
            var pomo = Pomo;
            if (pomo == null) return;
            pomo.OnUserInteraction();

            var now = DateTime.UtcNow;
            if (_pendingToggle && (now - _lastPress).TotalMilliseconds < DoubleTapMs)
            {
                _pendingToggle = false;
                _lastPress = DateTime.MinValue;
                pomo.Skin?.CycleNext();
                pomo.RaiseHaptic("phase_change");
                _cachedImage = null;
                try { this.ActionImageChanged(); } catch { }
                return;
            }

            _lastPress = now;
            _pendingToggle = true;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Thread.Sleep(DoubleTapMs);
                if (!_pendingToggle) return;
                _pendingToggle = false;

                var p = Pomo;
                if (p == null) return;
                SoundAlert.StopAll();
                p.ToggleTimer();
                p.RaiseHaptic(p.IsRunning() ? "timer_resumed" : "timer_paused");
                _cachedImage = null;
                _lastSec = -1;
                try { this.ActionImageChanged(); } catch { }
            });
        }

        protected override String GetCommandDisplayName(String actionParameter, PluginImageSize imageSize) => null;

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize)
        {
            try
            {
                if (_cachedImage != null && !_isLiquid) return _cachedImage;

                var pomo = Pomo;
                if (pomo == null)
                {
                    using var b = new BitmapBuilder(imageSize);
                    b.Clear(new BitmapColor(26, 29, 35));
                    b.DrawText("PomoDeck", 0, 30, b.Width, 20, new BitmapColor(140, 140, 140), 11);
                    return b.ToImage();
                }

                var skinType = pomo.Skin?.ActiveTimerWidget ?? "classic";
                var img = skinType switch
                {
                    "liquid" => LiquidRenderer.Render(pomo, imageSize),
                    _ => DialRenderer.RenderUnified(pomo, imageSize)
                };

                if (!_isLiquid) _cachedImage = img;
                return img;
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "PomoDeckWidget render failed");
                using var b = new BitmapBuilder(imageSize);
                b.Clear(new BitmapColor(26, 29, 35));
                b.DrawText("Error", 0, 30, b.Width, 20, new BitmapColor(230, 55, 45), 11);
                return b.ToImage();
            }
        }
    }
}
