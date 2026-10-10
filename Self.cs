using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Hub
{
    sealed class Fresh
    {
        public Dictionary<string, object> Hub;
        public List<string> From;
    }

    static class Self
    {
        const string Name = "V31null Hub.exe";
        const string Key = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\V31null Hub";
        static string Prono => @"HKCU\" + Env.Uninstall + @"\Prono";
        static string M2 => @"HKCU\" + Env.Uninstall + @"\M2";

        static string Local() => Env.Local;
        static string Home() => Path.Combine(Local(), "Programs", "V31null Hub");
        public static string Installed() => Path.Combine(Home(), Name);
        static string[] Links() => new[]
        {
            Path.Combine(Env.AppData, "Microsoft", "Windows", "Start Menu", "Programs", "V31null Hub.lnk"),
            Path.Combine(Env.Desktop, "V31null Hub.lnk")
        };
        static string Downloads() => Path.Combine(Env.Temp, "V31nullHub");
        static string Start() => Path.Combine(Env.UserData, "origin.json");

        public static void Remember(string[] argv)
        {
            var exe = Portable() ? Env.Exe : null;
            if (exe == null || argv.Any(a => a.StartsWith("--updated=", StringComparison.Ordinal)) || File.Exists(Start())) return;
            try
            {
                Fs.Mkdir(Path.GetDirectoryName(Start()));
                Fs.Create(Start(), Text.Utf8.GetBytes(J.Stringify(J.O("dir", Path.GetDirectoryName(exe)))));
            }
            catch (Exception) { }
        }

        public static string Origin()
        {
            if (Env.Demo != null) return Path.Combine(Env.Demo, "Origin");
            if (!Env.Packaged)
            {
                for (var d = Path.GetDirectoryName(Env.Exe); d != null; d = Path.GetDirectoryName(d))
                {
                    if (File.Exists(Path.Combine(d, "package.json")) && Directory.Exists(Path.Combine(d, "devtools"))) return Path.Combine(d, "devtools");
                }
                return null;
            }
            try
            {
                var dir = J.Get(J.Parse(Fs.ReadText(Start())), "dir") as string;
                return !string.IsNullOrEmpty(dir) ? dir : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        static string Value(string key, string name) => Reg.Value(key, name);

        static List<string> Flags(string[] argv) => argv.Skip(1).Where(a => a.StartsWith("--", StringComparison.Ordinal)).ToList();

        public static bool Portable() => Env.Demo == null && !string.Equals(Path.GetFullPath(Path.GetDirectoryName(Env.Exe)), Path.GetFullPath(Home()), StringComparison.OrdinalIgnoreCase);

        static long Size(string dir) =>
            new DirectoryInfo(dir).EnumerateFileSystemInfos().Sum(e => e is DirectoryInfo d ? Size(d.FullName) : ((FileInfo)e).Length);

        static async Task<bool> Place()
        {
            var src = Env.Exe;
            var dst = Home();
            var stage = dst + ".new";
            var old = dst + ".old";
            await Task.Run(() => Fs.Rm(stage, true));
            Fs.Mkdir(stage);
            await Task.Run(() => Fs.Copy(src, Path.Combine(stage, Name)));
            for (int i = 0; i < 60; i++)
            {
                try
                {
                    await Task.Run(() => Fs.Rm(old, true));
                    if (Fs.Exists(dst)) Fs.Rename(dst, old);
                    Fs.Rename(stage, dst);
                    try { await Task.Run(() => Fs.Rm(old, true)); } catch (Exception) { }
                    return true;
                }
                catch (Exception)
                {
                    if (!Fs.Exists(dst) && Fs.Exists(old)) try { Fs.Rename(old, dst); } catch (Exception) { }
                    await Task.Delay(500);
                }
            }
            try { await Task.Run(() => Fs.Rm(stage, true)); } catch (Exception) { }
            return false;
        }

        static async Task Register()
        {
            var exe = Installed();
            var kb = Math.Round(await Task.Run(() => Size(Home())) / 1024.0, MidpointRounding.AwayFromZero);
            var entries = new (string, string, object)[]
            {
                ("DisplayName", "REG_SZ", "V31null Hub"),
                ("DisplayVersion", "REG_SZ", Env.Version),
                ("Publisher", "REG_SZ", "V31null"),
                ("InstallLocation", "REG_SZ", Home()),
                ("DisplayIcon", "REG_SZ", exe),
                ("UninstallString", "REG_SZ", "\"" + exe + "\" --remove-hub"),
                ("QuietUninstallString", "REG_SZ", "\"" + exe + "\" --remove-hub"),
                ("EstimatedSize", "REG_DWORD", kb),
                ("NoModify", "REG_DWORD", 1),
                ("NoRepair", "REG_DWORD", 1)
            };
            foreach (var (name, type, data) in entries) Reg.Add(Key, name, type, data);
            foreach (var lnk in Links())
            {
                Fs.Mkdir(Path.GetDirectoryName(lnk));
                Lnk.Write(lnk, exe, Home(), exe, 0);
            }
        }

        static async Task Adopt()
        {
            foreach (var (key, flag) in new[] { (Prono, "--uninstall"), (M2, "--uninstall-m2") })
            {
                var want = "\"" + Installed() + "\" " + flag;
                var now = Value(key, "UninstallString");
                if (!string.IsNullOrEmpty(now) && now != want) Reg.Add(key, "UninstallString", "REG_SZ", want);
            }
            try { await Task.Run(() => Fs.Rm(Path.Combine(Local(), "V31null Hub"), true)); } catch (Exception) { }
        }

        public static async Task<bool> Setup(string[] argv)
        {
            var have = File.Exists(Installed()) ? Value(Key, "DisplayVersion") : null;
            if (string.IsNullOrEmpty(have) || Ver.Cmp(Env.Version, have) > 0)
            {
                if (!await Place()) return false;
                await Register();
            }
            await Adopt();
            Proc.Spawn(Installed(), Flags(argv));
            return true;
        }

        static bool Valid(object h) =>
            h is Dictionary<string, object> &&
            J.Get(h, "file") as string == Name &&
            J.Get(h, "version") is string v && Regex.IsMatch(v, @"^\d+(\.\d+){1,3}$") &&
            J.Get(h, "sha256") is string s && Regex.IsMatch(s, "^[0-9a-f]{64}$", RegexOptions.IgnoreCase) &&
            J.Finite(J.Get(h, "bytes")) && J.Num(J.Get(h, "bytes")) > 0;

        static async Task<Dictionary<string, object>> Fingerprint(string url)
        {
            try
            {
                using (var res = await Web.Fetch(url, Web.Gate, timeout: Net.Wait))
                {
                    if (!res.Ok) return null;
                    var h = await res.Json();
                    return Valid(h) ? (Dictionary<string, object>)h : null;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static async Task<Fresh> Check()
        {
            var all = Net.Hubs();
            async Task<List<(Host m, Dictionary<string, object> h)>> Ask(IEnumerable<Host> group) =>
                (await Task.WhenAll(group.Select(async m => (m, h: await Fingerprint(m.Url))))).Where(x => x.h != null).ToList();
            var got = await Ask(all.Where(m => m.Role == "primary"));
            if (got.Count == 0) got = await Ask(all.Where(m => m.Role == "backup"));
            if (got.Count == 0) return null;
            var best = got.Aggregate((a, b) => Ver.Cmp((string)b.h["version"], (string)a.h["version"]) > 0 ? b : a);
            return new Fresh { Hub = best.h, From = new[] { best.m.Exe }.Concat(all.Select(m => m.Exe)).Distinct().ToList() };
        }

        public static async Task<bool> Update(Fresh fresh, string[] argv)
        {
            if (fresh == null) return false;
            var h = fresh.Hub;
            var current = Env.Version;
            var tried = argv.Select(a => Regex.Match(a, "^--updated=(.+)$")).FirstOrDefault(m => m.Success);
            if (tried != null && Ver.Cmp(tried.Groups[1].Value, current) > 0) return false;
            if (!Valid(h) || Ver.Cmp((string)h["version"], current) <= 0) return false;
            var next = Path.Combine(Downloads(), "V31null Hub " + h["version"] + Path.GetExtension(Name));
            foreach (var url in fresh.From)
            {
                try
                {
                    Fs.Mkdir(Downloads());
                    using (var res = await Web.Fetch(url, Web.Gate, timeout: 15 * 60 * 1000))
                    {
                        if (!res.Ok) throw new Exception("http");
                        long got = 0;
                        string hex;
                        using (var sha = SHA256.Create())
                        using (var output = Fs.Out(next, false))
                        {
                            var buf = new byte[1 << 16];
                            int n;
                            while ((n = await res.Read(buf)) > 0)
                            {
                                sha.TransformBlock(buf, 0, n, null, 0);
                                got += n;
                                await output.WriteAsync(buf, 0, n);
                            }
                            sha.TransformFinalBlock(new byte[0], 0, 0);
                            hex = Bytes.Hex(sha.Hash);
                        }
                        if (got != (long)J.Num(h["bytes"]) || hex != ((string)h["sha256"]).ToLowerInvariant()) throw new Exception("hash");
                    }
                }
                catch (Exception)
                {
                    try { Fs.Rm(next); } catch (Exception) { }
                    continue;
                }
                Proc.Spawn(next, Flags(argv).Where(a => !a.StartsWith("--updated=", StringComparison.Ordinal)).Concat(new[] { "--updated=" + h["version"] }));
                return true;
            }
            return false;
        }

        public static async Task Tidy()
        {
            try { await Task.Run(() => Fs.Rm(Downloads(), true)); } catch (Exception) { }
        }

        public static async Task Remove()
        {
            foreach (var lnk in Links()) try { Fs.Rm(lnk); } catch (Exception) { }
            Reg.Delete(Key);
            string q(string p) => "\"" + p.Replace("%", "%%") + "\"";
            var bat = Path.Combine(Env.Temp, "v31null-hub-remove.cmd");
            var lines = new[]
            {
                "@echo off",
                "chcp 65001 >nul",
                "set n=0",
                ":loop",
                "taskkill /F /T /IM " + q(Name) + " >nul 2>&1",
                "rmdir /s /q " + q(Home()) + " >nul 2>&1",
                "if not exist " + q(Home()) + " goto done",
                "set /a n+=1",
                "if %n% geq 120 goto done",
                "ping -n 2 127.0.0.1 >nul",
                "goto loop",
                ":done",
                "rmdir /s /q " + q(Env.UserData) + " >nul 2>&1",
                "(goto) 2>nul & del \"%~f0\""
            };
            Fs.Write(bat, string.Join("\r\n", lines));
            var helper = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo("cmd.exe", "/d /c start \"\" /b cmd.exe /d /c " + q(bat)) { UseShellExecute = false, CreateNoWindow = true }
            };
            helper.Start();
            await Task.WhenAny(Task.Run(() => helper.WaitForExit()), Task.Delay(5000));
        }
    }
}
