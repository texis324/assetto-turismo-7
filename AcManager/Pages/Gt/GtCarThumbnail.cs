using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AcManager.Pages.Gt {
    /// <summary>Conservative preview matte removal for the floating Brand Central car strip.</summary>
    internal static class GtCarThumbnail {
        private const int CacheLimit = 48;
        private const long CacheBytesLimit = 16 * 1024 * 1024;
        private static readonly object CacheLock = new object();
        private static readonly Dictionary<string, Entry> Cache = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private static readonly LinkedList<string> Order = new LinkedList<string>();
        private static long _cacheBytes;

        private sealed class Entry {
            internal BitmapSource Image;
            internal long Bytes;
            internal LinkedListNode<string> Node;
        }

        public static BitmapSource Load(string previewPath, int decodeWidth) {
            if (string.IsNullOrEmpty(previewPath)) return null;
            string key;
            try {
                var file = new FileInfo(previewPath);
                if (!file.Exists) return null;
                previewPath = file.FullName;
                key = previewPath + "|" + file.LastWriteTimeUtc.Ticks + "|" + file.Length + "|" + decodeWidth;
                lock (CacheLock) {
                    Entry cached;
                    if (Cache.TryGetValue(key, out cached)) {
                        Order.Remove(cached.Node);
                        Order.AddFirst(cached.Node);
                        return cached.Image;
                    }
                }
            } catch { return WorldMap.LoadBitmap(previewPath, decodeWidth); }

            var original = WorldMap.LoadBitmap(previewPath, decodeWidth);
            if (original == null) return null;
            BitmapSource result = original;
            try { result = RemoveMatte(original); } catch { /* An ambiguous/unsupported preview stays intact. */ }
            if (result.CanFreeze && !result.IsFrozen) result.Freeze();
            var bytes = (long)result.PixelWidth * result.PixelHeight * Math.Max(4, (result.Format.BitsPerPixel + 7) / 8);
            if (bytes > CacheBytesLimit) return result;
            lock (CacheLock) {
                Entry duplicate;
                if (Cache.TryGetValue(key, out duplicate)) return duplicate.Image;
                while (Order.Count >= CacheLimit || _cacheBytes + bytes > CacheBytesLimit) {
                    var oldest = Order.Last;
                    if (oldest == null) break;
                    _cacheBytes -= Cache[oldest.Value].Bytes;
                    Cache.Remove(oldest.Value);
                    Order.RemoveLast();
                }
                var node = Order.AddFirst(key);
                Cache.Add(key, new Entry { Image = result, Bytes = bytes, Node = node });
                _cacheBytes += bytes;
            }
            return result;
        }

        private static bool HasAlpha(BitmapSource source) {
            var f = source.Format;
            if (f == PixelFormats.Bgra32 || f == PixelFormats.Pbgra32 || f == PixelFormats.Rgba64
                    || f == PixelFormats.Prgba64 || f == PixelFormats.Rgba128Float || f == PixelFormats.Prgba128Float) return true;
            if (source.Palette != null) {
                foreach (var color in source.Palette.Colors) if (color.A != 255) return true;
            }
            return false;
        }

        private static bool DarkNeutral(byte[] pixels, int offset, int ceiling) {
            var b = (int)pixels[offset]; var g = (int)pixels[offset + 1]; var r = (int)pixels[offset + 2];
            var max = Math.Max(r, Math.Max(g, b));
            var min = Math.Min(r, Math.Min(g, b));
            return max <= ceiling && max - min <= 10;
        }

        private static BitmapSource RemoveMatte(BitmapSource source) {
            var width = source.PixelWidth; var height = source.PixelHeight;
            // Work only on thumbnail-sized, opaque images. Existing artist-provided alpha is authoritative.
            if (HasAlpha(source) || width < 32 || height < 24 || (long)width * height > 1024 * 1024) return source;
            var stride = checked(width * 4);
            var pixels = new byte[checked(stride * height)];
            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            converted.CopyPixels(pixels, stride, 0);
            var patch = Math.Max(2, Math.Min(8, Math.Min(width, height) / 20));
            for (var corner = 0; corner < 4; corner++) {
                var ox = (corner & 1) == 0 ? 0 : width - patch;
                var oy = (corner & 2) == 0 ? 0 : height - patch;
                var good = 0;
                for (var y = oy; y < oy + patch; y++) for (var x = ox; x < ox + patch; x++) {
                    if (DarkNeutral(pixels, (y * width + x) * 4, 24)) good++;
                }
                if (good < patch * patch * 0.95) return source;
            }

            var count = width * height;
            var matte = new bool[count];
            var rowMin = new int[height]; var rowMax = new int[height];
            var colMin = new int[width]; var colMax = new int[width];
            for (var y = 0; y < height; y++) { rowMin[y] = width; rowMax[y] = -1; }
            for (var x = 0; x < width; x++) { colMin[x] = height; colMax[x] = -1; }
            var foreground = 0;
            for (var y = 0; y < height; y++) for (var x = 0; x < width; x++) {
                var i = y * width + x;
                matte[i] = DarkNeutral(pixels, i * 4, 28);
                if (matte[i]) continue;
                foreground++;
                rowMin[y] = Math.Min(rowMin[y], x); rowMax[y] = Math.Max(rowMax[y], x);
                colMin[x] = Math.Min(colMin[x], y); colMax[x] = Math.Max(colMax[x], y);
            }
            if (foreground < count * 0.04 || foreground > count * 0.75) return source;

            // A dark windshield/bumper can merge completely with the studio black.
            // Preserve the convex envelope of visible car pixels, including its dark
            // interior. This deliberately retains some original shadow in concavities
            // rather than guessing where opaque black bodywork ends.
            ProtectEnvelope(matte, rowMin, rowMax, width, height);
            var background = new bool[count];
            var queue = new int[count]; var head = 0; var tail = 0;
            for (var x = 0; x < width; x++) {
                Enqueue(x, matte, background, queue, ref tail);
                Enqueue((height - 1) * width + x, matte, background, queue, ref tail);
            }
            for (var y = 1; y < height - 1; y++) {
                Enqueue(y * width, matte, background, queue, ref tail);
                Enqueue(y * width + width - 1, matte, background, queue, ref tail);
            }
            while (head < tail) {
                var i = queue[head++]; var x = i % width; var y = i / width;
                if (x > 0) Enqueue(i - 1, matte, background, queue, ref tail);
                if (x + 1 < width) Enqueue(i + 1, matte, background, queue, ref tail);
                if (y > 0) Enqueue(i - width, matte, background, queue, ref tail);
                if (y + 1 < height) Enqueue(i + width, matte, background, queue, ref tail);
            }
            if (tail < count * 0.15 || tail > count * 0.94) return source;

            // Feather exclusively inside the identified matte: car pixels are never faded.
            // Keep a soft two-pixel transition instead of a hard threshold contour.
            for (var n = 0; n < tail; n++) {
                var i = queue[n]; var x = i % width; var y = i / width;
                var distance = 3;
                for (var dy = -2; dy <= 2; dy++) for (var dx = -2; dx <= 2; dx++) {
                    var d = Math.Abs(dx) + Math.Abs(dy);
                    if (d == 0 || d >= distance || x + dx < 0 || x + dx >= width || y + dy < 0 || y + dy >= height) continue;
                    if (!background[(y + dy) * width + x + dx]) distance = d;
                }
                var alpha = distance == 1 ? 176 : distance == 2 ? 64 : 0;
                var offset = i * 4;
                pixels[offset] = (byte)((pixels[offset] * alpha + 127) / 255);
                pixels[offset + 1] = (byte)((pixels[offset + 1] * alpha + 127) / 255);
                pixels[offset + 2] = (byte)((pixels[offset + 2] * alpha + 127) / 255);
                pixels[offset + 3] = (byte)alpha;
            }
            var result = BitmapSource.Create(width, height, source.DpiX, source.DpiY, PixelFormats.Pbgra32, null, pixels, stride);
            result.Freeze();
            return result;
        }

        private struct PixelPoint {
            internal int X, Y;
            internal PixelPoint(int x, int y) { X = x; Y = y; }
        }

        private static long Cross(PixelPoint a, PixelPoint b, PixelPoint c) {
            return (long)(b.X - a.X) * (c.Y - a.Y) - (long)(b.Y - a.Y) * (c.X - a.X);
        }

        private static void ProtectEnvelope(bool[] matte, int[] rowMin, int[] rowMax, int width, int height) {
            var points = new List<PixelPoint>();
            for (var y = 0; y < height; y++) if (rowMax[y] >= 0) {
                points.Add(new PixelPoint(rowMin[y], y));
                points.Add(new PixelPoint(rowMax[y], y));
            }
            points.Sort((a, b) => a.X == b.X ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X));
            if (points.Count < 3) return;
            var hull = new List<PixelPoint>();
            foreach (var p in points) {
                while (hull.Count >= 2 && Cross(hull[hull.Count - 2], hull[hull.Count - 1], p) <= 0) hull.RemoveAt(hull.Count - 1);
                hull.Add(p);
            }
            var lower = hull.Count;
            for (var i = points.Count - 2; i >= 0; i--) {
                var p = points[i];
                while (hull.Count > lower && Cross(hull[hull.Count - 2], hull[hull.Count - 1], p) <= 0) hull.RemoveAt(hull.Count - 1);
                hull.Add(p);
            }
            for (var y = 0; y < height; y++) {
                var left = (double)width; var right = -1d;
                for (var i = 1; i < hull.Count; i++) {
                    var a = hull[i - 1]; var b = hull[i];
                    if (y < Math.Min(a.Y, b.Y) || y > Math.Max(a.Y, b.Y)) continue;
                    if (a.Y == b.Y) {
                        left = Math.Min(left, Math.Min(a.X, b.X)); right = Math.Max(right, Math.Max(a.X, b.X));
                    } else {
                        var x = a.X + (double)(y - a.Y) * (b.X - a.X) / (b.Y - a.Y);
                        left = Math.Min(left, x); right = Math.Max(right, x);
                    }
                }
                for (var x = Math.Max(0, (int)Math.Floor(left)); x <= Math.Min(width - 1, (int)Math.Ceiling(right)); x++) matte[y * width + x] = false;
            }
        }
        private static void Enqueue(int i, bool[] matte, bool[] background, int[] queue, ref int tail) {
            if (!matte[i] || background[i]) return;
            background[i] = true;
            queue[tail++] = i;
        }
    }
}