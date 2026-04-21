namespace Loupedeck.PomoDeckPlugin
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json.Serialization;
    using System.Timers;
    using SkiaSharp;

    /// <summary>
    /// Task button — shows the active task. Press to cycle.
    /// Theme-aware background, consistent Segoe UI font, uppercase labels.
    /// </summary>
    public class TaskWidget : PluginDynamicCommand
    {
        private PomoDeckPlugin Pomo => this.Plugin as PomoDeckPlugin;
        private readonly Timer _pollTimer;
        private List<TaskItem> _activeTasks = new();
        private String _lastDisplay = "";
        private readonly DateTime _startTime = DateTime.UtcNow;

        public TaskWidget()
            : base("1. Active Task", "Press to cycle tasks. Double-press to add a new task. Requires PomoDeck app", "2. Tasks (App Required)")
        {
            this.IsWidget = true;

            // Poll every 10s — tasks are pushed by the app on change, this is just a fallback
            _pollTimer = new Timer(10000) { AutoReset = true };
            _pollTimer.Elapsed += (_, _) =>
            {
                try { RequestTasks(); }
                catch { }
            };
            _pollTimer.Start();
        }

        protected override Boolean OnLoad()
        {
            Pomo?.RegisterTaskListener(UpdateTasks);
            Pomo?.RegisterThemeListener(() => { RenderGate.Request("TaskWidget", () => { try { this.ActionImageChanged(); } catch { } }); });
            RequestTasks();

            // Scroll animation timer — only runs when there's a long title
            _scrollTimer = new System.Timers.Timer(200) { AutoReset = true };
            _scrollTimer.Elapsed += (_, _) =>
            {
                if (_needsScroll) RenderGate.Request("TaskWidget", () => { try { this.ActionImageChanged(); } catch { } });
            };
            return true;
        }

        protected override Boolean OnUnload()
        {
            _pollTimer?.Stop(); _pollTimer?.Dispose();
            _scrollTimer?.Stop(); _scrollTimer?.Dispose();
            return true;
        }

        private System.Timers.Timer _scrollTimer;
        private Boolean _needsScroll;
        private volatile Boolean _scrollActive;
        private DateTime _scrollStart = DateTime.UtcNow;

        private void RequestTasks()
        {
            var bridge = Pomo?.Bridge;
            if (bridge != null && bridge.IsConnected)
                bridge.Send("{\"type\":\"getTasks\"}");
        }

        private DateTime _lastTaskPress = DateTime.MinValue;
        private const Int32 DoubleTapMs = 400;
        private volatile Boolean _pendingCycle;
        private DateTime _lastCycleTime = DateTime.MinValue;

        protected override void RunCommand(String actionParameter)
        {
            var pomo = Pomo;
            if (pomo == null) return;

            // When app is connected — instant cycling, rapid second press opens add-task
            if (pomo.IsRemote)
            {
                var now = DateTime.UtcNow;
                if ((now - _lastCycleTime).TotalMilliseconds < DoubleTapMs)
                {
                    // Rapid second press — open add-task in app
                    _lastCycleTime = DateTime.MinValue;
                    pomo.Bridge.Send("{\"type\":\"openTasks\"}");
                    pomo.RaiseHaptic("phase_change");
                    SoundAlert.PlayTick();
                    return;
                }

                _lastCycleTime = now;

                if (_activeTasks.Count == 0)
                {
                    pomo.RaiseHaptic("sharp_collision");
                    RequestTasks();
                    return;
                }

                // Instant cycle
                var currentIdx = _activeTasks.FindIndex(t => t.IsActive);
                var nextIdx = (currentIdx + 1) % _activeTasks.Count;
                var nextTask = _activeTasks[nextIdx];
                pomo.Bridge.Send($"{{\"type\":\"setActiveTask\",\"id\":{nextTask.Id}}}");
                SoundAlert.PlayWinding();
                pomo.RaiseHaptic("sharp_collision");
                // Restart scroll so user can read the full task name
                _scrollActive = true;
                _scrollStart = DateTime.UtcNow;
                RenderGate.Request("TaskWidget", () => { try { this.ActionImageChanged(); } catch { } });
                return;
            }

            // Standalone — double-tap to launch app, single press = haptic only
            var now2 = DateTime.UtcNow;
            if (_pendingCycle && (now2 - _lastTaskPress).TotalMilliseconds < DoubleTapMs)
            {
                _pendingCycle = false;
                _lastTaskPress = DateTime.MinValue;
                PomodoroApplication.Launch();
                pomo.RaiseHaptic("phase_change");
                SoundAlert.PlayTick();
                return;
            }

            _lastTaskPress = now2;
            _pendingCycle = true;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                System.Threading.Thread.Sleep(DoubleTapMs);
                if (!_pendingCycle) return;
                _pendingCycle = false;
                pomo.RaiseHaptic("sharp_collision");
            });
        }

        protected override String GetCommandDisplayName(String actionParameter, PluginImageSize imageSize) => null;

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize)
        {
            var pomo = Pomo;
            var tc = ThemeHelper.Resolve(pomo);
            var size = ThemeHelper.RenderSize(imageSize);

            using var bitmap = new SKBitmap(size, size);
            using var canvas = new SKCanvas(bitmap);
            canvas.Scale((Single)size / 80f);
            canvas.Clear(tc.Bg);

            // Show "app required" when not connected
            if (pomo == null || !pomo.IsRemote)
            {
                using var lp = new SKPaint
                {
                    IsAntialias = true, Color = tc.Dim, TextSize = 9,
                    TextAlign = SKTextAlign.Center, Typeface = ThemeHelper.Typeface,
                    SubpixelText = true
                };
                canvas.DrawText("TASKS", 40, 32, lp);
                using var sp = new SKPaint
                {
                    IsAntialias = true, Color = tc.Track, TextSize = 8,
                    TextAlign = SKTextAlign.Center, Typeface = ThemeHelper.Typeface,
                    SubpixelText = true
                };
                canvas.DrawText("APP REQUIRED", 40, 48, sp);
                return Encode(bitmap);
            }

            if (_activeTasks.Count == 0)
            {
                using var p = new SKPaint
                {
                    IsAntialias = true, Color = tc.Text, TextSize = 10,
                    TextAlign = SKTextAlign.Center, Typeface = ThemeHelper.Typeface,
                    SubpixelText = true
                };
                canvas.DrawText("NO TASKS", 40, 42, p);
                return Encode(bitmap);
            }

            var active = _activeTasks.FirstOrDefault(t => t.IsActive) ?? _activeTasks[0];
            var idx = _activeTasks.IndexOf(active) + 1;

            // Task color tag bar (left side)
            var tagColor = tc.Phase;
            if (!String.IsNullOrEmpty(active.Color))
            {
                try { tagColor = SkiaSharp.SKColor.Parse(active.Color); } catch { }
            }
            using var accentPaint = new SKPaint { IsAntialias = true, Color = tagColor, Style = SKPaintStyle.Fill };
            canvas.DrawRoundRect(2, 8, 4, 64, 2, 2, accentPaint);

            // Task counter — uppercase
            using var numPaint = new SKPaint
            {
                IsAntialias = true, Color = tc.Dim, TextSize = 9,
                TextAlign = SKTextAlign.Left, Typeface = ThemeHelper.Typeface,
                SubpixelText = true
            };
            canvas.DrawText($"#{idx}/{_activeTasks.Count}", 12, 18, numPaint);

            // Title — scroll once on task change, then show static
            var fullTitle = active.Title;
            using var titlePaint = new SKPaint
            {
                IsAntialias = true, Color = tc.Text, TextSize = 13,
                TextAlign = SKTextAlign.Left, Typeface = ThemeHelper.Typeface,
                SubpixelText = true
            };
            var titleWidth = titlePaint.MeasureText(fullTitle);
            var maxWidth = 62f;

            if (titleWidth > maxWidth && _scrollActive)
            {
                _needsScroll = true;
                if (!_scrollTimer.Enabled) _scrollTimer.Start();

                var padText = "    ";
                var loopText = fullTitle + padText;
                var loopWidth = titlePaint.MeasureText(loopText);
                var elapsed = (DateTime.UtcNow - _scrollStart).TotalSeconds;
                var offset = (Single)(elapsed * 30.0);

                // Stop after one full scroll cycle
                if (offset >= loopWidth)
                {
                    _scrollActive = false;
                    _needsScroll = false;
                    _scrollTimer.Stop();
                    canvas.DrawText(fullTitle, 12, 38, titlePaint);
                }
                else
                {
                    canvas.Save();
                    canvas.ClipRect(new SKRect(12, 24, 76, 44));
                    canvas.DrawText(loopText + fullTitle, 12 - offset, 38, titlePaint);
                    canvas.Restore();
                }
            }
            else
            {
                _needsScroll = false;
                if (_scrollTimer.Enabled) _scrollTimer.Stop();
                // Truncate with ellipsis for static display
                canvas.Save();
                canvas.ClipRect(new SKRect(12, 24, 76, 44));
                canvas.DrawText(fullTitle, 12, 38, titlePaint);
                canvas.Restore();
            }

            // Pomodoro dots — phase color for completed, yellow for exceeded, track for remaining
            var estimated = active.EstimatedPomodoros;
            var completed = active.CompletedPomodoros;
            var exceeded = completed > estimated;
            var totalDots = Math.Min(Math.Max(estimated, completed), 10);
            var dotX = 12f;
            var dotGap = totalDots > 6 ? 7f : 8f;

            for (var i = 0; i < totalDots; i++)
            {
                SKColor color;
                if (i < Math.Min(completed, estimated))
                    color = tc.Phase;
                else if (i < completed)
                    color = new SKColor(255, 200, 50);
                else
                    color = tc.Track;

                using var dotPaint = new SKPaint { IsAntialias = true, Color = color, Style = SKPaintStyle.Fill };
                canvas.DrawCircle(dotX + i * dotGap, 54, 3, dotPaint);
            }

            // Progress text
            var progColor = exceeded ? new SKColor(255, 200, 50) : tc.Dim;
            var progText = exceeded ? $"{completed}/{estimated} +{completed - estimated}" : $"{completed}/{estimated}";
            using var progPaint = new SKPaint
            {
                IsAntialias = true, Color = progColor, TextSize = 9,
                TextAlign = SKTextAlign.Left, Typeface = ThemeHelper.Typeface,
                SubpixelText = true
            };
            canvas.DrawText(progText, 12, 70, progPaint);

            return Encode(bitmap);
        }

        internal void UpdateTasks(List<TaskItem> allTasks)
        {
            _activeTasks = allTasks?.Where(t => t.Status == "active").ToList() ?? new();

            // Auto-select the first task if none is marked active
            if (_activeTasks.Count > 0 && !_activeTasks.Any(t => t.IsActive))
            {
                var first = _activeTasks[0];
                var bridge = Pomo?.Bridge;
                if (bridge != null && bridge.IsConnected)
                    bridge.Send($"{{\"type\":\"setActiveTask\",\"id\":{first.Id}}}");
            }

            var display = BuildDisplay();
            if (display != _lastDisplay)
            {
                _lastDisplay = display;
                // Restart scroll animation for new active task
                _scrollActive = true;
                _scrollStart = DateTime.UtcNow;
                RenderGate.Request("TaskWidget", () => { try { this.ActionImageChanged(); } catch { } });
            }
        }

        private String BuildDisplay()
        {
            var active = _activeTasks.FirstOrDefault(t => t.IsActive);
            if (active == null) return $"count:{_activeTasks.Count}";
            return $"{active.Id}:{active.CompletedPomodoros}/{active.EstimatedPomodoros}:{_activeTasks.Count}";
        }

        private static BitmapImage Encode(SKBitmap bitmap)
        {
            using var img = SKImage.FromBitmap(bitmap);
            using var data = img.Encode(SKEncodedImageFormat.Jpeg, 85);
            return BitmapImage.FromArray(data.ToArray());
        }
    }

    public class TaskItem
    {
        [JsonPropertyName("id")] public Int64 Id { get; set; }
        [JsonPropertyName("title")] public String Title { get; set; } = "";
        [JsonPropertyName("estimated_pomodoros")] public Int32 EstimatedPomodoros { get; set; } = 1;
        [JsonPropertyName("completed_pomodoros")] public Int32 CompletedPomodoros { get; set; }
        [JsonPropertyName("elapsed_work_secs")] public Int64 ElapsedWorkSecs { get; set; }
        [JsonPropertyName("status")] public String Status { get; set; } = "active";
        [JsonPropertyName("is_active")] public Boolean IsActive { get; set; }
        [JsonPropertyName("color")] public String Color { get; set; } = "";
    }
}
