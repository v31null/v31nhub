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
    static class J
    {
        public static Dictionary<string, object> O(params object[] kv)
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
            return d;
        }

        public static Dictionary<string, object> With(object a, params object[] kv)
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            if (a is Dictionary<string, object> x) foreach (var p in x) d[p.Key] = p.Value;
            for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
            return d;
        }

        public static Dictionary<string, object> Obj(object o) => o as Dictionary<string, object>;
        public static object Get(object o, string k) => o is Dictionary<string, object> d && d.TryGetValue(k, out var v) ? v : null;
        public static bool Has(object o, string k) => o is Dictionary<string, object> d && d.ContainsKey(k);
        public static string Str(object o) => o as string;

        public static double? Num(object o)
        {
            switch (o)
            {
                case double d: return d;
                case int i: return i;
                case long l: return l;
                case float f: return f;
                default: return null;
            }
        }

        public static bool Finite(object o) => Num(o) is double d && !double.IsNaN(d) && !double.IsInfinity(d);

        public static string Text(object o)
        {
            switch (o)
            {
                case null: return "null";
                case string s: return s;
                case bool b: return b ? "true" : "false";
                case double d: return NumText(d);
                case int i: return i.ToString(CultureInfo.InvariantCulture);
                case long l: return l.ToString(CultureInfo.InvariantCulture);
                default: return Convert.ToString(o, CultureInfo.InvariantCulture);
            }
        }

        public static bool Truthy(object o)
        {
            switch (o)
            {
                case null: return false;
                case bool b: return b;
                case string s: return s.Length > 0;
                case double d: return d != 0 && !double.IsNaN(d);
                case int i: return i != 0;
                case long l: return l != 0;
                default: return true;
            }
        }

        public static string NumText(double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) return "null";
            if (d == Math.Floor(d) && Math.Abs(d) < 9e15) return ((long)d).ToString(CultureInfo.InvariantCulture);
            return d.ToString("R", CultureInfo.InvariantCulture).Replace("E+", "e+").Replace("E-", "e-");
        }

        public static string Stringify(object v)
        {
            var sb = new StringBuilder();
            Write(sb, v);
            return sb.ToString();
        }

        static void Write(StringBuilder sb, object v)
        {
            switch (v)
            {
                case null: sb.Append("null"); return;
                case string s: Quote(sb, s); return;
                case bool b: sb.Append(b ? "true" : "false"); return;
                case double d: sb.Append(NumText(d)); return;
                case float f: sb.Append(NumText(f)); return;
                case int i: sb.Append(i.ToString(CultureInfo.InvariantCulture)); return;
                case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); return;
                case uint u: sb.Append(u.ToString(CultureInfo.InvariantCulture)); return;
                case IDictionary<string, object> d:
                    {
                        sb.Append('{');
                        var first = true;
                        foreach (var p in d)
                        {
                            if (p.Value is Undefined) continue;
                            if (!first) sb.Append(',');
                            first = false;
                            Quote(sb, p.Key);
                            sb.Append(':');
                            Write(sb, p.Value);
                        }
                        sb.Append('}');
                        return;
                    }
                case Raw r: sb.Append(r.Json); return;
                case Undefined _: sb.Append("null"); return;
                case IEnumerable e:
                    {
                        sb.Append('[');
                        var first = true;
                        foreach (var x in e)
                        {
                            if (!first) sb.Append(',');
                            first = false;
                            Write(sb, x is Undefined ? null : x);
                        }
                        sb.Append(']');
                        return;
                    }
                default: Quote(sb, Convert.ToString(v, CultureInfo.InvariantCulture)); return;
            }
        }

        public static void Quote(StringBuilder sb, string s)
        {
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                var c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); continue;
                    case '\\': sb.Append("\\\\"); continue;
                    case '\b': sb.Append("\\b"); continue;
                    case '\f': sb.Append("\\f"); continue;
                    case '\n': sb.Append("\\n"); continue;
                    case '\r': sb.Append("\\r"); continue;
                    case '\t': sb.Append("\\t"); continue;
                }
                if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                else if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) sb.Append(c).Append(s[++i]);
                else if (char.IsSurrogate(c)) sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            sb.Append('"');
        }

        public static object Parse(string s)
        {
            var p = new Parser(s);
            p.Space();
            var v = p.Value();
            p.Space();
            if (p.At < s.Length) throw new FormatException("json");
            return v;
        }

        sealed class Parser
        {
            readonly string s;
            public int At;
            public Parser(string text) { s = text; }

            public void Space()
            {
                while (At < s.Length && (s[At] == ' ' || s[At] == '\t' || s[At] == '\n' || s[At] == '\r')) At++;
            }

            char Next => At < s.Length ? s[At] : '\0';

            public object Value()
            {
                switch (Next)
                {
                    case '{': return Object();
                    case '[': return Array();
                    case '"': return String();
                    case 't': Word("true"); return true;
                    case 'f': Word("false"); return false;
                    case 'n': Word("null"); return null;
                    default: return Number();
                }
            }

            void Word(string w)
            {
                if (string.CompareOrdinal(s, At, w, 0, w.Length) != 0) throw new FormatException("json");
                At += w.Length;
            }

            Dictionary<string, object> Object()
            {
                var d = new Dictionary<string, object>(StringComparer.Ordinal);
                At++;
                Space();
                if (Next == '}') { At++; return d; }
                for (;;)
                {
                    Space();
                    if (Next != '"') throw new FormatException("json");
                    var k = String();
                    Space();
                    if (Next != ':') throw new FormatException("json");
                    At++;
                    Space();
                    d[k] = Value();
                    Space();
                    if (Next == ',') { At++; continue; }
                    if (Next == '}') { At++; return d; }
                    throw new FormatException("json");
                }
            }

            List<object> Array()
            {
                var l = new List<object>();
                At++;
                Space();
                if (Next == ']') { At++; return l; }
                for (;;)
                {
                    Space();
                    l.Add(Value());
                    Space();
                    if (Next == ',') { At++; continue; }
                    if (Next == ']') { At++; return l; }
                    throw new FormatException("json");
                }
            }

            string String()
            {
                At++;
                var sb = new StringBuilder();
                for (;;)
                {
                    if (At >= s.Length) throw new FormatException("json");
                    var c = s[At++];
                    if (c == '"') return sb.ToString();
                    if (c < 0x20) throw new FormatException("json");
                    if (c != '\\') { sb.Append(c); continue; }
                    if (At >= s.Length) throw new FormatException("json");
                    var e = s[At++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (At + 4 > s.Length) throw new FormatException("json");
                            sb.Append((char)int.Parse(s.Substring(At, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            At += 4;
                            break;
                        default: throw new FormatException("json");
                    }
                }
            }

            object Number()
            {
                var start = At;
                if (Next == '-') At++;
                if (!char.IsDigit(Next)) throw new FormatException("json");
                if (Next == '0') At++;
                else while (char.IsDigit(Next)) At++;
                if (Next == '.')
                {
                    At++;
                    if (!char.IsDigit(Next)) throw new FormatException("json");
                    while (char.IsDigit(Next)) At++;
                }
                if (Next == 'e' || Next == 'E')
                {
                    At++;
                    if (Next == '+' || Next == '-') At++;
                    if (!char.IsDigit(Next)) throw new FormatException("json");
                    while (char.IsDigit(Next)) At++;
                }
                return double.Parse(s.Substring(start, At - start), NumberStyles.Float, CultureInfo.InvariantCulture);
            }
        }
    }

    sealed class Undefined
    {
        public static readonly Undefined V = new Undefined();
    }

    sealed class Raw
    {
        public readonly string Json;
        public Raw(string json) { Json = json; }
    }

    static class Crc32
    {
        static readonly uint[] T = Enumerable.Range(0, 256).Select(n =>
        {
            var c = (uint)n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            return c;
        }).ToArray();

        public static uint Of(byte[] b, uint crc = 0) => Of(b, 0, b.Length, crc);

        public static uint Of(byte[] b, int off, int len, uint crc = 0)
        {
            crc = ~crc;
            for (int i = off; i < off + len; i++) crc = T[(crc ^ b[i]) & 0xFF] ^ (crc >> 8);
            return ~crc;
        }
    }

    static class Bytes
    {
        public static int IndexOf(byte[] buf, byte[] pat, int from, int limit = -1)
        {
            var end = (limit < 0 ? buf.Length : limit) - pat.Length;
            if (from < 0) from = 0;
            for (int i = from; i <= end; i++)
            {
                int j = 0;
                while (j < pat.Length && buf[i + j] == pat[j]) j++;
                if (j == pat.Length) return i;
            }
            return -1;
        }

        public static byte[] Sub(byte[] b, int start, int end)
        {
            if (end > b.Length) end = b.Length;
            if (start > end) start = end;
            var r = new byte[end - start];
            Buffer.BlockCopy(b, start, r, 0, r.Length);
            return r;
        }

        public static bool Eq(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        public static byte[] Cat(params byte[][] parts)
        {
            var r = new byte[parts.Sum(p => p.Length)];
            var o = 0;
            foreach (var p in parts)
            {
                Buffer.BlockCopy(p, 0, r, o, p.Length);
                o += p.Length;
            }
            return r;
        }

        public static string Hex(byte[] b)
        {
            var sb = new StringBuilder(b.Length * 2);
            foreach (var x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }

        public static byte[] FromHex(string h)
        {
            var r = new byte[h.Length / 2];
            for (int i = 0; i < r.Length; i++) r[i] = Convert.ToByte(h.Substring(i * 2, 2), 16);
            return r;
        }
    }

    static class Embedded
    {
        static readonly Assembly Me = typeof(Embedded).Assembly;
        static readonly Dictionary<string, string> Names = Me.GetManifestResourceNames().ToDictionary(n => n.Replace('\\', '/'), n => n, StringComparer.Ordinal);

        public static bool Has(string name) => Names.ContainsKey(name);

        public static Stream Open(string name) => Names.TryGetValue(name, out var n) ? Me.GetManifestResourceStream(n) : null;

        public static byte[] Bytes(string name)
        {
            using (var s = Open(name))
            {
                if (s == null) return null;
                var m = new MemoryStream();
                s.CopyTo(m);
                return m.ToArray();
            }
        }

        public static string Text(string name)
        {
            var b = Bytes(name);
            return b == null ? null : new UTF8Encoding(false).GetString(b);
        }

        public static Assembly Resolve(object sender, ResolveEventArgs e)
        {
            var name = new AssemblyName(e.Name).Name + ".dll";
            var b = Bytes(name);
            return b == null ? null : Assembly.Load(b);
        }
    }

    static class Text
    {
        public static readonly Encoding Utf8 = new UTF8Encoding(false);
    }
}
