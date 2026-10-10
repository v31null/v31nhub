using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace Hub
{
    static class Fp
    {
        public const int Sample = 65536;

        static readonly Dictionary<string, string> Types = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "mp3", "audio/mpeg" }, { "wav", "audio/x-wav" }, { "mp4", "video/mp4" }, { "webm", "video/webm" },
            { "png", "image/png" }, { "jpg", "image/jpeg" }, { "jpeg", "image/jpeg" }, { "gif", "image/gif" },
            { "svg", "image/svg+xml" }, { "ico", "image/x-icon" }, { "woff", "font/woff" }, { "woff2", "font/woff2" },
            { "ttf", "font/ttf" }, { "otf", "font/otf" }, { "css", "text/css" }, { "js", "text/javascript" }, { "stl", "model/stl" }
        };

        public static string MimeOf(string key, string raw)
        {
            var ext = key.Split('.').Last().ToLowerInvariant();
            if (Types.TryGetValue(ext, out var t)) return t;
            var r = (raw ?? "").Split(';')[0].Trim().ToLowerInvariant();
            return r.Length > 0 ? r : "application/octet-stream";
        }

        static byte[] Meta(string key, string mime, long size) =>
            Text.Utf8.GetBytes(J.Stringify(J.O("protocol", 1, "key", key, "mime", mime, "size", size, "sampleBytes", Sample)) + "\n");

        public static string Of(byte[] content, string key, string mime) => Of(content, content.Length, key, mime);

        public static string Of(byte[] content, int length, string key, string mime)
        {
            long size = length;
            var first = (int)Math.Min(Sample, size);
            var lastStart = (int)Math.Max(first, size - Sample);
            using (var sha = SHA256.Create())
            {
                var meta = Meta(key, mime, size);
                sha.TransformBlock(meta, 0, meta.Length, null, 0);
                sha.TransformBlock(content, 0, first, null, 0);
                sha.TransformFinalBlock(content, lastStart, (int)(size - lastStart));
                return Bytes.Hex(sha.Hash);
            }
        }

        public static async Task<(string fp, long size)> OfFile(string file, string key, string mime)
        {
            using (var fd = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, true))
            {
                var size = fd.Length;
                var first = (int)Math.Min(Sample, size);
                var lastStart = Math.Max(first, size - Sample);
                var a = new byte[first];
                var b = new byte[size - lastStart];
                await ReadAt(fd, a, 0);
                await ReadAt(fd, b, lastStart);
                using (var sha = SHA256.Create())
                {
                    var meta = Meta(key, mime, size);
                    sha.TransformBlock(meta, 0, meta.Length, null, 0);
                    sha.TransformBlock(a, 0, a.Length, null, 0);
                    sha.TransformFinalBlock(b, 0, b.Length);
                    return (Bytes.Hex(sha.Hash), size);
                }
            }
        }

        static async Task ReadAt(FileStream fd, byte[] buf, long pos)
        {
            fd.Position = pos;
            var o = 0;
            while (o < buf.Length)
            {
                var n = await fd.ReadAsync(buf, o, buf.Length - o);
                if (n <= 0) break;
                o += n;
            }
        }
    }
}
