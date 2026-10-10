(() => {
const view = window.chrome && window.chrome.webview;
if (!view || window !== window.top || window.pronoDesktop) return;

let seq = 0;
const wait = new Map();
const ons = {};
view.addEventListener("message", (e) => {
  const m = e.data;
  if (m && m.id !== undefined) {
    const p = wait.get(m.id);
    if (!p) return;
    wait.delete(m.id);
    if (m.err !== undefined) p[1](new Error(m.err));
    else p[0](m.value);
    return;
  }
  if (m && ons[m.ch]) ons[m.ch].slice().forEach((cb) => cb(m.data));
});
const invoke = (ch, ...args) =>
  new Promise((res, rej) => {
    const id = seq++;
    wait.set(id, [res, rej]);
    view.postMessage({ id, ch, args });
  });
const send = (ch, ...args) => view.postMessage({ ch, args });
const on = (ch, cb) => {
  (ons[ch] = ons[ch] || []).push(cb);
};

window.pronoDesktop = Object.freeze({
  isDesktop: true,
  onActivity: (cb) => {
    if (typeof cb !== "function") return;
    on("prono-activity", (activity) => cb(activity));
  },
  memory: () => invoke("prono-memory"),
  onMemory: (cb) => {
    if (typeof cb !== "function") return;
    on("prono-memory", (m) => cb(m));
  },
  purge: () => send("prono-purge"),
});

const BAR_H = 16;
const OVERHANG = 8;
const ARGENT = "#f6f6f6";
const SABLE = "#252525";
const GULES = "#ff0000";
const FLAG_ANGLE = "177deg";

let barEl = null;
let maximized = false;

try {
  document.documentElement.classList.add("prono-desktop");
} catch (e) {}

function set(el, styles) {
  for (const k in styles) el.style.setProperty(k, styles[k], "important");
}

function flagGradient() {
  return (
    "linear-gradient(" +
    FLAG_ANGLE +
    ", " +
    ARGENT +
    " 0 33.0%, " +
    SABLE +
    " 33.7% 66.3%, " +
    GULES +
    " 67.0% 100%)"
  );
}

function svg(inner) {
  return (
    '<svg viewBox="0 0 12 12" width="10" height="10" fill="none" stroke="' +
    ARGENT +
    '" stroke-width="2" stroke-linecap="square">' +
    inner +
    "</svg>"
  );
}

const ICON_MIN = svg('<line x1="1" y1="6" x2="11" y2="6"/>');
const ICON_MAX = svg('<rect x="1.5" y="1.5" width="9" height="9"/>');
const ICON_RESTORE = svg('<rect x="1.5" y="3.5" width="7" height="7"/><path d="M3.5 3.5 V1.5 H10.5 V8.5 H8.5"/>');
const ICON_CLOSE = svg('<line x1="1.5" y1="1.5" x2="10.5" y2="10.5"/><line x1="10.5" y1="1.5" x2="1.5" y2="10.5"/>');

function applyChrome() {
  const inset = maximized ? OVERHANG : 0;
  const total = BAR_H + inset;
  if (barEl) {
    barEl.style.setProperty("top", inset + "px", "important");
  }
  const container = document.getElementById("container");
  if (container) {
    set(container, { "margin-top": total + "px", height: "calc(100vh - " + total + "px)" });
  } else if (document.body) {
    set(document.body, { "padding-top": total + "px", "box-sizing": "border-box" });
  }
}

function makeBtn(iconHtml, action, title, isClose) {
  const b = document.createElement("button");
  b.innerHTML = iconHtml;
  b.title = title;
  set(b, {
    "-webkit-app-region": "no-drag",
    width: "40px",
    height: BAR_H + "px",
    display: "flex",
    "align-items": "center",
    "justify-content": "center",
    background: "transparent",
    border: "0",
    margin: "0",
    padding: "0",
    cursor: "pointer",
    "flex-shrink": "0",
    filter: "drop-shadow(0 0 1px rgba(0,0,0,0.85))",
  });
  b.addEventListener("mouseenter", () => {
    b.style.setProperty("background", isClose ? "rgba(0,0,0,0.28)" : "rgba(255,255,255,0.18)", "important");
  });
  b.addEventListener("mouseleave", () => {
    b.style.setProperty("background", "transparent", "important");
  });
  b.addEventListener("click", () => send(action));
  return b;
}

function injectBar() {
  if (!document.body || document.getElementById("prono-titlebar")) return;

  const bar = document.createElement("div");
  bar.id = "prono-titlebar";
  bar.title = "Prono";
  set(bar, {
    position: "fixed",
    top: "0",
    left: "0",
    right: "0",
    width: "100%",
    height: BAR_H + "px",
    background: "transparent",
    display: "flex",
    "align-items": "center",
    "justify-content": "flex-end",
    "z-index": "2147483647",
    "-webkit-app-region": "drag",
    "-webkit-user-select": "none",
    "user-select": "none",
    margin: "0",
    padding: "0",
    "box-sizing": "border-box",
    overflow: "hidden",
  });

  const bg = document.createElement("div");
  set(bg, {
    position: "absolute",
    top: "-10px",
    left: "-10px",
    right: "-10px",
    bottom: "-10px",
    background: flagGradient(),
    filter: "blur(4px)",
    "z-index": "0",
    "pointer-events": "none",
  });

  const controls = document.createElement("div");
  set(controls, {
    display: "flex",
    "align-items": "center",
    height: "100%",
    "-webkit-app-region": "no-drag",
    position: "relative",
    "z-index": "1",
  });

  const min = makeBtn(ICON_MIN, "win:minimize", "Minimize", false);
  const max = makeBtn(ICON_MAX, "win:toggle-maximize", "Maximize", false);
  const close = makeBtn(ICON_CLOSE, "win:close", "Close", true);

  on("win:maximized", (isMax) => {
    maximized = isMax;
    max.innerHTML = isMax ? ICON_RESTORE : ICON_MAX;
    max.title = isMax ? "Restore" : "Maximize";
    applyChrome();
  });

  controls.appendChild(min);
  controls.appendChild(max);
  controls.appendChild(close);
  bar.appendChild(bg);
  bar.appendChild(controls);
  barEl = bar;
  document.body.insertBefore(bar, document.body.firstChild);
}

function boot() {
  injectBar();
  applyChrome();
}

if (document.readyState === "loading") {
  document.addEventListener("DOMContentLoaded", boot);
} else {
  boot();
}
})();
