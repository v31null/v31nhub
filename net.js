const fs = require("fs");
const os = require("os");
const path = require("path");
const { net } = require("electron");

const GATE = { "X-V31null-Hub": "1" };
const WAIT = 4000;
const RANK = { primary: 0, backup: 1 };

const list = () => {
  try {
    return JSON.parse(fs.readFileSync(path.join(__dirname, "net.json"), "utf8"));
  } catch (e) {
    return {};
  }
};

const services = () =>
  Object.entries(list().services || {}).map(([name, hosts]) => ({
    name,
    hosts: (Array.isArray(hosts) ? hosts : [])
      .filter(h => h && typeof h.url === "string" && /^https?:\/\/[^/]+/i.test(h.url) && Object.hasOwn(RANK, h.role))
      .map((h, i) => ({ url: h.url.replace(/\/+$/, ""), role: h.role, i }))
      .sort((a, b) => RANK[a.role] - RANK[b.role] || a.i - b.i)
      .map(({ url, role }) => ({ url, role }))
  }));

const web = u => typeof u === "string" && /^https:\/\/[^/]+/i.test(u);

const hubs = () =>
  (Array.isArray(list().hub) ? list().hub : [])
    .map(h => (process.platform === "linux" ? h && h.linux && { ...h.linux, role: h.role } : h))
    .filter(h => h && web(h.url) && web(h.exe) && Object.hasOwn(RANK, h.role))
    .map((h, i) => ({ url: h.url, exe: h.exe, role: h.role, i }))
    .sort((a, b) => RANK[a.role] - RANK[b.role] || a.i - b.i)
    .map(({ url, exe, role }) => ({ url, exe, role }));

const servers = (name = "prono") => {
  const s = services().find(x => x.name === name);
  return s ? [...new Set(s.hosts.map(h => h.url))] : [];
};

const needs = item => (list().online || []).includes(item);

const cause = e => {
  const c = (e && e.cause) || {};
  const code = String(c.code || (e && e.code) || "");
  if (e && (e.name === "TimeoutError" || e.name === "AbortError")) return "timeout";
  if (/ENOTFOUND|EAI_AGAIN|EAI_NONAME|EAI_FAIL/.test(code)) return "dns";
  if (/ECONNREFUSED/.test(code)) return "refused";
  if (/ETIMEDOUT|UND_ERR_CONNECT_TIMEOUT|UND_ERR_HEADERS_TIMEOUT|UND_ERR_BODY_TIMEOUT/.test(code)) return "timeout";
  if (/CERT|TLS|SSL|SELF_SIGNED|UNABLE_TO_VERIFY|ERR_SSL/.test(code)) return "tls";
  return "unreachable";
};

const get = async url => {
  const t = Date.now();
  try {
    const res = await fetch(url, { cache: "no-store", headers: GATE, signal: AbortSignal.timeout(WAIT) });
    if (!res.ok) return { status: "http", code: res.status, ms: Date.now() - t, body: null };
    try {
      return { status: "online", ms: Date.now() - t, body: await res.json() };
    } catch (e) {
      return { status: "bad", ms: Date.now() - t, body: null };
    }
  } catch (e) {
    return { status: cause(e), ms: Date.now() - t, body: null };
  }
};

const socketed = name => (list().socket || []).includes(name);

const knock = async url => {
  const t = performance.now();
  try {
    await fetch(url, { method: "HEAD", cache: "no-store", headers: GATE, signal: AbortSignal.timeout(WAIT) });
    return Math.round(performance.now() - t);
  } catch (e) {
    return null;
  }
};

const ping = async url => {
  const a = await knock(url);
  const b = await knock(url);
  const got = [a, b].filter(v => v != null);
  return got.length ? Math.min(...got) : null;
};

const sockets = new Map();

const drop = (base, c) => {
  if (sockets.get(base) === c) sockets.delete(base);
  try {
    c.ws.close();
  } catch (e) {}
  c.acks.forEach(done => done(false));
  c.acks.clear();
};

const link = base => {
  const have = sockets.get(base);
  if (have) return have.ready;
  const c = { ws: null, acks: new Map(), id: 0 };
  sockets.set(base, c);
  c.ready = new Promise(res => {
    try {
      c.ws = new WebSocket(`${base.replace(/^http/i, "ws")}/socket.io/?EIO=4&transport=websocket`, { headers: { Origin: base } });
    } catch (e) {
      sockets.delete(base);
      return res(null);
    }
    const timer = setTimeout(() => {
      drop(base, c);
      res(null);
    }, WAIT);
    c.ws.onmessage = m => {
      const d = String(m.data);
      if (d === "2") return c.ws.send("3");
      if (d.startsWith("40")) {
        clearTimeout(timer);
        return res(c);
      }
      if (d.startsWith("43")) {
        const id = /^43(\d+)/.exec(d);
        const done = id && c.acks.get(Number(id[1]));
        if (done) done(true);
        return;
      }
      if (d.startsWith("41") || d.startsWith("44")) {
        clearTimeout(timer);
        drop(base, c);
        return res(null);
      }
      if (d.startsWith("0")) c.ws.send("40");
    };
    c.ws.onerror = () => {};
    c.ws.onclose = () => {
      clearTimeout(timer);
      drop(base, c);
      res(null);
    };
  });
  return c.ready;
};

const rtt = async base => {
  const c = await link(base);
  if (!c) return null;
  const id = c.id++;
  const t = performance.now();
  return new Promise(res => {
    const timer = setTimeout(() => {
      c.acks.delete(id);
      drop(base, c);
      res(null);
    }, WAIT);
    c.acks.set(id, ok => {
      clearTimeout(timer);
      c.acks.delete(id);
      res(ok ? Math.round(performance.now() - t) : null);
    });
    try {
      c.ws.send(`42${id}["rtt"]`);
    } catch (e) {
      c.acks.get(id)(false);
    }
  });
};

const adapters = () =>
  Object.entries(os.networkInterfaces()).flatMap(([name, all]) =>
    (all || []).filter(a => !a.internal && (a.family === "IPv4" || a.family === 4)).map(a => ({ name, address: a.address }))
  );

const scan = async () => {
  const all = services();
  const rows = await Promise.all(
    all.map(async s => ({
      name: s.name,
      hosts: await Promise.all(
        s.hosts.map(async h => {
          const at = `${h.url}/app/manifest.json`;
          const live = socketed(s.name);
          const [manifest, wsMs] = await Promise.all([get(at), live ? rtt(h.url) : null]);
          const ms = live ? wsMs : manifest.status === "online" ? await ping(at) : null;
          return { url: h.url, host: new URL(h.url).host, role: h.role, manifest, ms };
        })
      )
    }))
  );
  return {
    pc: net.isOnline(),
    adapters: adapters(),
    services: rows.map(s => ({
      name: s.name,
      hosts: s.hosts.map(h => ({ host: h.host, role: h.role, status: h.manifest.status, code: h.manifest.code || null, ms: h.ms })),
      raw: s.hosts
    }))
  };
};

const report = r => ({ pc: r.pc, adapters: r.adapters, services: r.services.map(({ name, hosts }) => ({ name, hosts })) });

const probe = async () => {
  const r = await scan();
  const up = name => {
    const s = r.services.find(x => x.name === name);
    const hit = s && s.raw.find(h => h.manifest.status === "online");
    return hit ? { base: hit.url } : null;
  };
  return { live: up("prono"), musik: up("nullpunkts"), report: report(r) };
};

const order = (first, name) => [...new Set([first, ...servers(name)].filter(Boolean))];

const MUSIK = ["nd-category-positions.json", "nd-song-durations.json", "m2list.json", "nd-song-categories.json", "nd-category-routes.json"];

const musik = async file => {
  if (!MUSIK.includes(file)) return null;
  for (const base of servers("nullpunkts")) {
    const r = await get(`${base}/m/${file}`);
    if (r.status === "online") return r.body;
  }
  return null;
};

module.exports = { GATE, WAIT, hubs, servers, needs, probe, scan: async () => report(await scan()), order, musik };
