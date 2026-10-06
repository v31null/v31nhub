const fs = require("fs");
const os = require("os");
const { execFile } = require("child_process");

let cpu = null;
let wire = null;

const ticks = () =>
  os.cpus().reduce(
    (a, c) => {
      const t = c.times;
      a.idle += t.idle;
      a.all += t.user + t.nice + t.sys + t.idle + t.irq;
      return a;
    },
    { idle: 0, all: 0 }
  );

const proc = () => {
  try {
    const rows = fs
      .readFileSync("/proc/net/dev", "utf8")
      .split("\n")
      .map(l => l.trim().match(/^([^:]+):\s*(\d+)(?:\s+\d+){7}\s+(\d+)/))
      .filter(m => m && m[1] !== "lo");
    return { rx: rows.reduce((n, m) => n + Number(m[2]), 0), tx: rows.reduce((n, m) => n + Number(m[3]), 0), at: Date.now() };
  } catch (e) {
    return null;
  }
};

const netstat = () =>
  new Promise(res =>
    execFile("netstat", ["-e"], { windowsHide: true, timeout: 2000 }, (e, out) => {
      if (e) return res(null);
      const row = String(out)
        .split(/\r?\n/)
        .map(l => l.trim().match(/(\d+)\s+(\d+)$/))
        .find(Boolean);
      res(row ? { rx: Number(row[1]), tx: Number(row[2]), at: Date.now() } : null);
    })
  );

const bytes = async () => (process.platform === "linux" ? proc() : netstat());

const sample = async () => {
  const t = ticks();
  const b = await bytes();
  const out = { cpu: null, ram: Math.round((1 - os.freemem() / os.totalmem()) * 100), rx: null, tx: null };
  if (cpu) {
    const all = t.all - cpu.all;
    if (all > 0) out.cpu = Math.round((1 - (t.idle - cpu.idle) / all) * 100);
  }
  if (b && wire && b.at > wire.at) {
    const s = (b.at - wire.at) / 1000;
    out.rx = Math.max(0, (b.rx - wire.rx) / 1024 / s);
    out.tx = Math.max(0, (b.tx - wire.tx) / 1024 / s);
  }
  cpu = t;
  wire = b;
  return out;
};

module.exports = { sample };
