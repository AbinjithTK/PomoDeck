namespace Loupedeck.PomoDeckPlugin
{
    using System;
    using System.IO;
    using System.Reflection;
    using System.Runtime.InteropServices;
    using System.Threading;

    /// <summary>
    /// Zero-lag sound via PlaySound with SND_MEMORY.
    /// 
    /// WAV bytes are loaded into pinned memory buffers at init.
    /// PlaySound(ptr, SND_MEMORY | SND_ASYNC) plays directly from RAM —
    /// no file I/O, no device management, no MCI, instant playback.
    /// 
    /// Limitation: PlaySound is system-wide singleton — only one sound
    /// at a time. New call stops the previous. For our use case this is
    /// fine: task-done replaces winding, phase-complete replaces everything.
    /// </summary>
    public static class SoundAlert
    {
        private const UInt32 SND_MEMORY    = 0x0004;
        private const UInt32 SND_ASYNC     = 0x0001;
        private const UInt32 SND_NODEFAULT = 0x0002;

        [DllImport("winmm.dll", EntryPoint = "PlaySoundA", SetLastError = true)]
        private static extern Boolean PlaySoundMem(IntPtr pData, IntPtr hmod, UInt32 flags);

        // Pinned byte arrays — never GC'd, stable pointers for PlaySound
        private static Byte[] _phaseBytes;
        private static Byte[] _windBytes;
        private static Byte[] _taskBytes;
        private static Byte[] _tickBytes;
        private static Byte[] _skipWorkBytes;
        private static Byte[] _skipShortBytes;
        private static Byte[] _skipLongBytes;
        private static GCHandle _phasePin;
        private static GCHandle _windPin;
        private static GCHandle _taskPin;
        private static GCHandle _tickPin;
        private static GCHandle _skipWorkPin;
        private static GCHandle _skipShortPin;
        private static GCHandle _skipLongPin;

        private static volatile Boolean _ready;
        private static readonly Object _initLock = new();

        private static readonly System.Timers.Timer _windStop = new(150) { AutoReset = false };

        static SoundAlert()
        {
            _windStop.Elapsed += (_, _) =>
            {
                PlaySoundMem(IntPtr.Zero, IntPtr.Zero, 0);
            };
        }

        public static void PlayPhaseComplete() { EnsureReady(); Play(_phasePin); }
        public static void PlayTaskDone() { EnsureReady(); Play(_taskPin); }
        public static void PlayTick() { EnsureReady(); Play(_tickPin); }
        public static void BlockingActivated() => PlayPhaseComplete();

        /// <summary>Play skip feedback sound based on the phase being skipped TO.</summary>
        public static void PlaySkipWork() { EnsureReady(); Play(_skipWorkPin); }
        public static void PlaySkipShortBreak() { EnsureReady(); Play(_skipShortPin); }
        public static void PlaySkipLongBreak() { EnsureReady(); Play(_skipLongPin); }

        /// <summary>Play the appropriate skip sound for the given next phase.</summary>
        public static void PlaySkipForPhase(PomodoroTimer.TimerPhase nextPhase)
        {
            switch (nextPhase)
            {
                case PomodoroTimer.TimerPhase.Work: PlaySkipWork(); break;
                case PomodoroTimer.TimerPhase.ShortBreak: PlaySkipShortBreak(); break;
                case PomodoroTimer.TimerPhase.LongBreak: PlaySkipLongBreak(); break;
                default: PlayPhaseComplete(); break;
            }
        }

        public static void PlayWinding()
        {
            EnsureReady();
            Play(_windPin);
            // Reset the stop timer — if no new tick in 150ms, sound stops
            _windStop.Stop();
            _windStop.Start();
        }

        private static readonly Object _playLock = new();

        private static void Play(GCHandle pin)
        {
            if (!pin.IsAllocated) return;
            lock (_playLock)
            {
                // Stops any currently playing sound and starts the new one instantly
                PlaySoundMem(pin.AddrOfPinnedObject(), IntPtr.Zero, SND_MEMORY | SND_ASYNC | SND_NODEFAULT);
            }
        }

        /// <summary>Stop any currently playing sound immediately.</summary>
        public static void StopAll()
        {
            lock (_playLock)
            {
                PlaySoundMem(IntPtr.Zero, IntPtr.Zero, 0);
            }
        }

        private static void EnsureReady()
        {
            if (_ready) return;
            lock (_initLock)
            {
                if (_ready) return;
                _phaseBytes = LoadResource("Loupedeck.PomoDeckPlugin.audio.phase_complete.wav");
                _windBytes = LoadResource("Loupedeck.PomoDeckPlugin.audio.winding.wav");
                _taskBytes = LoadResource("Loupedeck.PomoDeckPlugin.audio.taskdone.wav");
                _tickBytes = LoadResource("Loupedeck.PomoDeckPlugin.audio.tick.wav");
                _skipWorkBytes = LoadResource("Loupedeck.PomoDeckPlugin.audio.skip_work.wav");
                _skipShortBytes = LoadResource("Loupedeck.PomoDeckPlugin.audio.skip_short_break.wav");
                _skipLongBytes = LoadResource("Loupedeck.PomoDeckPlugin.audio.skip_long_break.wav");

                if (_phaseBytes != null) _phasePin = GCHandle.Alloc(_phaseBytes, GCHandleType.Pinned);
                if (_windBytes != null) _windPin = GCHandle.Alloc(_windBytes, GCHandleType.Pinned);
                if (_taskBytes != null) _taskPin = GCHandle.Alloc(_taskBytes, GCHandleType.Pinned);
                if (_tickBytes != null) _tickPin = GCHandle.Alloc(_tickBytes, GCHandleType.Pinned);
                if (_skipWorkBytes != null) _skipWorkPin = GCHandle.Alloc(_skipWorkBytes, GCHandleType.Pinned);
                if (_skipShortBytes != null) _skipShortPin = GCHandle.Alloc(_skipShortBytes, GCHandleType.Pinned);
                if (_skipLongBytes != null) _skipLongPin = GCHandle.Alloc(_skipLongBytes, GCHandleType.Pinned);

                PluginLog.Info($"[audio] Loaded: phase={_phaseBytes?.Length ?? 0}B wind={_windBytes?.Length ?? 0}B task={_taskBytes?.Length ?? 0}B skip_w={_skipWorkBytes?.Length ?? 0}B skip_s={_skipShortBytes?.Length ?? 0}B skip_l={_skipLongBytes?.Length ?? 0}B");
                _ready = true;
            }
        }

        private static Byte[] LoadResource(String resourceName)
        {
            try
            {
                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
                if (stream == null)
                {
                    PluginLog.Warning($"[audio] Resource not found: {resourceName}");
                    return null;
                }
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                return ms.ToArray();
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, $"[audio] Failed to load: {resourceName}");
                return null;
            }
        }

        public static void Shutdown()
        {
            // Stop any playing sound
            PlaySoundMem(IntPtr.Zero, IntPtr.Zero, 0);
            // Free pinned buffers
            if (_phasePin.IsAllocated) _phasePin.Free();
            if (_windPin.IsAllocated) _windPin.Free();
            if (_taskPin.IsAllocated) _taskPin.Free();
            if (_tickPin.IsAllocated) _tickPin.Free();
            if (_skipWorkPin.IsAllocated) _skipWorkPin.Free();
            if (_skipShortPin.IsAllocated) _skipShortPin.Free();
            if (_skipLongPin.IsAllocated) _skipLongPin.Free();
        }
    }
}
