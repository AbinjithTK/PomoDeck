namespace Loupedeck.PomoDeckPlugin
{
    using System;
    using System.Diagnostics;

    /// <summary>
    /// Shared press animation state for action buttons.
    /// On press: icon scales down to 80%, then bounces back with ease-out.
    /// Total duration ~400ms. Call Kick() on press, query Scale/Alpha each frame.
    /// </summary>
    internal sealed class PressAnimation
    {
        private readonly Stopwatch _clock = new();
        private readonly System.Timers.Timer _frameTimer;
        private readonly Action _invalidate;
        private volatile Boolean _active;

        private const Int32 DownMs = 80;
        private const Int32 UpMs = 320;
        private const Int32 TotalMs = DownMs + UpMs;

        private const Single MinScale = 0.80f;
        private const Single MaxBright = 1.4f;

        public Boolean IsActive => _active;

        /// <summary>Current scale factor (1.0 = normal, 0.8 = pressed).</summary>
        public Single Scale
        {
            get
            {
                if (!_active) return 1f;
                var t = (Single)_clock.ElapsedMilliseconds;
                if (t < DownMs)
                {
                    var p = t / DownMs;
                    return 1f - (1f - MinScale) * EaseOutCubic(p);
                }
                var up = (t - DownMs) / (Single)UpMs;
                if (up >= 1f) return 1f;
                return MinScale + (1f - MinScale) * EaseOutBack(up);
            }
        }

        /// <summary>Brightness multiplier (1.0 = normal, peaks at press).</summary>
        public Single Brightness
        {
            get
            {
                if (!_active) return 1f;
                var t = (Single)_clock.ElapsedMilliseconds;
                if (t < DownMs)
                {
                    var p = t / DownMs;
                    return 1f + (MaxBright - 1f) * EaseOutCubic(p);
                }
                var up = (t - DownMs) / (Single)UpMs;
                if (up >= 1f) return 1f;
                return MaxBright - (MaxBright - 1f) * EaseOutCubic(up);
            }
        }

        public PressAnimation(Action invalidate)
        {
            _invalidate = invalidate;
            _frameTimer = new System.Timers.Timer(33) { AutoReset = true };
            _frameTimer.Elapsed += (_, _) =>
            {
                try
                {
                    if (!_active || _clock.ElapsedMilliseconds >= TotalMs)
                    {
                        _active = false;
                        _frameTimer.Stop();
                        _clock.Stop();
                        _invalidate?.Invoke();
                        return;
                    }
                    _invalidate?.Invoke();
                }
                catch { }
            };
        }

        public void Kick()
        {
            _active = true;
            _clock.Restart();
            _frameTimer.Start();
        }

        private static Single EaseOutCubic(Single t) { var f = 1f - t; return 1f - f * f * f; }
        private static Single EaseOutBack(Single t) { const Single c = 1.70158f; return 1f + (c + 1f) * Pow3(t - 1f) + c * Pow2(t - 1f); }
        private static Single Pow2(Single x) => x * x;
        private static Single Pow3(Single x) => x * x * x;
    }
}
