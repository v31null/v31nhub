using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Hub;
using Microsoft.Web.WebView2.Core;

namespace M2
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            AppDomain.CurrentDomain.AssemblyResolve += Embedded.Resolve;
            App.Run(args);
        }
    }

    static class App
    {
        const string Flag = "--m2-host=";
        static readonly Regex PageRe = new Regex(@"^/musik/?$");
        static readonly Regex FrameRe = new Regex(@"^/m/serv/");
        static readonly Regex Shut = new Regex(@"(^|/)(editm|ꜷth|amqury|dmqury)\.php$", RegexOptions.IgnoreCase);

        static List<string> hosts;
        static int at;
        static Shell win;
        static Archive archive;
        static Restore restore;

        public static string UserData => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "M2");

        static Uri Parse(string s)
        {
            try
            {
                return s == null ? null : new Uri(s);
            }
            catch (Exception)
            {
                return null;
            }
        }

        static string Origin(Uri u) => u.GetLeftPart(UriPartial.Authority);

        public static bool Ours(Uri u) => u != null && (u.Scheme == "https" || u.Scheme == "http") && hosts.Contains(Origin(u));

        static string Plain(Uri u)
        {
            try
            {
                return Uri.UnescapeDataString(u.AbsolutePath);
            }
            catch (Exception)
            {
                return u.AbsolutePath;
            }
        }

        public static bool IsPage(string s)
        {
            var u = Parse(s);
            return Ours(u) && PageRe.IsMatch(u.AbsolutePath);
        }

        public static bool IsFrame(string s)
        {
            var u = Parse(s);
            return u != null && (u.OriginalString == "about:blank" || (Ours(u) && FrameRe.IsMatch(u.AbsolutePath) && !Shut.IsMatch(Plain(u))));
        }

        public static bool IsShut(Uri u) => Ours(u) && Shut.IsMatch(Plain(u));

        static HttpRequestMessage Mark(HttpRequestMessage req)
        {
            if (Ours(req.RequestUri)) req.Headers.TryAddWithoutValidation("X-V31null-M2", "1");
            return req;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Run(string[] args)
        {
            object config;
            try
            {
                config = J.Parse(Embedded.Text("config.json"));
            }
            catch (Exception)
            {
                config = J.O();
            }
            var given = args.FirstOrDefault(x => x.StartsWith(Flag, StringComparison.Ordinal));
            var all = new List<string> { given?.Substring(Flag.Length) };
            all.AddRange((J.Get(config, "hosts") as List<object> ?? new List<object>()).Select(J.Str));
            hosts = all.Select(Parse).Where(u => u != null && u.Scheme == "https").Select(Origin).Distinct().ToList();
            var one = new One("M2-desktop");
            SynchronizationContext ui = null;
            if (!one.Take(_ => ui?.Post(__ => Second(), null))) return;
            if (hosts.Count == 0) return;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            ui = new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(ui);
            Directory.CreateDirectory(UserData);
            Web2.Loader(UserData);
            Page.Data = UserData;
            restore = new Restore(UserData, Ours);
            win = new Shell();
            win.FormClosed += (s, e) => Application.ExitThread();
            Boot();
            Application.Run();
        }

        static void Second()
        {
            if (win == null || win.IsDisposed) return;
            win.Front();
        }

        static async Task<string> Cookies(string url)
        {
            if (win?.Web == null) return null;
            try
            {
                var list = await win.Web.CookieManager.GetCookiesAsync(url);
                return string.Join("; ", list.Select(c => c.Name + "=" + c.Value));
            }
            catch (Exception)
            {
                return null;
            }
        }

        static async void Boot()
        {
            var web = await win.Ready;
            archive = new Archive(UserData, Ours, Mark, Cookies);
            var env = await Page.Environment();
            try { await web.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.ServiceWorkers); } catch (Exception) { }
            await Task.WhenAny(archive.Load(hosts[at]), Task.Delay(5000));
            foreach (var h in hosts) web.AddWebResourceRequestedFilter(h + "/*", CoreWebView2WebResourceContext.All);
            web.WebResourceRequested += async (o, e) =>
            {
                if (restore.Blank(e, env)) return;
                Uri u;
                try
                {
                    u = new Uri(e.Request.Uri);
                }
                catch (Exception)
                {
                    return;
                }
                if (!Ours(u)) return;
                if (e.ResourceContext != CoreWebView2WebResourceContext.Document && IsShut(u))
                {
                    e.Response = env.CreateWebResourceResponse(null, 403, "Forbidden", "");
                    return;
                }
                e.Request.Headers.SetHeader("X-V31null-M2", "1");
                var deferral = e.GetDeferral();
                try
                {
                    var r = await archive.Handle(e.Request);
                    if (r != null) e.Response = env.CreateWebResourceResponse(r.Body, r.Status, r.Reason, r.HeaderText);
                }
                catch (Exception) { }
                finally
                {
                    deferral.Complete();
                }
            };
            web.WebResourceResponseReceived += (o, e) => archive.Received(e);
            win.Go();
            Open();
        }

        public static async void Open()
        {
            await restore.Run(hosts[at]);
            if (win != null && !win.IsDisposed && win.Web != null) win.Web.Navigate(hosts[at] + "/musik");
        }

        public static void Failed()
        {
            if (at >= hosts.Count - 1) return;
            at++;
            Open();
        }
    }

    sealed class Shell : Page
    {
        readonly string stateFile = Path.Combine(App.UserData, "window-state.json");
        readonly System.Windows.Forms.Timer keep = new System.Windows.Forms.Timer { Interval = 400 };

        public Shell() : base(Color.Black)
        {
            var b = Place.Read(stateFile);
            var ok = new[] { "x", "y", "width", "height" }.All(k => J.Finite(J.Get(b, k)));
            var dip = ok ? new Rectangle(N(J.Get(b, "x")), N(J.Get(b, "y")), N(J.Get(b, "width")), N(J.Get(b, "height"))) : Rectangle.Empty;
            if (ok && Place.Seen(dip)) Bounds = Place.Physical(dip);
            else Bounds = Place.Centered(1280, 800);
            keep.Tick += (s, e) =>
            {
                keep.Stop();
                Save();
            };
            Start();
            Show();
        }

        static int N(object o) => J.Num(o) is double d ? (int)Math.Round(d) : 0;

        void Save()
        {
            try
            {
                var b = Place.Dip(this);
                File.WriteAllText(stateFile, J.Stringify(J.O("x", b.X, "y", b.Y, "width", b.Width, "height", b.Height)));
            }
            catch (Exception) { }
        }

        public void Go()
        {
            Web.NewWindowRequested += (o, e) => e.Handled = true;
            Web.NavigationStarting += (o, e) =>
            {
                if (!App.IsPage(e.Uri)) e.Cancel = true;
            };
            Web.FrameNavigationStarting += (o, e) =>
            {
                if (!App.IsFrame(e.Uri)) e.Cancel = true;
            };
            Web.NavigationCompleted += (o, e) =>
            {
                if (e.IsSuccess || e.HttpStatusCode != 0 || e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled) return;
                App.Failed();
            };
        }

        void Persist()
        {
            if (!IsHandleCreated) return;
            keep.Stop();
            keep.Start();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Persist();
        }

        protected override void OnMove(EventArgs e)
        {
            base.OnMove(e);
            Persist();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            Save();
            base.OnFormClosing(e);
        }
    }
}
