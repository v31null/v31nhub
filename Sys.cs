using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Hub
{
    static class Sys
    {
        [DllImport("kernel32.dll")]
        static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

        [StructLayout(LayoutKind.Sequential)]
        sealed class MemoryStatus
        {
            public uint Length = (uint)Marshal.SizeOf(typeof(MemoryStatus));
            public uint Load;
            public ulong TotalPhys, AvailPhys, TotalPage, AvailPage, TotalVirtual, AvailVirtual, AvailExtended;
        }

        [DllImport("kernel32.dll")]
        static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatus m);

        sealed class Ticks
        {
            public double Idle, All;
        }

        sealed class Wire
        {
            public double Rx, Tx;
            public long At;
        }

        static Ticks cpu;
        static Wire wire;

        static Ticks Now()
        {
            GetSystemTimes(out var idle, out var kernel, out var user);
            return new Ticks { Idle = idle / 1e4, All = (kernel + user) / 1e4 };
        }

        static async Task<Wire> Netstat()
        {
            var r = await Proc.Exec("netstat", new[] { "-e" }, 2000);
            if (!r.ok) return null;
            var row = r.output.Split('\n').Select(l => Regex.Match(l.Trim(), @"(\d+)\s+(\d+)$")).FirstOrDefault(m => m.Success);
            return row == null ? null : new Wire { Rx = double.Parse(row.Groups[1].Value), Tx = double.Parse(row.Groups[2].Value), At = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
        }

        public static async Task<object> Sample()
        {
            var t = Now();
            var b = await Netstat();
            var m = new MemoryStatus();
            GlobalMemoryStatusEx(m);
            var output = J.O("cpu", null, "ram", Math.Round((1 - m.AvailPhys / (double)m.TotalPhys) * 100, MidpointRounding.AwayFromZero), "rx", null, "tx", null);
            if (cpu != null)
            {
                var all = t.All - cpu.All;
                if (all > 0) output["cpu"] = Math.Round((1 - (t.Idle - cpu.Idle) / all) * 100, MidpointRounding.AwayFromZero);
            }
            if (b != null && wire != null && b.At > wire.At)
            {
                var s = (b.At - wire.At) / 1000.0;
                output["rx"] = Math.Max(0, (b.Rx - wire.Rx) / 1024 / s);
                output["tx"] = Math.Max(0, (b.Tx - wire.Tx) / 1024 / s);
            }
            cpu = t;
            wire = b;
            return output;
        }
    }
}
