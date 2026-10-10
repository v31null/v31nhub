using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Hub
{
    sealed class FetchError : Exception
    {
        public readonly string Name;
        public readonly string Code;
        public FetchError(string name, string code, string message = null) : base(message ?? name)
        {
            Name = name;
            Code = code ?? "";
        }
    }

    sealed class Res : IDisposable
    {
        readonly HttpResponseMessage m;
        readonly CancellationToken ct;
        readonly Func<bool> timedOut;
        readonly CancellationTokenRegistration reg;
        Stream body;

        public Res(HttpResponseMessage msg, CancellationToken token, Func<bool> timed)
        {
            m = msg;
            ct = token;
            timedOut = timed;
            reg = token.Register(() =>
            {
                try { m.Dispose(); } catch (Exception) { }
            });
        }

        public int Status => (int)m.StatusCode;
        public bool Ok => Status >= 200 && Status < 300;

        public string Header(string name)
        {
            if (m.Headers.TryGetValues(name, out var v)) return string.Join(", ", v);
            if (m.Content != null && m.Content.Headers.TryGetValues(name, out v)) return string.Join(", ", v);
            return null;
        }

        Exception Map(Exception e)
        {
            if (e is FetchError) return e;
            if (ct.IsCancellationRequested) return timedOut() ? new FetchError("TimeoutError", "") : new FetchError("AbortError", "");
            return new FetchError("TypeError", "ECONNRESET", "terminated");
        }

        public async Task<int> Read(byte[] buf)
        {
            try
            {
                if (body == null) body = await m.Content.ReadAsStreamAsync();
                ct.ThrowIfCancellationRequested();
                return await body.ReadAsync(buf, 0, buf.Length, ct);
            }
            catch (Exception e)
            {
                throw Map(e);
            }
        }

        public async Task<byte[]> Bytes()
        {
            var ms = new MemoryStream();
            var buf = new byte[1 << 16];
            int n;
            while ((n = await Read(buf)) > 0) ms.Write(buf, 0, n);
            return ms.ToArray();
        }

        public async Task<string> Text() => Hub.Text.Utf8.GetString(await Bytes());

        public async Task<object> Json() => J.Parse(await Text());

        public void Dispose()
        {
            reg.Dispose();
            try { m.Dispose(); } catch (Exception) { }
        }
    }

    static class Web
    {
        public static readonly Dictionary<string, string> Gate = new Dictionary<string, string> { { "X-V31null-Hub", "1" } };
        static HttpClient main, jarClient;
        static CookieContainer jar;
        static string jarFile;

        public static void Init()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            ServicePointManager.DefaultConnectionLimit = 32;
            ServicePointManager.Expect100Continue = false;
            main = new HttpClient(Handler(null)) { Timeout = Timeout.InfiniteTimeSpan };
        }

        static HttpMessageHandler Handler(CookieContainer cookies)
        {
            if (Env.Demo != null) return new DemoNet();
            var h = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                UseCookies = cookies != null
            };
            if (cookies != null) h.CookieContainer = cookies;
            return h;
        }

        public static HttpClient Jar(string agent)
        {
            if (jarClient != null) return jarClient;
            jarFile = Path.Combine(Env.UserData, "Partitions", "imgsw", "Cookies.bin");
            jar = null;
            try
            {
                using (var f = File.OpenRead(jarFile)) jar = (CookieContainer)new BinaryFormatter().Deserialize(f);
            }
            catch (Exception) { }
            jar = jar ?? new CookieContainer();
            jarClient = new HttpClient(Handler(jar)) { Timeout = Timeout.InfiniteTimeSpan };
            jarClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", agent);
            return jarClient;
        }

        public static void SaveJar()
        {
            if (jar == null) return;
            try
            {
                Fs.Mkdir(Path.GetDirectoryName(jarFile));
                var ms = new MemoryStream();
                new BinaryFormatter().Serialize(ms, jar);
                Fs.Write(jarFile, ms.ToArray());
            }
            catch (Exception) { }
        }

        public static async Task<Res> Fetch(string url, IDictionary<string, string> headers = null, string method = "GET", string body = null, string type = null, CancellationToken ct = default, int timeout = 0, HttpClient via = null)
        {
            CancellationTokenSource timer = null;
            var token = ct;
            if (timeout > 0)
            {
                timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timer.CancelAfter(timeout);
                token = timer.Token;
            }
            Func<bool> timedOut = () => timer != null && timer.IsCancellationRequested && !ct.IsCancellationRequested;
            HttpRequestMessage req;
            try
            {
                req = new HttpRequestMessage(new HttpMethod(method), url);
            }
            catch (Exception)
            {
                throw new FetchError("TypeError", "ERR_INVALID_URL");
            }
            if (headers != null)
            {
                foreach (var h in headers)
                {
                    if (h.Key.Equals("Range", StringComparison.OrdinalIgnoreCase)) req.Headers.Range = Range(h.Value);
                    else if (h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) type = h.Value;
                    else req.Headers.TryAddWithoutValidation(h.Key, h.Value);
                }
            }
            if (body != null)
            {
                req.Content = new StringContent(body, Hub.Text.Utf8);
                if (type != null) req.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(type);
            }
            try
            {
                var m = await (via ?? main).SendAsync(req, HttpCompletionOption.ResponseHeadersRead, token);
                return new Res(m, token, timedOut);
            }
            catch (Exception e)
            {
                if (token.IsCancellationRequested) throw timedOut() ? new FetchError("TimeoutError", "") : new FetchError("AbortError", "");
                throw Map(e);
            }
        }

        static RangeHeaderValue Range(string v)
        {
            var s = v.Trim();
            if (s.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) s = s.Substring(6);
            var dash = s.IndexOf('-');
            var a = s.Substring(0, dash).Trim();
            var b = s.Substring(dash + 1).Trim();
            return new RangeHeaderValue(a.Length > 0 ? long.Parse(a) : (long?)null, b.Length > 0 ? long.Parse(b) : (long?)null);
        }

        public static Exception Map(Exception e)
        {
            if (e is FetchError) return e;
            var we = Find<WebException>(e);
            var se = Find<SocketException>(e);
            if (se != null)
            {
                switch (se.SocketErrorCode)
                {
                    case SocketError.HostNotFound:
                    case SocketError.NoData:
                        return new FetchError("TypeError", "ENOTFOUND");
                    case SocketError.TryAgain:
                        return new FetchError("TypeError", "EAI_AGAIN");
                    case SocketError.ConnectionRefused:
                        return new FetchError("TypeError", "ECONNREFUSED");
                    case SocketError.TimedOut:
                        return new FetchError("TypeError", "ETIMEDOUT");
                }
            }
            if (we != null)
            {
                switch (we.Status)
                {
                    case WebExceptionStatus.NameResolutionFailure:
                    case WebExceptionStatus.ProxyNameResolutionFailure:
                        return new FetchError("TypeError", "ENOTFOUND");
                    case WebExceptionStatus.TrustFailure:
                    case WebExceptionStatus.SecureChannelFailure:
                        return new FetchError("TypeError", "ERR_TLS_CERT");
                    case WebExceptionStatus.Timeout:
                        return new FetchError("TypeError", "ETIMEDOUT");
                    case WebExceptionStatus.ConnectFailure:
                        return new FetchError("TypeError", "ECONNREFUSED");
                    case WebExceptionStatus.ConnectionClosed:
                    case WebExceptionStatus.ReceiveFailure:
                    case WebExceptionStatus.SendFailure:
                    case WebExceptionStatus.KeepAliveFailure:
                        return new FetchError("TypeError", "ECONNRESET");
                }
            }
            return new FetchError("TypeError", "", e.Message);
        }

        static T Find<T>(Exception e) where T : Exception
        {
            for (var x = e; x != null; x = x.InnerException) if (x is T t) return t;
            return null;
        }
    }
}
