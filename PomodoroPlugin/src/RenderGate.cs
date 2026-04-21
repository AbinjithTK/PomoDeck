namespace Loupedeck.PomoDeckPlugin
{
    using System;
    using System.Collections.Concurrent;

    /// <summary>
    /// Global render throttle + deduplication.
    /// 
    /// 1. Per-widget cooldown (150ms) — prevents rapid-fire renders
    /// 2. Global concurrency cap (4) — prevents thread pool exhaustion
    /// 3. Image hash dedup — skips ActionImageChanged if the rendered
    ///    bytes are identical to the last sent image (eliminates
    ///    redundant USB transfers that heat the device)
    /// </summary>
    internal static class RenderGate
    {
        private const Int32 MIN_INTERVAL_MS = 150;
        private static readonly ConcurrentDictionary<String, DateTime> _lastRender = new();
        private static readonly ConcurrentDictionary<String, Int32> _lastHash = new();
        private static volatile Int32 _pendingCount;
        private const Int32 MAX_PENDING = 4;

        internal static void Request(String actionId, Action invalidate)
        {
            var now = DateTime.UtcNow;
            if (_lastRender.TryGetValue(actionId, out var last))
            {
                if ((now - last).TotalMilliseconds < MIN_INTERVAL_MS)
                    return;
            }
            if (_pendingCount >= MAX_PENDING)
                return;

            _lastRender[actionId] = now;
            System.Threading.Interlocked.Increment(ref _pendingCount);
            try { invalidate(); }
            catch { }
            finally { System.Threading.Interlocked.Decrement(ref _pendingCount); }
        }

        /// <summary>
        /// Check if the image bytes are different from the last sent image.
        /// Call this from GetCommandImage before returning — if false, return
        /// the cached bytes without calling ActionImageChanged.
        /// </summary>
        internal static Boolean HasChanged(String actionId, Byte[] imageBytes)
        {
            if (imageBytes == null || imageBytes.Length == 0) return true;

            // Fast hash: XOR first 64 bytes + length (avoids hashing entire image)
            var hash = imageBytes.Length;
            var len = Math.Min(imageBytes.Length, 64);
            for (var i = 0; i < len; i++)
                hash = hash * 31 + imageBytes[i];

            if (_lastHash.TryGetValue(actionId, out var prev) && prev == hash)
                return false; // identical — skip USB transfer

            _lastHash[actionId] = hash;
            return true;
        }
    }
}
