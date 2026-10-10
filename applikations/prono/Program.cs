using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Hub;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace Prono
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

    sealed class Target
    {
        public string Url;
        public bool Host;
    }

    static class App
    {
        static readonly HashSet<string> TrustedZrok = new HashSet<string> { "prono.share.zrok.io", "imgsw.share.zrok.io", "nullpunkts.share.zrok.io" };

        static object config;
        static string domain;
        static Uri hubHost;
        static Target target;
        static Shell win;
        static Rpc ipc;
        static Games games;
        static List<object> ipcActs = new List<object>();
        static List<object> gameActs = new List<object>();
        static int seqCounter;
        static readonly Dictionary<string, int> seqMap = new Dictionary<string, int>();
        static string lastSent = "";

        public static string UserData => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "prono-desktop");

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Run(string[] args)
        {
            try
            {
                config = J.Parse(Embedded.Text("config.json"));
            }
            catch (Exception)
            {
                config = J.O();
            }
            domain = J.Truthy(J.Get(config, "domain")) ? J.Text(J.Get(config, "domain")) : "prono.com.pr";
            hubHost = HubHost(args);
            target = Resolve();
            Protocol();
            var one = new One("Prono-desktop");
            SynchronizationContext ui = null;
            if (!one.Take(_ => ui?.Post(__ => Second(), null))) return;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            ui = new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(ui);
            Directory.CreateDirectory(UserData);
            Web2.Loader(UserData);
            Page.Data = UserData;
            win = new Shell();
            win.FormClosed += (s, e) =>
            {
                ipc?.Close();
                games?.Stop();
                Application.ExitThread();
            };
            Boot();
            Application.Run();
        }

        static Uri HubHost(string[] args)
        {
            var a = args.FirstOrDefault(x => x.StartsWith("--prono-host=", StringComparison.Ordinal));
            if (a == null) return null;
            try
            {
                var u = new Uri(a.Substring("--prono-host=".Length));
                return u.Scheme == "https" ? u : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        static string Origin(Uri u) => u.GetLeftPart(UriPartial.Authority);

        static Target Resolve()
        {
            var v = Vhost.Detect(domain);
            if (v.Host) return new Target { Url = v.HostUrl, Host = true };
            if (hubHost != null) return new Target { Url = Origin(hubHost), Host = false };
            return new Target { Url = v.PublicUrl ?? J.Str(J.Get(config, "publicUrl")), Host = false };
        }

        static void Protocol()
        {
            try
            {
                var exe = Process.GetCurrentProcess().MainModule.FileName;
                using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\prono"))
                {
                    k.SetValue("", "URL:prono");
                    k.SetValue("URL Protocol", "");
                    using (var c = k.CreateSubKey(@"shell\open\command")) c.SetValue("", "\"" + exe + "\" \"%1\"");
                }
            }
            catch (Exception) { }
        }

        static void Second()
        {
            if (win == null || win.IsDisposed) return;
            win.Front();
        }

        public static bool Allowed(string url)
        {
            try
            {
                var u = new Uri(url);
                if (u.Scheme != "https" && u.Scheme != "wss") return false;
                var h = u.Host.ToLowerInvariant();
                return h == domain || (hubHost != null && h == hubHost.Host.ToLowerInvariant()) || TrustedZrok.Contains(h) || h.EndsWith(".saturngruppe.de", StringComparison.Ordinal);
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static void OpenExternal(string url)
        {
            try
            {
                var u = new Uri(url);
                if (u.Scheme == "https" || u.Scheme == "http") Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true });
            }
            catch (Exception) { }
        }

        static string Key(object a)
        {
            var id = J.Get(a, "application_id");
            if (J.Truthy(id)) return J.Text(id);
            var n = J.Get(a, "name");
            return J.Truthy(n) ? J.Text(n) : "";
        }

        static List<object> Merge()
        {
            var ids = new HashSet<string>(ipcActs.Select(a => J.Get(a, "application_id")).Where(J.Truthy).Select(J.Text));
            var extra = gameActs.Where(g => !J.Truthy(J.Get(g, "application_id")) || !ids.Contains(J.Text(J.Get(g, "application_id"))));
            var all = ipcActs.Concat(extra).ToList();
            var present = new HashSet<string>();
            foreach (var a in all)
            {
                var k = Key(a);
                present.Add(k);
                if (!seqMap.ContainsKey(k)) seqMap[k] = seqCounter++;
            }
            foreach (var k in seqMap.Keys.ToList()) if (!present.Contains(k)) seqMap.Remove(k);
            return all.Select((a, i) => (a, i)).OrderBy(x => seqMap[Key(x.a)]).ThenBy(x => x.i).Select(x => x.a).ToList();
        }

        public static void Push()
        {
            if (win == null || win.IsDisposed) return;
            var json = J.Stringify(Merge());
            if (json == lastSent) return;
            lastSent = json;
            win.Send("prono-activity", new Raw(json));
        }

        public static void Loaded()
        {
            lastSent = "";
            Push();
        }

        async static void Boot()
        {
            await win.Ready;
            await win.Go(target);
            ipc = Rpc.Start(J.Get(config, "captureDiscord") as bool? != false, list =>
            {
                ipcActs = list;
                Push();
            });
            if (J.Get(config, "detectGames") as bool? != false)
            {
                games = Games.Start(list =>
                {
                    gameActs = list;
                    Push();
                });
            }
        }
    }

    sealed class Shell : Page
    {
        [DllImport("user32.dll")] static extern int GetSystemMetricsForDpi(int index, uint dpi);
        [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);

        [StructLayout(LayoutKind.Sequential)]
        sealed class MemoryStatus
        {
            public uint Length = (uint)Marshal.SizeOf(typeof(MemoryStatus));
            public uint Load;
            public ulong TotalPhys, AvailPhys, TotalPage, AvailPage, TotalVirtual, AvailVirtual, AvailExtended;
        }

        [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatus m);

        readonly string stateFile = Path.Combine(App.UserData, "window-state.json");
        readonly System.Windows.Forms.Timer persist = new System.Windows.Forms.Timer { Interval = 400 };
        readonly System.Windows.Forms.Timer memory = new System.Windows.Forms.Timer { Interval = 1000 };
        bool wasMax;

        public Shell() : base(Color.FromArgb(0x1a, 0x1a, 0x1e))
        {
            FormBorderStyle = FormBorderStyle.Sizable;
            var st = Place.Read(stateFile);
            var saved = new Rectangle(N(J.Get(st, "x")), N(J.Get(st, "y")), N(J.Get(st, "width")), N(J.Get(st, "height")));
            var useSaved = st != null && J.Finite(J.Get(st, "x")) && J.Finite(J.Get(st, "y")) && Place.Seen(saved);
            var dip = new Rectangle(saved.X, saved.Y, useSaved && saved.Width > 0 ? saved.Width : 1280, useSaved && saved.Height > 0 ? saved.Height : 800);
            if (useSaved) Bounds = Place.Physical(dip);
            else Bounds = Place.Centered(dip.Width, dip.Height);
            var k = Native.Scale(Bounds);
            MinimumSize = new Size((int)Math.Round(900 * k), (int)Math.Round(600 * k));
            if (J.Truthy(J.Get(st, "maximized"))) WindowState = FormWindowState.Maximized;
            wasMax = WindowState == FormWindowState.Maximized;
            persist.Tick += (s, e) =>
            {
                persist.Stop();
                Save();
            };
            memory.Tick += (s, e) => Send("prono-memory", Mem());
            memory.Start();
            Answer("prono-memory", a => Task.FromResult(Mem()));
            On("win:minimize", a => WindowState = FormWindowState.Minimized);
            On("win:toggle-maximize", a => WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized);
            On("win:close", a => Close());
            On("prono-purge", a => Purge());
            Start();
            Show();
        }

        static int N(object o) => J.Num(o) is double d ? (int)Math.Round(d) : 0;

        static object Mem()
        {
            var m = new MemoryStatus();
            GlobalMemoryStatusEx(m);
            return J.O("total", (double)(m.TotalPhys / 1024), "free", (double)(m.AvailPhys / 1024));
        }

        void Purge()
        {
            if (Web == null) return;
            try
            {
                Web.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
                Web.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
            }
            catch (Exception) { }
        }

        void Save()
        {
            try
            {
                var b = Place.Dip(this);
                File.WriteAllText(stateFile, J.Stringify(J.O("x", b.X, "y", b.Y, "width", b.Width, "height", b.Height, "maximized", WindowState == FormWindowState.Maximized)));
            }
            catch (Exception) { }
        }

        public async Task Go(Target target)
        {
            Web.Settings.IsNonClientRegionSupportEnabled = true;
            await Web.AddScriptToExecuteOnDocumentCreatedAsync(Embedded.Text("preload.js"));
            Web.NewWindowRequested += (o, e) =>
            {
                App.OpenExternal(e.Uri);
                e.Handled = true;
            };
            Web.NavigationStarting += (o, e) =>
            {
                if (e.IsRedirected || App.Allowed(e.Uri)) return;
                e.Cancel = true;
                App.OpenExternal(e.Uri);
            };
            Web.ServerCertificateErrorDetected += (o, e) =>
            {
                if (target.Host && App.Allowed(e.RequestUri)) e.Action = CoreWebView2ServerCertificateErrorAction.AlwaysAllow;
                else e.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
            };
            Web.NavigationCompleted += (o, e) =>
            {
                Send("win:maximized", WindowState == FormWindowState.Maximized);
                App.Loaded();
            };
            Ctl.AcceleratorKeyPressed += (o, e) =>
            {
                if (e.KeyEventKind != CoreWebView2KeyEventKind.KeyDown || (Control.ModifierKeys & Keys.Control) == 0 || e.VirtualKey != (uint)Keys.R) return;
                e.Handled = true;
                if ((Control.ModifierKeys & Keys.Shift) != 0) _ = Web.CallDevToolsProtocolMethodAsync("Page.reload", "{\"ignoreCache\":true}");
                else Web.Reload();
            };
            if (!string.IsNullOrEmpty(target.Url)) Web.Navigate(target.Url);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0083 && m.WParam != IntPtr.Zero)
            {
                if (WindowState != FormWindowState.Maximized)
                {
                    var dpi = GetDpiForWindow(Handle);
                    var pad = GetSystemMetricsForDpi(92, dpi);
                    var fx = GetSystemMetricsForDpi(32, dpi) + pad;
                    var fy = GetSystemMetricsForDpi(33, dpi) + pad;
                    var r = (Native.Rect)Marshal.PtrToStructure(m.LParam, typeof(Native.Rect));
                    r.Left += fx;
                    r.Right -= fx;
                    r.Bottom -= fy;
                    Marshal.StructureToPtr(r, m.LParam, false);
                }
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            var max = WindowState == FormWindowState.Maximized;
            if (max != wasMax)
            {
                wasMax = max;
                Send("win:maximized", max);
            }
            Persist();
        }

        protected override void OnMove(EventArgs e)
        {
            base.OnMove(e);
            Persist();
        }

        void Persist()
        {
            if (!IsHandleCreated) return;
            persist.Stop();
            persist.Start();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            Save();
            base.OnFormClosing(e);
        }
    }
}
