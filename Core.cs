using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Hub
{
    interface IUnit
    {
        Task<object> Info();
        Task<object> Start();
        void Pause();
        void Resume();
        void Cancel();
        Task<object> Uninstall(object o);
        Task<object> Launch(object stay);
        Task<object> ChoosePath();
    }

    sealed class Spec
    {
        public string Name, Exe, Data, Temp, State, Root, Flag, Uninstall;
        public bool Legacy;
    }

    static class Reg
    {
        static RegistryKey Base(string hive) => RegistryKey.OpenBaseKey(hive == "HKLM" ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Registry64);

        static (string hive, string path) Split(string full)
        {
            var i = full.IndexOf('\\');
            return (full.Substring(0, i), full.Substring(i + 1));
        }

        static string Show(object v, RegistryValueKind k)
        {
            switch (k)
            {
                case RegistryValueKind.DWord: return "0x" + Convert.ToUInt32((int)v).ToString("x");
                case RegistryValueKind.QWord: return "0x" + Convert.ToUInt64((long)v).ToString("x");
                case RegistryValueKind.MultiString: return string.Join("\\0", (string[])v);
                case RegistryValueKind.Binary: return Bytes.Hex((byte[])v).ToUpperInvariant();
                default: return Convert.ToString(v);
            }
        }

        public static Dictionary<string, string> Values(string full)
        {
            var r = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                var (hive, path) = Split(full);
                using (var b = Base(hive))
                using (var k = b.OpenSubKey(path))
                {
                    if (k == null) return r;
                    foreach (var n in k.GetValueNames())
                    {
                        if (n.Length == 0) continue;
                        r[n] = Show(k.GetValue(n, null, RegistryValueOptions.DoNotExpandEnvironmentNames), k.GetValueKind(n)).Trim();
                    }
                }
            }
            catch (Exception) { }
            return r;
        }

        public static string Value(string full, string name)
        {
            var v = Values(full);
            return v.TryGetValue(name, out var s) ? s : null;
        }

        public static bool Add(string full, string name, string type, object value)
        {
            try
            {
                var (hive, path) = Split(full);
                using (var b = Base(hive))
                using (var k = b.CreateSubKey(path))
                {
                    if (type == "REG_DWORD") k.SetValue(name, unchecked((int)Convert.ToUInt32(Convert.ToDouble(value))), RegistryValueKind.DWord);
                    else k.SetValue(name, Convert.ToString(value), RegistryValueKind.String);
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static bool Delete(string full)
        {
            try
            {
                var (hive, path) = Split(full);
                using (var b = Base(hive)) b.DeleteSubKeyTree(path, true);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static List<string> Find(string full, string text)
        {
            var r = new List<string>();
            try
            {
                var (hive, path) = Split(full);
                using (var b = Base(hive))
                using (var k = b.OpenSubKey(path)) if (k != null) Walk(k, full, text, r);
            }
            catch (Exception) { }
            return r;
        }

        static void Walk(RegistryKey k, string name, string text, List<string> r)
        {
            foreach (var s in k.GetSubKeyNames())
            {
                try
                {
                    using (var c = k.OpenSubKey(s))
                    {
                        if (c == null) continue;
                        var full = name + "\\" + s;
                        if (c.GetValueNames().Any(n => Show(c.GetValue(n, null, RegistryValueOptions.DoNotExpandEnvironmentNames), c.GetValueKind(n)).IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)) r.Add("HKEY_" + full);
                        Walk(c, full, text, r);
                    }
                }
                catch (Exception) { }
            }
        }
    }

    static class Lnk
    {
        public static bool Write(string file, string target, string cwd, string icon, int index)
        {
            try
            {
                Fs.Guard(file);
                dynamic sh = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
                try
                {
                    dynamic sc = sh.CreateShortcut(file);
                    sc.TargetPath = target;
                    sc.WorkingDirectory = cwd;
                    sc.IconLocation = icon + "," + index;
                    sc.Save();
                    return true;
                }
                finally
                {
                    System.Runtime.InteropServices.Marshal.FinalReleaseComObject(sh);
                }
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    sealed class Unit : IUnit
    {
        public static readonly HashSet<Process> Kids = new HashSet<Process>();

        static readonly string Root = Env.Uninstall;
        static readonly string Manifest = "manifest.json";
        static readonly string[] Hives =
        {
            @"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall",
            @"HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall",
            @"HKLM\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
        };

        public static readonly Dictionary<string, Spec> Specs = new Dictionary<string, Spec>
        {
            { "prono", new Spec { Name = "Prono", Exe = "Prono.exe", Data = "prono-desktop", Temp = "PronoHub", State = "state.json", Root = "/app", Flag = "--prono-host=", Uninstall = "--uninstall", Legacy = true } },
            { "m2", new Spec { Name = "M2", Exe = "M2.exe", Data = "M2", Temp = "M2Hub", State = "state-m2.json", Root = "/m/app", Flag = "--m2-host=", Uninstall = "--uninstall-m2", Legacy = false } }
        };

        readonly string key;
        readonly Spec spec;
        readonly Win win;
        readonly List<string> mirrors;
        readonly bool offline;
        readonly string[] argv;
        readonly string reg;
        readonly bool banned;
        List<string> bases;
        readonly string stateFile, tempDir, startMenu, desktop, data, defaultDir;
        Job job;

        public Unit(string key, Win win, List<string> mirrors, bool offline, string[] argv)
        {
            this.key = key;
            spec = Specs[key];
            this.win = win;
            this.mirrors = mirrors;
            this.offline = offline;
            this.argv = argv;
            reg = @"HKCU\" + Root + "\\" + spec.Name;
            banned = offline && Net.Needs(key);
            bases = mirrors;
            stateFile = Path.Combine(Env.UserData, spec.State);
            tempDir = Path.Combine(Env.Temp, spec.Temp);
            startMenu = Path.Combine(Env.AppData, "Microsoft", "Windows", "Start Menu", "Programs", spec.Name + ".lnk");
            desktop = Path.Combine(Env.Desktop, spec.Name + ".lnk");
            data = Path.Combine(Env.AppData, spec.Data);
            defaultDir = Path.Combine(Env.Local, "Programs", spec.Name);
        }

        Dictionary<string, object> ReadState()
        {
            try
            {
                return J.Parse(Fs.ReadText(stateFile)) as Dictionary<string, object> ?? J.O();
            }
            catch (Exception)
            {
                return J.O();
            }
        }

        void WriteState(Dictionary<string, object> s)
        {
            Fs.Mkdir(Path.GetDirectoryName(stateFile));
            Fs.Write(stateFile, J.Stringify(s));
        }

        async Task<(string version, string path)?> Own()
        {
            var v = await Task.Run(() => Reg.Values(reg));
            v.TryGetValue("DisplayVersion", out var dv);
            v.TryGetValue("InstallLocation", out var il);
            if (string.IsNullOrEmpty(dv) || string.IsNullOrEmpty(il) || !Fs.Exists(Path.Combine(il, spec.Exe))) return null;
            return (dv, il);
        }

        sealed class Old
        {
            public string Version, Exe, Args;
        }

        async Task<Old> Legacy()
        {
            if (!spec.Legacy) return null;
            var found = new List<Old>();
            var exact = new Regex("\\\\Uninstall\\\\" + spec.Name + "$", RegexOptions.IgnoreCase);
            var named = new Regex("^" + spec.Name + "( |$)");
            var hives = Env.Demo != null ? new[] { @"HKCU\" + Root } : Hives;
            foreach (var hive in hives)
            {
                var keys = await Task.Run(() => Reg.Find(hive, spec.Name));
                foreach (var k in keys)
                {
                    if (exact.IsMatch(k)) continue;
                    var v = await Task.Run(() => Reg.Values(k.Substring(5)));
                    v.TryGetValue("DisplayName", out var dn);
                    v.TryGetValue("DisplayVersion", out var dv);
                    if (!named.IsMatch(dn ?? "") || !Regex.IsMatch(dv ?? "", @"^\d+(\.\d+)+$")) continue;
                    v.TryGetValue("QuietUninstallString", out var q);
                    v.TryGetValue("UninstallString", out var u);
                    var quiet = !string.IsNullOrEmpty(q) ? q : !string.IsNullOrEmpty(u) ? u + " /S" : "";
                    var m = Regex.Match(quiet, "^\"([^\"]+)\"\\s*(.*)$");
                    if (!m.Success) continue;
                    found.Add(new Old { Version = dv, Exe = m.Groups[1].Value, Args = m.Groups[2].Value });
                }
            }
            return found.OrderByDescending(x => x.Version, Comparer<string>.Create(Ver.Cmp)).FirstOrDefault();
        }

        async Task<bool> Running()
        {
            if (Env.Demo != null) return Demo.Mine(spec.Exe).Count > 0;
            var img = new Regex(Regex.Escape(spec.Exe), RegexOptions.IgnoreCase);
            return img.IsMatch(await Proc.Read("tasklist.exe", "/FI", "IMAGENAME eq " + spec.Exe, "/NH"));
        }

        public async Task<bool> Shut()
        {
            if (!await Running()) return true;
            foreach (var force in new[] { false, true })
            {
                if (Env.Demo != null) Demo.Stop(spec.Exe, force);
                else
                {
                    var a = new List<string> { "/IM", spec.Exe, "/T" };
                    if (force) a.Add("/F");
                    await Proc.Read("taskkill.exe", a.ToArray());
                }
                for (int i = 0; i < 12; i++)
                {
                    if (!await Running())
                    {
                        await Task.Delay(400);
                        return true;
                    }
                    await Task.Delay(250);
                }
            }
            return false;
        }

        void Send(string channel, Dictionary<string, object> msg)
        {
            if (win != null && win.Alive) win.Send(channel, J.With(msg, "app", key));
        }

        async Task<string> Target()
        {
            var o = await Own();
            return o?.path ?? J.Str(ReadState().Get("path")) ?? defaultDir;
        }

        async Task Recover(string dir)
        {
            var old = dir + ".old";
            if (!Fs.Exists(dir) && Fs.Exists(old))
            {
                try { Fs.Rename(old, dir); } catch (Exception) { }
            }
            await Task.Run(() =>
            {
                try { Fs.Rm(dir + ".new", true); } catch (Exception) { }
                try { Fs.Rm(old, true); } catch (Exception) { }
            });
        }

        static bool Valid(object m) =>
            m is Dictionary<string, object> &&
            J.Get(m, "version") is string v && Regex.IsMatch(v, @"^\d+(\.\d+){1,3}$") &&
            J.Get(m, "date") is string d && Regex.IsMatch(d, @"^\d{8}$") &&
            J.Get(m, "file") is string f && Regex.IsMatch(f, @"^[A-Za-z0-9._-]+\.exe$") &&
            J.Get(m, "sha256") is string h && Regex.IsMatch(h, "^[0-9a-f]{64}$", RegexOptions.IgnoreCase) &&
            new[] { "bytes", "unpacked", "files" }.All(n => J.Finite(J.Get(m, n)) && J.Num(J.Get(m, n)) > 0);

        async Task<Dictionary<string, object>> Manifest_()
        {
            if (!offline)
            {
                foreach (var b in mirrors)
                {
                    try
                    {
                        object m;
                        using (var res = await Web.Fetch(b + spec.Root + "/" + Manifest, Web.Gate, timeout: 8000))
                        {
                            if (!res.Ok) throw new Exception("http");
                            m = await res.Json();
                        }
                        if (!Valid(m)) throw new Exception("manifest");
                        WriteState(J.With(ReadState(), "manifest", m));
                        bases = new[] { b }.Concat(mirrors.Where(x => x != b)).ToList();
                        return (Dictionary<string, object>)m;
                    }
                    catch (Exception) { }
                }
            }
            var cached = ReadState().Get("manifest");
            return Valid(cached) ? (Dictionary<string, object>)cached : null;
        }

        string PartOf(Dictionary<string, object> m) => Path.Combine(tempDir, (string)m["file"] + ".part");

        static double N(Dictionary<string, object> m, string k) => J.Num(m[k]) ?? 0;

        async Task<Dictionary<string, object>> Room(Dictionary<string, object> m, string dir)
        {
            double got = 0;
            try { got = new FileInfo(PartOf(m)).Length; } catch (Exception) { }
            if (!File.Exists(PartOf(m))) got = 0;
            var fetchNeed = Math.Max(N(m, "bytes") - got, 0) / 1e9;
            var same = Fs.Root(tempDir) == Fs.Root(dir);
            var dirNeed = N(m, "unpacked") / 1e9 + (same ? fetchNeed : 0);
            var dirFree = await Fs.Free(dir);
            var tempFree = same ? dirFree : await Fs.Free(tempDir);
            if (dirFree >= dirNeed && tempFree >= fetchNeed) return null;
            return dirFree < dirNeed ? J.O("need", dirNeed, "free", dirFree) : J.O("need", fetchNeed, "free", tempFree);
        }

        async Task Download(Dictionary<string, object> m, string part)
        {
            long got = 0;
            if (File.Exists(part)) got = new FileInfo(part).Length;
            var total = (long)N(m, "bytes");
            if (got > total)
            {
                got = 0;
                Fs.Rm(part);
            }
            var mark = Environment.TickCount;
            var marked = got;
            double speed = 0;
            var tick = new Throttle();
            var at = 0;
            var buf = new byte[1 << 16];
            while (got < total)
            {
                var ct = job.Fresh();
                FileStream output = null;
                try
                {
                    var h = new Dictionary<string, string>(Web.Gate);
                    if (got > 0) h["Range"] = "bytes=" + got + "-";
                    using (var res = await Web.Fetch(bases[at] + spec.Root + "/" + (string)m["file"], h, ct: ct))
                    {
                        if (res.Status == 200) got = 0;
                        else if (res.Status != 206) throw new Exception("http");
                        output = Fs.Out(part, got > 0);
                        int n;
                        while ((n = await res.Read(buf)) > 0)
                        {
                            await output.WriteAsync(buf, 0, n);
                            got += n;
                            tick.Run(() =>
                            {
                                var now = Environment.TickCount;
                                speed = (got - marked) / (double)Math.Max(now - mark, 1) * 1000 / 1e6;
                                mark = now;
                                marked = got;
                                Send("hub:progress", J.O("phase", "fetch", "frac", Math.Min(got / (double)total, 1), "speed", speed));
                            });
                        }
                    }
                }
                catch (Exception)
                {
                    if (!job.Paused && !job.Cancelled && ++at >= bases.Count) throw;
                }
                finally
                {
                    if (output != null) output.Dispose();
                }
                await job.Gate();
            }
            Send("hub:progress", J.O("phase", "fetch", "frac", 1.0, "speed", 0.0));
        }

        async Task Verify(Dictionary<string, object> m, string part)
        {
            var total = N(m, "bytes");
            var tick = new Throttle();
            string hex;
            using (var sha = SHA256.Create())
            using (var f = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, true))
            {
                var buf = new byte[1 << 20];
                long n = 0;
                int r;
                while ((r = await f.ReadAsync(buf, 0, buf.Length)) > 0)
                {
                    sha.TransformBlock(buf, 0, r, null, 0);
                    n += r;
                    tick.Run(() => Send("hub:progress", J.O("phase", "verify", "frac", Math.Min(n / total, 1), "speed", 0.0)));
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                hex = Bytes.Hex(sha.Hash);
            }
            if (hex == ((string)m["sha256"]).ToLowerInvariant()) return;
            Fs.Rm(part);
            throw new HubError("hash");
        }

        static async Task<T> Retry<T>(Func<T> fn, int tries = 10, int wait = 300)
        {
            for (int i = 1; ; i++)
            {
                try
                {
                    return fn();
                }
                catch (Exception)
                {
                    if (i >= tries) throw;
                    await Task.Delay(wait);
                }
            }
        }

        async Task Place(string part, string dir)
        {
            var stage = dir + ".new";
            var old = dir + ".old";
            if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any() && !Fs.Exists(Path.Combine(dir, spec.Exe))) throw new HubError("busy");
            await Task.Run(() => Fs.Rm(stage, true));
            Fs.Mkdir(stage);
            try
            {
                await Task.Run(() => Fs.Copy(part, Path.Combine(stage, spec.Exe)));
            }
            catch (Exception)
            {
                try { await Task.Run(() => Fs.Rm(stage, true)); } catch (Exception) { }
                throw;
            }
            if (!await Shut())
            {
                try { await Task.Run(() => Fs.Rm(stage, true)); } catch (Exception) { }
                throw new HubError("run");
            }
            await Task.Run(() => Fs.Rm(old, true));
            try
            {
                if (Fs.Exists(dir)) await Retry(() => { Fs.Rename(dir, old); return true; });
            }
            catch (Exception)
            {
                try { await Task.Run(() => Fs.Rm(stage, true)); } catch (Exception) { }
                throw new HubError("busy");
            }
            try
            {
                await Retry(() => { Fs.Rename(stage, dir); return true; });
            }
            catch (Exception)
            {
                if (Fs.Exists(old)) try { Fs.Rename(old, dir); } catch (Exception) { }
                try { await Task.Run(() => Fs.Rm(stage, true)); } catch (Exception) { }
                throw new HubError("busy");
            }
            try { await Task.Run(() => Fs.Rm(old, true)); } catch (Exception) { }
            Send("hub:progress", J.O("phase", "extract", "frac", 1.0, "speed", 0.0));
        }

        async Task Link(Dictionary<string, object> m, string dir)
        {
            var exe = Path.Combine(dir, spec.Exe);
            Fs.Mkdir(Path.GetDirectoryName(startMenu));
            Lnk.Write(startMenu, exe, dir, exe, 0);
            Fs.Mkdir(Path.GetDirectoryName(desktop));
            Lnk.Write(desktop, exe, dir, exe, 0);
            Send("hub:progress", J.O("phase", "link", "frac", 0.5, "speed", 0.0));
            var hub = Fs.Exists(Self.Installed()) ? Self.Installed() : Env.Exe;
            var entries = new (string name, string type, object value)[]
            {
                ("DisplayName", "REG_SZ", spec.Name),
                ("DisplayVersion", "REG_SZ", m["version"]),
                ("Publisher", "REG_SZ", spec.Name),
                ("InstallLocation", "REG_SZ", dir),
                ("DisplayIcon", "REG_SZ", exe),
                ("UninstallString", "REG_SZ", "\"" + hub + "\" " + spec.Uninstall),
                ("EstimatedSize", "REG_DWORD", Math.Round(N(m, "unpacked") / 1024, MidpointRounding.AwayFromZero)),
                ("NoModify", "REG_DWORD", 1),
                ("NoRepair", "REG_DWORD", 1)
            };
            foreach (var (name, type, value) in entries)
            {
                if (!await Task.Run(() => Reg.Add(reg, name, type, value))) throw new HubError("fail");
            }
            WriteState(J.With(ReadState(), "installed", m["version"], "path", dir, "manifest", m));
            Send("hub:progress", J.O("phase", "link", "frac", 1.0, "speed", 0.0));
        }

        async Task Install(Dictionary<string, object> m, string dir)
        {
            var part = PartOf(m);
            Fs.Mkdir(tempDir);
            await Download(m, part);
            await job.Gate();
            await Verify(m, part);
            await job.Gate();
            await Place(part, dir);
            await Link(m, dir);
            Fs.Rm(part);
        }

        async Task<Dictionary<string, object>> RemoveLegacy()
        {
            var old = await Legacy();
            if (old == null) return J.O("ok", false);
            if (!await Shut()) return J.O("ok", false, "code", "run");
            if (!await Elevate(old.Exe, old.Args)) return J.O("ok", false);
            for (int i = 0; i < 60; i++)
            {
                var now = await Legacy();
                if (now == null || now.Version != old.Version) return J.O("ok", true);
                await Task.Delay(500);
            }
            return J.O("ok", false);
        }

        static Task<bool> Elevate(string exe, string args)
        {
            if (Env.Demo != null) return Task.FromResult(false);
            string q(string t) => "'" + t.Replace("'", "''") + "'";
            var cmd = "Start-Process -FilePath " + q(exe) + (string.IsNullOrEmpty(args) ? "" : " -ArgumentList " + q(args)) + " -Verb RunAs -Wait";
            return Proc.Run("powershell.exe", "-NoProfile", "-NonInteractive", "-Command", cmd);
        }

        async Task<Dictionary<string, object>> RemoveOwn((string version, string path) o)
        {
            var dir = o.path;
            if (!await Shut()) return J.O("ok", false, "code", "run");
            var gone = dir + ".removing";
            try
            {
                await Task.Run(() => Fs.Rm(gone, true));
                await Retry(() => { Fs.Rename(dir, gone); return true; });
            }
            catch (Exception)
            {
                return J.O("ok", false, "code", "busy");
            }
            try { await Task.Run(() => Fs.RmRetry(gone, true, 5, 300)); } catch (Exception) { }
            Fs.Rm(startMenu);
            Fs.Rm(desktop);
            await Task.Run(() => Reg.Delete(reg));
            var s = ReadState();
            s.Remove("installed");
            WriteState(s);
            return J.O("ok", true);
        }

        public async Task<object> Info()
        {
            var st = ReadState();
            var o = await Own();
            var dir = o?.path ?? J.Str(st.Get("path")) ?? defaultDir;
            await Recover(dir);
            var m = await Manifest_();
            var old = o != null ? null : await Legacy();
            var installed = o?.version ?? old?.Version;
            return J.O(
                "manifest", m,
                "installed", installed,
                "legacy", old != null,
                "path", dir,
                "free", await Fs.Free(dir),
                "autoUninstall", argv.Contains(spec.Uninstall) && installed != null,
                "banned", banned);
        }

        public async Task<object> Start()
        {
            if (job != null) return J.O("ok", true);
            if (banned) return J.O("ok", false, "code", "net");
            var m = await Manifest_();
            if (m == null) return J.O("ok", false, "code", "net");
            var dir = await Target();
            await Recover(dir);
            var shortOf = await Room(m, dir);
            if (shortOf != null) return J.O("ok", false, "space", shortOf);
            var j = job = new Job();
            Run(m, dir, j);
            return J.O("ok", true);
        }

        async void Run(Dictionary<string, object> m, string dir, Job j)
        {
            try
            {
                await Install(m, dir);
                Send("hub:done", J.O());
            }
            catch (Exception e)
            {
                if (!j.Cancelled) Send("hub:error", J.O("code", Classify(e)));
            }
            finally
            {
                job = null;
            }
        }

        public static string Classify(Exception e)
        {
            if (e is HubError h) return h.Code;
            if (e is UnauthorizedAccessException) return "busy";
            if (e is IOException io && !(e is FileNotFoundException) && !(e is DirectoryNotFoundException))
            {
                var code = io.HResult & 0xFFFF;
                if (code == 32 || code == 33 || code == 5 || code == 170) return "busy";
            }
            if (e is FetchError || e.Message == "http") return "net";
            return "fail";
        }

        public void Pause() => job?.Pause();
        public void Resume() => job?.Resume();
        public void Cancel() => job?.Cancel();

        public async Task<object> Uninstall(object o)
        {
            if (job != null) return J.O("ok", false);
            var mine = await Own();
            var r = mine != null ? await RemoveOwn(mine.Value) : await RemoveLegacy();
            if (J.Truthy(r["ok"]) && o != null && J.Truthy(J.Get(o, "wipe")))
            {
                try { await Task.Run(() => Fs.RmRetry(data, true, 5, 300)); } catch (Exception) { }
            }
            return r;
        }

        public async Task<object> Launch(object stay)
        {
            if (banned) return false;
            var o = await Own();
            if (o == null) return false;
            Process child;
            try
            {
                child = Proc.Spawn(Path.Combine(o.Value.path, spec.Exe), bases.Count > 0 ? new[] { spec.Flag + bases[0] } : new string[0], o.Value.path);
            }
            catch (Exception)
            {
                if (win.Alive)
                {
                    win.ShowWin();
                    win.FocusWin();
                }
                return true;
            }
            Kids.Add(child);
            var ctx = System.Threading.SynchronizationContext.Current;
            child.EnableRaisingEvents = true;
            child.Exited += (s, e) => ctx.Post(_ =>
            {
                Kids.Remove(child);
                if (!win.Alive) return;
                win.ShowWin();
                win.FocusWin();
            }, null);
            if (!J.Truthy(stay)) win.HideWin();
            return true;
        }

        public async Task<object> ChoosePath()
        {
            if (job != null || await Own() != null) return null;
            var picked = Dialogs.PickFolder(win);
            if (picked == null) return null;
            var dir = Path.Combine(picked, spec.Name);
            WriteState(J.With(ReadState(), "path", dir));
            return J.O("path", dir, "free", await Fs.Free(dir));
        }
    }

    static class DictExt
    {
        public static object Get(this Dictionary<string, object> d, string k) => d != null && d.TryGetValue(k, out var v) ? v : null;
    }

    static class Core
    {
        public static Dictionary<string, IUnit> Units;

        public static void Start(Win win, string[] argv, Dictionary<string, (List<string> mirrors, bool offline)> apps)
        {
            Units = apps.ToDictionary(a => a.Key, a => a.Key == "arc" ? (IUnit)new Arc(win, a.Value.mirrors, a.Value.offline) : new Unit(a.Key, win, a.Value.mirrors, a.Value.offline, argv));
            IUnit pick(object k) => k is string s && Units.ContainsKey(s) ? Units[s] : Units["prono"];
            object arg(object[] a, int i) => i < a.Length ? a[i] : null;
            Ipc.Handle("hub:info", async (w, a) => await pick(arg(a, 0)).Info());
            Ipc.Handle("hub:start", async (w, a) => await pick(arg(a, 0)).Start());
            Ipc.Handle("hub:pause", (w, a) => { pick(arg(a, 0)).Pause(); return Task.FromResult<object>(Undefined.V); });
            Ipc.Handle("hub:resume", (w, a) => { pick(arg(a, 0)).Resume(); return Task.FromResult<object>(Undefined.V); });
            Ipc.Handle("hub:cancel", (w, a) => { pick(arg(a, 0)).Cancel(); return Task.FromResult<object>(Undefined.V); });
            Ipc.Handle("hub:uninstall", async (w, a) => await pick(arg(a, 0)).Uninstall(arg(a, 1)));
            Ipc.Handle("hub:launch", async (w, a) => await pick(arg(a, 0)).Launch(arg(a, 1)));
            Ipc.Handle("hub:path", async (w, a) => await pick(arg(a, 0)).ChoosePath());
            Ipc.Handle("hub:plan", async (w, a) => await Plan.List());
            Ipc.Handle("hub:plan-save", async (w, a) =>
            {
                try
                {
                    return await Plan.Save(J.Text(arg(a, 0)));
                }
                catch (Exception)
                {
                    return J.O("ok", false);
                }
            });
        }

        public static bool Busy() => Unit.Kids.Count > 0;
    }
}
