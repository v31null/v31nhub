using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Hub
{
    sealed class HubError : Exception
    {
        public readonly string Code;
        public HubError(string code) : base(code) { Code = code; }
    }

    static class Env
    {
        public static string Demo;
        public static bool Offline;
        public static bool Packaged;
        public static string Version;
        public static string Exe;

        public static void Init(string[] argv)
        {
            Exe = Process.GetCurrentProcess().MainModule.FileName;
#if DEBUG
            Packaged = false;
#else
            Packaged = true;
#endif
            var d = argv.FirstOrDefault(a => a.StartsWith("--demo=", StringComparison.Ordinal));
            if (d != null)
            {
                Demo = Path.GetFullPath(d.Substring(7)).TrimEnd('\\');
                Offline = argv.Contains("--demo-off");
                Directory.CreateDirectory(Demo);
            }
            try
            {
                Version = J.Str(J.Get(J.Parse(Embedded.Text("package.json")), "version")) ?? "0.0.0";
            }
            catch (Exception)
            {
                Version = "0.0.0";
            }
        }

        static string Under(string name) => Path.Combine(Demo, name);

        public static string AppData => Demo != null ? Under("Roaming") : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        public static string Local => Demo != null ? Under("Local") : Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? AppData;
        public static string UserData => Path.Combine(AppData, "v31hub");
        public static string Temp => Demo != null ? Under("Temp") : Path.GetTempPath().TrimEnd('\\');
        public static string Desktop => Demo != null ? Under("Desktop") : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        public static string Home => Demo != null ? Under("Home") : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        public static string Uninstall => Demo != null ? @"Software\V31nullHubDemo\Uninstall" : @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    }

    static class Fs
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool MoveFileEx(string from, string to, int flags);

        public static void Guard(string p)
        {
            if (Env.Demo == null) return;
            var f = Path.GetFullPath(p);
            if (!f.StartsWith(Env.Demo + "\\", StringComparison.OrdinalIgnoreCase)) throw new HubError("fail");
        }

        public static bool Exists(string p) => File.Exists(p) || Directory.Exists(p);

        public static void Mkdir(string p)
        {
            Guard(p);
            Directory.CreateDirectory(p);
        }

        public static void Rm(string p, bool recursive = false)
        {
            Guard(p);
            if (File.Exists(p))
            {
                File.SetAttributes(p, FileAttributes.Normal);
                File.Delete(p);
                return;
            }
            if (!Directory.Exists(p)) return;
            if (!recursive) throw new IOException("EISDIR");
            Tree(p);
        }

        static void Tree(string d)
        {
            var a = File.GetAttributes(d);
            if ((a & FileAttributes.ReparsePoint) != 0)
            {
                Directory.Delete(d);
                return;
            }
            foreach (var f in Directory.EnumerateFiles(d))
            {
                File.SetAttributes(f, FileAttributes.Normal);
                File.Delete(f);
            }
            foreach (var s in Directory.EnumerateDirectories(d)) Tree(s);
            File.SetAttributes(d, FileAttributes.Directory);
            Directory.Delete(d);
        }

        public static async Task RmRetry(string p, bool recursive, int tries, int wait)
        {
            for (int i = 0; ; i++)
            {
                try
                {
                    Rm(p, recursive);
                    return;
                }
                catch (Exception)
                {
                    if (i >= tries) throw;
                    await Task.Delay(wait);
                }
            }
        }

        public static void Rename(string a, string b)
        {
            Guard(a);
            Guard(b);
            if (!MoveFileEx(a, b, 0x1 | 0x8)) throw new IOException("rename", Marshal.GetHRForLastWin32Error());
        }

        public static void Write(string p, byte[] data)
        {
            Guard(p);
            File.WriteAllBytes(p, data);
        }

        public static void Write(string p, string text) => Write(p, new UTF8Encoding(false).GetBytes(text));

        public static void Create(string p, byte[] data)
        {
            Guard(p);
            using (var f = new FileStream(p, FileMode.CreateNew, FileAccess.Write)) f.Write(data, 0, data.Length);
        }

        public static FileStream Out(string p, bool append)
        {
            Guard(p);
            return new FileStream(p, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16, true);
        }

        public static string ReadText(string p) => new UTF8Encoding(false).GetString(File.ReadAllBytes(p));

        public static void Copy(string a, string b)
        {
            Guard(b);
            File.Copy(a, b, true);
        }

        public static string Root(string p) => Path.GetPathRoot(Path.GetFullPath(p)).ToLowerInvariant();

        public static async Task<double> Free(string dir)
        {
            var at = dir;
            while (!Exists(at) && Path.GetDirectoryName(at) != null) at = Path.GetDirectoryName(at);
            try
            {
                return await Task.Run(() => new DriveInfo(Path.GetPathRoot(Path.GetFullPath(at))).AvailableFreeSpace / 1e9);
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }

    static class Proc
    {
        public static string Quote(string a)
        {
            if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return a;
            var sb = new StringBuilder("\"");
            int slashes = 0;
            foreach (var c in a)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') sb.Append('\\', slashes * 2 + 1).Append('"');
                else sb.Append('\\', slashes).Append(c);
                slashes = 0;
            }
            sb.Append('\\', slashes * 2).Append('"');
            return sb.ToString();
        }

        public static string Line(IEnumerable<string> args) => string.Join(" ", args.Select(Quote));

        public static Task<(bool ok, string output)> Exec(string file, IEnumerable<string> args, int timeout = 0)
        {
            var tcs = new TaskCompletionSource<(bool, string)>();
            var p = new Process
            {
                StartInfo = new ProcessStartInfo(file, Line(args))
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage)
                },
                EnableRaisingEvents = true
            };
            var sb = new StringBuilder();
            p.OutputDataReceived += (s, e) => { if (e.Data != null) lock (sb) sb.Append(e.Data).Append('\n'); };
            p.ErrorDataReceived += (s, e) => { };
            p.Exited += (s, e) =>
            {
                p.WaitForExit();
                string o;
                lock (sb) o = sb.ToString();
                tcs.TrySetResult((p.ExitCode == 0, o));
            };
            try
            {
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
            }
            catch (Exception)
            {
                tcs.TrySetResult((false, ""));
            }
            if (timeout > 0)
            {
                Task.Delay(timeout).ContinueWith(_ =>
                {
                    if (tcs.Task.IsCompleted) return;
                    try { p.Kill(); } catch (Exception) { }
                    tcs.TrySetResult((false, ""));
                });
            }
            return tcs.Task;
        }

        public static async Task<bool> Run(string file, params string[] args) => (await Exec(file, args)).ok;

        public static async Task<string> Read(string file, params string[] args)
        {
            var r = await Exec(file, args);
            return r.ok ? r.output : "";
        }

        public static Process Spawn(string file, IEnumerable<string> args, string cwd = null)
        {
            var si = new ProcessStartInfo(file, Line(args)) { UseShellExecute = false };
            if (cwd != null) si.WorkingDirectory = cwd;
            return Process.Start(si);
        }
    }

    static class Ver
    {
        public static int Cmp(string a, string b)
        {
            var x = a.Split('.').Select(N).ToArray();
            var y = b.Split('.').Select(N).ToArray();
            for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
            {
                var p = i < x.Length ? x[i] : 0;
                var q = i < y.Length ? y[i] : 0;
                if (p != q) return p > q ? 1 : -1;
            }
            return 0;
        }

        static double N(string s) => s.Trim().Length == 0 ? 0 : double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && !double.IsNaN(d) ? d : 0;
    }

    sealed class Throttle
    {
        long t;
        public void Run(Action fn)
        {
            var n = Environment.TickCount;
            if (n - t >= 200)
            {
                t = n;
                fn();
            }
        }
    }

    sealed class Job
    {
        public bool Paused, Cancelled;
        public CancellationTokenSource Ctl;
        TaskCompletionSource<bool> gate;

        public async Task Gate()
        {
            while (Paused) await gate.Task;
            if (Cancelled) throw new Exception("cancelled");
        }

        public CancellationToken Fresh()
        {
            Ctl = new CancellationTokenSource();
            return Ctl.Token;
        }

        public void Pause()
        {
            if (Paused) return;
            Paused = true;
            gate = new TaskCompletionSource<bool>();
            Ctl?.Cancel();
        }

        public void Resume()
        {
            if (!Paused) return;
            Paused = false;
            gate.TrySetResult(true);
        }

        public void Cancel()
        {
            Cancelled = true;
            Paused = false;
            Ctl?.Cancel();
            gate?.TrySetResult(true);
        }
    }
}
