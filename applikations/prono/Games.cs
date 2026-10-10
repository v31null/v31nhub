using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Hub;

namespace Prono
{
    sealed class Games
    {
        const string DetectableUrl = "https://discord.com/api/v10/applications/detectable";

        sealed class Hit
        {
            public string Id, Name, Icon;
        }

        static readonly HttpClient Http = new HttpClient();
        Dictionary<string, Hit> detectable;
        readonly Dictionary<string, double> detectedStart = new Dictionary<string, double>();
        readonly Dictionary<string, string> detected = new Dictionary<string, string>();
        readonly Dictionary<string, string> iconCache = new Dictionary<string, string>();
        readonly Action<List<object>> onGames;
        readonly int period;
        bool stopped;

        Games(Action<List<object>> onGames, int period)
        {
            this.onGames = onGames;
            this.period = period;
        }

        public static Games Start(Action<List<object>> onGames, int intervalMs = 0)
        {
            var g = new Games(onGames, intervalMs > 0 ? intervalMs : 15000);
            g.Tick();
            return g;
        }

        public void Stop() => stopped = true;

        static string IconUrlFor(string id, string hash) => !string.IsNullOrEmpty(hash) ? "https://cdn.discordapp.com/app-icons/" + id + "/" + hash + ".png" : "";

        static async Task<object> Json(string url)
        {
            using (var r = await Http.GetAsync(url))
            {
                if (!r.IsSuccessStatusCode) return null;
                return J.Parse(await r.Content.ReadAsStringAsync());
            }
        }

        async Task LoadDetectable()
        {
            try
            {
                if (!(await Json(DetectableUrl) is List<object> arr)) return;
                var map = new Dictionary<string, Hit>();
                foreach (var app in arr)
                {
                    if (app == null || !J.Truthy(J.Get(app, "id")) || !(J.Get(app, "executables") is List<object> exes)) continue;
                    foreach (var ex in exes)
                    {
                        if (ex == null || !J.Truthy(J.Get(ex, "name")) || J.Truthy(J.Get(ex, "is_launcher"))) continue;
                        if (J.Truthy(J.Get(ex, "os")) && J.Text(J.Get(ex, "os")) != "win32") continue;
                        var bas = J.Text(J.Get(ex, "name")).Split('\\', '/').Last().ToLowerInvariant();
                        if (bas.Length > 0 && !map.ContainsKey(bas))
                        {
                            map[bas] = new Hit
                            {
                                Id = J.Text(J.Get(app, "id")),
                                Name = J.Truthy(J.Get(app, "name")) ? J.Text(J.Get(app, "name")) : "Game",
                                Icon = J.Truthy(J.Get(app, "icon")) ? J.Text(J.Get(app, "icon")) : ""
                            };
                        }
                    }
                }
                detectable = map;
            }
            catch (Exception) { }
        }

        static Task<List<string>> ListProcesses() => Task.Run(() =>
        {
            var names = new List<string>();
            try
            {
                foreach (var p in Process.GetProcesses())
                {
                    using (p) names.Add((p.ProcessName + ".exe").ToLowerInvariant());
                }
            }
            catch (Exception) { }
            return names;
        });

        List<object> BuildGames() => detected.Select(d => (object)J.O(
            "type", 0,
            "kind", "game",
            "application_id", d.Key,
            "name", d.Value,
            "details", "",
            "state", "",
            "large_text", "",
            "small_text", "",
            "large_image", iconCache.TryGetValue(d.Key, out var i) ? i : "",
            "start", detectedStart.TryGetValue(d.Key, out var s) ? (object)s : Undefined.V,
            "end", null)).ToList();

        async void ResolveIcon(string id)
        {
            if (iconCache.ContainsKey(id)) return;
            iconCache[id] = "";
            try
            {
                var j = await Json("https://discord.com/api/v10/applications/" + id + "/rpc");
                if (j == null) return;
                var url = IconUrlFor(id, J.Truthy(J.Get(j, "icon")) ? J.Text(J.Get(j, "icon")) : null);
                iconCache[id] = url;
                if (url.Length > 0 && !stopped && detected.ContainsKey(id)) onGames(BuildGames());
            }
            catch (Exception) { }
        }

        async void Tick()
        {
            if (stopped) return;
            if (detectable == null) await LoadDetectable();
            if (detectable != null && !stopped)
            {
                var procs = await ListProcesses();
                var seen = new HashSet<string>();
                detected.Clear();
                var now = (double)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                foreach (var p in procs)
                {
                    if (!detectable.TryGetValue(p, out var hit) || seen.Contains(hit.Id)) continue;
                    seen.Add(hit.Id);
                    detected[hit.Id] = hit.Name;
                    if (!detectedStart.ContainsKey(hit.Id)) detectedStart[hit.Id] = now;
                    if (!iconCache.ContainsKey(hit.Id))
                    {
                        if (hit.Icon.Length > 0) iconCache[hit.Id] = IconUrlFor(hit.Id, hit.Icon);
                        else ResolveIcon(hit.Id);
                    }
                }
                foreach (var id in detectedStart.Keys.ToList()) if (!seen.Contains(id)) detectedStart.Remove(id);
                onGames(BuildGames());
            }
            if (!stopped)
            {
                await Task.Delay(period);
                Tick();
            }
        }
    }
}
