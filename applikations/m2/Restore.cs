using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using Hub;
using Microsoft.Web.WebView2.Core;

namespace M2
{
    sealed class Restore
    {
        const string Mark = "m2-import";
        static readonly HashSet<string> Skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "window-state.json", "imported.json" };

        readonly string dir;
        readonly Func<Uri, bool> ours;

        public Restore(string userData, Func<Uri, bool> ours)
        {
            dir = userData;
            this.ours = ours;
        }

        string Book => Path.Combine(dir, "imported.json");

        Dictionary<string, object> Seen()
        {
            try
            {
                return J.Parse(File.ReadAllText(Book, Text.Utf8)) as Dictionary<string, object> ?? J.O();
            }
            catch (Exception)
            {
                return J.O();
            }
        }

        static double Ms(string p) => (File.GetLastWriteTimeUtc(p) - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;

        (double t, object databases)? Fresh(double after)
        {
            (double t, object databases)? best = null;
            string[] names;
            try
            {
                names = Directory.GetFiles(dir);
            }
            catch (Exception)
            {
                return null;
            }
            foreach (var p in names)
            {
                var n = Path.GetFileName(p);
                if (!n.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || Skip.Contains(n)) continue;
                try
                {
                    var t = Ms(p);
                    if (t <= after || (best != null && t <= best.Value.t)) continue;
                    var d = J.Parse(File.ReadAllText(p, Text.Utf8));
                    if (J.Get(d, "databases") is List<object> dbs) best = (t, dbs);
                }
                catch (Exception) { }
            }
            return best;
        }

        public bool Blank(CoreWebView2WebResourceRequestedEventArgs e, CoreWebView2Environment env)
        {
            Uri u;
            try
            {
                u = new Uri(e.Request.Uri);
            }
            catch (Exception)
            {
                return false;
            }
            if (!ours(u) || u.Query != "?" + Mark) return false;
            e.Response = env.CreateWebResourceResponse(new MemoryStream(Text.Utf8.GetBytes("<!doctype html>")), 200, "OK", "content-type: text/html");
            return true;
        }

        public async Task<bool> Run(string origin)
        {
            var b = Seen();
            var got = Fresh(J.Num(b.Get(origin)) ?? 0);
            if (got == null) return false;
            var env = await Page.Environment();
            var w = new Hidden();
            try
            {
                var web = await w.Ready;
                web.AddWebResourceRequestedFilter(origin + "/*", CoreWebView2WebResourceContext.All);
                web.WebResourceRequested += (o, e) => Blank(e, env);
                var loaded = new TaskCompletionSource<bool>();
                web.NavigationCompleted += (o, e) => loaded.TrySetResult(e.IsSuccess);
                web.Navigate(origin + "/musik?" + Mark);
                if (!await loaded.Task) return false;
                var expr = "(" + Embedded.Text("load.js") + ")(" + J.Stringify(got.Value.databases) + ")";
                var raw = await web.CallDevToolsProtocolMethodAsync("Runtime.evaluate", J.Stringify(J.O("expression", expr, "awaitPromise", true, "returnByValue", true)));
                if (J.Get(J.Parse(raw), "exceptionDetails") != null) return false;
                b[origin] = got.Value.t;
                File.WriteAllText(Book, J.Stringify(b), Text.Utf8);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                w.Close();
                w.Dispose();
            }
        }

        sealed class Hidden : Page
        {
            public Hidden() : base(Color.Black)
            {
                ShowInTaskbar = false;
                Size = new Size(800, 600);
                Start();
            }
        }
    }

    static class DictExt
    {
        public static object Get(this Dictionary<string, object> d, string k) => d != null && d.TryGetValue(k, out var v) ? v : null;
    }
}
