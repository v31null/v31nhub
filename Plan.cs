using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace Hub
{
    static class Plan
    {
        static readonly byte[][] Mark = { Encoding.UTF8.GetBytes("mTerminal"), Encoding.BigEndianUnicode.GetBytes("mTerminal") };
        static readonly Regex Store = new Regex(@"^https_(.+)_0\.indexeddb\.leveldb$");
        const string File_ = "IWILLRAPEÞETERMINALSOHARDITWILLPREPAREÞEPLANSFORMENOW.json";

        const string Dump = @"(async () => {
  const b64 = buf => {
    let s = """";
    const u = new Uint8Array(buf);
    for (let i = 0; i < u.length; i += 32768) s += String.fromCharCode(...u.subarray(i, i + 32768));
    return btoa(s);
  };
  const enc = async v => {
    if (typeof v === ""bigint"") return { $bigint: String(v) };
    if (typeof v === ""number"" && !Number.isFinite(v)) return { $number: String(v) };
    if (v === undefined) return { $undefined: true };
    if (v === null || typeof v !== ""object"") return v;
    if (v instanceof Date) return { $date: v.toISOString() };
    if (v instanceof Blob) return { $blob: v.type, name: v instanceof File ? v.name : undefined, data: b64(await v.arrayBuffer()) };
    if (v instanceof ArrayBuffer) return { $bytes: ""ArrayBuffer"", data: b64(v) };
    if (ArrayBuffer.isView(v)) return { $bytes: v.constructor.name, data: b64(v.buffer.slice(v.byteOffset, v.byteOffset + v.byteLength)) };
    if (typeof FileSystemHandle !== ""undefined"" && v instanceof FileSystemHandle) return { $handle: v.kind, name: v.name };
    if (v instanceof Map) return { $map: await Promise.all([...v].map(async ([k, x]) => [await enc(k), await enc(x)])) };
    if (v instanceof Set) return { $set: await Promise.all([...v].map(enc)) };
    if (Array.isArray(v)) return Promise.all(v.map(enc));
    const o = {};
    for (const k of Object.keys(v)) o[k] = await enc(v[k]);
    return o;
  };
  const ask = r => new Promise((res, rej) => {
    r.onsuccess = () => res(r.result);
    r.onerror = () => rej(r.error);
  });
  const out = [];
  for (const d of await indexedDB.databases()) {
    const db = await ask(indexedDB.open(d.name));
    const stores = [];
    for (const name of db.objectStoreNames) {
      const s = db.transaction(name).objectStore(name);
      const indexes = [...s.indexNames].map(i => {
        const x = s.index(i);
        return { name: x.name, keyPath: x.keyPath, unique: x.unique, multiEntry: x.multiEntry };
      });
      const [keys, values] = await Promise.all([ask(s.getAllKeys()), ask(s.getAll())]);
      const records = [];
      for (let i = 0; i < keys.length; i++) records.push({ key: await enc(keys[i]), value: await enc(values[i]) });
      stores.push({ name, keyPath: s.keyPath, autoIncrement: s.autoIncrement, indexes, records });
    }
    out.push({ name: db.name, version: db.version, stores });
    db.close();
  }
  return JSON.stringify(out);
})()";

        static HashSet<string> Mirrors() => new HashSet<string>(Net.Servers("nullpunkts").Select(u =>
        {
            try
            {
                return new Uri(u).Host;
            }
            catch (Exception)
            {
                return null;
            }
        }).Where(h => h != null));

        static IEnumerable<string> Roots()
        {
            var local = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "";
            return new[] { new[] { "Google", "Chrome", "User Data" }, new[] { "Microsoft", "Edge", "User Data" }, new[] { "BraveSoftware", "Brave-Browser", "User Data" }, new[] { "Chromium", "User Data" } }
                .Select(p => Path.Combine(new[] { local }.Concat(p).ToArray()));
        }

        static bool Holds(string dir)
        {
            try
            {
                return Directory.GetFileSystemEntries(dir).Any(f =>
                {
                    var b = File.ReadAllBytes(f);
                    return Mark.Any(m => Bytes.IndexOf(b, m, 0) >= 0);
                });
            }
            catch (Exception)
            {
                return false;
            }
        }

        static Dictionary<string, string> Stores()
        {
            var found = new Dictionary<string, string>(StringComparer.Ordinal);
            var ours = Mirrors();
            foreach (var root in Roots())
            {
                string[] profiles;
                try { profiles = Directory.GetFileSystemEntries(root).Select(Path.GetFileName).ToArray(); } catch (Exception) { continue; }
                foreach (var p in profiles)
                {
                    var dir = Path.Combine(root, p, "IndexedDB");
                    string[] names;
                    try { names = Directory.GetFileSystemEntries(dir).Select(Path.GetFileName).ToArray(); } catch (Exception) { continue; }
                    foreach (var n in names)
                    {
                        var m = Store.Match(n);
                        if (m.Success && !found.ContainsKey(m.Groups[1].Value) && (ours.Contains(m.Groups[1].Value) || Vhost.Detect(m.Groups[1].Value).Host) && Holds(Path.Combine(dir, n))) found[m.Groups[1].Value] = dir;
                    }
                }
            }
            return found;
        }

        static void Copy(string from, string to)
        {
            Fs.Mkdir(to);
            foreach (var e in new DirectoryInfo(from).EnumerateFileSystemInfos())
            {
                if (e.Name == "LOCK") continue;
                var a = Path.Combine(from, e.Name);
                var b = Path.Combine(to, e.Name);
                if (e is DirectoryInfo) Copy(a, b);
                else Fs.Write(b, File.ReadAllBytes(a));
            }
        }

        static async Task<string> Read(string dir, string host)
        {
            var tmp = Path.Combine(Env.Temp, "v31n-plan-" + Path.GetRandomFileName().Replace(".", "").Substring(0, 6));
            Fs.Mkdir(tmp);
            foreach (var kind in new[] { "leveldb", "blob" })
            {
                var name = "https_" + host + "_0.indexeddb." + kind;
                if (Fs.Exists(Path.Combine(dir, name))) await Task.Run(() => Copy(Path.Combine(dir, name), Path.Combine(tmp, "EBWebView", "Default", "IndexedDB", name)));
            }
            var env = await CoreWebView2Environment.CreateAsync(null, tmp);
            var form = new Form { ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.None, Size = new Size(1, 1) };
            var handle = form.Handle;
            CoreWebView2Controller ctl = null;
            try
            {
                ctl = await env.CreateCoreWebView2ControllerAsync(handle);
                ctl.IsVisible = false;
                var web = ctl.CoreWebView2;
                web.AddWebResourceRequestedFilter("https://*", CoreWebView2WebResourceContext.All);
                web.WebResourceRequested += (s, e) => e.Response = env.CreateWebResourceResponse(new MemoryStream(Text.Utf8.GetBytes("<!doctype html>")), 200, "OK", "content-type: text/html");
                var loaded = new TaskCompletionSource<bool>();
                web.NavigationCompleted += (s, e) => loaded.TrySetResult(e.IsSuccess);
                web.Navigate("https://" + host + "/");
                if (!await loaded.Task) throw new Exception("load");
                var raw = await web.CallDevToolsProtocolMethodAsync("Runtime.evaluate", J.Stringify(J.O("expression", Dump, "awaitPromise", true, "returnByValue", true)));
                var r = J.Get(J.Parse(raw), "result");
                if (J.Get(r, "type") as string != "string") throw new Exception("dump");
                var json = (string)J.Get(r, "value");
                J.Parse(json);
                return json;
            }
            finally
            {
                ctl?.Close();
                form.Dispose();
            }
        }

        public static async Task<object> List()
        {
            if (Env.Demo != null) return Demo.PlanList();
            return await Task.Run(() => Stores().Keys.Cast<object>().ToList());
        }

        public static async Task<object> Save(string host)
        {
            var target = Path.Combine(Env.AppData, "M2");
            string databases;
            if (Env.Demo != null)
            {
                if (!(await Demo.PlanSave(host))) return J.O("ok", false);
                databases = "[]";
            }
            else
            {
                var stores = await Task.Run(Stores);
                if (!stores.TryGetValue(host, out var dir)) return J.O("ok", false);
                databases = await Read(dir, host);
            }
            Fs.Mkdir(target);
            var file = Path.Combine(target, File_);
            var tmp = file + "." + System.Diagnostics.Process.GetCurrentProcess().Id + ".tmp";
            var sb = new StringBuilder("{\"host\":");
            J.Quote(sb, host);
            sb.Append(",\"databases\":").Append(databases).Append('}');
            Fs.Write(tmp, sb.ToString());
            Fs.Rename(tmp, file);
            return J.O("ok", true);
        }
    }
}
