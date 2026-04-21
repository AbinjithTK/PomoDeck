namespace Loupedeck.PomoDeckPlugin
{
    using System;

    /// <summary>
    /// Pomodoro timer settings. Persisted via the Loupedeck plugin settings API.
    /// </summary>
    public class PomodoroSettings
    {
        public Int32 WorkMinutes { get; set; } = 25;
        public Int32 ShortBreakMinutes { get; set; } = 5;
        public Int32 LongBreakMinutes { get; set; } = 15;
        public Int32 SessionsBeforeLongBreak { get; set; } = 3;
    }
}
