const { app, BrowserWindow, Menu, protocol, net, ipcMain, screen } = require("electron");
const path = require("path");
const { pathToFileURL } = require("url");
const core = require("./core.js");
const self = require("./self.js");
const link = require("./net.js");
const gauge = require("./sys.js");

const UI = path.join(__dirname, "ui");
const argv = process.argv;
const mode = !app.isPackaged ? "run" : argv.includes("--remove-hub") ? "remove" : self.portable() ? "setup" : "run";

if (process.platform === "linux" && !argv.includes("--no-sandbox")) {
  require("child_process").spawn(process.env.APPIMAGE || process.execPath, [...argv.slice(1), "--no-sandbox"], { detached: true, stdio: "ignore" }).unref();
  process.exit(0);
}

protocol.registerSchemesAsPrivileged([{ scheme: "hub", privileges: { standard: true, secure: true, supportFetchAPI: true } }]);

if (mode === "run" && !app.requestSingleInstanceLock()) {
  app.quit();
} else {
  let win = null;
  let ask = null;

  app.on("second-instance", (e, args) => {
    const w = [win, ask].find(x => x && !x.isDestroyed());
    if (!w) return;
    if (w.isMinimized()) w.restore();
    w.show();
    w.focus();
    if (w !== win) return;
    if (args.includes("--uninstall")) win.webContents.send("hub:ask-uninstall", "prono");
    else if (args.includes("--uninstall-m2")) win.webContents.send("hub:ask-uninstall", "m2");
  });

  const open = (page, width, height) => {
    const w = new BrowserWindow({
      width,
      height,
      frame: false,
      transparent: true,
      resizable: false,
      maximizable: false,
      fullscreenable: false,
      show: false,
      icon: path.join(__dirname, "build", process.platform === "linux" ? "icon.png" : "icon.ico"),
      webPreferences: { preload: path.join(__dirname, "preload.js"), contextIsolation: true, nodeIntegration: false, sandbox: true }
    });
    w.webContents.setWindowOpenHandler(() => ({ action: "deny" }));
    w.webContents.on("will-navigate", e => e.preventDefault());
    w.webContents.on("did-finish-load", () => {
      w.webContents.insertCSS("html, body { background: transparent !important; }");
      w.show();
    });
    w.loadURL(`hub://app/${page}`);
    return w;
  };

  const from = e => BrowserWindow.fromWebContents(e.sender);

  app.whenReady().then(async () => {
    Menu.setApplicationMenu(null);

    if (mode === "remove") {
      await self.remove();
      return app.quit();
    }
    if (mode === "setup") {
      if (await self.setup(argv)) return app.quit();
      if (!app.requestSingleInstanceLock()) return app.quit();
    }

    const [{ live, musik, report }, fresh] = await Promise.all([link.probe(), app.isPackaged ? self.check() : null]);
    if (app.isPackaged) {
      await self.tidy();
      if (await self.update({ fresh, argv })) return app.quit();
    }

    protocol.handle("hub", req => {
      const name = decodeURIComponent(new URL(req.url).pathname);
      const file = path.join(UI, name === "/" ? "download.html" : name);
      if (!file.startsWith(UI + path.sep)) return new Response("", { status: 403 });
      return net.fetch(pathToFileURL(file).toString());
    });

    ipcMain.handle("hub:min", e => from(e) && from(e).minimize());
    ipcMain.handle("hub:close", e => from(e) && from(e).close());
    let drag = null;
    ipcMain.on("hub:drag", (e, on) => {
      clearInterval(drag);
      drag = null;
      const w = from(e);
      if (!on || !w || w.isDestroyed()) return;
      const start = screen.getCursorScreenPoint();
      const [x, y] = w.getPosition();
      drag = setInterval(() => {
        if (w.isDestroyed()) return clearInterval(drag);
        const at = screen.getCursorScreenPoint();
        w.setPosition(x + at.x - start.x, y + at.y - start.y);
      }, 8);
    });

    let first = live ? null : report;
    let answer = null;
    ipcMain.handle("hub:scan", async () => {
      const r = first || (await link.scan());
      first = null;
      return r;
    });
    ipcMain.handle("hub:sys", () => gauge.sample());
    ipcMain.handle("hub:musik", (e, file) => link.musik(file));
    ipcMain.handle("hub:proceed", () => answer && answer(true));
    ipcMain.handle("hub:quit", () => answer && answer(false));

    const board = () =>
      new Promise(res => {
        answer = v => {
          answer = null;
          res(v);
        };
        ask = open("nointernet.html", 900, 280);
        ask.on("closed", () => answer && answer(false));
      });

    const tv = async () => {
      const shot = await win.webContents.capturePage();
      const b = win.getBounds();
      const d = screen.getDisplayMatching(b).bounds;
      const t = new BrowserWindow({
        x: d.x,
        y: d.y,
        width: d.width,
        height: d.height,
        frame: false,
        transparent: true,
        resizable: false,
        movable: false,
        focusable: false,
        skipTaskbar: true,
        hasShadow: false,
        show: false,
        webPreferences: { preload: path.join(__dirname, "preload.js"), contextIsolation: true, nodeIntegration: false, sandbox: true }
      });
      t.setIgnoreMouseEvents(true);
      t.setAlwaysOnTop(true, "screen-saver");
      t.webContents.setWindowOpenHandler(() => ({ action: "deny" }));
      t.webContents.on("will-navigate", e => e.preventDefault());
      const state = s =>
        new Promise(res => {
          const end = () => {
            clearTimeout(cap);
            ipcMain.removeListener("hub:tv", hear);
            res();
          };
          const hear = (e, v) => {
            if (e.sender === t.webContents && v === s) end();
          };
          const cap = setTimeout(end, 3000);
          ipcMain.on("hub:tv", hear);
        });
      const ready = state("ready");
      const done = state("done");
      await t.loadURL("hub://app/tv.html");
      t.showInactive();
      t.webContents.send("hub:tv-play", { src: shot.toDataURL(), x: b.x - d.x, y: b.y - d.y, w: b.width, h: b.height });
      await ready;
      win.hide();
      await done;
      t.destroy();
    };

    let flipping = false;
    ipcMain.handle("hub:board", async () => {
      if (flipping || !win || win.isDestroyed() || (ask && !ask.isDestroyed())) return;
      flipping = true;
      await tv();
      const go = await board();
      flipping = false;
      if (!go) return app.quit();
      if (ask && !ask.isDestroyed()) ask.destroy();
      ask = null;
      win.show();
      win.focus();
    });

    if (!live) {
      if (!(await board())) return app.quit();
      ask.hide();
    }

    win = open("", 900, 560);
    core({
      win,
      argv,
      apps: {
        prono: { mirrors: link.order(live && live.base), offline: !live },
        m2: { mirrors: link.order(musik && musik.base, "nullpunkts"), offline: !musik },
        arc: { mirrors: link.order(musik && musik.base, "nullpunkts"), offline: !musik }
      }
    });
    if (ask && !ask.isDestroyed()) ask.destroy();
    ask = null;
  });

  app.on("window-all-closed", () => app.quit());
}
