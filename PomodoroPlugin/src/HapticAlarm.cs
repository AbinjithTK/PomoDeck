namespace Loupedeck.PomoDeckPlugin
{
    using System;

    /// <summary>
    /// Simple single-pulse haptic. One tick per event. No sequences.
    /// </summary>
    public sealed class HapticAlarm : IDisposable
    {
        private readonly Action<String> _raiseEvent;

        public Boolean IsRinging => false;

        public HapticAlarm(Action<String> raiseEvent)
        {
            _raiseEvent = raiseEvent;
        }

        public void Ring(String eventName)
        {
            try { _raiseEvent(eventName); } catch { }
        }

        public void Stop() { }

        public void Pulse(String eventName)
        {
            try { _raiseEvent(eventName); } catch { }
        }

        public void Dispose() { }
    }
}
