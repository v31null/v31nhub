using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace Hub
{
    class Page : Form
    {
        static Task<CoreWebView2Environment> env;
        public static string Data { get; set; }

        public static Task<CoreWebView2Environment> Environment() =>
            env ?? (env = CoreWebView2Environment.CreateAsync(null, Data, new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required")));

        static readonly Icon AppIcon = LoadIcon();

        static Icon LoadIcon()
        {
            try
            {
                using (var s = Embedded.Open("icon.ico")) return new Icon(s);
            }
            catch (Exception)
            {
                return null;
            }
        }

        protected CoreWebView2Controller Ctl;
        public CoreWebView2 Web;
        readonly Color back;
        readonly TaskCompletionSource<CoreWebView2> ready = new TaskCompletionSource<CoreWebView2>();
        public Task<CoreWebView2> Ready => ready.Task;
        readonly Dictionary<string, Func<object[], Task<object>>> handlers = new Dictionary<string, Func<object[], Task<object>>>();
        readonly Dictionary<string, List<Action<object[]>>> listeners = new Dictionary<string, List<Action<object[]>>>();

        public Page(Color back)
        {
            this.back = back;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = back;
            StartPosition = FormStartPosition.Manual;
            Text = "";
            if (AppIcon != null) Icon = AppIcon;
        }

        protected void Start()
        {
            CreateHandle();
            Init();
        }

        async void Init()
        {
            try
            {
                var e = await Environment();
                if (IsDisposed) return;
                Ctl = await e.CreateCoreWebView2ControllerAsync(Handle);
                if (IsDisposed)
                {
                    Ctl.Close();
                    return;
                }
                Ctl.DefaultBackgroundColor = back;
                Ctl.Bounds = ClientRectangle;
                Ctl.IsVisible = Visible;
                Web = Ctl.CoreWebView2;
                var s = Web.Settings;
                s.AreDefaultContextMenusEnabled = false;
                s.AreDevToolsEnabled = false;
                s.IsZoomControlEnabled = false;
                s.IsPinchZoomEnabled = false;
                s.AreBrowserAcceleratorKeysEnabled = false;
                s.IsStatusBarEnabled = false;
                s.IsBuiltInErrorPageEnabled = true;
                Web.DocumentTitleChanged += (o, a) => Text = Web.DocumentTitle;
                Web.WebMessageReceived += (o, a) => Dispatch(a.WebMessageAsJson);
                ready.TrySetResult(Web);
            }
            catch (Exception x)
            {
                ready.TrySetException(x);
            }
        }

        public void Answer(string ch, Func<object[], Task<object>> fn) => handlers[ch] = fn;

        public void On(string ch, Action<object[]> fn)
        {
            if (!listeners.TryGetValue(ch, out var l)) listeners[ch] = l = new List<Action<object[]>>();
            l.Add(fn);
        }

        async void Dispatch(string json)
        {
            object m;
            try
            {
                m = J.Parse(json);
            }
            catch (Exception)
            {
                return;
            }
            var ch = J.Get(m, "ch") as string;
            var args = (J.Get(m, "args") as List<object> ?? new List<object>()).ToArray();
            if (!J.Has(m, "id"))
            {
                if (ch != null && listeners.TryGetValue(ch, out var l)) foreach (var fn in l.ToList()) fn(args);
                return;
            }
            var id = J.Get(m, "id");
            if (ch == null || !handlers.TryGetValue(ch, out var h))
            {
                Post(J.O("id", id, "err", "No handler registered for '" + ch + "'"));
                return;
            }
            try
            {
                Post(J.O("id", id, "value", await h(args) ?? Undefined.V));
            }
            catch (Exception e)
            {
                Post(J.O("id", id, "err", e.Message));
            }
        }

        void Post(object m)
        {
            if (Web == null || IsDisposed) return;
            try { Web.PostWebMessageAsJson(J.Stringify(m)); } catch (Exception) { }
        }

        public void Send(string ch, object data) => Post(J.O("ch", ch, "data", data ?? Undefined.V));

        public double Dpi => Native.Scale(Bounds);

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (Ctl != null && WindowState != FormWindowState.Minimized) Ctl.Bounds = ClientRectangle;
        }

        protected override void OnMove(EventArgs e)
        {
            base.OnMove(e);
            Ctl?.NotifyParentWindowPositionChanged();
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            try { Ctl?.MoveFocus(CoreWebView2MoveFocusReason.Programmatic); } catch (Exception) { }
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Ctl != null) Ctl.IsVisible = Visible;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            try { Ctl?.Close(); } catch (Exception) { }
            Ctl = null;
            Web = null;
        }

        public void Front()
        {
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            Native.SetForegroundWindow(Handle);
        }
    }
}
