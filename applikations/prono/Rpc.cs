using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Hub;
using Microsoft.Win32.SafeHandles;

namespace Prono
{
    sealed class Rpc
    {
        const int OpHandshake = 0, OpFrame = 1, OpClose = 2, OpPing = 3, OpPong = 4;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode, uint maxInstances, uint outSize, uint inSize, uint timeout, IntPtr security);

        readonly Action<List<object>> onActivities;
        readonly SynchronizationContext ui;
        readonly Dictionary<object, object> activities = new Dictionary<object, object>();
        readonly List<object> order = new List<object>();
        readonly List<NamedPipeServerStream> open = new List<NamedPipeServerStream>();
        bool closed;

        Rpc(Action<List<object>> onActivities)
        {
            this.onActivities = onActivities;
            ui = SynchronizationContext.Current;
        }

        public static Rpc Start(bool captureDiscord, Action<List<object>> onActivities)
        {
            var r = new Rpc(onActivities);
            r.Bind("prono-ipc");
            if (captureDiscord) r.Bind("discord-ipc");
            return r;
        }

        static object ParseTs(object v)
        {
            if (v == null) return null;
            double s;
            if (J.Num(v) is double d) s = d;
            else if (v is string str && double.TryParse(str.Trim().Length == 0 ? "0" : str, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var p)) s = p;
            else if (v is bool b) s = b ? 1 : 0;
            else return null;
            if (double.IsNaN(s) || double.IsInfinity(s) || s <= 0) return null;
            if (s < 1e12) s = s * 1000;
            return Math.Floor(s);
        }

        static string Str(object o) => o == null ? "" : J.Text(o);

        static Dictionary<string, object> Normalize(object args, string clientId)
        {
            var a = J.Get(args, "activity");
            if (!(a is Dictionary<string, object>)) return null;
            var assets = J.Get(a, "assets") ?? J.O();
            var ts = J.Get(a, "timestamps") ?? J.O();
            var type = J.Num(J.Get(a, "type")) is double t && t == Math.Floor(t) && !double.IsInfinity(t) ? t : 0;
            var output = J.O(
                "name", Str(J.Truthy(J.Get(a, "name")) ? J.Get(a, "name") : ""),
                "type", type,
                "application_id", clientId ?? "",
                "details", Str(J.Truthy(J.Get(a, "details")) ? J.Get(a, "details") : ""),
                "state", Str(J.Truthy(J.Get(a, "state")) ? J.Get(a, "state") : ""),
                "large_text", Str(J.Truthy(J.Get(assets, "large_text")) ? J.Get(assets, "large_text") : ""),
                "small_text", Str(J.Truthy(J.Get(assets, "small_text")) ? J.Get(assets, "small_text") : ""),
                "large_image", Str(J.Truthy(J.Get(assets, "large_image")) ? J.Get(assets, "large_image") : ""),
                "start", ParseTs(J.Get(ts, "start")),
                "end", ParseTs(J.Get(ts, "end")));
            if ((string)output["details"] == "" && (string)output["state"] == "" && (string)output["name"] == "") return null;
            return output;
        }

        static byte[] Encode(int op, object payload)
        {
            var json = Text.Utf8.GetBytes(J.Stringify(payload));
            var b = new byte[8 + json.Length];
            BitConverter.GetBytes(op).CopyTo(b, 0);
            BitConverter.GetBytes(json.Length).CopyTo(b, 4);
            json.CopyTo(b, 8);
            return b;
        }

        void Emit()
        {
            var list = order.Where(k => activities.ContainsKey(k)).Select(k => activities[k]).ToList();
            ui.Post(_ => onActivities(list), null);
        }

        void Set(object conn, object activity)
        {
            lock (activities)
            {
                if (activity == null)
                {
                    activities.Remove(conn);
                    order.Remove(conn);
                }
                else
                {
                    if (!activities.ContainsKey(conn)) order.Add(conn);
                    activities[conn] = activity;
                }
                Emit();
            }
        }

        bool Drop(object conn)
        {
            lock (activities)
            {
                if (!activities.ContainsKey(conn)) return false;
                activities.Remove(conn);
                order.Remove(conn);
                Emit();
                return true;
            }
        }

        NamedPipeServerStream Make(string name, bool first)
        {
            var h = CreateNamedPipe(@"\\.\pipe\" + name, 0x3 | 0x40000000 | (first ? 0x80000u : 0), 0, 255, 65536, 65536, 0, IntPtr.Zero);
            if (h.IsInvalid) return null;
            return new NamedPipeServerStream(PipeDirection.InOut, true, false, h);
        }

        void Bind(string prefix)
        {
            for (int i = 0; i <= 9; i++)
            {
                var name = prefix + "-" + i;
                var s = Make(name, true);
                if (s == null) continue;
                Accept(name, s);
                return;
            }
        }

        async void Accept(string name, NamedPipeServerStream s)
        {
            for (;;)
            {
                lock (open) open.Add(s);
                try
                {
                    await Task.Factory.FromAsync(s.BeginWaitForConnection, s.EndWaitForConnection, null);
                }
                catch (Exception)
                {
                    lock (open) open.Remove(s);
                    s.Dispose();
                    return;
                }
                if (closed) return;
                Serve(s);
                s = Make(name, false);
                if (s == null) return;
            }
        }

        static async Task Write(Stream s, byte[] b)
        {
            await s.WriteAsync(b, 0, b.Length);
            await s.FlushAsync();
        }

        async void Serve(NamedPipeServerStream s)
        {
            var conn = new object();
            var buffer = new MemoryStream();
            string clientId = null;
            var handshaken = false;
            var chunk = new byte[65536];
            try
            {
                for (;;)
                {
                    var n = await s.ReadAsync(chunk, 0, chunk.Length);
                    if (n <= 0) break;
                    buffer.Write(chunk, 0, n);
                    for (;;)
                    {
                        var data = buffer.ToArray();
                        if (data.Length < 8) break;
                        var op = BitConverter.ToInt32(data, 0);
                        var len = BitConverter.ToInt32(data, 4);
                        if (data.Length < 8 + len) break;
                        var body = Text.Utf8.GetString(data, 8, len);
                        buffer = new MemoryStream();
                        buffer.Write(data, 8 + len, data.Length - 8 - len);
                        object msg;
                        try
                        {
                            msg = J.Parse(body);
                        }
                        catch (Exception)
                        {
                            continue;
                        }
                        if (op == OpHandshake)
                        {
                            var id = J.Get(msg, "client_id") ?? J.Get(msg, "clientId");
                            clientId = J.Truthy(id) ? J.Text(id) : null;
                            handshaken = true;
                            await Write(s, Encode(OpFrame, J.O(
                                "cmd", "DISPATCH",
                                "evt", "READY",
                                "data", J.O(
                                    "v", 1,
                                    "config", J.O("cdn_host", "cdn.discordapp.com", "api_endpoint", "//discord.com/api", "environment", "production"),
                                    "user", J.O("id", "1045800378228281345", "username", "prono", "discriminator", "0000", "global_name", "Prono", "avatar", null, "bot", false, "flags", 0, "premium_type", 0)),
                                "nonce", J.Get(msg, "nonce") ?? null)));
                        }
                        else if (op == OpPing)
                        {
                            await Write(s, Encode(OpPong, msg));
                        }
                        else if (op == OpClose)
                        {
                            s.Disconnect();
                            throw new EndOfStreamException();
                        }
                        else if (op == OpFrame)
                        {
                            if (!handshaken) continue;
                            var cmd = J.Get(msg, "cmd");
                            if (J.Str(cmd) == "SET_ACTIVITY")
                            {
                                Set(conn, Normalize(J.Get(msg, "args"), clientId));
                                await Write(s, Encode(OpFrame, J.O(
                                    "cmd", "SET_ACTIVITY",
                                    "data", J.Get(J.Get(msg, "args"), "activity") ?? null,
                                    "evt", null,
                                    "nonce", J.Get(msg, "nonce") ?? null)));
                            }
                            else if (J.Truthy(J.Get(msg, "nonce")))
                            {
                                await Write(s, Encode(OpFrame, J.O("cmd", J.Truthy(cmd) ? cmd : "DISPATCH", "data", null, "evt", null, "nonce", J.Get(msg, "nonce"))));
                            }
                        }
                    }
                }
            }
            catch (Exception) { }
            lock (open) open.Remove(s);
            try { s.Dispose(); } catch (Exception) { }
            Drop(conn);
        }

        public void Close()
        {
            closed = true;
            lock (open)
            {
                foreach (var s in open.ToList())
                {
                    try { s.Dispose(); } catch (Exception) { }
                }
                open.Clear();
            }
        }
    }
}
