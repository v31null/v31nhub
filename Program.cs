using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Hub
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
        static string[] argv;
        static string mode;
        static One one;
        static Win win, ask;
        static bool quitting, ended;
        static SynchronizationContext ui;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Run(string[] args)
        {
            argv = new[] { Process_() }.Concat(args).ToArray();
            Env.Init(argv);
            mode = Env.Demo != null || !Env.Packaged ? "run" : argv.Contains("--remove-hub") ? "remove" : Self.Portable() ? "setup" : "run";
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string id;
            using (var sha = SHA256.Create()) id = Bytes.Hex(sha.ComputeHash(Text.Utf8.GetBytes(Env.UserData.ToLowerInvariant()))).Substring(0, 16);
            one = new One("V31nullHub-" + id);
            if (mode == "run" && !Lock(id))
            {
                one.Tell(argv);
                return;
            }
            ui = new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(ui);
            Win.Empty += Quit;
            Boot(id);
            if (!ended) Application.Run();
        }

        static string Process_() => System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;

        static bool Lock(string id) => one.Take(got => ui?.Post(_ => Second(got), null));

        static void Second(string[] args)
        {
            var w = new[] { win, ask }.FirstOrDefault(x => x != null && x.Alive);
            if (w == null) return;
            if (w.Minimized) w.Restore();
            w.ShowWin();
            w.FocusWin();
            if (w != win) return;
            if (args.Contains("--uninstall")) win.Send("hub:ask-uninstall", "prono");
            else if (args.Contains("--uninstall-m2")) win.Send("hub:ask-uninstall", "m2");
        }

        static void Quit()
        {
            if (quitting) return;
            quitting = true;
            foreach (var w in Win.All.ToList()) w.CloseWin();
            foreach (var w in Win.All.ToList()) w.Destroy();
            ended = true;
            Application.ExitThread();
        }

        static Win Open(string page, int width, int height) => new Win(page, width, height);

        static async void Boot(string id)
        {
            try
            {
                await Start(id);
            }
            catch (Exception)
            {
                Quit();
            }
        }

        static async Task Start(string id)
        {
            Web.Init();
            if (mode == "remove")
            {
                await Self.Remove();
                Quit();
                return;
            }
            if (mode == "setup")
            {
                Self.Remember(argv);
                if (await Self.Setup(argv))
                {
                    Quit();
                    return;
                }
                if (!Lock(id))
                {
                    Quit();
                    return;
                }
            }

            if (Env.Demo != null) await Demo.Server;
            var probe = Net.Probe();
            var check = Env.Packaged ? Self.Check() : Task.FromResult<Fresh>(null);
            await Task.WhenAll(probe, check);
            var (live, musik, report) = probe.Result;
            if (Env.Packaged)
            {
                await Self.Tidy();
                if (await Self.Update(check.Result, argv))
                {
                    Quit();
                    return;
                }
            }

            Wv.Loader();
            await Wv.Ensure();

            Ipc.Handle("hub:min", (w, a) =>
            {
                w.Minimize();
                return Task.FromResult<object>(Undefined.V);
            });
            Ipc.Handle("hub:close", (w, a) =>
            {
                w.CloseWin();
                return Task.FromResult<object>(Undefined.V);
            });
            System.Windows.Forms.Timer drag = null;
            Ipc.On("hub:drag", (w, a) =>
            {
                drag?.Stop();
                drag?.Dispose();
                drag = null;
                if (a.Length == 0 || !J.Truthy(a[0]) || !w.Alive) return;
                var start = Cursor.Position;
                var at = w.Position;
                var t = new System.Windows.Forms.Timer { Interval = 8 };
                t.Tick += (s, e) =>
                {
                    if (!w.Alive)
                    {
                        t.Stop();
                        return;
                    }
                    var now = Cursor.Position;
                    w.Position = new Point(at.X + now.X - start.X, at.Y + now.Y - start.Y);
                };
                drag = t;
                t.Start();
            });

            var first = live != null ? null : report;
            Action<bool> answer = null;
            Ipc.Handle("hub:scan", async (w, a) =>
            {
                var r = first ?? await Net.Scan();
                first = null;
                return r;
            });
            Ipc.Handle("hub:sys", async (w, a) => await Sys.Sample());
            Ipc.Handle("hub:musik", async (w, a) => await Net.Music(a.Length > 0 ? a[0] : null));
            Ipc.Handle("hub:imgsw", async (w, a) => await Net.Bcm(a.Length > 0 ? a[0] : null, a.Length > 1 ? a[1] : null, a.Length > 2 ? a[2] : null));
            Ipc.Handle("hub:imgsw-bin", async (w, a) => await Net.Keep(Self.Origin(), a.Length > 0 ? a[0] : null));
            Ipc.Handle("hub:proceed", (w, a) =>
            {
                answer?.Invoke(true);
                return Task.FromResult<object>(Undefined.V);
            });
            Ipc.Handle("hub:quit", (w, a) =>
            {
                answer?.Invoke(false);
                return Task.FromResult<object>(Undefined.V);
            });

            Task<bool> Board()
            {
                var tcs = new TaskCompletionSource<bool>();
                answer = v =>
                {
                    answer = null;
                    tcs.TrySetResult(v);
                };
                ask = Open("nointernet.html", 900, 280);
                ask.Gone += () => answer?.Invoke(false);
                return tcs.Task;
            }

            async Task Tv()
            {
                var shot = await win.Shot();
                var b = win.Bounds;
                var d = Screen.FromRectangle(b).Bounds;
                var t = new Win("tv.html", d.Width, d.Height, at: d, taskbar: false, focusable: false, through: true, top: true, show: false);
                Task State(string s)
                {
                    var tcs = new TaskCompletionSource<bool>();
                    System.Windows.Forms.Timer cap = null;
                    Action<Win, object[]> hear = null;
                    void End()
                    {
                        cap.Stop();
                        cap.Dispose();
                        Ipc.Off("hub:tv", hear);
                        tcs.TrySetResult(true);
                    }
                    hear = (w, a) =>
                    {
                        if (w == t && a.Length > 0 && J.Str(a[0]) == s) End();
                    };
                    cap = new System.Windows.Forms.Timer { Interval = 3000 };
                    cap.Tick += (o, e) => End();
                    cap.Start();
                    Ipc.On("hub:tv", hear);
                    return tcs.Task;
                }
                var ready = State("ready");
                var done = State("done");
                await t.Ready;
                t.ShowInactive();
                var k = t.Dpi;
                t.Send("hub:tv-play", J.O("src", shot, "x", (b.X - d.X) / k, "y", (b.Y - d.Y) / k, "w", b.Width / k, "h", b.Height / k));
                await ready;
                win.HideWin();
                await done;
                t.Destroy();
            }

            var flipping = false;
            Ipc.Handle("hub:board", async (w, a) =>
            {
                if (flipping || win == null || !win.Alive || (ask != null && ask.Alive)) return Undefined.V;
                flipping = true;
                await Tv();
                var go = await Board();
                flipping = false;
                if (!go)
                {
                    Quit();
                    return Undefined.V;
                }
                if (ask != null && ask.Alive) ask.Destroy();
                ask = null;
                win.ShowWin();
                win.FocusWin();
                return Undefined.V;
            });

            if (live == null)
            {
                if (!await Board())
                {
                    Quit();
                    return;
                }
                ask.HideWin();
            }

            win = Open("", 900, 560);
            Core.Start(win, argv, new Dictionary<string, (List<string>, bool)>
            {
                { "prono", (Net.Order(live), live == null) },
                { "m2", (Net.Order(musik, "nullpunkts"), musik == null) },
                { "arc", (Net.Order(musik, "nullpunkts"), musik == null) }
            });
            win.Closing = () =>
            {
                if (quitting || !Core.Busy()) return false;
                win.HideWin();
                return true;
            };
            if (ask != null && ask.Alive) ask.Destroy();
            ask = null;
        }
    }
}
