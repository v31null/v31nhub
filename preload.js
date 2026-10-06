const { contextBridge, ipcRenderer } = require("electron");

const listen = channel => cb => ipcRenderer.on(channel, (e, m) => cb(m));

contextBridge.exposeInMainWorld("hub", {
  platform: process.platform,
  info:() => ipcRenderer.invoke("hub:info"),
  start: () => ipcRenderer.invoke("hub:start"),
  pause: () => ipcRenderer.invoke("hub:pause"),
  resume: () => ipcRenderer.invoke("hub:resume"),
  cancel: () => ipcRenderer.invoke("hub:cancel"),
  uninstall: o => ipcRenderer.invoke("hub:uninstall", o),
  minimize: () => ipcRenderer.invoke("hub:min"),
  close: () => ipcRenderer.invoke("hub:close"),
  drag: on => ipcRenderer.send("hub:drag", on),
  launch: () => ipcRenderer.invoke("hub:launch"),
  proceed: () => ipcRenderer.invoke("hub:proceed"),
  quit: () => ipcRenderer.invoke("hub:quit"),
  scan: () => ipcRenderer.invoke("hub:scan"),
  sys: () => ipcRenderer.invoke("hub:sys"),
  board: () => ipcRenderer.invoke("hub:board"),
  tv: s => ipcRenderer.send("hub:tv", s),
  onTv: listen("hub:tv-play"),
  choosePath: () => ipcRenderer.invoke("hub:path"),
  onProgress: listen("hub:progress"),
  onDone: listen("hub:done"),
  onError: listen("hub:error"),
  onAskUninstall: listen("hub:ask-uninstall")
});
