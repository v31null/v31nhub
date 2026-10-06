const fs = require("fs");
const fsp = fs.promises;
const path = require("path");
const crypto = require("crypto");
const { once } = require("events");
const { spawn, execFile } = require("child_process");
const { app, shell } = require("electron");

const NAME = "V31null Hub.exe";
const { GATE, WAIT, hubs } = require("./net.js");
const KEY = "HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\V31null Hub";
const PRONO = "HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\Prono";

const local = () => process.env.LOCALAPPDATA || app.getPath("appData");
const home = () => path.join(local(), "Programs", "V31null Hub");
const installed = () => path.join(home(), NAME);
const links = () => [
  path.join(app.getPath("appData"), "Microsoft", "Windows", "Start Menu", "Programs", "V31null Hub.lnk"),
  path.join(app.getPath("desktop"), "V31null Hub.lnk")
];
const downloads = () => path.join(app.getPath("temp"), "V31nullHub");

const sleep = t => new Promise(r => setTimeout(r, t));
const run = (file, args) => new Promise(res => execFile(file, args, { windowsHide: true }, e => res(!e)));
const read = (file, args) => new Promise(res => execFile(file, args, { windowsHide: true }, (e, out) => res(e ? "" : String(out))));

const plain = async fn => {
  process.noAsar = true;
  try {
    return await fn();
  } finally {
    process.noAsar = false;
  }
};

const cmp = (a, b) => {
  const x = String(a).split(".").map(Number), y = String(b).split(".").map(Number);
  for (let i = 0; i < Math.max(x.length, y.length); i++) {
    if ((x[i] || 0) !== (y[i] || 0)) return (x[i] || 0) - (y[i] || 0);
  }
  return 0;
};

const value = async (key, name) => {
  const m = (await read("reg.exe", ["query", key, "/v", name])).match(new RegExp(`^\\s+${name}\\s+REG_\\w+\\s+(.*)$`, "m"));
  return m ? m[1].trim() : null;
};

const flags = argv => argv.slice(1).filter(a => a.startsWith("--"));

const clean = () => Object.fromEntries(Object.entries(process.env).filter(([k]) => !/^PORTABLE_EXECUTABLE_/i.test(k)));

const portable = () => Boolean(process.env.PORTABLE_EXECUTABLE_FILE) && path.resolve(path.dirname(process.execPath)).toLowerCase() !== path.resolve(home()).toLowerCase();

const size = dir =>
  fs.readdirSync(dir, { withFileTypes: true }).reduce((n, e) => {
    const p = path.join(dir, e.name);
    return n + (e.isDirectory() ? size(p) : fs.statSync(p).size);
  }, 0);

const place = () =>
  plain(async () => {
    const src = path.dirname(process.execPath);
    const dst = home(), stage = `${dst}.new`, old = `${dst}.old`;
    await fsp.rm(stage, { recursive: true, force: true });
    await fsp.cp(src, stage, { recursive: true });
    for (let i = 0; i < 60; i++) {
      try {
        await fsp.rm(old, { recursive: true, force: true });
        if (fs.existsSync(dst)) await fsp.rename(dst, old);
        await fsp.rename(stage, dst);
        await fsp.rm(old, { recursive: true, force: true }).catch(() => {});
        return true;
      } catch (e) {
        if (!fs.existsSync(dst) && fs.existsSync(old)) await fsp.rename(old, dst).catch(() => {});
        await sleep(500);
      }
    }
    await fsp.rm(stage, { recursive: true, force: true }).catch(() => {});
    return false;
  });

const register = async () => {
  const exe = installed();
  const kb = Math.round((await plain(async () => size(home()))) / 1024);
  const entries = [
    ["DisplayName", "REG_SZ", "V31null Hub"],
    ["DisplayVersion", "REG_SZ", app.getVersion()],
    ["Publisher", "REG_SZ", "V31null"],
    ["InstallLocation", "REG_SZ", home()],
    ["DisplayIcon", "REG_SZ", exe],
    ["UninstallString", "REG_SZ", `"${exe}" --remove-hub`],
    ["QuietUninstallString", "REG_SZ", `"${exe}" --remove-hub`],
    ["EstimatedSize", "REG_DWORD", kb],
    ["NoModify", "REG_DWORD", 1],
    ["NoRepair", "REG_DWORD", 1]
  ];
  for (const [name, type, data] of entries) await run("reg.exe", ["add", KEY, "/v", name, "/t", type, "/d", String(data), "/f"]);
  for (const lnk of links()) {
    fs.mkdirSync(path.dirname(lnk), { recursive: true });
    shell.writeShortcutLink(lnk, "create", { target: exe, cwd: home(), icon: exe, iconIndex: 0 });
  }
};

const adopt = async () => {
  const want = `"${installed()}" --uninstall`;
  const now = await value(PRONO, "UninstallString");
  if (now && now !== want) await run("reg.exe", ["add", PRONO, "/v", "UninstallString", "/t", "REG_SZ", "/d", want, "/f"]);
  await fsp.rm(path.join(local(), "V31null Hub"), { recursive: true, force: true }).catch(() => {});
};

const setup = async argv => {
  const have = fs.existsSync(installed()) ? await value(KEY, "DisplayVersion") : null;
  if (!have || cmp(app.getVersion(), have) > 0) {
    if (!(await place())) return false;
    await register();
  }
  await adopt();
  spawn(installed(), flags(argv), { detached: true, stdio: "ignore", env: clean() }).unref();
  return true;
};

const valid = h =>
  h && h.file === NAME && /^\d+(\.\d+){1,3}$/.test(h.version) && /^[0-9a-f]{64}$/i.test(h.sha256) && Number.isFinite(h.bytes) && h.bytes > 0;

const fingerprint = async url => {
  try {
    const res = await fetch(url, { cache: "no-store", headers: GATE, signal: AbortSignal.timeout(WAIT) });
    if (!res.ok) return null;
    const h = await res.json();
    return valid(h) ? h : null;
  } catch (e) {
    return null;
  }
};

const check = async () => {
  const all = hubs();
  const ask = async group => (await Promise.all(group.map(async m => ({ m, h: await fingerprint(m.url) })))).filter(x => x.h);
  let got = await ask(all.filter(m => m.role === "primary"));
  if (!got.length) got = await ask(all.filter(m => m.role === "backup"));
  if (!got.length) return null;
  const best = got.reduce((a, b) => (cmp(b.h.version, a.h.version) > 0 ? b : a));
  return { hub: best.h, from: [...new Set([best.m.exe, ...all.map(m => m.exe)])] };
};

const update = async ({ fresh, argv }) => {
  if (!fresh) return false;
  const h = fresh.hub;
  const current = app.getVersion();
  const tried = argv.map(a => a.match(/^--updated=(.+)$/)).find(Boolean);
  if (tried && cmp(tried[1], current) > 0) return false;
  if (!valid(h) || cmp(h.version, current) <= 0) return false;
  const next = path.join(downloads(), `V31null Hub ${h.version}.exe`);
  for (const url of fresh.from) {
    try {
      await fsp.mkdir(downloads(), { recursive: true });
      const res = await fetch(url, { headers: GATE, signal: AbortSignal.timeout(15 * 60 * 1000) });
      if (!res.ok) throw new Error("http");
      const hash = crypto.createHash("sha256");
      const out = fs.createWriteStream(next);
      let got = 0;
      try {
        for await (const chunk of res.body) {
          hash.update(chunk);
          got += chunk.length;
          if (!out.write(chunk)) await once(out, "drain");
        }
      } finally {
        await new Promise(r => out.end(r));
      }
      if (got !== h.bytes || hash.digest("hex") !== h.sha256.toLowerCase()) throw new Error("hash");
    } catch (e) {
      await fsp.rm(next, { force: true }).catch(() => {});
      continue;
    }
    spawn(next, [...flags(argv).filter(a => !a.startsWith("--updated=")), `--updated=${h.version}`], { detached: true, stdio: "ignore", env: clean() }).unref();
    return true;
  }
  return false;
};

const tidy = () => fsp.rm(downloads(), { recursive: true, force: true }).catch(() => {});

const remove = async () => {
  for (const lnk of links()) await fsp.rm(lnk, { force: true }).catch(() => {});
  await run("reg.exe", ["delete", KEY, "/f"]);
  const q = p => `"${p.replace(/%/g, "%%")}"`;
  const bat = path.join(app.getPath("temp"), "v31null-hub-remove.cmd");
  const lines = [
    "@echo off",
    "chcp 65001 >nul",
    "set n=0",
    ":loop",
    `taskkill /F /T /IM ${q(NAME)} >nul 2>&1`,
    `rmdir /s /q ${q(home())} >nul 2>&1`,
    `if not exist ${q(home())} goto done`,
    "set /a n+=1",
    "if %n% geq 120 goto done",
    "ping -n 2 127.0.0.1 >nul",
    "goto loop",
    ":done",
    `rmdir /s /q ${q(app.getPath("userData"))} >nul 2>&1`,
    '(goto) 2>nul & del "%~f0"'
  ];
  await fsp.writeFile(bat, lines.join("\r\n"), "utf8");
  const helper = spawn("cmd.exe", ["/d", "/c", `start "" /b cmd.exe /d /c ${q(bat)}`], { stdio: "ignore", windowsHide: true, windowsVerbatimArguments: true });
  await Promise.race([once(helper, "exit"), once(helper, "error"), sleep(5000)]);
};

module.exports = { setup, check, update, tidy, remove, installed, portable };
