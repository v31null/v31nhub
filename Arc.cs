using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Hub
{
    sealed class Arc : IUnit
    {
        const string PackPath = "/m/m2.v31np";
        public static readonly byte[] Magic = Encoding.ASCII.GetBytes("V31NSPACKFORMAT");
        public static readonly byte[] Pi = Bytes.FromHex("243F6A8885A308D313198A2E");
        public static readonly byte[] Phi = Bytes.FromHex("9E3779B97F4A7C15F39CC060");
        public const long Limit = 4503599627370496L;

        static List<List<byte[]>> Rows(byte[] buf)
        {
            var output = new List<List<byte[]>>();
            List<byte[]> row = null;
            var from = 0;
            for (;;)
            {
                var hit = Bytes.IndexOf(buf, Phi, from);
                var end = hit < 0 ? buf.Length : hit - 1;
                if (row != null) row.Add(Bytes.Sub(buf, from, end));
                else if (end != 0) return null;
                if (hit < 0) return output;
                if (hit < 1) return null;
                if (buf[hit - 1] == 0) output.Add(row = new List<byte[]>());
                else if (buf[hit - 1] != 1 || row == null) return null;
                from = hit + Phi.Length;
            }
        }

        static long? Num(byte[] b)
        {
            if (b.Length == 0 || b.Length > 7) return null;
            long n = 0;
            for (int i = b.Length - 1; i >= 0; i--) n = (n << 8) | b[i];
            return n < Limit ? n : (long?)null;
        }

        static async Task<uint> CrcOf(string file, uint crc)
        {
            try
            {
                using (var f = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, true))
                {
                    var buf = new byte[1 << 20];
                    int n;
                    while ((n = await f.ReadAsync(buf, 0, buf.Length)) > 0) crc = Crc32.Of(buf, 0, n, crc);
                }
                return crc;
            }
            catch (Exception)
            {
                throw new HubError("fail");
            }
        }

        static string DecodeUri(string s)
        {
            var sb = new StringBuilder();
            var utf8 = new UTF8Encoding(false, true);
            for (int i = 0; i < s.Length;)
            {
                if (s[i] != '%')
                {
                    sb.Append(s[i++]);
                    continue;
                }
                var bytes = new List<byte>();
                while (i < s.Length && s[i] == '%')
                {
                    if (i + 2 >= s.Length || !Uri.IsHexDigit(s[i + 1]) || !Uri.IsHexDigit(s[i + 2])) throw new FormatException("uri");
                    bytes.Add(Convert.ToByte(s.Substring(i + 1, 2), 16));
                    i += 3;
                }
                sb.Append(utf8.GetString(bytes.ToArray()));
            }
            return sb.ToString();
        }

        readonly Win win;
        readonly List<string> mirrors;
        readonly bool offline;
        readonly string dir, indexFile, stateFile;
        Job job;

        public Arc(Win win, List<string> mirrors, bool offline)
        {
            this.win = win;
            this.mirrors = mirrors;
            this.offline = offline;
            dir = Path.Combine(Env.AppData, "M2", "Archive");
            indexFile = Path.Combine(dir, "index.json");
            stateFile = Path.Combine(dir, "m2.v31np");
        }

        void Send(string channel, Dictionary<string, object> msg)
        {
            if (win != null && win.Alive) win.Send(channel, J.With(msg, "app", "arc"));
        }

        string Where(string key)
        {
            string[] rel;
            try
            {
                rel = DecodeUri(key).Split('/').Where(p => p.Length > 0).ToArray();
            }
            catch (Exception)
            {
                return null;
            }
            if (rel.Length == 0 || rel.Any(p => p == "." || p == "..")) return null;
            try
            {
                var file = Path.GetFullPath(Path.Combine(new[] { dir }.Concat(rel).ToArray()));
                return file.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase) ? file : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        sealed class Index
        {
            public object V;
            public bool Complete;
            public Dictionary<string, object> Files = new Dictionary<string, object>(StringComparer.Ordinal);
        }

        Index ReadIndex()
        {
            try
            {
                var j = J.Parse(Fs.ReadText(indexFile));
                if (!(j is Dictionary<string, object> d)) return new Index();
                return new Index { V = d.Get("v"), Complete = J.Truthy(d.Get("complete")), Files = d.Get("files") as Dictionary<string, object> ?? new Dictionary<string, object>(StringComparer.Ordinal) };
            }
            catch (Exception)
            {
                return new Index();
            }
        }

        Index WriteIndex(object v, bool? complete, Dictionary<string, object> files, IEnumerable<string> drop = null)
        {
            var now = ReadIndex();
            var next = new Index { V = now.V, Complete = now.Complete, Files = new Dictionary<string, object>(now.Files, StringComparer.Ordinal) };
            if (v != null) next.V = v;
            if (complete != null) next.Complete = complete.Value;
            if (files != null) foreach (var p in files) next.Files[p.Key] = p.Value;
            if (drop != null) foreach (var k in drop) next.Files.Remove(k);
            Fs.Mkdir(dir);
            var tmp = indexFile + "." + System.Diagnostics.Process.GetCurrentProcess().Id + ".tmp";
            var o = J.O();
            if (next.V != null) o["v"] = next.V;
            o["complete"] = next.Complete;
            o["files"] = next.Files;
            Fs.Write(tmp, J.Stringify(o));
            Fs.Rename(tmp, indexFile);
            return next;
        }

        static bool IsInt(object v) => J.Num(v) is double d && d == Math.Floor(d) && !double.IsInfinity(d);

        async Task<Dictionary<string, object>> List()
        {
            if (offline) return null;
            foreach (var b in mirrors)
            {
                try
                {
                    using (var res = await Web.Fetch(b + "/m/m2list.json", Web.Gate, timeout: 8000))
                    {
                        if (!res.Ok) continue;
                        var j = await res.Json() as Dictionary<string, object>;
                        if (j != null && IsInt(j.Get("v")) && j.Get("list") is Dictionary<string, object>) return j;
                    }
                }
                catch (Exception) { }
            }
            return null;
        }

        async Task<Dictionary<string, object>> FetchOne(string key, string want, Action<long> tick)
        {
            var file = Where(key);
            if (file == null) throw new HubError("fail");
            var part = file + ".part";
            Fs.Mkdir(Path.GetDirectoryName(file));
            var at = 0;
            var buf = new byte[1 << 16];
            for (;;)
            {
                await job.Gate();
                var ct = job.Fresh();
                FileStream output = null;
                try
                {
                    using (var res = await Web.Fetch(mirrors[at] + key, Web.Gate, ct: ct))
                    {
                        if (!res.Ok) throw new Exception("http");
                        var mime = Fp.MimeOf(key, res.Header("content-type"));
                        output = Fs.Out(part, false);
                        int n;
                        while ((n = await res.Read(buf)) > 0)
                        {
                            await output.WriteAsync(buf, 0, n);
                            tick(n);
                        }
                        output.Dispose();
                        output = null;
                        var got = await Fp.OfFile(part, key, mime);
                        if (got.fp != want)
                        {
                            Fs.Rm(part);
                            throw new HubError("hash");
                        }
                        Fs.Rm(file);
                        Fs.Rename(part, file);
                        return J.O("fp", got.fp, "size", (double)got.size, "mime", mime);
                    }
                }
                catch (Exception e)
                {
                    output?.Dispose();
                    if (e is HubError) throw;
                    if (job.Cancelled) throw;
                    if (job.Paused) continue;
                    if (++at >= mirrors.Count) throw new HubError("net");
                }
            }
        }

        async Task<byte[]> Header(string url, CancellationToken ct, Dictionary<string, object> list)
        {
            long most = Magic.Length + 3 * Pi.Length + list.Keys.Sum(k => 105L + 5 * Text.Utf8.GetByteCount(k));
            using (var res = await Web.Fetch(url, Web.Gate, ct: ct))
            {
                if (!res.Ok) throw new Exception("http");
                var ms = new MemoryStream();
                var chunk = new byte[1 << 16];
                int n;
                while ((n = await res.Read(chunk)) > 0)
                {
                    ms.Write(chunk, 0, n);
                    var buf = ms.GetBuffer();
                    var len = (int)ms.Length;
                    var m = Math.Min(len, Magic.Length);
                    for (int i = 0; i < m; i++) if (buf[i] != Magic[i]) throw new HubError("fail");
                    var a = Magic.Length;
                    if (len >= a + Pi.Length)
                    {
                        for (int i = 0; i < Pi.Length; i++) if (buf[a + i] != Pi[i]) throw new HubError("fail");
                    }
                    var b = len >= a + Pi.Length ? Bytes.IndexOf(buf, Pi, a + Pi.Length, len) : -1;
                    var c = b < 0 ? -1 : Bytes.IndexOf(buf, Pi, b + Pi.Length, len);
                    if (c >= 0) return Bytes.Sub(buf, 0, c + Pi.Length);
                    if (len > most) throw new HubError("fail");
                }
                throw new HubError("fail");
            }
        }

        sealed class Entry
        {
            public string Key;
            public long At, Size;
        }

        (List<Entry> files, long end)? Parse(byte[] head, long total, Dictionary<string, object> list)
        {
            var a = Magic.Length + Pi.Length;
            var b = Bytes.IndexOf(head, Pi, a);
            var c = Bytes.IndexOf(head, Pi, b + Pi.Length);
            var t1 = Rows(Bytes.Sub(head, a, b));
            var t2 = Rows(Bytes.Sub(head, b + Pi.Length, c));
            if (t1 == null || t2 == null || t1.Count != t2.Count) return null;
            var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var r in t2)
            {
                if (r.Count != 5) return null;
                var key = Text.Utf8.GetString(r[0]);
                var size = Num(r[1]);
                if (size == null || sizes.ContainsKey(key) || !Bytes.Eq(Bytes.Cat(r[2], r[3], r[4]), r[0])) return null;
                sizes[key] = size.Value;
            }
            var files = new List<Entry>();
            long next = c + Pi.Length;
            foreach (var r in t1)
            {
                if (r.Count != 2) return null;
                var key = Text.Utf8.GetString(r[0]);
                var at = Num(r[1]);
                if (at != next || !sizes.ContainsKey(key) || !list.ContainsKey(key) || Where(key) == null) return null;
                files.Add(new Entry { Key = key, At = at.Value, Size = sizes[key] });
                next = at.Value + sizes[key];
                sizes.Remove(key);
            }
            return next == total - 16 ? (files, next) : ((List<Entry>, long)?)null;
        }

        sealed class Absent : Exception
        {
        }

        async Task<bool> Pack(Dictionary<string, object> l, HashSet<string> done, Func<string, Dictionary<string, object>, Task> record, Func<List<string>, Task> drop, Action<long> bytes)
        {
            var list = (Dictionary<string, object>)l["list"];
            int at = 0, absent = 0;
            for (;;)
            {
                await job.Gate();
                var ct = job.Fresh();
                FileStream wout = null;
                try
                {
                    var url = mirrors[at] + PackPath;
                    long total;
                    byte[] foot;
                    using (var tail = await Web.Fetch(url, new Dictionary<string, string>(Web.Gate) { { "Range", "bytes=-16" } }, ct: ct))
                    {
                        if (tail.Status == 404) throw new Absent();
                        if (tail.Status != 206) throw new Exception("http");
                        var cr = tail.Header("content-range") ?? "";
                        var parts = cr.Split('/');
                        total = parts.Length > 1 && long.TryParse(parts[1], out var t) ? t : -1;
                        foot = await tail.Bytes();
                    }
                    if (total < 0 || total >= Limit || foot.Length != 16 || !Bytes.Eq(Bytes.Sub(foot, 0, Pi.Length), Pi)) throw new HubError("fail");
                    var head = await Header(url, ct, list);
                    var parsed = Parse(head, total, list);
                    if (parsed == null) throw new HubError("fail");
                    var files = parsed.Value.files;
                    var end = parsed.Value.end;
                    var state = Bytes.Cat(head, foot);
                    byte[] saved = null;
                    try { saved = File.ReadAllBytes(stateFile); } catch (Exception) { }
                    if (saved == null || !Bytes.Eq(saved, state))
                    {
                        await drop(files.Select(f => f.Key).ToList());
                        Fs.Mkdir(dir);
                        var tmp = stateFile + "." + System.Diagnostics.Process.GetCurrentProcess().Id + ".tmp";
                        Fs.Write(tmp, state);
                        Fs.Rename(tmp, stateFile);
                    }
                    var i = files.FindIndex(f => !done.Contains(f.Key));
                    if (i < 0) i = files.Count;
                    var crc = Crc32.Of(head);
                    foreach (var f in files.Take(i)) crc = await CrcOf(Where(f.Key), crc);
                    var pos = i < files.Count ? files[i].At : end;
                    if (pos < end)
                    {
                        using (var res = await Web.Fetch(url, new Dictionary<string, string>(Web.Gate) { { "Range", "bytes=" + pos + "-" + (end - 1) } }, ct: ct))
                        {
                            if (res.Status != 206 || !(res.Header("content-range") ?? "").StartsWith("bytes " + pos + "-", StringComparison.Ordinal)) throw new Exception("http");
                            async Task Close()
                            {
                                var f = files[i++];
                                if (wout == null) return;
                                var output = wout;
                                wout = null;
                                output.Dispose();
                                var file = Where(f.Key);
                                var part = file + ".part";
                                var mime = Fp.MimeOf(f.Key, "");
                                var got = await Fp.OfFile(part, f.Key, mime);
                                if (got.fp != J.Str(list.Get(f.Key)))
                                {
                                    Fs.Rm(part);
                                    return;
                                }
                                Fs.Rm(file);
                                Fs.Rename(part, file);
                                await record(f.Key, J.O("fp", got.fp, "size", (double)got.size, "mime", mime));
                            }
                            async Task Open()
                            {
                                while (i < files.Count)
                                {
                                    var f = files[i];
                                    if (!done.Contains(f.Key))
                                    {
                                        var file = Where(f.Key);
                                        Fs.Mkdir(Path.GetDirectoryName(file));
                                        wout = Fs.Out(file + ".part", false);
                                    }
                                    if (f.Size > 0) return;
                                    await Close();
                                }
                            }
                            await Open();
                            var chunk = new byte[1 << 16];
                            int n;
                            while ((n = await res.Read(chunk)) > 0)
                            {
                                crc = Crc32.Of(chunk, 0, n, crc);
                                var o = 0;
                                while (o < n)
                                {
                                    if (i >= files.Count) throw new HubError("fail");
                                    var f = files[i];
                                    var take = (int)Math.Min(n - o, f.At + f.Size - pos);
                                    if (wout != null) await wout.WriteAsync(chunk, o, take);
                                    o += take;
                                    pos += take;
                                    bytes(take);
                                    if (pos == f.At + f.Size)
                                    {
                                        await Close();
                                        await Open();
                                    }
                                }
                            }
                            if (pos != end) throw new Exception("short");
                        }
                    }
                    if (Crc32.Of(Pi, crc) != BitConverter.ToUInt32(foot, Pi.Length))
                    {
                        await drop(files.Select(f => f.Key).ToList());
                        throw new HubError("hash");
                    }
                    return true;
                }
                catch (Exception e)
                {
                    if (wout != null) wout.Dispose();
                    if (e is HubError) throw;
                    if (job.Cancelled) throw;
                    if (job.Paused) continue;
                    if (e is Absent) absent++;
                    if (++at >= mirrors.Count)
                    {
                        if (absent == mirrors.Count) return false;
                        throw new HubError("net");
                    }
                }
            }
        }

        async Task Run()
        {
            var l = await List();
            if (l == null) throw new HubError("net");
            var list = (Dictionary<string, object>)l["list"];
            var keys = list.Keys.ToList();
            var have = ReadIndex();
            var fresh = File.Exists(stateFile) || have.Files.Count == 0;
            if (!Equals(J.Num(have.V), J.Num(l["v"])) || !have.Complete) WriteIndex(l["v"], false, null);
            var tick = new Throttle();
            var todo = new List<string>();
            var keep = new Dictionary<string, object>(StringComparer.Ordinal);
            for (int i = 0; i < keys.Count; i++)
            {
                await job.Gate();
                var key = keys[i];
                var ent = have.Files.Get(key) as Dictionary<string, object>;
                var file = Where(key);
                var ok = false;
                if (file != null && ent != null && J.Str(ent.Get("fp")) == J.Str(list[key]) && Fs.Exists(file))
                {
                    try
                    {
                        ok = (await Fp.OfFile(file, key, J.Text(ent.Get("mime")))).fp == J.Str(ent.Get("fp"));
                    }
                    catch (Exception) { }
                }
                if (ok) keep[key] = ent;
                else todo.Add(key);
                var frac = (i + 1) / (double)keys.Count;
                tick.Run(() => Send("hub:progress", J.O("phase", "verify", "frac", frac, "speed", 0.0)));
            }
            var stale = have.Files.Keys.Where(k => !list.ContainsKey(k)).ToList();
            foreach (var k in stale)
            {
                var f = Where(k);
                if (f != null) try { Fs.Rm(f); } catch (Exception) { }
            }
            WriteIndex(null, null, keep, stale.Concat(todo));
            Send("hub:progress", J.O("phase", "verify", "frac", 1.0, "speed", 0.0));

            var mark = Environment.TickCount;
            long moved = 0;
            double speed = 0;
            var batch = new Dictionary<string, object>(StringComparer.Ordinal);
            var done = new HashSet<string>(keep.Keys, StringComparer.Ordinal);
            var bas = done.Count;
            void Show(int extra) => Send("hub:progress", J.O("phase", "fetch", "frac", Math.Min(1, Math.Max(0, done.Count - bas + extra) / (double)Math.Max(todo.Count, 1)), "speed", speed));
            Action<long> Bytes_(int extra) => n =>
            {
                moved += n;
                tick.Run(() =>
                {
                    var now = Environment.TickCount;
                    speed = moved / (double)Math.Max(now - mark, 1) * 1000 / 1e6;
                    mark = now;
                    moved = 0;
                    Show(extra);
                });
            };
            void Flush()
            {
                if (batch.Count == 0) return;
                WriteIndex(null, null, batch);
                batch = new Dictionary<string, object>(StringComparer.Ordinal);
            }
            if (fresh)
            {
                await Pack(l, done, (key, ent) =>
                {
                    batch[key] = ent;
                    done.Add(key);
                    if (batch.Count >= 25) Flush();
                    Show(0);
                    return Task.CompletedTask;
                }, names =>
                {
                    Flush();
                    foreach (var k in names) done.Remove(k);
                    WriteIndex(null, null, null, names);
                    return Task.CompletedTask;
                }, Bytes_(0));
            }
            foreach (var key in keys.Where(k => !done.Contains(k)).ToList())
            {
                batch[key] = await FetchOne(key, J.Str(list[key]), Bytes_(1));
                done.Add(key);
                if (batch.Count >= 25) Flush();
                Show(0);
            }
            Fs.Rm(stateFile);
            WriteIndex(l["v"], true, batch);
            Send("hub:progress", J.O("phase", "fetch", "frac", 1.0, "speed", 0.0));
        }

        public async Task<object> Info()
        {
            var l = await List();
            var idx = ReadIndex();
            var v = l != null ? l["v"] : idx.V;
            var done = idx.Complete && IsInt(idx.V) ? J.Text(idx.V) : null;
            return J.O(
                "manifest", IsInt(v) ? J.O("version", J.Text(v), "files", (double)(l != null ? ((Dictionary<string, object>)l["list"]).Count : idx.Files.Count)) : null,
                "installed", done,
                "path", dir,
                "free", await Fs.Free(dir),
                "autoUninstall", false,
                "banned", offline);
        }

        public Task<object> Start()
        {
            if (job != null) return Task.FromResult<object>(J.O("ok", true));
            if (offline) return Task.FromResult<object>(J.O("ok", false, "code", "net"));
            var j = job = new Job();
            Go(j);
            return Task.FromResult<object>(J.O("ok", true));
        }

        async void Go(Job j)
        {
            try
            {
                await Run();
                Send("hub:done", J.O());
            }
            catch (Exception e)
            {
                if (!j.Cancelled) Send("hub:error", J.O("code", e is HubError h ? h.Code : "fail"));
            }
            finally
            {
                job = null;
            }
        }

        public void Pause() => job?.Pause();
        public void Resume() => job?.Resume();
        public void Cancel() => job?.Cancel();
        public Task<object> Uninstall(object o) => Task.FromResult<object>(J.O("ok", false));
        public Task<object> Launch(object stay) => Task.FromResult<object>(false);
        public Task<object> ChoosePath() => Task.FromResult<object>(null);
    }
}
