using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Hub
{
    sealed class Detected
    {
        public bool Host;
        public string HostUrl, PublicUrl, KeyPath;
    }

    static class Vhost
    {
        static IEnumerable<string> Candidates() => new[]
        {
            Environment.GetEnvironmentVariable("PRONO_VHOSTS"),
            "C:/xampp/apache/conf/extra/httpd-vhosts.conf",
            "/xampp/apache/conf/extra/httpd-vhosts.conf",
            "/etc/apache2/sites-enabled",
            "/usr/local/apache2/conf/extra/httpd-vhosts.conf"
        }.Where(c => !string.IsNullOrEmpty(c));

        static string ReadVhostText()
        {
            foreach (var c in Candidates())
            {
                try
                {
                    if (Directory.Exists(c))
                    {
                        var buf = new StringBuilder();
                        foreach (var f in Directory.GetFileSystemEntries(c).Select(Path.GetFileName))
                        {
                            if (Regex.IsMatch(f, @"\.conf$", RegexOptions.IgnoreCase)) buf.Append(File.ReadAllText(Path.Combine(c, f), Encoding.UTF8)).Append("\n");
                        }
                        if (buf.Length > 0) return buf.ToString();
                    }
                    else if (File.Exists(c))
                    {
                        return File.ReadAllText(c, Encoding.UTF8);
                    }
                }
                catch (Exception) { }
            }
            return "";
        }

        static List<string> ParseBlocks(string text)
        {
            var blocks = new List<string>();
            foreach (Match m in Regex.Matches(text, @"<VirtualHost[^>]*>([\s\S]*?)<\/VirtualHost>", RegexOptions.IgnoreCase)) blocks.Add(m.Groups[1].Value);
            return blocks;
        }

        static string Pick(string block, string directive)
        {
            var m = Regex.Match(block, directive + "\\s+\"?([^\"\\r\\n]+?)\"?\\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
            return m.Success ? m.Groups[1].Value.Trim() : "";
        }

        static List<string> PickAll(string block, string directive)
        {
            var output = new List<string>();
            foreach (Match m in Regex.Matches(block, directive + "\\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.Multiline))
            {
                foreach (var raw in Regex.Split(m.Groups[1].Value, "\\s+"))
                {
                    var v = Regex.Replace(Regex.Replace(raw, "^\"|\"$", ""), "\r$", "").Trim();
                    if (v.Length > 0) output.Add(v);
                }
            }
            return output;
        }

        static string NormalizePath(string p) => string.IsNullOrEmpty(p) ? p : Regex.Replace(p.Replace('\\', '/'), "^\"|\"$", "").Trim();

        public static Detected Detect(string domain)
        {
            var text = ReadVhostText();
            var result = new Detected { Host = false, HostUrl = "https://" + domain };
            if (text.Length == 0) return result;
            foreach (var block in ParseBlocks(text))
            {
                var names = new[] { Pick(block, "ServerName") }.Concat(PickAll(block, "ServerAlias")).Where(n => n.Length > 0).ToList();
                if (!names.Contains(domain)) continue;
                var serverName = Pick(block, "ServerName");
                if (serverName.Length == 0) serverName = domain;
                result.HostUrl = "https://" + serverName;
                var alias = names.FirstOrDefault(n => n.ToLowerInvariant() == "prono.share.zrok.io");
                if (alias != null) result.PublicUrl = "https://" + alias;
                var key = NormalizePath(Pick(block, "SSLCertificateKeyFile"));
                if (!string.IsNullOrEmpty(key))
                {
                    result.KeyPath = key;
                    try
                    {
                        if (new FileInfo(key).Length > 0) result.Host = true;
                    }
                    catch (Exception) { }
                }
                if (result.Host) break;
            }
            return result;
        }
    }
}
