using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace Hub
{
    static class Ipc
    {
        public static readonly Dictionary<string, Func<Win, object[], Task<object>>> Handlers = new Dictionary<string, Func<Win, object[], Task<object>>>();
        public static readonly Dictionary<string, List<Action<Win, object[]>>> Listeners = new Dictionary<string, List<Action<Win, object[]>>>();

        public static void Handle(string ch, Func<Win, object[], Task<object>> fn) => Handlers[ch] = fn;

        public static void On(string ch, Action<Win, object[]> fn)
        {
            if (!Listeners.TryGetValue(ch, out var l)) Listeners[ch] = l = new List<Action<Win, object[]>>();
            l.Add(fn);
        }

        public static void Off(string ch, Action<Win, object[]> fn)
        {
            if (Listeners.TryGetValue(ch, out var l)) l.Remove(fn);
        }

        public static async void Dispatch(Win w, string json)
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
                if (ch != null && Listeners.TryGetValue(ch, out var l)) foreach (var fn in l.ToList()) fn(w, args);
                return;
            }
            var id = J.Get(m, "id");
            if (ch == null || !Handlers.TryGetValue(ch, out var h))
            {
                w.Reply(J.O("id", id, "err", "No handler registered for '" + ch + "'"));
                return;
            }
            try
            {
                var v = await h(w, args);
                w.Reply(J.O("id", id, "value", v ?? Undefined.V));
            }
            catch (Exception e)
            {
                w.Reply(J.O("id", id, "err", e.Message));
            }
        }
    }

    static class Wv
    {
        static Task<CoreWebView2Environment> shared;

        public static void Loader() => Web2.Loader(Env.UserData);

        public static async Task Ensure()
        {
            try
            {
                CoreWebView2Environment.GetAvailableBrowserVersionString();
                return;
            }
            catch (Exception) { }
            var setup = Path.Combine(Env.Temp, "V31nullHub", "MicrosoftEdgeWebview2Setup.exe");
            try
            {
                Fs.Mkdir(Path.GetDirectoryName(setup));
                using (var res = await Web.Fetch("https://go.microsoft.com/fwlink/p/?LinkId=2124703"))
                {
                    if (!res.Ok) return;
                    Fs.Write(setup, await res.Bytes());
                }
                await Proc.Exec(setup, new[] { "/silent", "/install" });
            }
            catch (Exception) { }
        }

        public static Task<CoreWebView2Environment> Shared()
        {
            if (shared != null) return shared;
            var scheme = new CoreWebView2CustomSchemeRegistration("hub") { TreatAsSecure = true, HasAuthorityComponent = true };
            scheme.AllowedOrigins.Add("hub://app");
            var opts = new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required", null, null, false, new List<CoreWebView2CustomSchemeRegistration> { scheme });
            return shared = CoreWebView2Environment.CreateAsync(null, Env.UserData, opts);
        }

        static readonly Dictionary<string, string> Mime = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { ".html", "text/html" }, { ".js", "text/javascript" }, { ".json", "application/json" }, { ".css", "text/css" },
            { ".png", "image/png" }, { ".svg", "image/svg+xml" }, { ".ico", "image/x-icon" }, { ".jpg", "image/jpeg" },
            { ".jpeg", "image/jpeg" }, { ".gif", "image/gif" }, { ".webp", "image/webp" }, { ".woff", "font/woff" }, { ".woff2", "font/woff2" },
            { ".ttf", "font/ttf" }, { ".otf", "font/otf" }, { ".mp3", "audio/mpeg" }, { ".wav", "audio/wav" }, { ".mp4", "video/mp4" }, { ".webm", "video/webm" }
        };

        public static void Serve(CoreWebView2Environment env, CoreWebView2WebResourceRequestedEventArgs e)
        {
            string name;
            try
            {
                name = Uri.UnescapeDataString(new Uri(e.Request.Uri).AbsolutePath);
            }
            catch (Exception)
            {
                e.Response = env.CreateWebResourceResponse(null, 403, "Forbidden", "");
                return;
            }
            var rel = name == "/" ? "download.html" : name.TrimStart('/');
            var root = Path.GetFullPath(@"C:\ui") + "\\";
            string full;
            try
            {
                full = Path.GetFullPath(Path.Combine(root, rel.Replace('/', '\\')));
            }
            catch (Exception)
            {
                full = "";
            }
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                e.Response = env.CreateWebResourceResponse(null, 403, "Forbidden", "");
                return;
            }
            var res = "ui/" + full.Substring(root.Length).Replace('\\', '/');
            var s = Embedded.Open(res);
            if (s == null)
            {
                e.Response = env.CreateWebResourceResponse(null, 404, "Not Found", "");
                return;
            }
            var ms = new MemoryStream();
            using (s) s.CopyTo(ms);
            ms.Position = 0;
            Mime.TryGetValue(Path.GetExtension(res), out var type);
            e.Response = env.CreateWebResourceResponse(ms, 200, "OK", "Content-Type: " + (type ?? "application/octet-stream") + "\r\nAccess-Control-Allow-Origin: hub://app");
        }

        public const string Bridge = @"(() => {
  const w = window.chrome && window.chrome.webview;
  if (!w || window.hub) return;
  let n = 0;
  const wait = new Map();
  const ons = {};
  w.addEventListener('message', e => {
    const m = e.data;
    if (m && m.id !== undefined) {
      const p = wait.get(m.id);
      if (!p) return;
      wait.delete(m.id);
      if (m.err !== undefined) p[1](new Error(m.err));
      else p[0](m.value);
      return;
    }
    if (m && ons[m.ch]) ons[m.ch].slice().forEach(cb => cb(m.data));
  });
  const invoke = (ch, ...args) => new Promise((res, rej) => {
    const id = n++;
    wait.set(id, [res, rej]);
    w.postMessage({ id, ch, args });
  });
  const send = (ch, ...args) => w.postMessage({ ch, args });
  const listen = ch => cb => { (ons[ch] = ons[ch] || []).push(cb); };
  window.hub = Object.freeze({
    platform: 'win32',
    info: key => invoke('hub:info', key),
    start: key => invoke('hub:start', key),
    pause: key => invoke('hub:pause', key),
    resume: key => invoke('hub:resume', key),
    cancel: key => invoke('hub:cancel', key),
    uninstall: (key, o) => invoke('hub:uninstall', key, o),
    minimize: () => invoke('hub:min'),
    close: () => invoke('hub:close'),
    drag: on => send('hub:drag', on),
    launch: (key, stay) => invoke('hub:launch', key, stay),
    proceed: () => invoke('hub:proceed'),
    quit: () => invoke('hub:quit'),
    scan: () => invoke('hub:scan'),
    sys: () => invoke('hub:sys'),
    board: () => invoke('hub:board'),
    musik: file => invoke('hub:musik', file),
    imgsw: (number, key, info) => invoke('hub:imgsw', number, key, info),
    imgswBin: info => invoke('hub:imgsw-bin', info),
    tv: s => send('hub:tv', s),
    onTv: listen('hub:tv-play'),
    choosePath: key => invoke('hub:path', key),
    plan: () => invoke('hub:plan'),
    planSave: host => invoke('hub:plan-save', host),
    onProgress: listen('hub:progress'),
    onDone: listen('hub:done'),
    onError: listen('hub:error'),
    onAskUninstall: listen('hub:ask-uninstall')
  });
})();";
    }

    sealed class Win : Form
    {
        static readonly Icon AppIcon = LoadIcon();
        public static readonly List<Win> All = new List<Win>();
        public static event Action Empty;

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

        readonly string page;
        readonly bool focusable, through, autoShow;
        CoreWebView2Controller ctl;
        CoreWebView2 web;
        CoreWebView2Environment env;
        bool destroyed, killing, started, loaded, inactive;
        readonly List<string> queue = new List<string>();
        public new Func<bool> Closing;
        public event Action Gone;
        public readonly Task<bool> Ready;
        readonly TaskCompletionSource<bool> ready = new TaskCompletionSource<bool>();

        public Win(string page, int width, int height, Rectangle? at = null, bool taskbar = true, bool focusable = true, bool through = false, bool top = false, bool show = true)
        {
            this.page = page;
            this.focusable = focusable;
            this.through = through;
            autoShow = show;
            Ready = ready.Task;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = taskbar;
            MaximizeBox = false;
            TopMost = top;
            Text = "";
            if (AppIcon != null) Icon = AppIcon;
            if (at != null) Bounds = at.Value;
            else
            {
                var area = Screen.PrimaryScreen.WorkingArea;
                var scale = Native.Scale(new Point(area.Left + area.Width / 2, area.Top + area.Height / 2));
                var w = (int)Math.Round(width * scale);
                var h = (int)Math.Round(height * scale);
                Bounds = new Rectangle(area.Left + (area.Width - w) / 2, area.Top + (area.Height - h) / 2, w, h);
            }
            All.Add(this);
            CreateHandle();
            Init();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var p = base.CreateParams;
                p.Style |= Native.WS_MINIMIZEBOX | Native.WS_SYSMENU;
                p.ExStyle |= Native.WS_EX_NOREDIRECTIONBITMAP;
                if (!focusable) p.ExStyle |= Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW;
                if (through) p.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT;
                return p;
            }
        }

        protected override bool ShowWithoutActivation => inactive || !focusable;

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (through) Native.SetLayeredWindowAttributes(Handle, 0, 255, 2);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
        }

        public bool Alive => !destroyed && !IsDisposed;

        public double Dpi => Native.Scale(Bounds);

        async void Init()
        {
            try
            {
                env = await Wv.Shared();
                if (!Alive) return;
                ctl = await env.CreateCoreWebView2ControllerAsync(Handle);
                if (!Alive)
                {
                    ctl.Close();
                    return;
                }
                ctl.DefaultBackgroundColor = Color.Transparent;
                ctl.Bounds = ClientRectangle;
                web = ctl.CoreWebView2;
                var s = web.Settings;
                s.AreDefaultContextMenusEnabled = false;
                s.AreDevToolsEnabled = !Env.Packaged;
                s.IsZoomControlEnabled = false;
                s.IsPinchZoomEnabled = false;
                s.AreBrowserAcceleratorKeysEnabled = false;
                s.IsStatusBarEnabled = false;
                s.IsBuiltInErrorPageEnabled = false;
                web.AddWebResourceRequestedFilter("hub://*", CoreWebView2WebResourceContext.All);
                web.WebResourceRequested += (o, e) => Wv.Serve(env, e);
                web.NewWindowRequested += (o, e) => e.Handled = true;
                web.NavigationStarting += (o, e) =>
                {
                    if (started) e.Cancel = true;
                    started = true;
                };
                web.NavigationCompleted += Loaded;
                web.WebMessageReceived += (o, e) =>
                {
                    if (Alive) Ipc.Dispatch(this, e.WebMessageAsJson);
                };
                await web.AddScriptToExecuteOnDocumentCreatedAsync(Wv.Bridge);
                if (!Alive) return;
                web.Navigate("hub://app/" + page);
            }
            catch (Exception)
            {
                ready.TrySetResult(false);
                Destroy();
            }
        }

        async void Loaded(object o, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (loaded || !Alive) return;
            loaded = true;
            try
            {
                await web.ExecuteScriptAsync("(() => { const s = document.createElement('style'); s.textContent = 'html, body { background: transparent !important; }'; (document.head || document.documentElement).appendChild(s); })()");
            }
            catch (Exception) { }
            if (!Alive) return;
            foreach (var m in queue) web.PostWebMessageAsJson(m);
            queue.Clear();
            if (autoShow) ShowWin();
            ready.TrySetResult(true);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (ctl != null && WindowState != FormWindowState.Minimized) ctl.Bounds = ClientRectangle;
        }

        protected override void OnMove(EventArgs e)
        {
            base.OnMove(e);
            ctl?.NotifyParentWindowPositionChanged();
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            try { ctl?.MoveFocus(CoreWebView2MoveFocusReason.Programmatic); } catch (Exception) { }
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (ctl != null) ctl.IsVisible = Visible;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_DPICHANGED)
            {
                var r = (Native.Rect)Marshal.PtrToStructure(m.LParam, typeof(Native.Rect));
                Native.SetWindowPos(Handle, IntPtr.Zero, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, 0x0014);
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!killing && Closing != null && Closing())
            {
                e.Cancel = true;
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            destroyed = true;
            try { ctl?.Close(); } catch (Exception) { }
            ctl = null;
            web = null;
            ready.TrySetResult(false);
            All.Remove(this);
            Gone?.Invoke();
            if (All.Count == 0) Empty?.Invoke();
        }

        void Post(string json)
        {
            if (!Alive) return;
            if (!loaded || web == null)
            {
                queue.Add(json);
                return;
            }
            try { web.PostWebMessageAsJson(json); } catch (Exception) { }
        }

        public void Reply(Dictionary<string, object> m) => Post(J.Stringify(m));

        public void Send(string ch, object data) => Post(J.Stringify(J.O("ch", ch, "data", data ?? Undefined.V)));

        public void ShowWin()
        {
            if (!Alive) return;
            if (!Visible) Show();
            else if (WindowState == FormWindowState.Minimized) Native.ShowWindow(Handle, 9);
        }

        public void ShowInactive()
        {
            if (!Alive) return;
            inactive = true;
            try
            {
                Show();
                Native.ShowWindow(Handle, Native.SW_SHOWNOACTIVATE);
                if (TopMost) Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, 0x0013);
            }
            finally
            {
                inactive = false;
            }
        }

        public void FocusWin()
        {
            if (!Alive || !focusable) return;
            Activate();
            Native.SetForegroundWindow(Handle);
        }

        public void HideWin()
        {
            if (Alive) Hide();
        }

        public bool Minimized => Alive && WindowState == FormWindowState.Minimized;

        public void Minimize()
        {
            if (Alive) WindowState = FormWindowState.Minimized;
        }

        public void Restore()
        {
            if (Alive && WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        }

        public void CloseWin()
        {
            if (Alive) Close();
        }

        public void Destroy()
        {
            if (!Alive) return;
            killing = true;
            Close();
        }

        public Point Position
        {
            get => Location;
            set
            {
                if (Alive) Location = value;
            }
        }

        public async Task<string> Shot()
        {
            if (web == null) return "data:image/png;base64,";
            var ms = new MemoryStream();
            await web.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
            return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
        }
    }

    static class Dialogs
    {
        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        class FileOpenDialog
        {
        }

        [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IFileDialog
        {
            [PreserveSig] int Show(IntPtr parent);
            void SetFileTypes(uint n, IntPtr specs);
            void SetFileTypeIndex(uint i);
            void GetFileTypeIndex(out uint i);
            void Advise(IntPtr sink, out uint cookie);
            void Unadvise(uint cookie);
            void SetOptions(uint fos);
            void GetOptions(out uint fos);
            void SetDefaultFolder(IShellItem si);
            void SetFolder(IShellItem si);
            void GetFolder(out IShellItem si);
            void GetCurrentSelection(out IShellItem si);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
            void GetResult(out IShellItem si);
            void AddPlace(IShellItem si, int fdap);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string ext);
            void Close(int hr);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr filter);
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItem
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem si);
            void GetDisplayName(uint sigdn, [MarshalAs(UnmanagedType.LPWStr)] out string name);
            void GetAttributes(uint mask, out uint attrs);
            void Compare(IShellItem si, uint hint, out int order);
        }

        public static string PickFolder(Win owner)
        {
            IFileDialog d = null;
            try
            {
                d = (IFileDialog)new FileOpenDialog();
                d.GetOptions(out var fos);
                d.SetOptions(fos | 0x20 | 0x40 | 0x800);
                if (d.Show(owner != null && owner.Alive ? owner.Handle : IntPtr.Zero) != 0) return null;
                d.GetResult(out var item);
                item.GetDisplayName(0x80058000, out var path);
                Marshal.ReleaseComObject(item);
                return string.IsNullOrEmpty(path) ? null : path;
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                if (d != null) Marshal.ReleaseComObject(d);
            }
        }
    }
}
