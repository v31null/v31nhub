using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Hub
{
    sealed class DemoNet : HttpMessageHandler
    {
        const double Rate = 6e6;

        public static async Task<double?> Rtt(string b)
        {
            if (Env.Offline) return null;
            var ms = Demo.Next(20, 90);
            await Task.Delay(ms);
            return ms;
        }

        static HttpResponseMessage Status(int code) => new HttpResponseMessage((HttpStatusCode)code) { Content = new ByteArrayContent(new byte[0]) };

        static HttpResponseMessage Json(object o) => Body(Text.Utf8.GetBytes(J.Stringify(o)), "application/json");

        static HttpResponseMessage Body(byte[] b, string type)
        {
            var m = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(b) };
            m.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(type);
            return m;
        }

        static HttpResponseMessage File_(HttpRequestMessage req, string file, string type, bool slow)
        {
            var size = new FileInfo(file).Length;
            long from = 0, to = size - 1;
            var partial = false;
            var r = req.Headers.Range?.Ranges.FirstOrDefault();
            if (r != null)
            {
                if (r.From == null && r.To != null)
                {
                    from = Math.Max(0, size - r.To.Value);
                }
                else
                {
                    from = r.From ?? 0;
                    if (r.To != null) to = Math.Min(r.To.Value, size - 1);
                }
                if (from >= size || from > to)
                {
                    var bad = Status(416);
                    bad.Content.Headers.ContentRange = new ContentRangeHeaderValue(size);
                    return bad;
                }
                partial = true;
            }
            var len = to - from + 1;
            var head = req.Method == HttpMethod.Head;
            HttpContent content = head ? (HttpContent)new ByteArrayContent(new byte[0]) : new StreamContent(new Slow(file, from, len, slow ? Rate : 0), 1 << 16);
            var m = new HttpResponseMessage(partial ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = content };
            m.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(type);
            m.Content.Headers.ContentLength = head ? 0 : len;
            if (partial) m.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, size);
            m.Headers.AcceptRanges.Add("bytes");
            return m;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            await Task.Delay(Demo.Next(15, 60), ct);
            if (Env.Offline) throw new HttpRequestException("dns", new WebException("dns", WebExceptionStatus.NameResolutionFailure));
            var root = await Demo.Server;
            var u = req.RequestUri;
            var origin = (u.Scheme + "://" + u.Authority).ToLowerInvariant();
            var path = Uri.UnescapeDataString(u.AbsolutePath);
            var whole = u.GetLeftPart(UriPartial.Path);
            HttpResponseMessage res = null;
            if (Net.Hubs().Any(h => string.Equals(h.Url, whole, StringComparison.OrdinalIgnoreCase))) res = Body(File.ReadAllBytes(Path.Combine(root, "hub.json")), "application/json");
            else if (Hosts("prono").Contains(origin)) res = App(req, root, "prono", "/app/", path);
            else if (path == "/app/manifest.json" && Hosts("nullpunkts").Contains(origin)) res = Json(J.O("service", "nullpunkts"));
            else if (path == "/app/manifest.json" && Hosts("imgsw").Contains(origin)) res = Json(J.O("service", "imgsw"));
            else if (Hosts("nullpunkts").Contains(origin))
            {
                res = App(req, root, "m2", "/m/app/", path);
                if (res == null && path == "/m/m2list.json") res = File_(req, Path.Combine(root, "m2list.json"), "application/json", false);
                else if (res == null && path == "/m/m2.v31np") res = File_(req, Path.Combine(root, "m2.v31np"), "application/octet-stream", true);
                else if (res == null && Demo.Keys.Contains(path)) res = File_(req, Demo.Under(root, path), "application/octet-stream", true);
            }
            else if (Hosts("imgsw").Contains(origin) && path == "/BCM.php" && req.Method == HttpMethod.Post)
            {
                var action = u.Query.TrimStart('?').Split('&').Select(p => p.Split('=')).Where(p => p.Length == 2 && p[0] == "action").Select(p => p[1]).FirstOrDefault();
                if (action == "request_number")
                {
                    var key = new byte[32];
                    using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(key);
                    res = Json(J.O("success", true, "number", Demo.Next(100000, 999999).ToString(), "key", Convert.ToBase64String(key).TrimEnd('=').Replace('+', '-').Replace('/', '_')));
                }
                else if (action == "verify") res = Json(J.O("success", true));
            }
            res = res ?? Status(404);
            res.RequestMessage = req;
            return res;
        }

        static HashSet<string> Hosts(string name) => new HashSet<string>(Net.Servers(name).Select(s => s.ToLowerInvariant()));

        static HttpResponseMessage App(HttpRequestMessage req, string root, string key, string prefix, string path)
        {
            if (!path.StartsWith(prefix, StringComparison.Ordinal)) return null;
            var name = path.Substring(prefix.Length);
            var dir = Path.Combine(root, key);
            if (name == "manifest.json") return File_(req, Path.Combine(dir, "manifest.json"), "application/json", false);
            if (name == Demo.Exe(key)) return File_(req, Path.Combine(dir, name), "application/octet-stream", true);
            return null;
        }

        sealed class Slow : Stream
        {
            readonly FileStream f;
            readonly double rate;
            readonly Stopwatch clock = Stopwatch.StartNew();
            long left, sent;

            public Slow(string file, long from, long len, double rate)
            {
                f = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, true);
                f.Position = from;
                left = len;
                this.rate = rate;
            }

            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            {
                if (left <= 0) return 0;
                var n = await f.ReadAsync(buffer, offset, (int)Math.Min(Math.Min(count, 1 << 16), left), ct);
                left -= n;
                sent += n;
                if (rate > 0)
                {
                    var ahead = sent / rate * 1000 - clock.Elapsed.TotalMilliseconds;
                    if (ahead > 1) await Task.Delay((int)ahead, ct);
                }
                return n;
            }

            public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

            protected override void Dispose(bool disposing)
            {
                if (disposing) f.Dispose();
                base.Dispose(disposing);
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }

    static class Demo
    {
        static readonly Random Rnd = new Random();
        static readonly Lazy<Task<string>> Build = new Lazy<Task<string>>(() => Task.Run(Make));
        static readonly Lazy<HashSet<string>> Names = new Lazy<HashSet<string>>(() => new HashSet<string>(List().Keys, StringComparer.Ordinal));

        public static Task<string> Server => Build.Value;
        public static HashSet<string> Keys => Names.Value;

        public static int Next(int a, int b)
        {
            lock (Rnd) return Rnd.Next(a, b);
        }

        public static string Exe(string key) => Unit.Specs[key].Exe;

        public static string Under(string root, string key) => Path.Combine(root, "files", key.TrimStart('/').Replace('/', '\\'));

        static Dictionary<string, object> List()
        {
            var l = J.Parse(Embedded.Text("ui/js/m2list.json"));
            return J.Get(l, "list") as Dictionary<string, object> ?? new Dictionary<string, object>();
        }

        static byte[] Fill(Random r, int n)
        {
            var b = new byte[n];
            r.NextBytes(b);
            return b;
        }

        static string Sha(string file)
        {
            using (var sha = SHA256.Create())
            using (var f = File.OpenRead(file)) return Bytes.Hex(sha.ComputeHash(f));
        }

        static string Make()
        {
            var root = Path.Combine(Env.Demo, "server");
            Fs.Mkdir(Path.Combine(Env.Demo, "Origin"));
            var mark = Path.Combine(root, "ready");
            if (File.Exists(mark)) return root;
            Fs.Mkdir(root);
            var stub = Embedded.Bytes("stub.exe");
            var seed = new Random(31);
            foreach (var key in new[] { "prono", "m2" })
            {
                var dir = Path.Combine(root, key);
                Fs.Mkdir(dir);
                var file = Path.Combine(dir, Exe(key));
                Fs.Write(file, stub);
                Fs.Write(Path.Combine(dir, "manifest.json"), J.Stringify(J.O(
                    "version", "1.0.0",
                    "date", DateTime.Now.ToString("yyyyMMdd"),
                    "file", Exe(key),
                    "sha256", Sha(file),
                    "bytes", (double)stub.Length,
                    "unpacked", (double)stub.Length,
                    "files", 1.0)));
            }

            var src = J.Parse(Embedded.Text("ui/js/m2list.json"));
            var keys = List().Keys.ToList();
            var list = J.O();
            var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var k in keys)
            {
                var content = Fill(seed, seed.Next(2048, 49152));
                var file = Under(root, k);
                Fs.Mkdir(Path.GetDirectoryName(file));
                Fs.Write(file, content);
                list[k] = Fp.Of(content, k, Fp.MimeOf(k, "application/octet-stream"));
                sizes[k] = content.Length;
            }
            Fs.Write(Path.Combine(root, "m2list.json"), J.Stringify(J.O("v", J.Get(src, "v") ?? 1.0, "list", list)));
            Pack(Path.Combine(root, "m2.v31np"), keys, sizes, k => Under(root, k));

            Fs.Write(Path.Combine(root, "hub.json"), J.Stringify(J.O(
                "file", "V31null Hub.exe",
                "version", Env.Version,
                "sha256", Sha(Env.Exe),
                "bytes", (double)new FileInfo(Env.Exe).Length)));
            Fs.Write(mark, "");
            return root;
        }

        static byte[] Number(long v)
        {
            var b = new List<byte>();
            do
            {
                b.Add((byte)(v & 0xFF));
                v >>= 8;
            } while (v > 0);
            return b.ToArray();
        }

        static void Pack(string target, List<string> keys, Dictionary<string, long> sizes, Func<string, string> source)
        {
            var u = Text.Utf8;
            var head = Bytes.Cat(new byte[] { 0 }, Arc.Phi);
            var datum = Bytes.Cat(new byte[] { 1 }, Arc.Phi);
            var t2 = new MemoryStream();
            void Put(MemoryStream m, params byte[][] parts)
            {
                foreach (var p in parts) m.Write(p, 0, p.Length);
            }
            foreach (var k in keys)
            {
                var slash = k.LastIndexOf('/');
                var dir = slash < 0 ? "" : k.Substring(0, slash + 1);
                var bas = k.Substring(dir.Length);
                var dot = bas.LastIndexOf('.');
                var ext = dot < 0 ? "" : bas.Substring(dot);
                var name = bas.Substring(0, bas.Length - ext.Length);
                Put(t2, head, u.GetBytes(k), datum, Number(sizes[k]), datum, u.GetBytes(dir), datum, u.GetBytes(name), datum, u.GetBytes(ext));
            }
            long start = 0, previous;
            byte[] header;
            do
            {
                previous = start;
                var t1 = new MemoryStream();
                var address = previous;
                foreach (var k in keys)
                {
                    Put(t1, head, u.GetBytes(k), datum, Number(address));
                    address += sizes[k];
                }
                header = Bytes.Cat(Arc.Magic, Arc.Pi, t1.ToArray(), Arc.Pi, t2.ToArray(), Arc.Pi);
                start = header.Length;
            } while (start != previous);
            using (var f = Fs.Out(target, false))
            {
                f.Write(header, 0, header.Length);
                var crc = Crc32.Of(header);
                foreach (var k in keys)
                {
                    var b = File.ReadAllBytes(source(k));
                    f.Write(b, 0, b.Length);
                    crc = Crc32.Of(b, crc);
                }
                f.Write(Arc.Pi, 0, Arc.Pi.Length);
                crc = Crc32.Of(Arc.Pi, crc);
                var sum = BitConverter.GetBytes(crc);
                f.Write(sum, 0, 4);
            }
        }

        public static List<Process> Mine(string exe)
        {
            var r = new List<Process>();
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))
            {
                try
                {
                    if (p.MainModule.FileName.StartsWith(Env.Demo + "\\", StringComparison.OrdinalIgnoreCase)) r.Add(p);
                }
                catch (Exception) { }
            }
            return r;
        }

        public static void Stop(string exe, bool force)
        {
            foreach (var p in Mine(exe))
            {
                try
                {
                    if (force) p.Kill();
                    else p.CloseMainWindow();
                }
                catch (Exception) { }
            }
        }

        public static object PlanList()
        {
            var first = Net.Servers("nullpunkts").FirstOrDefault();
            return first == null ? new List<object>() : new List<object> { new Uri(first).Host };
        }

        public static async Task<bool> PlanSave(string host)
        {
            await Task.Delay(600);
            return true;
        }
    }
}
