using System;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Web.WebView2.Core;

namespace Hub
{
    static class Web2
    {
        public static void Loader(string userData)
        {
            var dll = Embedded.Bytes("WebView2Loader.dll");
            string id;
            using (var sha = SHA256.Create()) id = Bytes.Hex(sha.ComputeHash(dll)).Substring(0, 12);
            var dir = Path.Combine(userData, "loader", id);
            var file = Path.Combine(dir, "WebView2Loader.dll");
            if (!File.Exists(file) || new FileInfo(file).Length != dll.Length)
            {
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(file, dll);
            }
            CoreWebView2Environment.SetLoaderDllFolderPath(dir);
        }
    }
}
