const fs = require("fs");
const fsp = fs.promises;
const path = require("path");
const crypto = require("crypto");
const { once } = require("events");
const { execFile, spawn } = require("child_process");
const extract = require("extract-zip");
const { app, shell, dialog, ipcMain } = require("electron");
const self = require("./self.js");
const { GATE, needs } = require("./net.js");

const LINUX = process.platform === "linux";
const REG = "HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\Prono";
const EXE = LINUX ? "prono-desktop" : "Prono.exe";
const MANIFEST = LINUX ? "manifest-linux.json" : "manifest.json";
const SHARE = () => process.env.XDG_DATA_HOME || path.join(app.getPath("home"), ".local", "share");
const HIVES = [
  "HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall",
  "HKLM\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall",
  "HKLM\\Software\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall"
];

const plain = async fn => {
  process.noAsar = true;
  try {
    return await fn();
  } finally {
    process.noAsar = false;
  }
};

const sleep = t => new Promise(r => setTimeout(r, t));

const run = (file, args) => new Promise((res, rej) => execFile(file, args, { windowsHide: true }, e => (e ? rej(e) : res())));

const read = (file, args) => new Promise(res => execFile(file, args, { windowsHide: true, maxBuffer: 1 << 24 }, (e, out) => res(e ? "" : String(out))));

const cmp = (a, b) => {
  const x = String(a).split(".").map(Number), y = String(b).split(".").map(Number);
  for (let i = 0; i < Math.max(x.length, y.length); i++) {
    if ((x[i] || 0) !== (y[i] || 0)) return (x[i] || 0) - (y[i] || 0);
  }
  return 0;
};

const values = out =>
  Object.fromEntries(
    out
      .split(/\r?\n/)
      .map(l => l.match(/^\s+(\S+)\s+REG_\w+\s+(.*)$/))
      .filter(Boolean)
      .map(m => [m[1], m[2].trim()])
  );

const fail = code => Object.assign(new Error(code), { hub: code });

const classify = e => {
  if (e.hub) return e.hub;
  if (/^(EBUSY|EPERM|EACCES)$/.test(e.code)) return "busy";
  if (["TypeError", "AbortError", "TimeoutError"].includes(e.name) || e.message === "http") return "net";
  if (/ECONNRESET|ECONNREFUSED|ETIMEDOUT|ENOTFOUND|EAI_AGAIN|UND_ERR/.test(String(e.code || (e.cause && e.cause.code) || ""))) return "net";
  return "fail";
};

const retry = async (fn, tries = 10, wait = 300) => {
  for (let i = 1; ; i++) {
    try {
      return await fn();
    } catch (e) {
      if (i >= tries) throw e;
      await sleep(wait);
    }
  }
};

const listed = () => {
  try {
    const s = JSON.parse(fs.readFileSync(path.join(app.getPath("userData"), "state.json"), "utf8"));
    return { DisplayVersion: s.installed, InstallLocation: s.path };
  } catch (e) {
    return {};
  }
};

const own = async () => {
  const v = LINUX ? listed() : values(await read("reg.exe", ["query", REG]));
  if (!v.DisplayVersion || !v.InstallLocation || !fs.existsSync(path.join(v.InstallLocation, EXE))) return null;
  return { version: v.DisplayVersion, path: v.InstallLocation };
};

const legacy = async () => {
  if (LINUX) return null;
  const found = [];
  for (const hive of HIVES) {
    const keys = (await read("reg.exe", ["query", hive, "/s", "/f", "Prono", "/d"])).split(/\r?\n/).filter(l => l.startsWith("HKEY_"));
    for (const key of keys) {
      if (/\\Uninstall\\Prono$/i.test(key)) continue;
      const v = values(await read("reg.exe", ["query", key]));
      if (!/^Prono( |$)/.test(v.DisplayName || "") || !/^\d+(\.\d+)+$/.test(v.DisplayVersion || "")) continue;
      const quiet = v.QuietUninstallString || (v.UninstallString ? `${v.UninstallString} /S` : "");
      const m = quiet.match(/^"([^"]+)"\s*(.*)$/);
      if (!m) continue;
      found.push({ version: v.DisplayVersion, exe: m[1], args: m[2] });
    }
  }
  found.sort((a, b) => cmp(b.version, a.version));
  return found[0] || null;
};

const elevate = (exe, args) => {
  const q = t => `'${t.replace(/'/g, "''")}'`;
  const cmd = `Start-Process -FilePath ${q(exe)}${args ? ` -ArgumentList ${q(args)}` : ""} -Verb RunAs -Wait`;
  return new Promise(res => execFile("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", cmd], { windowsHide: true }, e => res(!e)));
};

const mineOnly = () => ["-x", "-u", String(process.getuid()), EXE];

const running = async () =>
  LINUX ? /\d/.test(await read("pgrep", mineOnly())) : /Prono\.exe/i.test(await read("tasklist.exe", ["/FI", `IMAGENAME eq ${EXE}`, "/NH"]));

const closeProno = async () => {
  if (!(await running())) return true;
  for (const force of [false, true]) {
    if (LINUX) await read("pkill", [...(force ? ["-KILL"] : []), ...mineOnly()]);
    else await read("taskkill.exe", ["/IM", EXE, "/T", ...(force ? ["/F"] : [])]);
    for (let i = 0; i < 12; i++) {
      if (!(await running())) {
        await sleep(400);
        return true;
      }
      await sleep(250);
    }
  }
  return false;
};

const valid = m =>
  m &&
  /^\d+(\.\d+){1,3}$/.test(m.version) &&
  /^\d{8}$/.test(m.date) &&
  /^[A-Za-z0-9._-]+\.zip$/.test(m.file) &&
  /^[0-9a-f]{64}$/i.test(m.sha256) &&
  [m.bytes, m.unpacked, m.files].every(n => Number.isFinite(n) && n > 0);

module.exports = function core({ win, mirrors, offline, argv }) {
  const banned = offline && needs("prono");
  let bases = mirrors;
  const stateFile = path.join(app.getPath("userData"), "state.json");
  const tempDir = path.join(app.getPath("temp"), "PronoHub");
  const startMenu = LINUX ? path.join(SHARE(), "applications", "prono.desktop") : path.join(app.getPath("appData"), "Microsoft", "Windows", "Start Menu", "Programs", "Prono.lnk");
  const desktop = LINUX ? null : path.join(app.getPath("desktop"), "Prono.lnk");
  const data = path.join(app.getPath("appData"), "prono-desktop");
  const defaultDir = LINUX ? path.join(SHARE(), "Prono") : path.join(process.env.LOCALAPPDATA || app.getPath("appData"), "Programs", "Prono");

  const readState = () => {
    try {
      return JSON.parse(fs.readFileSync(stateFile, "utf8"));
    } catch (e) {
      return {};
    }
  };
  const writeState = s => {
    fs.mkdirSync(path.dirname(stateFile), { recursive: true });
    fs.writeFileSync(stateFile, JSON.stringify(s));
  };

  const send = (channel, msg) => {
    if (win && !win.isDestroyed()) win.webContents.send(channel, msg);
  };

  const free = async dir => {
    let at = dir;
    while (!fs.existsSync(at) && path.dirname(at) !== at) at = path.dirname(at);
    try {
      const s = await fsp.statfs(at);
      return (s.bavail * s.bsize) / 1e9;
    } catch (e) {
      return 0;
    }
  };

  const target = async () => {
    const o = await own();
    return (o && o.path) || readState().path || defaultDir;
  };

  const recover = dir =>
    plain(async () => {
      const old = `${dir}.old`;
      if (!fs.existsSync(dir) && fs.existsSync(old)) await fsp.rename(old, dir).catch(() => {});
      await fsp.rm(`${dir}.new`, { recursive: true, force: true }).catch(() => {});
      await fsp.rm(old, { recursive: true, force: true }).catch(() => {});
    });

  const manifest = async () => {
    if (!offline) {
      for (const base of mirrors) {
        try {
          const res = await fetch(`${base}/app/${MANIFEST}`, { cache: "no-store", headers: GATE, signal: AbortSignal.timeout(8000) });
          if (!res.ok) throw new Error("http");
          const m = await res.json();
          if (!valid(m)) throw new Error("manifest");
          writeState({ ...readState(), manifest: m });
          bases = [base, ...mirrors.filter(b => b !== base)];
          return m;
        } catch (e) {}
      }
    }
    const cached = readState().manifest;
    return valid(cached) ? cached : null;
  };

  const partOf = m => path.join(tempDir, `${m.file}.part`);

  const room = async (m, dir) => {
    let got = 0;
    try {
      got = (await fsp.stat(partOf(m))).size;
    } catch (e) {}
    const fetchNeed = Math.max(m.bytes - got, 0) / 1e9;
    const root = p => path.parse(path.resolve(p)).root.toLowerCase();
    const same = root(tempDir) === root(dir);
    const dirNeed = m.unpacked / 1e9 + (same ? fetchNeed : 0);
    const dirFree = await free(dir);
    const tempFree = same ? dirFree : await free(tempDir);
    if (dirFree >= dirNeed && tempFree >= fetchNeed) return null;
    return dirFree < dirNeed ? { need: dirNeed, free: dirFree } : { need: fetchNeed, free: tempFree };
  };

  let job = null;

  const gate = async () => {
    while (job.paused) await job.gate;
    if (job.cancelled) throw new Error("cancelled");
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

  const download = async (m, part) => {
    let got = 0;
    try {
      got = (await fsp.stat(part)).size;
    } catch (e) {}
    if (got > m.bytes) {
      got = 0;
      await fsp.rm(part, { force: true });
    }
    let mark = Date.now();
    let marked = got;
    let speed = 0;
    const tick = throttle();
    let at = 0;
    while (got < m.bytes) {
      job.ctl = new AbortController();
      let out = null;
      try {
        const res = await fetch(`${bases[at]}/app/${m.file}`, { headers: got ? { ...GATE, Range: `bytes=${got}-` } : GATE, signal: job.ctl.signal });
        if (res.status === 200) got = 0;
        else if (res.status !== 206) throw new Error("http");
        out = fs.createWriteStream(part, { flags: got ? "a" : "w" });
        for await (const chunk of res.body) {
          if (!out.write(chunk)) await once(out, "drain");
          got += chunk.length;
          tick(() => {
            const n = Date.now();
            speed = ((got - marked) / Math.max(n - mark, 1)) * 1000 / 1e6;
            mark = n;
            marked = got;
            send("hub:progress", { phase: "fetch", frac: Math.min(got / m.bytes, 1), speed });
          });
        }
      } catch (e) {
        if (!job.paused && !job.cancelled && ++at >= bases.length) throw e;
      } finally {
        if (out) await new Promise(r => out.end(r));
      }
      await gate();
    }
    send("hub:progress", { phase: "fetch", frac: 1, speed: 0 });
  };

  const verify = (m, part) =>
    new Promise((res, rej) => {
      const h = crypto.createHash("sha256");
      const tick = throttle();
      let n = 0;
      fs.createReadStream(part)
        .on("data", c => {
          h.update(c);
          n += c.length;
          tick(() => send("hub:progress", { phase: "verify", frac: Math.min(n / m.bytes, 1), speed: 0 }));
        })
        .on("end", () => {
          if (h.digest("hex") === m.sha256.toLowerCase()) return res();
          fsp.rm(part, { force: true }).then(() => rej(fail("hash")));
        })
        .on("error", rej);
    });

  const unpack = async (part, dir) => {
    const stage = `${dir}.new`, old = `${dir}.old`;
    if (fs.existsSync(dir) && fs.readdirSync(dir).length > 0 && !fs.existsSync(path.join(dir, EXE))) throw fail("busy");
    await fsp.rm(stage, { recursive: true, force: true });
    await fsp.mkdir(stage, { recursive: true });
    const tick = throttle();
    let n = 0;
    try {
      await extract(part, {
        dir: stage,
        onEntry: (entry, zip) => {
          n++;
          tick(() => send("hub:progress", { phase: "extract", frac: Math.min(n / zip.entryCount, 1), speed: 0 }));
        }
      });
    } catch (e) {
      await fsp.rm(stage, { recursive: true, force: true }).catch(() => {});
      throw e;
    }
    if (!(await closeProno())) {
      await fsp.rm(stage, { recursive: true, force: true }).catch(() => {});
      throw fail("run");
    }
    await fsp.rm(old, { recursive: true, force: true });
    try {
      if (fs.existsSync(dir)) await retry(() => fsp.rename(dir, old));
    } catch (e) {
      await fsp.rm(stage, { recursive: true, force: true }).catch(() => {});
      throw fail("busy");
    }
    try {
      await retry(() => fsp.rename(stage, dir));
    } catch (e) {
      if (fs.existsSync(old)) await fsp.rename(old, dir).catch(() => {});
      await fsp.rm(stage, { recursive: true, force: true }).catch(() => {});
      throw fail("busy");
    }
    await fsp.rm(old, { recursive: true, force: true }).catch(() => {});
    send("hub:progress", { phase: "extract", frac: 1, speed: 0 });
  };

  const entry = async (m, dir) => {
    const exe = path.join(dir, EXE);
    const lines = ["[Desktop Entry]", "Type=Application", "Name=Prono", `Exec="${exe}"`, `Icon=${path.join(dir, "icon.png")}`, "Terminal=false", ""];
    await fsp.mkdir(path.dirname(startMenu), { recursive: true });
    await fsp.writeFile(startMenu, lines.join("\n"), "utf8");
    send("hub:progress", { phase: "link", frac: 0.5, speed: 0 });
    writeState({ ...readState(), installed: m.version, path: dir, manifest: m });
    send("hub:progress", { phase: "link", frac: 1, speed: 0 });
  };

  const link = async (m, dir) => {
    if (LINUX) return entry(m, dir);
    const exe = path.join(dir, EXE);
    const spec = { target: exe, cwd: dir, icon: exe, iconIndex: 0 };
    fs.mkdirSync(path.dirname(startMenu), { recursive: true });
    shell.writeShortcutLink(startMenu, "create", spec);
    shell.writeShortcutLink(desktop, "create", spec);
    send("hub:progress", { phase: "link", frac: 0.5, speed: 0 });
    const hub = fs.existsSync(self.installed()) ? self.installed() : process.env.PORTABLE_EXECUTABLE_FILE || process.execPath;
    const entries = [
      ["DisplayName", "REG_SZ", "Prono"],
      ["DisplayVersion", "REG_SZ", m.version],
      ["Publisher", "REG_SZ", "Prono"],
      ["InstallLocation", "REG_SZ", dir],
      ["DisplayIcon", "REG_SZ", exe],
      ["UninstallString", "REG_SZ", `"${hub}" --uninstall`],
      ["EstimatedSize", "REG_DWORD", Math.round(m.unpacked / 1024)],
      ["NoModify", "REG_DWORD", 1],
      ["NoRepair", "REG_DWORD", 1]
    ];
    for (const [name, type, value] of entries) await run("reg.exe", ["add", REG, "/v", name, "/t", type, "/d", String(value), "/f"]);
    writeState({ ...readState(), installed: m.version, path: dir, manifest: m });
    send("hub:progress", { phase: "link", frac: 1, speed: 0 });
  };

  const install = async (m, dir) => {
    const part = partOf(m);
    await fsp.mkdir(tempDir, { recursive: true });
    await download(m, part);
    await gate();
    await verify(m, part);
    await gate();
    await plain(() => unpack(part, dir));
    await link(m, dir);
    await fsp.rm(part, { force: true });
  };

  ipcMain.handle("hub:info", async () => {
    const st = readState();
    const o = await own();
    const dir = (o && o.path) || st.path || defaultDir;
    await recover(dir);
    const m = await manifest();
    const old = o ? null : await legacy();
    const installed = (o && o.version) || (old && old.version) || null;
    return {
      manifest: m,
      installed,
      legacy: Boolean(old),
      path: dir,
      free: await free(dir),
      autoUninstall: argv.includes("--uninstall") && Boolean(installed),
      banned
    };
  });

  ipcMain.handle("hub:start", async () => {
    if (job) return { ok: true };
    if (banned) return { ok: false, code: "net" };
    const m = await manifest();
    if (!m) return { ok: false, code: "net" };
    const dir = await target();
    await recover(dir);
    const short = await room(m, dir);
    if (short) return { ok: false, space: short };
    job = { paused: false, cancelled: false, ctl: null, gate: null, release: () => {} };
    install(m, dir)
      .then(() => send("hub:done", {}))
      .catch(e => {
        if (!job.cancelled) send("hub:error", { code: classify(e) });
      })
      .finally(() => {
        job = null;
      });
    return { ok: true };
  });

  ipcMain.handle("hub:pause", () => {
    if (!job || job.paused) return;
    job.paused = true;
    job.gate = new Promise(r => {
      job.release = r;
    });
    if (job.ctl) job.ctl.abort();
  });

  ipcMain.handle("hub:resume", () => {
    if (!job || !job.paused) return;
    job.paused = false;
    job.release();
  });

  ipcMain.handle("hub:cancel", () => {
    if (!job) return;
    job.cancelled = true;
    job.paused = false;
    if (job.ctl) job.ctl.abort();
    job.release();
  });

  const removeLegacy = async () => {
    const old = await legacy();
    if (!old) return { ok: false };
    if (!(await closeProno())) return { ok: false, code: "run" };
    if (!(await elevate(old.exe, old.args))) return { ok: false };
    for (let i = 0; i < 60; i++) {
      const now = await legacy();
      if (!now || now.version !== old.version) return { ok: true };
      await sleep(500);
    }
    return { ok: false };
  };

  const removeOwn = async o => {
    const dir = o.path;
    if (!(await closeProno())) return { ok: false, code: "run" };
    const gone = `${dir}.removing`;
    try {
      await fsp.rm(gone, { recursive: true, force: true });
      await retry(() => fsp.rename(dir, gone));
    } catch (e) {
      return { ok: false, code: "busy" };
    }
    await plain(() => fsp.rm(gone, { recursive: true, force: true, maxRetries: 5, retryDelay: 300 })).catch(() => {});
    await fsp.rm(startMenu, { force: true });
    if (!LINUX) {
      await fsp.rm(desktop, { force: true });
      await run("reg.exe", ["delete", REG, "/f"]).catch(() => {});
    }
    const { installed, ...rest } = readState();
    writeState(rest);
    return { ok: true };
  };

  ipcMain.handle("hub:uninstall", async (e, o) => {
    if (job) return { ok: false };
    const mine = await own();
    const r = mine ? await removeOwn(mine) : await removeLegacy();
    if (r.ok && o && o.wipe) await fsp.rm(data, { recursive: true, force: true, maxRetries: 5, retryDelay: 300 }).catch(() => {});
    return r;
  });

  ipcMain.handle("hub:launch", async () => {
    if (banned) return false;
    const o = await own();
    if (!o) return false;
    const child = spawn(path.join(o.path, EXE), bases[0] ? [`--prono-host=${bases[0]}`] : [], { cwd: o.path, stdio: "ignore" });
    const back = () => {
      if (win.isDestroyed()) return;
      win.show();
      win.focus();
    };
    child.on("error", back);
    child.on("exit", back);
    win.hide();
    return true;
  });

  ipcMain.handle("hub:path", async () => {
    if (job || (await own())) return null;
    const r = await dialog.showOpenDialog(win, { properties: ["openDirectory", "createDirectory"] });
    if (r.canceled || !r.filePaths[0]) return null;
    const dir = path.join(r.filePaths[0], "Prono");
    writeState({ ...readState(), path: dir });
    return { path: dir, free: await free(dir) };
  });
};
