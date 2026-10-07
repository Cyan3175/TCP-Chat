'use strict'

/**
 * Preload bridge. The renderer runs with `contextIsolation: true` and
 * `nodeIntegration: false`, so this file is the only surface it can reach.
 *
 * The API is deliberately coarse-grained: the renderer asks for behaviour
 * ("send this", "give me the current state"), never for raw filesystem, network
 * or crypto primitives.
 */

const { contextBridge, ipcRenderer, webFrame } = require('electron')

/** Main -> renderer pushes. */
const EVENTS = [
  'message-added',
  'message-removed',
  'messages-reset',
  'status',
  'synced',
  'error',
  'diag',
  'cipher-state',
  'connection',
  'settings-changed',
  'glass-state',
  'glass-luma',
  'window-state',
]

function subscribe(channel, handler) {
  if (typeof handler !== 'function') return () => {}
  const listener = (_event, payload) => {
    // A throwing view callback must not take the IPC channel down with it.
    try {
      handler(payload)
    } catch (err) {
      console.error(`[tcpchat] listener for "${channel}" threw`, err)
    }
  }
  ipcRenderer.on(channel, listener)
  return () => ipcRenderer.removeListener(channel, listener)
}

const api = {
  /** Renderer/host versions, for the About line in settings. */
  versions: {
    // The main process always sets this; the fallback is derived so it cannot
    // drift from package.json when the app runs without it.
    app: process.env.TCPCHAT_VERSION || require('../../package.json').version,
    electron: process.versions.electron,
    chrome: process.versions.chrome,
    node: process.versions.node,
  },

  settings: {
    get: () => ipcRenderer.invoke('settings:get'),
    update: (patch) => ipcRenderer.invoke('settings:update', patch),
    test: (candidate) => ipcRenderer.invoke('settings:test', candidate),
  },

  chat: {
    /** Full renderer state: messages + connection + stats. */
    state: () => ipcRenderer.invoke('chat:state'),
    reconnect: () => ipcRenderer.invoke('chat:reconnect'),
    sendText: (text, quote) => ipcRenderer.invoke('chat:send-text', { text, quote }),
    sendFiles: (paths, kind, durationMs) =>
      ipcRenderer.invoke('chat:send-files', { paths, kind, durationMs }),
    /** Persist a recorded voice note and post it as a kind-5 attachment. */
    sendVoice: (bytes, seconds, durationMs) =>
      ipcRenderer.invoke('chat:send-voice', { bytes, seconds, durationMs }),
    deleteMessage: (remoteName) => ipcRenderer.invoke('chat:delete', { remoteName }),
    /** Download + decrypt into the local cache; resolves to a `tcpcache:` URL. */
    ensureAttachment: (remoteName) => ipcRenderer.invoke('chat:ensure-attachment', { remoteName }),
    openAttachment: (remoteName) => ipcRenderer.invoke('chat:open-attachment', { remoteName }),
    saveAttachment: (remoteName) => ipcRenderer.invoke('chat:save-attachment', { remoteName }),
    openExternal: (url) => ipcRenderer.invoke('chat:open-external', { url }),
  },

  glass: {
    status: () => ipcRenderer.invoke('glass:status'),
    setEnabled: (enabled) => ipcRenderer.invoke('glass:set-enabled', { enabled }),
    setQuality: (quality) => ipcRenderer.invoke('glass:set-quality', { quality }),
    /** Rebuild the panel after a driver or display change. */
    retry: () => ipcRenderer.invoke('glass:retry'),
    /** Bubble rects (content-relative CSS px) — one native panel each. */
    setBubbles: (rects) => ipcRenderer.invoke('glass:set-bubbles', { rects }),
    /** Addon counters, for diagnostics. */
    stats: () => ipcRenderer.invoke('glass:stats'),
  },

  files: {
    /** Native picker; returns [] when the user cancels. */
    pick: (kind) => ipcRenderer.invoke('files:pick', { kind }),
  },

  window: {
    state: () => ipcRenderer.invoke('window:state'),
    minimize: () => ipcRenderer.invoke('window:minimize'),
    toggleMaximize: () => ipcRenderer.invoke('window:toggle-maximize'),
    close: () => ipcRenderer.invoke('window:close'),
    /** Whole-window screenshot, written straight to the clipboard. */
    capture: () => ipcRenderer.invoke('window:capture'),
    /** Copy whatever image is rendered at that point in the page. */
    copyImageAt: (point) => ipcRenderer.invoke('window:copy-image-at', point),
    /** Absolute bounds in DIP — used by the custom resize handles. */
    setBounds: (bounds) => ipcRenderer.invoke('window:set-bounds', bounds),
    getBounds: () => ipcRenderer.invoke('window:get-bounds'),
    /*
     * Zoom, in Chromium zoom levels rather than a factor.
     *
     * A level is what the browser's own zoom is: 1.2 ** level, so 0 is 100% and
     * the menu's -3..3 is roughly 58%..173%. Levels are used because they are what
     * Chromium already speaks — Ctrl+wheel raises a zoom-changed event carrying
     * one, and the main process applies and persists that directly, instead of a
     * factor being recomputed here and pushed at a frame.
     *
     * Applied from the main process, on the web contents rather than the frame, so
     * a reload comes back at the zoom it was left at. setZoomFactor here would only
     * ever reach this document.
     *
     * What it solves is unchanged: the CSS pixel itself scales, so every dimension
     * in the app moves together. Driving a font-size variable instead meant the
     * message text grew while the composer did not, and the placeholder spilled out
     * of its box.
     */
    setZoomLevel: (level) => ipcRenderer.invoke('window:set-zoom-level', { level }),
    getZoomLevel: () => ipcRenderer.invoke('window:get-zoom-level'),
  },

  app: {
    openDataDir: () => ipcRenderer.invoke('app:open-data-dir'),
    openLog: () => ipcRenderer.invoke('app:open-log'),
    /** Flash the taskbar; the OS ignores it while the window is focused. */
    flash: () => ipcRenderer.invoke('app:flash'),
    copyText: (text) => ipcRenderer.invoke('app:copy-text', { text }),
    /** Opens the OS microphone privacy page (Windows). */
    openMicSettings: () => ipcRenderer.invoke('app:open-mic-settings'),
    /** The plain theme's backdrop: { url, path, fit }, url null when none is set. */
    background: () => ipcRenderer.invoke('app:background'),
    /** Preview plus the recently used pictures, both as small data URLs. */
    backgroundRecents: () => ipcRenderer.invoke('app:background-recents'),
    pickBackground: () => ipcRenderer.invoke('app:pick-background'),
    useBackground: (file) => ipcRenderer.invoke('app:use-background', { file }),
    setBackgroundFit: (fit) => ipcRenderer.invoke('app:set-background-fit', { fit }),
    clearBackground: () => ipcRenderer.invoke('app:clear-background'),
    platform: process.platform,
  },

  /** Subscribe to a main-process push. Returns an unsubscribe function. */
  on: subscribe,
  events: EVENTS.slice(),
}

contextBridge.exposeInMainWorld('tcpchat', api)
