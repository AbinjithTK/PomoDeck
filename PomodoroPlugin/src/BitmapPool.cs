namespace Loupedeck.PomoDeckPlugin
{
    using System;
    using SkiaSharp;

    /// <summary>
    /// Reusable SKBitmap pool — eliminates per-frame allocation/deallocation.
    /// Each widget gets one pooled bitmap that's reused across renders.
    /// The bitmap is only reallocated if the size changes.
    /// 
    /// Usage:
    ///   var (bmp, canvas) = BitmapPool.Get("WidgetName", size, size);
    ///   canvas.Clear(...);
    ///   // render...
    ///   var img = BitmapPool.Encode(bmp);
    ///   return img;
    /// 
    /// Never dispose the returned bitmap — the pool owns it.
    /// </summary>
    internal static class BitmapPool
    {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<String, (SKBitmap bmp, SKCanvas canvas)> _pool = new();

        internal static (SKBitmap bmp, SKCanvas canvas) Get(String key, Int32 width, Int32 height)
        {
            if (_pool.TryGetValue(key, out var entry))
            {
                if (entry.bmp.Width == width && entry.bmp.Height == height)
                {
                    entry.canvas.Clear(SKColors.Transparent);
                    return entry;
                }
                // Size changed — dispose old and create new
                entry.canvas.Dispose();
                entry.bmp.Dispose();
            }

            var bmp = new SKBitmap(width, height);
            var canvas = new SKCanvas(bmp);
            var result = (bmp, canvas);
            _pool[key] = result;
            return result;
        }

        /// <summary>Encode the pooled bitmap to JPEG bytes. Does NOT dispose the bitmap.</summary>
        internal static BitmapImage Encode(SKBitmap bmp)
        {
            using var img = SKImage.FromBitmap(bmp);
            using var data = img.Encode(SKEncodedImageFormat.Jpeg, 85);
            return BitmapImage.FromArray(data.ToArray());
        }

        /// <summary>Call on plugin unload to free all pooled bitmaps.</summary>
        internal static void Clear()
        {
            foreach (var entry in _pool.Values)
            {
                try { entry.canvas.Dispose(); } catch { }
                try { entry.bmp.Dispose(); } catch { }
            }
            _pool.Clear();
        }
    }
}
