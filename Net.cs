using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Hub
{
    sealed class Host
    {
        public string Url, Role, Exe;
    }

    static class Net
    {
        public const string Agent = "V31null Hub";
        public const int Wait = 4000;
        static readonly Regex Bin = new Regex(@"^\d{6}\.bin$");
        static readonly Dictionary<string, int> Rank = new Dictionary<string, int> { { "primary", 0 }, { "backup", 1 } };

        static object List()
        {
            try
            {
                return J.Parse(Embedded.Text("net.json"));
            }
            catch (Exception)
            {
                return J.O();
            }
        }

        static bool Web(object u) => u is string s && Regex.IsMatch(s, @"^https:\/\/[^/]+", RegexOptions.IgnoreCase);

        public static List<(string name, List<Host> hosts)> Services()
        {
            var r = new List<(string, List<Host>)>();
            if (!(J.Get(List(), "services") is Dictionary<string, object> s)) return r;
            foreach (var p in s)
            {
                var all = (p.Value as List<object> ?? new List<object>())
                    .Select((h, i) => (h, i))
                    .Where(x => J.Get(x.h, "url") is string u && Regex.IsMatch(u, @"^https?:\/\/[^/]+", RegexOptions.IgnoreCase) && J.Get(x.h, "role") is string role && Rank.ContainsKey(role))
                    .Select(x => (url: Regex.Replace((string)J.Get(x.h, "url"), @"\/+$", ""), role: (string)J.Get(x.h, "role"), x.i))
                    .OrderBy(x => Rank[x.role]).ThenBy(x => x.i)
                    .Select(x => new Host { Url = x.url, Role = x.role })
                    .ToList();
                r.Add((p.Key, all));
            }
            return r;
        }

        public static List<Host> Hubs()
        {
            var l = J.Get(List(), "hub") as List<object> ?? new List<object>();
            return l.Select((h, i) => (h, i))
                .Where(x => x.h != null && Web(J.Get(x.h, "url")) && Web(J.Get(x.h, "exe")) && J.Get(x.h, "role") is string role && Rank.ContainsKey(role))
                .Select(x => (url: (string)J.Get(x.h, "url"), exe: (string)J.Get(x.h, "exe"), role: (string)J.Get(x.h, "role"), x.i))
                .OrderBy(x => Rank[x.role]).ThenBy(x => x.i)
                .Select(x => new Host { Url = x.url, Exe = x.exe, Role = x.role })
                .ToList();
        }

        public static List<string> Servers(string name = "prono")
        {
            var s = Services().FirstOrDefault(x => x.name == name);
            return s.hosts == null ? new List<string>() : s.hosts.Select(h => h.Url).Distinct().ToList();
        }

        public static bool Needs(string item) => (J.Get(List(), "online") as List<object> ?? new List<object>()).Contains(item);

        static string Cause(Exception e)
        {
            var f = e as FetchError;
            var code = f?.Code ?? "";
            if (f != null && (f.Name == "TimeoutError" || f.Name == "AbortError")) return "timeout";
            if (Regex.IsMatch(code, "ENOTFOUND|EAI_AGAIN|EAI_NONAME|EAI_FAIL")) return "dns";
            if (Regex.IsMatch(code, "ECONNREFUSED")) return "refused";
            if (Regex.IsMatch(code, "ETIMEDOUT|UND_ERR_CONNECT_TIMEOUT|UND_ERR_HEADERS_TIMEOUT|UND_ERR_BODY_TIMEOUT")) return "timeout";
            if (Regex.IsMatch(code, "CERT|TLS|SSL|SELF_SIGNED|UNABLE_TO_VERIFY|ERR_SSL")) return "tls";
            return "unreachable";
        }

        static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        public static async Task<Dictionary<string, object>> Get(string url)
        {
            var t = Now;
            try
            {
                using (var res = await Hub.Web.Fetch(url, Hub.Web.Gate, timeout: Wait))
                {
                    if (!res.Ok) return J.O("status", "http", "code", (double)res.Status, "ms", (double)(Now - t), "body", null);
                    try
                    {
                        return J.O("status", "online", "ms", (double)(Now - t), "body", await res.Json());
                    }
                    catch (Exception)
                    {
                        return J.O("status", "bad", "ms", (double)(Now - t), "body", null);
                    }
                }
            }
            catch (Exception e)
            {
                return J.O("status", Cause(e), "ms", (double)(Now - t), "body", null);
            }
        }

        static bool Socketed(string name) => (J.Get(List(), "socket") as List<object> ?? new List<object>()).Contains(name);

        static async Task<double?> Knock(string url)
        {
            var t = Stopwatch.StartNew();
            try
            {
                using (await Hub.Web.Fetch(url, Hub.Web.Gate, "HEAD", timeout: Wait)) { }
                return Math.Round(t.Elapsed.TotalMilliseconds);
            }
            catch (Exception)
            {
                return null;
            }
        }

        static async Task<double?> Ping(string url)
        {
            var a = await Knock(url);
            var b = await Knock(url);
            var got = new[] { a, b }.Where(v => v != null).Select(v => v.Value).ToList();
            return got.Count > 0 ? got.Min() : (double?)null;
        }

        sealed class Sock
        {
            public ClientWebSocket Ws;
            public readonly Dictionary<int, Action<bool>> Acks = new Dictionary<int, Action<bool>>();
            public int Id;
            public Task<Sock> Ready;
        }

        static readonly Dictionary<string, Sock> Sockets = new Dictionary<string, Sock>();

        static void Drop(string b, Sock c)
        {
            if (Sockets.TryGetValue(b, out var have) && have == c) Sockets.Remove(b);
            try { c.Ws?.Abort(); } catch (Exception) { }
            foreach (var done in c.Acks.Values.ToList()) done(false);
            c.Acks.Clear();
        }

        static Task<Sock> Link(string b)
        {
            if (Sockets.TryGetValue(b, out var have)) return have.Ready;
            var c = new Sock();
            Sockets[b] = c;
            var tcs = new TaskCompletionSource<Sock>();
            c.Ready = tcs.Task;
            Open(b, c, tcs);
            return c.Ready;
        }

        static async void Open(string b, Sock c, TaskCompletionSource<Sock> tcs)
        {
            var timer = new CancellationTokenSource();
            var settled = false;
            Action<Sock> res = v =>
            {
                if (settled) return;
                settled = true;
                tcs.TrySetResult(v);
            };
            _ = Task.Delay(Wait, timer.Token).ContinueWith(t =>
            {
                if (t.IsCanceled) return;
                Drop(b, c);
                res(null);
            }, TaskScheduler.FromCurrentSynchronizationContext());
            try
            {
                c.Ws = new ClientWebSocket();
                c.Ws.Options.SetRequestHeader("Origin", b);
                await c.Ws.ConnectAsync(new Uri(Regex.Replace(b, "^http", "ws", RegexOptions.IgnoreCase) + "/socket.io/?EIO=4&transport=websocket"), CancellationToken.None);
            }
            catch (Exception)
            {
                timer.Cancel();
                Drop(b, c);
                res(null);
                return;
            }
            var buf = new byte[1 << 14];
            try
            {
                for (;;)
                {
                    var sb = new StringBuilder();
                    WebSocketReceiveResult r;
                    do
                    {
                        r = await c.Ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None);
                        if (r.MessageType == WebSocketMessageType.Close) throw new Exception("close");
                        sb.Append(Text.Utf8.GetString(buf, 0, r.Count));
                    } while (!r.EndOfMessage);
                    var d = sb.ToString();
                    if (d == "2")
                    {
                        Send(c, "3");
                        continue;
                    }
                    if (d.StartsWith("40"))
                    {
                        timer.Cancel();
                        res(c);
                        continue;
                    }
                    if (d.StartsWith("43"))
                    {
                        var m = Regex.Match(d, @"^43(\d+)");
                        if (m.Success && c.Acks.TryGetValue(int.Parse(m.Groups[1].Value), out var done)) done(true);
                        continue;
                    }
                    if (d.StartsWith("41") || d.StartsWith("44"))
                    {
                        timer.Cancel();
                        Drop(b, c);
                        res(null);
                        return;
                    }
                    if (d.StartsWith("0")) Send(c, "40");
                }
            }
            catch (Exception)
            {
                timer.Cancel();
                Drop(b, c);
                res(null);
            }
        }

        static async void Send(Sock c, string s)
        {
            try
            {
                await c.Ws.SendAsync(new ArraySegment<byte>(Text.Utf8.GetBytes(s)), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch (Exception) { }
        }

        static async Task<double?> Rtt(string b)
        {
            if (Env.Demo != null) return await DemoNet.Rtt(b);
            var c = await Link(b);
            if (c == null) return null;
            var id = c.Id++;
            var t = Stopwatch.StartNew();
            var tcs = new TaskCompletionSource<double?>();
            var timer = new CancellationTokenSource();
            _ = Task.Delay(Wait, timer.Token).ContinueWith(x =>
            {
                if (x.IsCanceled) return;
                c.Acks.Remove(id);
                Drop(b, c);
                tcs.TrySetResult(null);
            }, TaskScheduler.FromCurrentSynchronizationContext());
            c.Acks[id] = ok =>
            {
                timer.Cancel();
                c.Acks.Remove(id);
                tcs.TrySetResult(ok ? Math.Round(t.Elapsed.TotalMilliseconds) : (double?)null);
            };
            try
            {
                await c.Ws.SendAsync(new ArraySegment<byte>(Text.Utf8.GetBytes("42" + id + "[\"rtt\"]")), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch (Exception)
            {
                if (c.Acks.TryGetValue(id, out var f)) f(false);
            }
            return await tcs.Task;
        }

        static List<object> Adapters()
        {
            var r = new List<object>();
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.OperationalStatus != OperationalStatus.Up || n.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var a in n.GetIPProperties().UnicastAddresses)
                {
                    if (a.Address.AddressFamily != AddressFamily.InterNetwork || System.Net.IPAddress.IsLoopback(a.Address)) continue;
                    r.Add(J.O("name", n.Name, "address", a.Address.ToString()));
                }
            }
            return r;
        }

        sealed class Row
        {
            public string Url, HostName, Role;
            public Dictionary<string, object> Manifest;
            public double? Ms;
        }

        static async Task<(bool pc, List<object> adapters, List<(string name, List<Row> hosts)> services)> ScanRaw()
        {
            var all = Services();
            var rows = await Task.WhenAll(all.Select(async s =>
            {
                var hosts = await Task.WhenAll(s.hosts.Select(async h =>
                {
                    var at = h.Url + "/app/manifest.json";
                    var live = Socketed(s.name);
                    var mt = Get(at);
                    var wt = live ? Rtt(h.Url) : Task.FromResult<double?>(null);
                    await Task.WhenAll(mt, wt);
                    var manifest = mt.Result;
                    var ms = live ? wt.Result : (string)manifest["status"] == "online" ? await Ping(at) : null;
                    return new Row { Url = h.Url, HostName = new Uri(h.Url).Authority, Role = h.Role, Manifest = manifest, Ms = ms };
                }));
                return (s.name, hosts.ToList());
            }));
            var pc = NetworkInterface.GetIsNetworkAvailable();
            return (pc, Adapters(), rows.ToList());
        }

        static Dictionary<string, object> Report((bool pc, List<object> adapters, List<(string name, List<Row> hosts)> services) r) =>
            J.O("pc", r.pc, "adapters", r.adapters, "services", r.services.Select(s => (object)J.O("name", s.name, "hosts", s.hosts.Select(h => (object)J.O(
                "host", h.HostName,
                "role", h.Role,
                "status", h.Manifest["status"],
                "code", J.Get(h.Manifest, "code") is double d && d != 0 ? (object)d : null,
                "ms", h.Ms.HasValue ? (object)h.Ms.Value : null)).ToList())).ToList());

        public static async Task<Dictionary<string, object>> Scan() => Report(await ScanRaw());

        public static async Task<(string live, string musik, Dictionary<string, object> report)> Probe()
        {
            var r = await ScanRaw();
            string Up(string name)
            {
                var s = r.services.FirstOrDefault(x => x.name == name);
                var hit = s.hosts?.FirstOrDefault(h => (string)h.Manifest["status"] == "online");
                return hit?.Url;
            }
            return (Up("prono"), Up("nullpunkts"), Report(r));
        }

        public static List<string> Order(string first, string name = "prono")
        {
            var l = new List<string>();
            if (!string.IsNullOrEmpty(first)) l.Add(first);
            l.AddRange(Servers(name));
            return l.Distinct().ToList();
        }

        static readonly string[] Musik = { "nd-category-positions.json", "nd-song-durations.json", "m2list.json", "nd-song-categories.json", "nd-category-routes.json" };

        public static async Task<object> Music(object file)
        {
            if (!(file is string f) || !Musik.Contains(f)) return null;
            foreach (var b in Servers("nullpunkts"))
            {
                var r = await Get(b + "/m/" + f);
                if ((string)r["status"] == "online") return r["body"];
            }
            return null;
        }

        static bool IsObject(object o) => o is Dictionary<string, object> || o is List<object>;

        static async Task<Res> Imgsw(string url, string body) => await Hub.Web.Fetch(url, new Dictionary<string, string>(Hub.Web.Gate) { { "Content-Type", "application/json" } }, "POST", body, timeout: Wait, via: Hub.Web.Jar(Agent));

        static byte[] B64Url(string s)
        {
            var t = s.Replace('-', '+').Replace('_', '/');
            while (t.Length % 4 != 0) t += "=";
            return Convert.FromBase64String(t);
        }

        public static async Task<object> Keep(string dir, object info)
        {
            if (string.IsNullOrEmpty(dir) || !IsObject(info)) return J.O("state", "none");
            try
            {
                if (Directory.EnumerateFileSystemEntries(dir).Any(f => Bin.IsMatch(Path.GetFileName(f)))) return J.O("state", "have");
            }
            catch (Exception)
            {
                return J.O("state", "none");
            }
            foreach (var b in Servers("imgsw"))
            {
                object d;
                try
                {
                    using (var res = await Imgsw(b + "/BCM.php?action=request_number", J.Stringify(J.O("clientInfo", info))))
                    {
                        if (!res.Ok) continue;
                        d = await res.Json();
                    }
                    Hub.Web.SaveJar();
                }
                catch (Exception)
                {
                    continue;
                }
                if (d == null || !J.Truthy(J.Get(d, "success"))) return J.O("state", "refused");
                var key = J.Get(d, "key") as string;
                var raw = key != null && Regex.IsMatch(key, "^[A-Za-z0-9_-]{43}$") ? B64Url(key) : null;
                var number = J.Text(J.Get(d, "number"));
                if (!Regex.IsMatch(number, @"^\d{6}$") || raw == null || raw.Length != 32) return J.O("state", "invalid");
                try
                {
                    Fs.Create(Path.Combine(dir, number + ".bin"), raw);
                    return J.O("state", "saved");
                }
                catch (Exception)
                {
                    return J.O("state", "unsaved");
                }
            }
            return J.O("state", "off");
        }

        public static async Task<object> Bcm(object number, object key, object info)
        {
            var n = number == null ? "undefined" : J.Text(number);
            var k = key == null ? "undefined" : J.Text(key);
            if (!Regex.IsMatch(n, @"^\d{6}$") || !Regex.IsMatch(k, "^[A-Za-z0-9_-]{43}$") || !IsObject(info)) return J.O("state", "invalid");
            foreach (var b in Servers("imgsw"))
            {
                try
                {
                    using (var res = await Imgsw(b + "/BCM.php?action=verify", J.Stringify(J.O("number", number, "key", key, "clientInfo", info))))
                    {
                        Hub.Web.SaveJar();
                        if (res.Status == 429) return J.O("state", "busy");
                        if (!res.Ok) continue;
                        var d = await res.Json();
                        return J.O("state", J.Truthy(J.Get(d, "success")) ? "valid" : (J.Truthy(J.Get(d, "state")) ? J.Get(d, "state") : "invalid"));
                    }
                }
                catch (Exception) { }
            }
            return J.O("state", "off");
        }
    }
}
