namespace Loupedeck.PomoDeckPlugin
{
    using System;

    public class PomoDeckDial : PluginDynamicAdjustment
    {
        private PomoDeckPlugin Pomo => this.Plugin as PomoDeckPlugin;
        private PomodoroTimer.TimerPhase _selectedPhase = PomodoroTimer.TimerPhase.Work;
        private Int32 _cycleCount;
        private DateTime _lastCycle = DateTime.MinValue;
        private Int32 _tickAccum;
        private const Int32 TicksPerStep = 5;

        public PomoDeckDial()
            : base("2. Adjust Time", "Pair with Adjust Time dial. Assign to a button — press to reset the timer back to the start of the current phase", "1. Timer", hasReset: true)
        {
            this.ResetDisplayName = "Reset Timer";
        }

        protected override Boolean OnLoad()
        {
            this.Description = "Pair with Reset Timer button. Assign to dial — turn to change duration, press to switch phase";
            Pomo?.RegisterThemeListener(() => this.AdjustmentValueChanged());
            return true;
        }

        protected override void ApplyAdjustment(String actionParameter, Int32 diff)
        {
            _tickAccum += diff;
            if (Math.Abs(_tickAccum) < TicksPerStep) return;
            var steps = _tickAccum / TicksPerStep;
            _tickAccum %= TicksPerStep;

            var pomo = Pomo;
            if (pomo == null) return;
            if (pomo.IsRunning() && _selectedPhase == pomo.GetPhase()) return;

            var mins = GetMins(pomo, _selectedPhase);
            var newMins = Math.Clamp(mins + steps, 1, 120);
            if (newMins == mins) return;

            SetMins(pomo, _selectedPhase, newMins);
            pomo.SaveSettings();

            if (pomo.IsRemote)
                pomo.Bridge.SendSetting(Key(_selectedPhase), (newMins * 60).ToString());

            if (!pomo.IsRemote && _selectedPhase == pomo.Timer.Phase)
            {
                pomo.SuppressHaptics = true;
                try
                {
                    if (pomo.Timer.State == PomodoroTimer.TimerState.Stopped)
                        pomo.Timer.SwitchPhase(_selectedPhase);
                    else if (pomo.Timer.State == PomodoroTimer.TimerState.Paused)
                    {
                        pomo.Timer.Reset();
                        pomo.Timer.SwitchPhase(_selectedPhase);
                    }
                }
                finally { pomo.SuppressHaptics = false; }
            }

            if (pomo.IsRemote && _selectedPhase == pomo.GetPhase() && pomo.IsPaused())
                pomo.Bridge.SendReset();

            SoundAlert.PlayWinding();
            pomo.RaiseHaptic("sharp_collision");
            pomo.OnSettingsChanged();
            this.AdjustmentValueChanged();
        }

        protected override void RunCommand(String actionParameter)
        {
            var pomo = Pomo;
            if (pomo == null) return;
            pomo.OnUserInteraction();

            var timerPhase = pomo.GetPhase();
            var isRunning = pomo.IsRunning();

            // Always cycle the dial selector — never skip the main timer phase
            var nextPhase = _selectedPhase switch
            {
                PomodoroTimer.TimerPhase.Work => PomodoroTimer.TimerPhase.ShortBreak,
                PomodoroTimer.TimerPhase.ShortBreak => PomodoroTimer.TimerPhase.LongBreak,
                _ => PomodoroTimer.TimerPhase.Work
            };

            if (isRunning && nextPhase == timerPhase)
            {
                nextPhase = nextPhase switch
                {
                    PomodoroTimer.TimerPhase.Work => PomodoroTimer.TimerPhase.ShortBreak,
                    PomodoroTimer.TimerPhase.ShortBreak => PomodoroTimer.TimerPhase.LongBreak,
                    _ => PomodoroTimer.TimerPhase.Work
                };
            }

            if ((DateTime.UtcNow - _lastCycle).TotalSeconds > 5)
                _cycleCount = 0;
            _lastCycle = DateTime.UtcNow;
            _cycleCount++;

            _selectedPhase = nextPhase;

            if (_cycleCount >= 3)
            {
                _cycleCount = 0;
                pomo.Settings.WorkMinutes = 25;
                pomo.Settings.ShortBreakMinutes = 5;
                pomo.Settings.LongBreakMinutes = 15;
                pomo.SaveSettings();
                _selectedPhase = PomodoroTimer.TimerPhase.Work;

                if (pomo.IsRemote)
                {
                    pomo.Bridge.SendSetting("time_work_secs", "1500");
                    pomo.Bridge.SendSetting("time_short_break_secs", "300");
                    pomo.Bridge.SendSetting("time_long_break_secs", "900");
                }
                if (!pomo.IsRemote && pomo.IsStopped())
                    pomo.Timer.SwitchPhase(PomodoroTimer.TimerPhase.Work);

                pomo.RaiseHaptic("timer_complete");
                pomo.OnSettingsChanged();
            }
            else
            {
                pomo.RaiseHaptic("phase_change");
            }

            this.AdjustmentValueChanged();
        }

        protected override String GetAdjustmentDisplayName(String actionParameter, PluginImageSize imageSize)
        {
            return _selectedPhase switch
            {
                PomodoroTimer.TimerPhase.ShortBreak => "BREAK",
                PomodoroTimer.TimerPhase.LongBreak => "LONG BREAK",
                _ => "FOCUS"
            };
        }

        protected override String GetAdjustmentValue(String actionParameter)
        {
            var pomo = Pomo;
            if (pomo == null) return "--";
            return $"{GetMins(pomo, _selectedPhase)} min";
        }

        private static Int32 GetMins(PomoDeckPlugin p, PomodoroTimer.TimerPhase ph) => ph switch
        {
            PomodoroTimer.TimerPhase.ShortBreak => p.Settings.ShortBreakMinutes,
            PomodoroTimer.TimerPhase.LongBreak => p.Settings.LongBreakMinutes,
            _ => p.Settings.WorkMinutes
        };

        private static String Key(PomodoroTimer.TimerPhase ph) => ph switch
        {
            PomodoroTimer.TimerPhase.ShortBreak => "time_short_break_secs",
            PomodoroTimer.TimerPhase.LongBreak => "time_long_break_secs",
            _ => "time_work_secs"
        };

        private static void SetMins(PomoDeckPlugin p, PomodoroTimer.TimerPhase ph, Int32 m)
        {
            switch (ph)
            {
                case PomodoroTimer.TimerPhase.ShortBreak: p.Settings.ShortBreakMinutes = m; break;
                case PomodoroTimer.TimerPhase.LongBreak: p.Settings.LongBreakMinutes = m; break;
                default: p.Settings.WorkMinutes = m; break;
            }
        }
    }
}
