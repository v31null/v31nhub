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
          const manifest = await get(`${h.url}/app/manifest.json`);
          return { url: h.url, host: new URL(h.url).host, role: h.role, manifest };
        })
      )
    }))
  );
  return {
    pc: net.isOnline(),
    adapters: adapters(),
    services: rows.map(s => ({
      name: s.name,
      hosts: s.hosts.map(h => ({ host: h.host, role: h.role, status: h.manifest.status, code: h.manifest.code || null, ms: h.manifest.ms })),
      raw: s.hosts
    }))
  };
};

const report = r => ({ pc: r.pc, adapters: r.adapters, services: r.services.map(({ name, hosts }) => ({ name, hosts })) });

const probe = async () => {
  const r = await scan();
  const prono = r.services.find(s => s.name === "prono");
  const hit = prono && prono.raw.find(h => h.manifest.status === "online");
  return { live: hit ? { base: hit.url } : null, report: report(r) };
};

const order = first => [...new Set([first, ...servers()].filter(Boolean))];

module.exports = { GATE, WAIT, hubs, servers, needs, probe, scan: async () => report(await scan()), order };
