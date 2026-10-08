const fs = require("fs");
const fsp = fs.promises;
const path = require("path");
const crypto = require("crypto");
const zlib = require("zlib");
const { once } = require("events");
const { app } = require("electron");
const { GATE } = require("./net.js");

const SAMPLE = 65536;
const PACK = "/m/m2.v31np";
const MAGIC = Buffer.from("V31NSPACKFORMAT");
const PI = Buffer.from("243F6A8885A308D313198A2E", "hex");
const PHI = Buffer.from("9E3779B97F4A7C15F39CC060", "hex");
const LIMIT = 2 ** 52;
const TYPES = {
  mp3: "audio/mpeg",
  wav: "audio/x-wav",
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

const rows = buf => {
  const out = [];
  let row = null, from = 0;
  for (;;) {
    const hit = buf.indexOf(PHI, from);
    const end = hit < 0 ? buf.length : hit - 1;
    if (row) row.push(buf.subarray(from, end));
    else if (end !== 0) return null;
    if (hit < 0) return out;
    if (hit < 1) return null;
    if (buf[hit - 1] === 0) out.push((row = []));
    else if (buf[hit - 1] !== 1 || !row) return null;
    from = hit + PHI.length;
  }
};

const num = b => {
  if (!b.length || b.length > 7) return null;
  const wide = Buffer.alloc(8);
  b.copy(wide);
  const n = wide.readUInt32LE(4) * 2 ** 32 + wide.readUInt32LE(0);
  return n < LIMIT ? n : null;
};

const crcOf = async (file, crc) => {
  try {
    for await (const chunk of fs.createReadStream(file)) crc = zlib.crc32(chunk, crc);
    return crc;
  } catch (e) {
    throw fail("fail");
  }
};

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
  const stateFile = path.join(dir, "m2.v31np");

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

  const header = async (url, signal, list) => {
    const most = MAGIC.length + 3 * PI.length + Object.keys(list).reduce((n, k) => n + 105 + 5 * Buffer.byteLength(k), 0);
    const res = await fetch(url, { headers: GATE, signal });
    if (!res.ok) throw new Error("http");
    let buf = Buffer.alloc(0);
    for await (const chunk of res.body) {
      buf = Buffer.concat([buf, chunk]);
      if (!buf.subarray(0, MAGIC.length).equals(MAGIC.subarray(0, Math.min(buf.length, MAGIC.length)))) throw fail("fail");
      const a = MAGIC.length;
      if (buf.length >= a + PI.length && !buf.subarray(a, a + PI.length).equals(PI)) throw fail("fail");
      const b = buf.length >= a + PI.length ? buf.indexOf(PI, a + PI.length) : -1;
      const c = b < 0 ? -1 : buf.indexOf(PI, b + PI.length);
      if (c >= 0) return buf.subarray(0, c + PI.length);
      if (buf.length > most) throw fail("fail");
    }
    throw fail("fail");
  };

  const parse = (head, total, list) => {
    const a = MAGIC.length + PI.length;
    const b = head.indexOf(PI, a), c = head.indexOf(PI, b + PI.length);
    const t1 = rows(head.subarray(a, b)), t2 = rows(head.subarray(b + PI.length, c));
    if (!t1 || !t2 || t1.length !== t2.length) return null;
    const sizes = new Map();
    for (const r of t2) {
      if (r.length !== 5) return null;
      const key = r[0].toString(), size = num(r[1]);
      if (size === null || sizes.has(key) || !Buffer.concat([r[2], r[3], r[4]]).equals(r[0])) return null;
      sizes.set(key, size);
    }
    const files = [];
    let next = c + PI.length;
    for (const r of t1) {
      if (r.length !== 2) return null;
      const key = r[0].toString(), at = num(r[1]);
      if (at !== next || !sizes.has(key) || !Object.hasOwn(list, key) || !where(key)) return null;
      files.push({ key, at, size: sizes.get(key) });
      next = at + sizes.get(key);
      sizes.delete(key);
    }
    return next === total - 16 ? { files, end: next } : null;
  };

  const pack = async ({ l, done, record, drop, bytes }) => {
    let at = 0, absent = 0;
    for (;;) {
      await gate();
      job.ctl = new AbortController();
      const signal = job.ctl.signal;
      const w = { out: null };
      try {
        const url = `${mirrors[at]}${PACK}`;
        const tail = await fetch(url, { headers: { ...GATE, Range: "bytes=-16" }, signal });
        if (tail.status === 404) throw Object.assign(new Error("absent"), { absent: true });
        if (tail.status !== 206) throw new Error("http");
        const total = Number(String(tail.headers.get("content-range") || "").split("/")[1]);
        const foot = Buffer.from(await tail.arrayBuffer());
        if (!Number.isSafeInteger(total) || total >= LIMIT || foot.length !== 16 || !foot.subarray(0, PI.length).equals(PI)) throw fail("fail");
        const head = await header(url, signal, l.list);
        const b = parse(head, total, l.list);
        if (!b) throw fail("fail");
        const state = Buffer.concat([head, foot]);
        const saved = await fsp.readFile(stateFile).catch(() => null);
        if (!saved || !saved.equals(state)) {
          await drop(b.files.map(f => f.key));
          await fsp.mkdir(dir, { recursive: true });
          const tmp = `${stateFile}.${process.pid}.tmp`;
          await fsp.writeFile(tmp, state);
          await fsp.rename(tmp, stateFile);
        }
        let i = b.files.findIndex(f => !done.has(f.key));
        if (i < 0) i = b.files.length;
        let crc = zlib.crc32(head);
        for (const f of b.files.slice(0, i)) crc = await crcOf(where(f.key), crc);
        let pos = i < b.files.length ? b.files[i].at : b.end;
        if (pos < b.end) {
          const res = await fetch(url, { headers: { ...GATE, Range: `bytes=${pos}-${b.end - 1}` }, signal });
          if (res.status !== 206 || !String(res.headers.get("content-range") || "").startsWith(`bytes ${pos}-`)) throw new Error("http");
          const close = async () => {
            const f = b.files[i++];
            if (!w.out) return;
            const out = w.out;
            w.out = null;
            await new Promise(r => out.end(r));
            const file = where(f.key), part = `${file}.part`, mime = mimeOf(f.key, "");
            const got = await print(part, f.key, mime);
            if (got.fp !== l.list[f.key]) return fsp.rm(part, { force: true });
            await fsp.rm(file, { force: true });
            await fsp.rename(part, file);
            await record(f.key, { fp: got.fp, size: got.size, mime });
          };
          const open = async () => {
            while (i < b.files.length) {
              const f = b.files[i];
              if (!done.has(f.key)) {
                const file = where(f.key);
                await fsp.mkdir(path.dirname(file), { recursive: true });
                w.out = fs.createWriteStream(`${file}.part`);
              }
              if (f.size > 0) return;
              await close();
            }
          };
          await open();
          for await (const piece of res.body) {
            const chunk = Buffer.from(piece.buffer, piece.byteOffset, piece.byteLength);
            crc = zlib.crc32(chunk, crc);
            let o = 0;
            while (o < chunk.length) {
              if (i >= b.files.length) throw fail("fail");
              const f = b.files[i], take = Math.min(chunk.length - o, f.at + f.size - pos);
              if (w.out && !w.out.write(chunk.subarray(o, o + take))) await once(w.out, "drain");
              o += take;
              pos += take;
              bytes(take);
              if (pos === f.at + f.size) {
                await close();
                await open();
              }
            }
          }
          if (pos !== b.end) throw new Error("short");
        }
        if (zlib.crc32(PI, crc) !== foot.readUInt32LE(PI.length)) {
          await drop(b.files.map(f => f.key));
          throw fail("hash");
        }
        return true;
      } catch (e) {
        if (w.out) await new Promise(r => w.out.end(r));
        if (e.hub) throw e;
        if (job.cancelled) throw e;
        if (job.paused) continue;
        if (e.absent) absent++;
        if (++at >= mirrors.length) {
          if (absent === mirrors.length) return false;
          throw fail("net");
        }
      }
    }
  };

  const run = async () => {
    const l = await list();
    if (!l) throw fail("net");
    const keys = Object.keys(l.list);
    const have = readIndex();
    const fresh = fs.existsSync(stateFile) || !Object.keys(have.files).length;
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
    const done = new Set(Object.keys(keep));
    const base = done.size;
    const show = extra => send("hub:progress", { phase: "fetch", frac: Math.min(1, Math.max(0, done.size - base + extra) / Math.max(todo.length, 1)), speed });
    const bytes = extra => n => {
      moved += n;
      tick(() => {
        const now = Date.now();
        speed = (moved / Math.max(now - mark, 1)) * 1000 / 1e6;
        mark = now;
        moved = 0;
        show(extra);
      });
    };
    const flush = async () => {
      if (!Object.keys(batch).length) return;
      await writeIndex({ files: batch });
      batch = {};
    };
    if (fresh) {
      await pack({
        l,
        done,
        bytes: bytes(0),
        record: async (key, ent) => {
          batch[key] = ent;
          done.add(key);
          if (Object.keys(batch).length >= 25) await flush();
          show(0);
        },
        drop: async names => {
          await flush();
          names.forEach(k => done.delete(k));
          await writeIndex({}, names);
        }
      });
    }
    for (const key of keys.filter(k => !done.has(k))) {
      batch[key] = await fetchOne(key, l.list[key], bytes(1));
      done.add(key);
      if (Object.keys(batch).length >= 25) await flush();
      show(0);
    }
    await fsp.rm(stateFile, { force: true });
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
