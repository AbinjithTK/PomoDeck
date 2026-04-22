namespace Loupedeck.PomoDeckPlugin
{
    using System;

    public class PomoDeckPlugin : Plugin
    {
        public override Boolean HasNoApplication => true;
        public override Boolean UsesApplicationApiOnly => true;

        internal PomodoroTimer Timer { get; private set; }
        internal PomodoroSettings Settings { get; private set; }
        internal HapticAlarm Alarm { get; private set; }
        internal PomoDeckBridge Bridge { get; private set; }
        internal SkinEngine Skin { get; private set; }
        private System.Timers.Timer _gcTimer;

        internal Boolean IsRemote => Bridge?.IsConnected == true && Bridge.State != null;

        internal const String KeyWorkMinutes = "work_minutes";
        internal const String KeyShortBreakMinutes = "short_break_minutes";
        internal const String KeyLongBreakMinutes = "long_break_minutes";
        internal const String KeySessionsBeforeLong = "sessions_before_long";

        public PomoDeckPlugin()
        {
            PluginLog.Init(this.Log);
            PluginResources.Init(this.Assembly);
        }

        public override void Load()
        {
            PluginLog.Info("PomoDeck loading");

            this.PluginEvents.AddEvent("timer_complete", "Timer Complete", "Phase finished");
            this.PluginEvents.AddEvent("work_complete", "Work Complete", "Focus finished");
            this.PluginEvents.AddEvent("break_complete", "Break Complete", "Break finished");
            this.PluginEvents.AddEvent("long_break_complete", "Long Break Complete", "Long break finished");
            this.PluginEvents.AddEvent("phase_change", "Phase Change", "Phase changed");
            this.PluginEvents.AddEvent("work_start", "Work Started", "Focus begun");
            this.PluginEvents.AddEvent("break_start", "Break Started", "Break begun");
            this.PluginEvents.AddEvent("timer_paused", "Timer Paused", "Paused");
            this.PluginEvents.AddEvent("timer_resumed", "Timer Resumed", "Resumed");
            this.PluginEvents.AddEvent("cycle_complete", "Cycle Complete", "All rounds done");
            this.PluginEvents.AddEvent("sharp_collision", "Dial Click", "Tactile dial feedback");

            Settings = LoadSettings();
            Timer = new PomodoroTimer(Settings);
            Alarm = new HapticAlarm(RaiseHaptic);

            Timer.PhaseCompleted += OnPhaseCompleted;
            Timer.PhaseChanged += OnPhaseChanged;
            Timer.Tick += OnTimerTick;

            // Pre-extract tick sound so it's ready for ambient playback
            AmbientTick.EnsureReady();

            // Periodic GC — Gen2 every 2 minutes to reclaim SkiaSharp native memory.
            // Gen0 is handled per-frame in PomoDeckWidget. This catches all other widgets.
            _gcTimer = new System.Timers.Timer(120000) { AutoReset = true };
            _gcTimer.Elapsed += (_, _) => { try { GC.Collect(2, GCCollectionMode.Optimized, false, true); } catch { } };
            _gcTimer.Start();

            // Bridge connects on background thread — never blocks Load
            Bridge = new PomoDeckBridge();
            Bridge.ConnectionChanged += (connected) =>
            {
                PluginLog.Info(connected ? "Bridge: connected" : "Bridge: disconnected");
                if (connected)
                {
                    // If local timer is running, sync it TO the app instead of resetting
                    if (Timer.State == PomodoroTimer.TimerState.Running || Timer.State == PomodoroTimer.TimerState.Paused)
                    {
                        // Send current timer state to app so it picks up where we are
                        var phase = Timer.Phase switch
                        {
                            PomodoroTimer.TimerPhase.ShortBreak => "short-break",
                            PomodoroTimer.TimerPhase.LongBreak => "long-break",
                            _ => "work"
                        };
                        var remaining = (Int32)Math.Ceiling(Timer.Remaining.TotalSeconds);
                        var total = Timer.TotalSecs;
                        // Tell app to start this phase with the right duration
                        Bridge.SendSetting($"time_{phase.Replace("-", "_")}_secs", total.ToString());
                        if (Timer.State == PomodoroTimer.TimerState.Running)
                            Bridge.Send("{\"type\":\"toggle\"}"); // start the app timer
                        PluginLog.Info($"Bridge: synced local timer to app ({phase} {remaining}s remaining)");
                        Timer.Reset(); // now safe to hand off
                    }
                    // Delay redraw — let state/theme/flow responses arrive first
                    System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                    {
                        System.Threading.Thread.Sleep(200);
                        if (!_unloading) OnSettingsChanged();
                    });
                }
                else
                {
                    try { CleanupProxy(); } catch { }
                    // Delay redraw to let the bridge fully disconnect and state clear
                    System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                    {
                        System.Threading.Thread.Sleep(300);
                        if (!_unloading) OnSettingsChanged();
                    });
                }
            };
            Bridge.StateUpdated += () =>
            {
                var gen = System.Threading.Interlocked.Increment(ref _stateGen);
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    System.Threading.Thread.Sleep(100);
                    if (_stateGen != gen) return;
                    if (_unloading) return;
                    foreach (var listener in _settingsListeners)
                        try { listener(); } catch { }
                });
            };
            Bridge.TasksReceived += OnTasksReceived;
            Bridge.ThemeUpdated += OnThemeUpdated;
            Bridge.SkinChanged += OnSkinChanged;
            Bridge.FlowUpdated += OnFlowUpdated;
            Bridge.JarReceived += OnJarReceived;
            Bridge.SettingsSynced += OnSettingsSynced;

            // Skin engine — simple, no remote manifest needed
            Skin = new SkinEngine();
            Skin.SkinChanged += () =>
            {
                foreach (var listener in _themeListeners)
                    try { listener(); } catch { }
            };

            PluginLog.Info("PomoDeck loaded");
        }

        public override void Unload()
        {
            // Mark as unloading to prevent render calls during shutdown
            _unloading = true;

            if (Timer != null)
            {
                Timer.PhaseCompleted -= OnPhaseCompleted;
                Timer.PhaseChanged -= OnPhaseChanged;
            }
            _settingsListeners.Clear();
            _themeListeners.Clear();
            _taskListeners.Clear();
            _flowListeners.Clear();
            _jarListeners.Clear();

            SoundAlert.Shutdown();
            BitmapPool.Clear();
            _gcTimer?.Stop();
            _gcTimer?.Dispose();
            Bridge?.Dispose();
            Alarm?.Dispose();
            Timer?.Dispose();
            PluginLog.Info("PomoDeck unloaded");
        }

        internal volatile Boolean _unloading;

        // ── Unified API (local or remote) ─────────────────────────────────

        internal void ToggleTimer()
        {
            // If bridge is connected, always send to app — even if state hasn't arrived yet
            if (Bridge?.IsConnected == true)
            {
                PluginLog.Info("Toggle via bridge");
                Bridge.SendToggle();
            }
            else
            {
                PluginLog.Info("Toggle local timer");
                Timer.ToggleStartPause();
            }
        }

        internal void SkipPhase()
        {
            if (Bridge?.IsConnected == true)
            {
                PluginLog.Info("Skip via bridge");
                Bridge.SendSkip();
            }
            else
            {
                _skipInProgress = true;
                Timer.Skip();
            }
        }

        internal void CompleteActiveTask()
        {
            if (IsRemote && Bridge != null && Bridge.IsConnected)
            {
                // Find active task from the last received task list and complete it
                var tasks = _lastTasks;
                if (tasks != null)
                {
                    var active = tasks.Find(t => t.IsActive && t.Status == "active");
                    if (active != null)
                    {
                        Bridge.Send($"{{\"type\":\"completeTask\",\"id\":{active.Id}}}");
                        PluginLog.Info($"Completed task: {active.Title}");
                    }
                }
            }
        }

        private System.Collections.Generic.List<TaskItem> _lastTasks;

        internal Int32 GetRemainingSecs()
        {
            if (_unloading) return 0;
            if (IsRemote) { var s = Bridge.State; return s?.RemainingSecs ?? 0; }
            return (Int32)Math.Ceiling(Timer.Remaining.TotalSeconds);
        }

        internal Double GetProgress()
        {
            if (_unloading) return 0.0;
            if (IsRemote) { var s = Bridge.State; return s?.Progress ?? 0.0; }
            return Timer.Progress;
        }

        internal String GetPhaseDisplay()
        {
            if (_unloading) return "FOCUS";
            if (IsRemote)
            {
                var s = Bridge.State;
                return s?.RoundType switch
                {
                    "work" => "FOCUS",
                    "short-break" => "BREAK",
                    "long-break" => "LONG BREAK",
                    _ => "FOCUS"
                };
            }
            return Timer.GetPhaseDisplayName();
        }

        internal PomodoroTimer.TimerPhase GetPhase()
        {
            if (IsRemote)
            {
                var s = Bridge.State;
                if (s == null) return PomodoroTimer.TimerPhase.Work;
                return s.RoundType switch
                {
                    "short-break" => PomodoroTimer.TimerPhase.ShortBreak,
                    "long-break" => PomodoroTimer.TimerPhase.LongBreak,
                    _ => PomodoroTimer.TimerPhase.Work
                };
            }
            return Timer.Phase;
        }

        internal new Boolean IsRunning()
        {
            if (IsRemote) { var s = Bridge.State; return s?.IsRunning == true; }
            return Timer.State == PomodoroTimer.TimerState.Running;
        }

        internal Boolean IsPaused()
        {
            if (IsRemote) { var s = Bridge.State; return s?.IsPaused == true; }
            return Timer.State == PomodoroTimer.TimerState.Paused;
        }

        internal Boolean IsStopped()
        {
            if (IsRemote) { var s = Bridge.State; return s == null || (!s.IsRunning && !s.IsPaused); }
            return Timer.State == PomodoroTimer.TimerState.Stopped;
        }

        internal Int32 GetCompletedSessions()
        {
            if (IsRemote) { var s = Bridge.State; return s != null ? Math.Max(0, s.WorkRoundNumber - 1) : 0; }
            return Timer.CompletedSessions;
        }

        // ── Haptics ───────────────────────────────────────────────────────

        private volatile Boolean _skipInProgress;
        private DateTime _ringUntil = DateTime.MinValue;
        private DateTime _lastRing = DateTime.MinValue;

        private void OnPhaseCompleted(Object sender, PomodoroTimer.TimerPhase phase)
        {
            if (IsRemote) return;

            if (_skipInProgress)
            {
                _skipInProgress = false;
                RaiseHaptic("phase_change");
                return;
            }

            // Debounce: skip if a ring played within the last 2 seconds
            var now = DateTime.UtcNow;
            if ((now - _lastRing).TotalSeconds < 2) return;
            _lastRing = now;

            _ringUntil = now.AddSeconds(3);

            if (phase == PomodoroTimer.TimerPhase.Work
                && Timer.Phase == PomodoroTimer.TimerPhase.LongBreak)
            {
                Alarm.Ring("cycle_complete");
                SoundAlert.PlayPhaseComplete();
                AutoStartAfterRing();
                return;
            }

            var evt = phase switch
            {
                PomodoroTimer.TimerPhase.Work => "work_complete",
                PomodoroTimer.TimerPhase.ShortBreak => "break_complete",
                PomodoroTimer.TimerPhase.LongBreak => "long_break_complete",
                _ => "timer_complete"
            };
            Alarm.Ring(evt);
            SoundAlert.PlayPhaseComplete();
            AutoStartAfterRing();
        }

        /// <summary>Auto-start next phase after ring sound finishes.</summary>
        private void AutoStartAfterRing()
        {
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                System.Threading.Thread.Sleep(3000); // wait for ring to finish
                if (IsRemote) return; // app took over
                if (Timer.State != PomodoroTimer.TimerState.Stopped) return; // user already started/paused
                Timer.StartOrResume();
            });
        }

        private void OnPhaseChanged(Object sender, PomodoroTimer.TimerPhase newPhase)
        {
            if (IsRemote) return;
            RaiseHaptic("phase_change");
            RaiseHaptic(newPhase == PomodoroTimer.TimerPhase.Work ? "work_start" : "break_start");
        }

        internal void OnUserInteraction() { if (Alarm.IsRinging) Alarm.Stop(); }

        /// <summary>Whether the tick sound is enabled (controlled by AmbientSoundCommand).</summary>
        internal volatile Boolean TickEnabled = true;

        private void OnTimerTick(Object sender, EventArgs e)
        {
            if (!TickEnabled) return;
            if (IsRemote) return;
            if (DateTime.UtcNow < _ringUntil) return; // ring sound still playing
            SoundAlert.PlayTick();
        }

        /// <summary>Suppress haptics during dial adjustments to prevent cascading.</summary>
        internal Boolean SuppressHaptics { get; set; }

        /// <summary>Called when settings change — forces widget redraw.</summary>
        internal void OnSettingsChanged()
        {
            if (_unloading) return;
            foreach (var listener in _settingsListeners)
                try { listener(); } catch { }
        }

        private readonly System.Collections.Generic.List<Action> _settingsListeners = new();
        private volatile Int32 _stateGen;
        internal void RegisterSettingsListener(Action listener) => _settingsListeners.Add(listener);

        // Task routing
        private readonly System.Collections.Generic.List<Action<System.Collections.Generic.List<TaskItem>>> _taskListeners = new();

        internal void RegisterTaskListener(Action<System.Collections.Generic.List<TaskItem>> listener) => _taskListeners.Add(listener);

        private void OnTasksReceived(System.Collections.Generic.List<TaskItem> tasks)
        {
            _lastTasks = tasks;
            foreach (var listener in _taskListeners)
                try { listener(tasks); } catch { }
        }

        // Theme routing
        private readonly System.Collections.Generic.List<Action> _themeListeners = new();
        private volatile Int32 _themeGen;
        internal void RegisterThemeListener(Action listener) => _themeListeners.Add(listener);

        private void OnThemeUpdated()
        {
            try
            {
                var theme = Bridge?.Theme;
                if (theme != null)
                {
                    var bg = theme.GetColor("--color-background", new SkiaSharp.SKColor(26, 29, 35));
                    var text = theme.GetColor("--color-foreground", new SkiaSharp.SKColor(232, 234, 237));
                    var lum = (bg.Red * 0.299 + bg.Green * 0.587 + bg.Blue * 0.114) / 255.0;
                    if (lum > 0.5)
                        bg = theme.GetColor("--color-background-lightest", new SkiaSharp.SKColor(209, 209, 214));
                    this.Info.BackgroundColor = ToArgb(bg);
                    this.Info.TextColor = ToArgb(text);
                }
                else
                {
                    this.Info.BackgroundColor = ToArgb(new SkiaSharp.SKColor(26, 29, 35));
                    this.Info.TextColor = ToArgb(new SkiaSharp.SKColor(232, 234, 237));
                }
            }
            catch { }

            // Coalesce rapid theme changes — wait 50ms, skip if newer arrived
            var gen = System.Threading.Interlocked.Increment(ref _themeGen);
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                System.Threading.Thread.Sleep(50);
                if (_themeGen != gen) return;
                foreach (var listener in _themeListeners)
                    try { listener(); } catch { }
            });
        }

        private static UInt32 ToArgb(SkiaSharp.SKColor c) =>
            (UInt32)((c.Alpha << 24) | (c.Red << 16) | (c.Green << 8) | c.Blue);

        private void OnSkinChanged(String skinId)
        {
            Skin?.SetActive(skinId);
        }

        // Flow state routing
        private readonly System.Collections.Generic.List<Action<RemoteFlowState>> _flowListeners = new();
        internal void RegisterFlowListener(Action<RemoteFlowState> listener) => _flowListeners.Add(listener);

        /// <summary>Current flow state from the app (null if not connected or blocking disabled).</summary>
        internal RemoteFlowState FlowState => Bridge?.Flow;

        private Int32 _prevFlowScore;

        private void OnFlowUpdated(RemoteFlowState flow)
        {
            if (flow != null)
            {
                var delta = flow.Score - _prevFlowScore;
                // Subtle haptic on score penalty (drop of 5+)
                if (delta <= -5 && flow.InFocus)
                {
                    RaiseHaptic("timer_paused"); // gentle nudge
                }
                _prevFlowScore = flow.Score;
            }

            foreach (var listener in _flowListeners)
                try { listener(flow); } catch { }
        }

        // Jar routing
        private readonly System.Collections.Generic.List<Action<System.Collections.Generic.List<JarTomato>>> _jarListeners = new();
        internal void RegisterJarListener(Action<System.Collections.Generic.List<JarTomato>> listener) => _jarListeners.Add(listener);

        private void OnJarReceived(System.Collections.Generic.List<JarTomato> tomatoes)
        {
            foreach (var listener in _jarListeners)
                try { listener(tomatoes); } catch { }
        }

        private void OnSettingsSynced(String key, Boolean value)
        {
            if (key == "tick_sounds_work" || key == "tick_sounds_break")
            {
                // Sync plugin tick state from app — use work setting as the master
                if (key == "tick_sounds_work")
                {
                    TickEnabled = value;
                    PluginLog.Info($"[settings] tick synced from app: {value}");
                    OnSettingsChanged();
                }
            }
        }

        internal void RaiseHaptic(String eventName)
        {
            if (SuppressHaptics) return;
            try { this.PluginEvents.RaiseEvent(eventName); }
            catch (Exception ex) { PluginLog.Warning(ex, $"Haptic: {eventName}"); }
        }

        /// <summary>Remove PAC proxy from registry if the app left it behind.</summary>
        private static void CleanupProxy()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Internet Settings",
                    writable: true);
                if (key == null) return;
                var val = key.GetValue("AutoConfigURL") as String;
                if (val != null && val.Contains("127.0.0.1") && val.Contains("9998"))
                {
                    key.DeleteValue("AutoConfigURL", throwOnMissingValue: false);
                    PluginLog.Info("[proxy] Cleaned up stale PAC proxy");
                }
            }
            catch { }
        }

        internal void SaveSettings()
        {
            this.SetPluginSetting(KeyWorkMinutes, Settings.WorkMinutes.ToString(), false);
            this.SetPluginSetting(KeyShortBreakMinutes, Settings.ShortBreakMinutes.ToString(), false);
            this.SetPluginSetting(KeyLongBreakMinutes, Settings.LongBreakMinutes.ToString(), false);
            this.SetPluginSetting(KeySessionsBeforeLong, Settings.SessionsBeforeLongBreak.ToString(), false);
        }

        private PomodoroSettings LoadSettings()
        {
            var s = new PomodoroSettings();
            if (this.TryGetPluginSetting(KeyWorkMinutes, out var wm) && Int32.TryParse(wm, out var wmVal)) s.WorkMinutes = wmVal;
            if (this.TryGetPluginSetting(KeyShortBreakMinutes, out var sb) && Int32.TryParse(sb, out var sbVal)) s.ShortBreakMinutes = sbVal;
            if (this.TryGetPluginSetting(KeyLongBreakMinutes, out var lb) && Int32.TryParse(lb, out var lbVal)) s.LongBreakMinutes = lbVal;
            if (this.TryGetPluginSetting(KeySessionsBeforeLong, out var sl) && Int32.TryParse(sl, out var slVal)) s.SessionsBeforeLongBreak = slVal;
            return s;
        }
    }
}
