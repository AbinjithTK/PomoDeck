namespace Loupedeck.PomoDeckPlugin
{
    using System;
    using System.Diagnostics;
    using System.Threading;

    /// <summary>
    /// Drift-correcting Pomodoro timer engine.
    /// 
    /// Modeled after pomotroid's Rust engine: uses a dedicated thread with
    /// Stopwatch (monotonic clock) to schedule ticks against a fixed timeline
    /// rather than sleeping for a constant 1s. This eliminates cumulative
    /// drift from thread wakeup latency.
    /// 
    /// Thread-safe: all public methods can be called from any thread.
    /// State reads are lock-free via volatile fields.
    /// </summary>
    public sealed class PomodoroTimer : IDisposable
    {
        // ── Public types ──────────────────────────────────────────────────

        public enum TimerPhase { Work, ShortBreak, LongBreak }
        public enum TimerState { Stopped, Running, Paused }

        // ── Events (fired on the engine thread — subscribers must not block) ──

        /// <summary>Fires every second while running. Guaranteed monotonic.</summary>
        public event EventHandler Tick;

        /// <summary>Fires when a phase completes (work/break finished).</summary>
        public event EventHandler<TimerPhase> PhaseCompleted;

        /// <summary>Fires when the active phase changes.</summary>
        public event EventHandler<TimerPhase> PhaseChanged;

        /// <summary>Fires when the timer is reset.</summary>
        public event EventHandler TimerReset;

        // ── Volatile state (lock-free reads from any thread) ──────────────

        private volatile TimerState _state = TimerState.Stopped;
        private volatile TimerPhase _phase = TimerPhase.Work;
        private volatile Int32 _elapsedSecs;
        private volatile Int32 _totalSecs;
        private volatile Int32 _completedSessions;
        private volatile Int32 _totalCompletedPomodoros;
        private volatile Int32 _workRoundNumber = 1;

        // ── Public read-only properties ───────────────────────────────────

        public TimerState State => _state;
        public TimerPhase Phase => _phase;
        public Int32 ElapsedSecs => _elapsedSecs;
        public Int32 TotalSecs => _totalSecs;
        public Int32 CompletedSessions => _completedSessions;
        public Int32 TotalCompletedPomodoros => _totalCompletedPomodoros;
        public Int32 WorkRoundNumber => _workRoundNumber;

        /// <summary>Remaining time as TimeSpan (computed from elapsed/total).</summary>
        public TimeSpan Remaining
        {
            get
            {
                var remaining = _totalSecs - _elapsedSecs;
                return TimeSpan.FromSeconds(Math.Max(0, remaining));
            }
        }

        /// <summary>Progress 0.0–1.0.</summary>
        public Double Progress
        {
            get
            {
                var total = _totalSecs;
                return total > 0 ? Math.Clamp((Double)_elapsedSecs / total, 0.0, 1.0) : 0.0;
            }
        }

        // ── Settings ──────────────────────────────────────────────────────

        private readonly PomodoroSettings _settings;

        // ── Engine thread ─────────────────────────────────────────────────

        private readonly Thread _engineThread;
        private readonly ManualResetEventSlim _wakeSignal = new(false);
        private volatile EngineCommand _pendingCommand = EngineCommand.None;
        private volatile Boolean _shutdown;

        private enum EngineCommand { None, Start, Pause, Resume, Reset, Skip, Shutdown }

        // ── Constructor ───────────────────────────────────────────────────

        public PomodoroTimer(PomodoroSettings settings)
        {
            _settings = settings;
            _totalSecs = settings.WorkMinutes * 60;

            _engineThread = new Thread(EngineLoop)
            {
                Name = "PomoDeck-Timer",
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal
            };
            _engineThread.Start();
        }

        // ── Public commands (thread-safe, non-blocking) ───────────────────

        public void StartOrResume()
        {
            if (_state == TimerState.Running) return;
            SendCommand(_state == TimerState.Paused ? EngineCommand.Resume : EngineCommand.Start);
        }

        public void Pause()
        {
            if (_state != TimerState.Running) return;
            SendCommand(EngineCommand.Pause);
        }

        public void ToggleStartPause()
        {
            if (_state == TimerState.Running)
                Pause();
            else
                StartOrResume();
        }

        public void Reset()
        {
            SendCommand(EngineCommand.Reset);
        }

        public void Skip()
        {
            SendCommand(EngineCommand.Skip);
        }

        public void SwitchPhase(TimerPhase newPhase)
        {
            _phase = newPhase;
            _totalSecs = GetPhaseDurationSecs(newPhase);
            _elapsedSecs = 0;
            SendCommand(EngineCommand.Reset);
            PhaseChanged?.Invoke(this, newPhase);
        }

        /// <summary>Adjust the current phase duration by delta minutes (clamped 1–120).</summary>
        public void AdjustDuration(Int32 deltaMinutes)
        {
            if (_state != TimerState.Stopped) return;

            var currentMinutes = _totalSecs / 60;
            var newMinutes = Math.Clamp(currentMinutes + deltaMinutes, 1, 120);
            _totalSecs = newMinutes * 60;
            _elapsedSecs = 0;

            switch (_phase)
            {
                case TimerPhase.Work: _settings.WorkMinutes = newMinutes; break;
                case TimerPhase.ShortBreak: _settings.ShortBreakMinutes = newMinutes; break;
                case TimerPhase.LongBreak: _settings.LongBreakMinutes = newMinutes; break;
            }
        }

        /// <summary>Update total duration without resetting elapsed. For paused state changes.</summary>
        public void SetPhaseDuration(TimerPhase phase, Int32 minutes)
        {
            var secs = Math.Clamp(minutes, 1, 120) * 60;
            if (phase == _phase)
                _totalSecs = secs;
        }

        private void SendCommand(EngineCommand cmd)
        {
            _pendingCommand = cmd;
            _wakeSignal.Set();
        }

        // ── Engine loop (dedicated thread, drift-correcting) ──────────────

        private void EngineLoop()
        {
            var stopwatch = new Stopwatch();
            var ticksFired = 0;
            var elapsedAtSegmentStart = 0;

            while (!_shutdown)
            {
                switch (_state)
                {
                    case TimerState.Stopped:
                    case TimerState.Paused:
                        // Block until a command arrives
                        _wakeSignal.Wait();
                        _wakeSignal.Reset();
                        break;

                    case TimerState.Running:
                        // Drift-correcting wait: target the absolute instant of the next tick
                        var nextTickMs = (ticksFired + 1) * 1000L;
                        var elapsedMs = stopwatch.ElapsedMilliseconds;
                        var waitMs = (Int32)Math.Max(0, nextTickMs - elapsedMs);

                        if (_wakeSignal.Wait(waitMs))
                        {
                            // Command arrived — handle it below
                            _wakeSignal.Reset();
                        }
                        else
                        {
                            // Tick fired
                            ticksFired++;
                            _elapsedSecs = elapsedAtSegmentStart + ticksFired;

                            Tick?.Invoke(this, EventArgs.Empty);

                            if (_elapsedSecs >= _totalSecs)
                            {
                                // Phase complete
                                stopwatch.Stop();
                                _state = TimerState.Stopped;

                                var completedPhase = _phase;
                                if (completedPhase == TimerPhase.Work)
                                    Interlocked.Increment(ref _totalCompletedPomodoros);

                                PhaseCompleted?.Invoke(this, completedPhase);
                                AdvancePhase();
                                // Don't auto-start here — let the plugin handle the delay
                                continue;
                            }
                        }
                        break;
                }

                // Process pending command
                var cmd = _pendingCommand;
                _pendingCommand = EngineCommand.None;

                switch (cmd)
                {
                    case EngineCommand.Start:
                        _elapsedSecs = 0;
                        _totalSecs = GetPhaseDurationSecs(_phase);
                        ticksFired = 0;
                        elapsedAtSegmentStart = 0;
                        stopwatch.Restart();
                        _state = TimerState.Running;
                        break;

                    case EngineCommand.Resume:
                        elapsedAtSegmentStart = _elapsedSecs;
                        ticksFired = 0;
                        stopwatch.Restart();
                        _state = TimerState.Running;
                        break;

                    case EngineCommand.Pause:
                        stopwatch.Stop();
                        _state = TimerState.Paused;
                        break;

                    case EngineCommand.Reset:
                        stopwatch.Stop();
                        _elapsedSecs = 0;
                        ticksFired = 0;
                        _state = TimerState.Stopped;
                        _totalSecs = GetPhaseDurationSecs(_phase);
                        TimerReset?.Invoke(this, EventArgs.Empty);
                        break;

                    case EngineCommand.Skip:
                        stopwatch.Stop();
                        _state = TimerState.Stopped;
                        _elapsedSecs = 0;
                        var skippedPhase = _phase;
                        if (skippedPhase == TimerPhase.Work)
                            Interlocked.Increment(ref _totalCompletedPomodoros);
                        PhaseCompleted?.Invoke(this, skippedPhase);
                        AdvancePhase();
                        break;

                    case EngineCommand.Shutdown:
                        _shutdown = true;
                        break;
                }
            }
        }

        // ── Sequence logic (mirrors pomotroid's sequence.rs) ──────────────

        private void AdvancePhase()
        {
            var previousPhase = _phase;

            if (previousPhase == TimerPhase.Work)
            {
                _completedSessions++;
                if (_completedSessions >= _settings.SessionsBeforeLongBreak)
                {
                    _completedSessions = 0;
                    _workRoundNumber = 1;
                    _phase = TimerPhase.LongBreak;
                }
                else
                {
                    _phase = TimerPhase.ShortBreak;
                }
            }
            else
            {
                if (previousPhase == TimerPhase.ShortBreak)
                    _workRoundNumber++;
                else // LongBreak
                    _workRoundNumber = 1;

                _phase = TimerPhase.Work;
            }

            _totalSecs = GetPhaseDurationSecs(_phase);
            _elapsedSecs = 0;
            PhaseChanged?.Invoke(this, _phase);
        }

        private Int32 GetPhaseDurationSecs(TimerPhase phase) => phase switch
        {
            TimerPhase.Work => _settings.WorkMinutes * 60,
            TimerPhase.ShortBreak => _settings.ShortBreakMinutes * 60,
            TimerPhase.LongBreak => _settings.LongBreakMinutes * 60,
            _ => 25 * 60
        };

        // ── Display helpers ───────────────────────────────────────────────

        public String GetPhaseDisplayName() => _phase switch
        {
            TimerPhase.Work => "FOCUS",
            TimerPhase.ShortBreak => "BREAK",
            TimerPhase.LongBreak => "LONG BREAK",
            _ => "POMODORO"
        };

        public String GetPhaseEmoji() => _phase switch
        {
            TimerPhase.Work => "🍅",
            TimerPhase.ShortBreak => "☕",
            TimerPhase.LongBreak => "🌴",
            _ => "⏱"
        };

        /// <summary>Returns true if the next phase after the current work round is a long break.</summary>
        public Boolean IsLongBreakNext() =>
            _phase == TimerPhase.Work && (_completedSessions + 1) >= _settings.SessionsBeforeLongBreak;

        // ── Cleanup ───────────────────────────────────────────────────────

        public void Dispose()
        {
            _shutdown = true;
            _wakeSignal.Set();
            _engineThread.Join(2000);
            _wakeSignal.Dispose();
        }
    }
}
