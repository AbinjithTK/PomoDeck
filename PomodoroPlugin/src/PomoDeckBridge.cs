namespace Loupedeck.PomoDeckPlugin
{
    using System;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading;
    using SkiaSharp;
    using WebSocketSharp;

    /// <summary>
    /// Background-thread WebSocket bridge to PomoDeck desktop.
    /// Connection runs on a dedicated thread — never blocks the plugin.
    /// </summary>
    public sealed class PomoDeckBridge : IDisposable
    {
        private readonly Thread _thread;
        private volatile Boolean _disposed;
        private volatile Boolean _connected;
        private volatile Boolean _connectCooldown;
        private WebSocket _ws;

        private const String Url = "ws://127.0.0.1:1314/ws";

        private readonly Object _lock = new();
        private RemoteTimerState _state;
        private RemoteTheme _theme;
        private RemoteFlowState _flow;

        public Boolean IsConnected => _connected;
        public RemoteTimerState State { get { lock (_lock) return _state; } }
        public RemoteTheme Theme { get { lock (_lock) return _theme; } }
        public RemoteFlowState Flow { get { lock (_lock) return _flow; } }

        public event Action StateUpdated;
        public event Action<Boolean> ConnectionChanged;
        public event Action ThemeUpdated;
        public event Action<System.Collections.Generic.List<TaskItem>> TasksReceived;
        public event Action<String> SkinChanged;
        public event Action<RemoteFlowState> FlowUpdated;
        public event Action<System.Collections.Generic.List<JarTomato>> JarReceived;
        public event Action<String, Boolean> SettingsSynced;

        public PomoDeckBridge()
        {
            _thread = new Thread(RunLoop) { IsBackground = true, Name = "PomoDeck-Bridge" };
            _thread.Start();
        }

        private void RunLoop()
        {
            // Wait for plugin to finish loading
            Thread.Sleep(3000);

            while (!_disposed)
            {
                try
                {
                    using var ws = new WebSocket(Url);
                    _ws = ws;

                    var connected = new ManualResetEventSlim(false);
                    var closed = new ManualResetEventSlim(false);

                    ws.OnOpen += (_, _) =>
                    {
                        _connected = true;
                        _connectCooldown = true;
                        PluginLog.Info("[bridge] Connected");
                        connected.Set();
                        ConnectionChanged?.Invoke(true);
                        ws.Send("{\"type\":\"getState\"}");
                        ws.Send("{\"type\":\"getTheme\"}");
                        ws.Send("{\"type\":\"getFlow\"}");
                        // Clear cooldown after initial responses arrive
                        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                        {
                            System.Threading.Thread.Sleep(500);
                            _connectCooldown = false;
                        });
                    };

                    ws.OnMessage += (_, e) =>
                    {
                        if (e.IsText) HandleMessage(e.Data);
                    };

                    ws.OnClose += (_, _) =>
                    {
                        _connected = false;
                        lock (_lock) { _state = null; _theme = null; _flow = null; }
                        closed.Set();
                        ConnectionChanged?.Invoke(false);
                        ThemeUpdated?.Invoke(); // redraw all actions with default theme
                    };

                    ws.OnError += (_, _) =>
                    {
                        _connected = false;
                        lock (_lock) { _state = null; _theme = null; _flow = null; }
                        closed.Set();
                    };

                    ws.Connect();

                    if (_connected)
                    {
                        // Stay here until disconnected
                        closed.Wait();
                        PluginLog.Info("[bridge] Disconnected");
                    }

                    _ws = null;
                }
                catch (Exception ex)
                {
                    PluginLog.Info($"[bridge] Error: {ex.Message}");
                }

                _connected = false;

                // Wait before retry
                for (var i = 0; i < 50 && !_disposed; i++)
                    Thread.Sleep(100); // 5 seconds total, but checks _disposed every 100ms
            }
        }

        private void HandleMessage(String data)
        {
            try
            {
                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;
                var type = root.GetProperty("type").GetString();

                switch (type)
                {
                    case "state":
                        // Tick updates: just store state silently — PomoDeckWidget
                        // has its own render timer that reads Bridge.State directly.
                        // Don't fire StateUpdated to avoid flooding other widgets.
                        if (root.TryGetProperty("payload", out var sp))
                        {
                            var s = JsonSerializer.Deserialize<RemoteTimerState>(sp.GetRawText());
                            lock (_lock) _state = s;
                        }
                        break;

                    case "roundChange":
                        // Significant event — notify all widgets
                        if (root.TryGetProperty("payload", out var p))
                        {
                            var s = JsonSerializer.Deserialize<RemoteTimerState>(p.GetRawText());
                            lock (_lock) _state = s;
                            StateUpdated?.Invoke();
                        }
                        break;

                    case "started":
                        lock (_lock) { if (_state != null) { _state.IsRunning = true; _state.IsPaused = false; } }
                        StateUpdated?.Invoke();
                        break;

                    case "paused":
                        if (root.TryGetProperty("payload", out var pp))
                        {
                            var el = pp.GetProperty("elapsed_secs").GetInt32();
                            lock (_lock) { if (_state != null) { _state.ElapsedSecs = el; _state.IsRunning = false; _state.IsPaused = true; } }
                        }
                        StateUpdated?.Invoke();
                        break;

                    case "resumed":
                        if (root.TryGetProperty("payload", out var rp))
                        {
                            var el = rp.GetProperty("elapsed_secs").GetInt32();
                            lock (_lock) { if (_state != null) { _state.ElapsedSecs = el; _state.IsRunning = true; _state.IsPaused = false; } }
                        }
                        StateUpdated?.Invoke();
                        break;

                    case "reset":
                        lock (_lock) { if (_state != null) { _state.ElapsedSecs = 0; _state.IsRunning = false; _state.IsPaused = false; } }
                        StateUpdated?.Invoke();
                        break;

                    case "themeChanged":
                        if (root.TryGetProperty("payload", out var tp))
                        {
                            var t = new RemoteTheme();
                            if (tp.TryGetProperty("name", out var tn)) t.Name = tn.GetString() ?? "PomoDeck";
                            if (tp.TryGetProperty("colors", out var tc))
                                foreach (var prop in tc.EnumerateObject())
                                    t.Colors[prop.Name] = prop.Value.GetString() ?? "";
                            lock (_lock) _theme = t;
                            if (!_connectCooldown) ThemeUpdated?.Invoke();
                        }
                        break;

                    case "tasksChanged":
                        if (root.TryGetProperty("payload", out var taskPayload))
                        {
                            try
                            {
                                var taskList = JsonSerializer.Deserialize<System.Collections.Generic.List<TaskItem>>(taskPayload.GetRawText());
                                TasksReceived?.Invoke(taskList ?? new());
                            }
                            catch { }
                        }
                        break;

                    case "skinChanged":
                        var skinId = "default";
                        if (root.TryGetProperty("payload", out var skinPayload))
                        {
                            if (skinPayload.TryGetProperty("id", out var sid))
                                skinId = sid.GetString() ?? "default";
                        }
                        SkinChanged?.Invoke(skinId);
                        break;

                    case "flowChanged":
                        if (root.TryGetProperty("payload", out var flowPayload))
                        {
                            try
                            {
                                var f = JsonSerializer.Deserialize<RemoteFlowState>(flowPayload.GetRawText());
                                if (f != null)
                                {
                                    lock (_lock) _flow = f;
                                    if (!_connectCooldown) FlowUpdated?.Invoke(f);
                                }
                            }
                            catch { }
                        }
                        break;

                    case "jarChanged":
                        if (root.TryGetProperty("payload", out var jarPayload))
                        {
                            try
                            {
                                if (jarPayload.TryGetProperty("tomatoes", out var tArr))
                                {
                                    var list = JsonSerializer.Deserialize<System.Collections.Generic.List<JarTomato>>(tArr.GetRawText());
                                    JarReceived?.Invoke(list ?? new());
                                }
                            }
                            catch { }
                        }
                        break;

                    case "settingsSync":
                        if (root.TryGetProperty("payload", out var settingsPayload))
                        {
                            try
                            {
                                if (settingsPayload.TryGetProperty("tick_sounds_work", out var tsw))
                                    if (!_connectCooldown) SettingsSynced?.Invoke("tick_sounds_work", tsw.GetBoolean());
                                if (settingsPayload.TryGetProperty("tick_sounds_break", out var tsb))
                                    if (!_connectCooldown) SettingsSynced?.Invoke("tick_sounds_break", tsb.GetBoolean());
                            }
                            catch { }
                        }
                        break;
                }
            }
            catch { }
        }

        public void Send(String json)
        {
            if (!_connected) return;
            try { _ws?.Send(json); } catch { }
        }

        public void SendToggle() => Send("{\"type\":\"toggle\"}");
        public void SendReset() => Send("{\"type\":\"reset\"}");
        public void SendSkip() => Send("{\"type\":\"skip\"}");
        public void SendSetting(String key, String value) =>
            Send($"{{\"type\":\"setSetting\",\"key\":\"{key}\",\"value\":\"{value}\"}}");

        public void Dispose()
        {
            _disposed = true;
            try { _ws?.Close(); } catch { }
            _thread.Join(3000); // Wait up to 3s for clean shutdown
        }
    }

    public class RemoteTimerState
    {
        [JsonPropertyName("round_type")] public String RoundType { get; set; } = "work";
        [JsonPropertyName("elapsed_secs")] public Int32 ElapsedSecs { get; set; }
        [JsonPropertyName("total_secs")] public Int32 TotalSecs { get; set; }
        [JsonPropertyName("is_running")] public Boolean IsRunning { get; set; }
        [JsonPropertyName("is_paused")] public Boolean IsPaused { get; set; }
        [JsonPropertyName("work_round_number")] public Int32 WorkRoundNumber { get; set; } = 1;
        [JsonPropertyName("work_rounds_total")] public Int32 WorkRoundsTotal { get; set; } = 4;
        public Int32 RemainingSecs => Math.Max(0, TotalSecs - ElapsedSecs);
        public Double Progress => TotalSecs > 0 ? Math.Clamp((Double)ElapsedSecs / TotalSecs, 0.0, 1.0) : 0.0;
    }

    public class RemoteTheme
    {
        public String Name { get; set; } = "PomoDeck";
        public System.Collections.Generic.Dictionary<String, String> Colors { get; set; } = new();

        public SKColor GetColor(String key, SKColor fallback)
        {
            if (Colors.TryGetValue(key, out var hex) && !String.IsNullOrEmpty(hex))
            {
                try { return SKColor.Parse(hex); } catch { }
            }
            return fallback;
        }
    }

    public class RemoteFlowState
    {
        [JsonPropertyName("score")] public Int32 Score { get; set; }
        [JsonPropertyName("level")] public String Level { get; set; } = "sleeping";
        [JsonPropertyName("streak")] public Int32 Streak { get; set; }
        [JsonPropertyName("pause_count")] public Int32 PauseCount { get; set; }
        [JsonPropertyName("in_focus")] public Boolean InFocus { get; set; }
        [JsonPropertyName("blocking_active")] public Boolean BlockingActive { get; set; }
        [JsonPropertyName("focus_minutes")] public Int32 FocusMinutes { get; set; }
    }
}
