namespace Loupedeck.PomoDeckPlugin
{
    using System;

    /// <summary>
    /// Thread-safe skin engine.
    /// User's skin choice persists — app broadcasts don't override local selection.
    /// </summary>
    public sealed class SkinEngine
    {
        private readonly Object _lock = new();
        private String _activeSkinId = "classic";

        public event Action SkinChanged;

        public String ActiveTimerWidget
        {
            get { lock (_lock) return _activeSkinId == "liquid" ? "liquid" : "classic"; }
        }

        public String ActiveId
        {
            get { lock (_lock) return _activeSkinId; }
        }

        public String ActiveName
        {
            get
            {
                lock (_lock) return _activeSkinId switch
                {
                    "liquid" => "Liquid Glass",
                    _ => "Classic"
                };
            }
        }

        /// <summary>Cycle skin. User's choice — persists until changed again.</summary>
        public String CycleNext()
        {
            lock (_lock)
            {
                _activeSkinId = _activeSkinId == "liquid" ? "classic" : "liquid";
                PluginLog.Info($"[skin] Cycled to: {_activeSkinId}");
            }
            SkinChanged?.Invoke();
            return ActiveId;
        }

        /// <summary>Set from app — only applies if user hasn't overridden locally.</summary>
        public void SetActive(String skinId)
        {
            // Ignore app broadcasts — user controls the skin via button/double-tap
        }

        /// <summary>No-op — skin persists across connect/disconnect.</summary>
        public void RevertToDefault()
        {
            // Don't revert — user's choice persists
        }
    }
}
