namespace Loupedeck.PomoDeckPlugin
{
    using System;
    using System.Timers;

    public class PomoDeckWidget : PluginDynamicCommand
    {
        private PomoDeckPlugin Pomo => this.Plugin as PomoDeckPlugin;
        private readonly Timer _renderTimer;
        private BitmapImage _cachedImage;
        private Int32 _lastSec = -1;
        private Boolean _isLiquid;

        private const Double ClassicMs = 1000;
        private const Double LiquidMs = 200;
        private const Double IdleMs = 3000;     // 0.33fps when stopped — saves USB bandwidth

        public PomoDeckWidget()
            : base("1. Focus Timer", "Your main timer. Tap to start or pause. Double-tap to switch style. Shows countdown, phase, and progress", "1. Timer")
        {
            this.IsWidget = true;
            _renderTimer = new Timer(ClassicMs) { AutoReset = true };
            _renderTimer.Elapsed += OnTick;
            _renderTimer.Start();
        }

        protected override Boolean OnLoad()
        {
            // Listen for significant state changes (started/paused/resumed/reset/roundChange)
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
            _renderTimer?.Stop(); _renderTimer?.Dispose();
            return true;
        }

        private void OnTick(Object sender, ElapsedEventArgs e)
        {
            try
            {
                var pomo = Pomo;
                if (pomo == null || pomo._unloading) return;

                SyncInterval();

                var sec = pomo.GetRemainingSecs();
                var running = pomo.IsRunning();
                var paused = pomo.IsPaused();

                // Only redraw when something visually changed
                if (_isLiquid && (running || paused))
                {
                    _cachedImage = null;
                    RenderGate.Request("PomoDeckWidget", () => { try { this.ActionImageChanged(); } catch { } });
                }
                else if (sec != _lastSec)
                {
                    _lastSec = sec;
                    _cachedImage = null;
                    RenderGate.Request("PomoDeckWidget", () => { try { this.ActionImageChanged(); } catch { } });
                }
            }
            catch { }
        }

        private void SyncInterval()
        {
            var pomo = Pomo;
            _isLiquid = (pomo?.Skin?.ActiveTimerWidget ?? "classic") == "liquid";
            var running = pomo?.IsRunning() ?? false;
            var paused = pomo?.IsPaused() ?? false;

            Double target;
            if (running)
                target = _isLiquid ? LiquidMs : ClassicMs;
            else if (paused && _isLiquid)
                target = IdleMs; // paused liquid doesn't animate
            else if (!running && !paused)
                target = IdleMs; // fully stopped
            else
                target = ClassicMs; // paused classic — 1fps for display

            if (Math.Abs(_renderTimer.Interval - target) > 1)
                _renderTimer.Interval = target;
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
                // Double-tap: cancel pending toggle, switch theme instead
                _pendingToggle = false;
                _lastPress = DateTime.MinValue;
                pomo.Skin?.CycleNext();
                pomo.RaiseHaptic("phase_change");
                _cachedImage = null;
                SyncInterval();
                try { this.ActionImageChanged(); } catch { }
                return;
            }

            // Schedule a delayed toggle — cancelled if double-tap arrives
            _lastPress = now;
            _pendingToggle = true;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                System.Threading.Thread.Sleep(DoubleTapMs);
                if (!_pendingToggle) return; // was cancelled by double-tap
                _pendingToggle = false;

                var p = Pomo;
                if (p == null) return;
                SoundAlert.StopAll(); // instant silence before toggle
                p.ToggleTimer();
                p.RaiseHaptic(p.IsRunning() ? "timer_resumed" : "timer_paused");
                _cachedImage = null;
                _lastSec = -1;
                SyncInterval();
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
