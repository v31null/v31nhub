const { contextBridge, ipcRenderer } = require("electron");

const listen = channel => cb => ipcRenderer.on(channel, (e, m) => cb(m));

contextBridge.exposeInMainWorld("hub", {
  platform: process.platform,
  info: key => ipcRenderer.invoke("hub:info", key),
  start: key => ipcRenderer.invoke("hub:start", key),
  pause: key => ipcRenderer.invoke("hub:pause", key),
  resume: key => ipcRenderer.invoke("hub:resume", key),
  cancel: key => ipcRenderer.invoke("hub:cancel", key),
  uninstall: (key, o) => ipcRenderer.invoke("hub:uninstall", key, o),
  minimize: () => ipcRenderer.invoke("hub:min"),
  close: () => ipcRenderer.invoke("hub:close"),
  drag: on => ipcRenderer.send("hub:drag", on),
  launch: (key, stay) => ipcRenderer.invoke("hub:launch", key, stay),
  proceed: () => ipcRenderer.invoke("hub:proceed"),
  quit: () => ipcRenderer.invoke("hub:quit"),
  scan: () => ipcRenderer.invoke("hub:scan"),
  sys: () => ipcRenderer.invoke("hub:sys"),
  board: () => ipcRenderer.invoke("hub:board"),
  musik: file => ipcRenderer.invoke("hub:musik", file),
  tv: s => ipcRenderer.send("hub:tv", s),
  onTv: listen("hub:tv-play"),
  choosePath: key => ipcRenderer.invoke("hub:path", key),
  onProgress: listen("hub:progress"),
  onDone: listen("hub:done"),
  onError: listen("hub:error"),
  onAskUninstall: listen("hub:ask-uninstall")
});
