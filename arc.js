const fs = require("fs");
const fsp = fs.promises;
const path = require("path");
const crypto = require("crypto");
const { once } = require("events");
const { app } = require("electron");
const { GATE } = require("./net.js");

const SAMPLE = 65536;
const TYPES = {
  mp3: "audio/mpeg",
  mp4: "video/mp4",
  webm: "video/webm",
  png: "image/png",
  jpg: "image/jpeg",
  jpeg: "image/jpeg",
  gif: "image/gif",
  svg: "image/svg+xml",
  ico: "image/x-icon",
  woff: "font/woff",
  woff2: "font/woff2",
  ttf: "font/ttf",
  otf: "font/otf",
  css: "text/css",
  js: "text/javascript",
  stl: "model/stl"
};

const mimeOf = (key, raw) => TYPES[key.split(".").pop().toLowerCase()] || String(raw || "").split(";")[0].trim().toLowerCase() || "application/octet-stream";

const print = async (file, key, mime) => {
  const fd = await fsp.open(file, "r");
  try {
    const { size } = await fd.stat();
    const first = Math.min(SAMPLE, size), lastStart = Math.max(first, size - SAMPLE);
    const a = Buffer.alloc(first), b = Buffer.alloc(size - lastStart);
    if (a.length) await fd.read(a, 0, a.length, 0);
    if (b.length) await fd.read(b, 0, b.length, lastStart);
    const fp = crypto.createHash("sha256").update(`${JSON.stringify({ protocol: 1, key, mime, size, sampleBytes: SAMPLE })}\n`).update(a).update(b).digest("hex");
    return { fp, size };
  } finally {
    await fd.close();
  }
};

const fail = code => Object.assign(new Error(code), { hub: code });

const throttle = () => {
  let t = 0;
  return fn => {
    const n = Date.now();
    if (n - t >= 200) {
      t = n;
      fn();
    }
  };
};

module.exports = ({ win, mirrors, offline }) => {
  const dir = path.join(app.getPath("appData"), "M2", "Archive");
  const indexFile = path.join(dir, "index.json");

  const send = (channel, msg) => {
    if (win && !win.isDestroyed()) win.webContents.send(channel, { ...msg, app: "arc" });
  };

  const where = key => {
    let rel;
    try {
      rel = decodeURIComponent(key).split("/").filter(Boolean);
    } catch (e) {
      return null;
    }
    if (!rel.length || rel.some(p => p === "." || p === "..")) return null;
    const file = path.join(dir, ...rel);
    return file.startsWith(dir + path.sep) ? file : null;
  };

  const readIndex = () => {
    try {
      const j = JSON.parse(fs.readFileSync(indexFile, "utf8"));
      return j && typeof j === "object" ? { v: j.v, complete: Boolean(j.complete), files: j.files && typeof j.files === "object" ? j.files : {} } : { files: {} };
    } catch (e) {
      return { files: {} };
    }
  };

  const writeIndex = async (patch, drop = []) => {
    const now = readIndex();
    const next = { ...now, ...patch, files: { ...now.files, ...(patch.files || {}) } };
    drop.forEach(k => delete next.files[k]);
    await fsp.mkdir(dir, { recursive: true });
    const tmp = `${indexFile}.${process.pid}.tmp`;
    await fsp.writeFile(tmp, JSON.stringify(next));
    await fsp.rename(tmp, indexFile);
    return next;
  };

  const list = async () => {
    if (offline) return null;
    for (const base of mirrors) {
      try {
        const res = await fetch(`${base}/m/m2list.json`, { cache: "no-store", headers: GATE, signal: AbortSignal.timeout(8000) });
        if (!res.ok) continue;
        const j = await res.json();
        if (Number.isInteger(j.v) && j.list && typeof j.list === "object") return j;
      } catch (e) {}
    }
    return null;
  };

  const free = async () => {
    let at = dir;
    while (!fs.existsSync(at) && path.dirname(at) !== at) at = path.dirname(at);
    try {
      const s = await fsp.statfs(at);
      return (s.bavail * s.bsize) / 1e9;
    } catch (e) {
      return 0;
    }
  };

  let job = null;

  const gate = async () => {
    while (job.paused) await job.gate;
    if (job.cancelled) throw new Error("cancelled");
  };

  const fetchOne = async (key, want, tick) => {
    const file = where(key);
    if (!file) throw fail("fail");
    const part = `${file}.part`;
    await fsp.mkdir(path.dirname(file), { recursive: true });
    let at = 0;
    for (;;) {
      await gate();
      job.ctl = new AbortController();
      let out = null;
      try {
        const res = await fetch(`${mirrors[at]}${key}`, { headers: GATE, signal: job.ctl.signal });
        if (!res.ok) throw new Error("http");
        const mime = mimeOf(key, res.headers.get("content-type"));
        out = fs.createWriteStream(part);
        for await (const chunk of res.body) {
          if (!out.write(chunk)) await once(out, "drain");
          tick(chunk.length);
        }
        await new Promise(r => out.end(r));
        out = null;
        const got = await print(part, key, mime);
        if (got.fp !== want) {
          await fsp.rm(part, { force: true });
          throw fail("hash");
        }
        await fsp.rm(file, { force: true });
        await fsp.rename(part, file);
        return { fp: got.fp, size: got.size, mime };
      } catch (e) {
        if (out) await new Promise(r => out.end(r));
        if (e.hub) throw e;
        if (job.cancelled) throw e;
        if (job.paused) continue;
        if (++at >= mirrors.length) throw fail("net");
      }
    }
  };

  const run = async () => {
    const l = await list();
    if (!l) throw fail("net");
    const keys = Object.keys(l.list);
    const have = readIndex();
    if (have.v !== l.v || !have.complete) await writeIndex({ v: l.v, complete: false });
    const tick = throttle();
    const todo = [];
    const keep = {};
    for (let i = 0; i < keys.length; i++) {
      await gate();
      const key = keys[i], ent = have.files[key], file = where(key);
      let ok = false;
      if (file && ent && ent.fp === l.list[key] && fs.existsSync(file)) {
        try {
          ok = (await print(file, key, ent.mime)).fp === ent.fp;
        } catch (e) {}
      }
      if (ok) keep[key] = ent;
      else todo.push(key);
      tick(() => send("hub:progress", { phase: "verify", frac: (i + 1) / keys.length, speed: 0 }));
    }
    const stale = Object.keys(have.files).filter(k => !Object.hasOwn(l.list, k));
    for (const k of stale) {
      const f = where(k);
      if (f) await fsp.rm(f, { force: true }).catch(() => {});
    }
    await writeIndex({ files: keep }, [...stale, ...todo]);
    send("hub:progress", { phase: "verify", frac: 1, speed: 0 });

    let mark = Date.now(), moved = 0, speed = 0, batch = {};
    for (let j = 0; j < todo.length; j++) {
      const key = todo[j];
      const show = () => send("hub:progress", { phase: "fetch", frac: (j + 1) / todo.length, speed });
      batch[key] = await fetchOne(key, l.list[key], n => {
        moved += n;
        tick(() => {
          const now = Date.now();
          speed = (moved / Math.max(now - mark, 1)) * 1000 / 1e6;
          mark = now;
          moved = 0;
          show();
        });
      });
      if (Object.keys(batch).length >= 25) {
        await writeIndex({ files: batch });
        batch = {};
      }
      show();
    }
    await writeIndex({ v: l.v, complete: true, files: batch });
    send("hub:progress", { phase: "fetch", frac: 1, speed: 0 });
  };

  return {
    info: async () => {
      const l = await list();
      const idx = readIndex();
      const v = l ? l.v : idx.v;
      const done = idx.complete && Number.isInteger(idx.v) ? String(idx.v) : null;
      return {
        manifest: Number.isInteger(v) ? { version: String(v), files: l ? Object.keys(l.list).length : Object.keys(idx.files).length } : null,
        installed: done,
        path: dir,
        free: await free(),
        autoUninstall: false,
        banned: offline
      };
    },

    start: async () => {
      if (job) return { ok: true };
      if (offline) return { ok: false, code: "net" };
      job = { paused: false, cancelled: false, ctl: null, gate: null, release: () => {} };
      run()
        .then(() => send("hub:done", {}))
        .catch(e => {
          if (!job.cancelled) send("hub:error", { code: e.hub || "fail" });
        })
        .finally(() => {
          job = null;
        });
      return { ok: true };
    },

    pause: () => {
      if (!job || job.paused) return;
      job.paused = true;
      job.gate = new Promise(r => {
        job.release = r;
      });
      if (job.ctl) job.ctl.abort();
    },

    resume: () => {
      if (!job || !job.paused) return;
      job.paused = false;
      job.release();
    },

    cancel: () => {
      if (!job) return;
      job.cancelled = true;
      job.paused = false;
      if (job.ctl) job.ctl.abort();
      job.release();
    },

    uninstall: async () => ({ ok: false }),
    launch: async () => false,
    choosePath: async () => null
  };
};
