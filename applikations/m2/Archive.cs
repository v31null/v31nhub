using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Hub;
using Microsoft.Web.WebView2.Core;

namespace M2
{
    sealed class Reply
    {
        public int Status;
        public string Reason;
        public List<KeyValuePair<string, string>> Headers = new List<KeyValuePair<string, string>>();
        public Stream Body;

        public Reply(int status, string reason, Stream body = null)
        {
            Status = status;
            Reason = reason;
            Body = body;
        }

        public Reply Set(string k, string v)
        {
            Headers.Add(new KeyValuePair<string, string>(k, v));
            return this;
        }

        public string HeaderText => string.Join("\r\n", Headers.Select(h => h.Key + ": " + h.Value));
    }

    sealed class Lift
    {
        public string Key, Mime, Path;
        public long? Size;
        public long Got;
        public Exception Error;
        public bool Finished;
        public Task<bool> Done;

        public void Wake()
        {
            lock (this) Monitor.PulseAll(this);
        }
    }

    sealed class Archive
    {
        static readonly Regex MediaRe = new Regex(@"\.(mp3|wav|mp4|webm)$", RegexOptions.IgnoreCase);
        const string StatePath = "/m/app/archive/state";
        const string InstallPath = "/m/app/archive/install";
        static readonly HttpClient Http = new HttpClient(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = true, AutomaticDecompression = DecompressionMethods.None }) { Timeout = Timeout.InfiniteTimeSpan };

        readonly string dir, indexFile;
        readonly Func<Uri, bool> ours;
        readonly Func<HttpRequestMessage, HttpRequestMessage> mark;
        readonly Func<string, Task<string>> cookies;
        Dictionary<string, object> list;
        string bas;
        readonly Dictionary<string, Task<(Lift f, HttpResponseMessage res)>> flights = new Dictionary<string, Task<(Lift, HttpResponseMessage)>>();
        readonly object gate = new object();
        Task writing = Task.CompletedTask;
        Task job;
        bool fault;

        public Archive(string userData, Func<Uri, bool> ours, Func<HttpRequestMessage, HttpRequestMessage> mark, Func<string, Task<string>> cookies)
        {
            dir = System.IO.Path.Combine(userData, "Archive");
            indexFile = System.IO.Path.Combine(dir, "index.json");
            this.ours = ours;
            this.mark = mark;
            this.cookies = cookies;
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }

        string Where(string key)
        {
            string[] rel;
            try
            {
                rel = Uri.UnescapeDataString(key).Split('/').Where(p => p.Length > 0).ToArray();
            }
            catch (Exception)
            {
                return null;
            }
            if (rel.Length == 0 || rel.Any(p => p == "." || p == "..")) return null;
            try
            {
                var file = System.IO.Path.GetFullPath(System.IO.Path.Combine(new[] { dir }.Concat(rel).ToArray()));
                return file.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase) ? file : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        (DateTime at, long size, Dictionary<string, object> j)? cache;

        Dictionary<string, object> ReadIndex()
        {
            try
            {
                var st = new FileInfo(indexFile);
                var c = cache;
                if (c != null && c.Value.at == st.LastWriteTimeUtc && c.Value.size == st.Length) return c.Value.j;
                var j = J.Parse(File.ReadAllText(indexFile, Text.Utf8)) as Dictionary<string, object>;
                var v = j == null ? J.O() : new Dictionary<string, object>(j, StringComparer.Ordinal);
                if (!(v.TryGetValue("files", out var f) && f is Dictionary<string, object>)) v["files"] = J.O();
                cache = (st.LastWriteTimeUtc, st.Length, v);
                return v;
            }
            catch (Exception)
            {
                return J.O("files", J.O());
            }
        }

        static Dictionary<string, object> Files(Dictionary<string, object> idx) => (Dictionary<string, object>)idx["files"];

        Task Remember(string key, object ent)
        {
            lock (gate)
            {
                writing = writing.ContinueWith(_ =>
                {
                    try
                    {
                        var now = ReadIndex();
                        var next = new Dictionary<string, object>(now, StringComparer.Ordinal);
                        var files = new Dictionary<string, object>(Files(now), StringComparer.Ordinal);
                        files[key] = ent;
                        next["files"] = files;
                        Directory.CreateDirectory(dir);
                        var tmp = indexFile + "." + Process.GetCurrentProcess().Id + ".tmp";
                        File.WriteAllText(tmp, J.Stringify(next), Text.Utf8);
                        if (File.Exists(indexFile)) File.Replace(tmp, indexFile, null);
                        else File.Move(tmp, indexFile);
                    }
                    catch (Exception) { }
                }, TaskScheduler.Default);
                return writing;
            }
        }

        static string KeyOf(Uri u)
        {
            foreach (var part in u.Query.TrimStart('?').Split('&'))
            {
                if (part.Length == 0) continue;
                var name = part.Split('=')[0];
                try { name = Uri.UnescapeDataString(name.Replace('+', ' ')); } catch (Exception) { }
                if (name != "v") return null;
            }
            return u.AbsolutePath;
        }

        object Want(string key) => list == null ? (object)Undefined.V : (list.TryGetValue(key, out var v) ? v : null);

        static (long start, long end)? Span(string range, long size, out bool bad)
        {
            bad = false;
            var m = Regex.Match(range ?? "", @"^bytes=(\d*)-(\d*)$");
            if (!m.Success || (m.Groups[1].Length == 0 && m.Groups[2].Length == 0)) return null;
            var start = m.Groups[1].Length > 0 ? long.Parse(m.Groups[1].Value) : Math.Max(0, size - long.Parse(m.Groups[2].Value));
            var end = m.Groups[1].Length > 0 && m.Groups[2].Length > 0 ? Math.Min(long.Parse(m.Groups[2].Value), size - 1) : size - 1;
            if (start >= size || start > end)
            {
                bad = true;
                return null;
            }
            return (start, end);
        }

        static string Range(CoreWebView2HttpRequestHeaders h) => h.Contains("Range") ? h.GetHeader("Range") : null;

        Reply Local(string key, string range)
        {
            var ent = J.Get(Files(ReadIndex()), key);
            if (ent == null) return null;
            var w = Want(key);
            if (w == null || (!(w is Undefined) && J.Str(w) != J.Str(J.Get(ent, "fp")))) return null;
            var file = Where(key);
            if (file == null || !File.Exists(file)) return null;
            var size = new FileInfo(file).Length;
            if (!(J.Num(J.Get(ent, "size")) is double s) || s != size) return null;
            var span = Span(range, size, out var bad);
            var mime = J.Text(J.Get(ent, "mime"));
            if (bad) return new Reply(416, "Range Not Satisfiable").Set("Content-Range", "bytes */" + size);
            if (span != null)
            {
                var (a, b) = span.Value;
                return new Reply(206, "Partial Content", new Part(file, a, b - a + 1)).Set("Content-Type", mime).Set("Accept-Ranges", "bytes").Set("Content-Range", "bytes " + a + "-" + b + "/" + size).Set("Content-Length", (b - a + 1).ToString());
            }
            return new Reply(200, "OK", new Part(file, 0, size)).Set("Content-Type", mime).Set("Accept-Ranges", "bytes").Set("Content-Length", size.ToString());
        }

        async Task<(Lift f, HttpResponseMessage res)> Fetch(string key, string url, IEnumerable<KeyValuePair<string, string>> headers, bool strict)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            foreach (var h in headers)
            {
                var n = h.Key.ToLowerInvariant();
                if (n == "range" || n == "host" || n == "cookie" || n == "accept-encoding" || n == "content-length") continue;
                req.Headers.TryAddWithoutValidation(h.Key, h.Value);
            }
            var c = await cookies(url);
            if (!string.IsNullOrEmpty(c)) req.Headers.TryAddWithoutValidation("Cookie", c);
            req.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
            var res = await Http.SendAsync(mark(req), HttpCompletionOption.ResponseHeadersRead);
            var file = Where(key);
            if (res.StatusCode != HttpStatusCode.OK || file == null) return (null, res);
            var length = res.Content.Headers.ContentLength;
            var size = length != null && !res.Content.Headers.ContentEncoding.Any() ? length : null;
            if (strict && size == null) return (null, res);
            var ct = res.Content.Headers.ContentType?.ToString();
            var f = new Lift { Key = key, Size = size, Mime = Fp.MimeOf(key, ct), Path = file + "." + Process.GetCurrentProcess().Id + ".part" };
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file));
            var output = new FileStream(f.Path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 1 << 16, true);
            f.Done = Pump(f, res, output, file);
            return (f, null);
        }

        async Task<bool> Pump(Lift f, HttpResponseMessage res, FileStream output, string file)
        {
            try
            {
                using (res)
                using (var body = await res.Content.ReadAsStreamAsync().ConfigureAwait(false))
                {
                    var buf = new byte[1 << 16];
                    int n;
                    try
                    {
                        while ((n = await body.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false)) > 0)
                        {
                            await output.WriteAsync(buf, 0, n).ConfigureAwait(false);
                            await output.FlushAsync().ConfigureAwait(false);
                            lock (f) f.Got += n;
                            f.Wake();
                        }
                    }
                    finally
                    {
                        output.Dispose();
                    }
                }
                if (f.Size != null && f.Got != f.Size) throw new IOException("short");
                lock (f) f.Size = f.Got;
                var fp = (await Fp.OfFile(f.Path, f.Key, f.Mime).ConfigureAwait(false)).fp;
                if (list == null || !list.TryGetValue(f.Key, out var want) || J.Str(want) != fp)
                {
                    try { File.Delete(f.Path); } catch (Exception) { }
                    return false;
                }
                if (File.Exists(file)) File.Delete(file);
                File.Move(f.Path, file);
                lock (f) f.Path = file;
                await Remember(f.Key, J.O("fp", fp, "size", (double)f.Size, "mime", f.Mime)).ConfigureAwait(false);
                return true;
            }
            catch (Exception e)
            {
                lock (f) f.Error = e;
                try { File.Delete(f.Path); } catch (Exception) { }
                throw;
            }
            finally
            {
                lock (f) f.Finished = true;
                f.Wake();
                lock (flights) flights.Remove(f.Key);
            }
        }

        Task<(Lift f, HttpResponseMessage res)> Fly(string key, string url, IEnumerable<KeyValuePair<string, string>> headers, bool strict)
        {
            var p = Fetch(key, url, headers, strict);
            lock (flights) flights[key] = p;
            p.ContinueWith(t =>
            {
                if (t.IsFaulted || t.IsCanceled || t.Result.f == null) lock (flights) flights.Remove(key);
            }, TaskScheduler.Default);
            return p;
        }

        static Reply Serve(Lift f, string range)
        {
            var size = f.Size.Value;
            var span = Span(range, size, out var bad);
            if (bad) return new Reply(416, "Range Not Satisfiable").Set("Content-Range", "bytes */" + size);
            if (span != null)
            {
                var (a, b) = span.Value;
                return new Reply(206, "Partial Content", new Tap(f, a, b)).Set("Content-Type", f.Mime).Set("Accept-Ranges", "bytes").Set("Content-Range", "bytes " + a + "-" + b + "/" + size).Set("Content-Length", (b - a + 1).ToString());
            }
            return new Reply(200, "OK", new Tap(f, 0, size - 1)).Set("Content-Type", f.Mime).Set("Accept-Ranges", "bytes").Set("Content-Length", size.ToString());
        }

        static Reply FromResponse(HttpResponseMessage res)
        {
            var r = new Reply((int)res.StatusCode, res.ReasonPhrase ?? "", res.Content.ReadAsStreamAsync().Result);
            foreach (var h in res.Headers.Concat(res.Content.Headers)) r.Set(h.Key, string.Join(", ", h.Value));
            return r;
        }

        async Task<bool> Fresh(string key)
        {
            var ent = J.Get(Files(ReadIndex()), key);
            var file = Where(key);
            return ent != null && list != null && list.TryGetValue(key, out var w) && J.Str(J.Get(ent, "fp")) == J.Str(w) && file != null && await Task.Run(() => File.Exists(file));
        }

        void Install()
        {
            if (job != null || bas == null) return;
            fault = false;
            job = Run();
        }

        async Task Run()
        {
            try
            {
                if (list == null) await Load(bas);
                if (list == null) throw new Exception("list");
                foreach (var key in list.Keys.ToList())
                {
                    for (;;)
                    {
                        if (await Fresh(key)) break;
                        Task<(Lift f, HttpResponseMessage res)> flying;
                        lock (flights) flights.TryGetValue(key, out flying);
                        var r = flying != null ? await flying : await Fly(key, bas + key, new KeyValuePair<string, string>[0], false);
                        if (r.f != null)
                        {
                            if (!await r.f.Done) throw new Exception("hash");
                            break;
                        }
                        r.res?.Dispose();
                        if (flying == null) throw new Exception("http");
                    }
                }
            }
            catch (Exception)
            {
                fault = true;
            }
            finally
            {
                job = null;
            }
        }

        Reply State()
        {
            var files = Files(ReadIndex());
            var keys = list != null ? list.Keys.ToList() : new List<string>();
            var completed = keys.Count(k => J.Get(files, k) is Dictionary<string, object> e && J.Str(J.Get(e, "fp")) == J.Str(list[k]));
            var now = job != null ? "installing" : fault ? "fault" : keys.Count > 0 && completed == keys.Count ? "complete" : "idle";
            var body = Text.Utf8.GetBytes(J.Stringify(J.O("state", now, "completed", completed, "total", keys.Count)));
            return new Reply(200, "OK", new MemoryStream(body)).Set("Content-Type", "application/json").Set("Cache-Control", "no-store");
        }

        public async Task<Reply> Handle(CoreWebView2WebResourceRequest req)
        {
            Uri u;
            try
            {
                u = new Uri(req.Uri);
            }
            catch (Exception)
            {
                return null;
            }
            if (req.Method != "GET" || !ours(u)) return null;
            if (u.AbsolutePath == StatePath) return State();
            if (u.AbsolutePath == InstallPath)
            {
                Install();
                return State();
            }
            var key = KeyOf(u);
            if (key == null || (list != null && !list.ContainsKey(key)) || (list == null && J.Get(Files(ReadIndex()), key) == null)) return null;
            var range = Range(req.Headers);
            var hit = await Task.Run(() => Local(key, range));
            if (hit != null) return hit;
            Task<(Lift f, HttpResponseMessage res)> flying;
            lock (flights) flights.TryGetValue(key, out flying);
            if (flying != null)
            {
                Lift f = null;
                try { f = (await flying).f; } catch (Exception) { }
                if (f != null && f.Size != null) return Serve(f, range);
                if (f != null) try { await f.Done; } catch (Exception) { }
                return await Task.Run(() => Local(key, range));
            }
            try
            {
                if (MediaRe.IsMatch(key) && list != null && list.ContainsKey(key))
                {
                    var r = await Fly(key, req.Uri, req.Headers.ToList(), true);
                    return r.f != null ? Serve(r.f, range) : FromResponse(r.res);
                }
                return null;
            }
            catch (Exception)
            {
                return await Task.Run(() => Local(key, range));
            }
        }

        public async void Received(CoreWebView2WebResourceResponseReceivedEventArgs e)
        {
            try
            {
                if (list == null || e.Request.Method != "GET" || e.Response.StatusCode != 200 || e.Request.Headers.Contains("Range")) return;
                var u = new Uri(e.Request.Uri);
                if (!ours(u)) return;
                var key = KeyOf(u);
                if (key == null || !list.TryGetValue(key, out var fp) || J.Str(fp) == null) return;
                if (MediaRe.IsMatch(key) || await Fresh(key)) return;
                var type = e.Response.Headers.Contains("Content-Type") ? e.Response.Headers.GetHeader("Content-Type") : null;
                var s = await e.Response.GetContentAsync();
                if (s == null) return;
                var ms = new MemoryStream();
                await s.CopyToAsync(ms);
                var buf = ms.ToArray();
                var mime = Fp.MimeOf(key, type);
                if (Fp.Of(buf, key, mime) != J.Str(fp)) return;
                var file = Where(key);
                if (file == null) return;
                await Task.Run(() =>
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file));
                    var part = file + "." + Process.GetCurrentProcess().Id + ".keep";
                    File.WriteAllBytes(part, buf);
                    if (File.Exists(file)) File.Delete(file);
                    File.Move(part, file);
                });
                await Remember(key, J.O("fp", J.Str(fp), "size", (double)buf.Length, "mime", mime));
            }
            catch (Exception) { }
        }

        public async Task Load(string host)
        {
            bas = host;
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, bas + "/m/m2list.json");
                req.Headers.TryAddWithoutValidation("Cache-Control", "no-store");
                req.Headers.TryAddWithoutValidation("Pragma", "no-cache");
                var c = await cookies(req.RequestUri.AbsoluteUri);
                if (!string.IsNullOrEmpty(c)) req.Headers.TryAddWithoutValidation("Cookie", c);
                using (var res = await Http.SendAsync(mark(req)))
                {
                    if (!res.IsSuccessStatusCode) return;
                    var j = J.Parse(await res.Content.ReadAsStringAsync());
                    if (J.Get(j, "list") is Dictionary<string, object> l) list = l;
                }
            }
            catch (Exception) { }
        }
    }

    sealed class Part : Stream
    {
        readonly FileStream f;
        long left;

        public Part(string file, long from, long len)
        {
            f = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            f.Position = from;
            left = len;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (left <= 0) return 0;
            var n = f.Read(buffer, offset, (int)Math.Min(count, left));
            left -= n;
            return n;
        }

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

    sealed class Tap : Stream
    {
        readonly Lift f;
        readonly long end;
        long pos;
        FileStream fd;

        public Tap(Lift f, long start, long end)
        {
            this.f = f;
            pos = start;
            this.end = end;
        }

        FileStream Open()
        {
            for (;;)
            {
                string p;
                lock (f) p = f.Path;
                try
                {
                    return new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                }
                catch (Exception)
                {
                    lock (f)
                    {
                        if (f.Error != null) throw new IOException("lift", f.Error);
                        if (f.Path == p && f.Finished) throw new IOException("lift");
                        Monitor.Wait(f, 200);
                    }
                }
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (pos > end) return 0;
            long got;
            lock (f)
            {
                while (f.Got <= pos && f.Error == null && !f.Finished) Monitor.Wait(f);
                if (f.Error != null) throw new IOException("lift", f.Error);
                got = f.Got;
            }
            if (got <= pos) return 0;
            if (fd == null) fd = Open();
            fd.Position = pos;
            var want = (int)Math.Min(Math.Min(end + 1, got) - pos, Math.Min(count, 1 << 20));
            var n = fd.Read(buffer, offset, want);
            pos += n;
            return n;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) fd?.Dispose();
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
