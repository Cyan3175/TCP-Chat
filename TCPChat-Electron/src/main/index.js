'use strict'

/**
 * Electron main process: window lifecycle, IPC surface, glass backdrop,
 * attachment protocol and the chat service wiring.
 *
 * Window design: a frameless, transparent window whose rounded "app shell" is
 * drawn in the DOM. Keeping the window transparent at all times means toggling
 * the glass backdrop never requires recreating the window — with glass off the
 * shell simply paints an opaque DSH-themed background instead.
 */

const path = require('path')
const fs = require('fs')
const fsp = require('fs/promises')

const {
  app,
  BrowserWindow,
  Notification,
  clipboard,
  dialog,
  ipcMain,
  nativeImage,
  nativeTheme,
  protocol,
  screen,
  shell,
} = require('electron')

const { log, installCrashHandlers } = require('./logger')
const { ensureDataDir, cacheDir } = require('./paths')
const settingsModule = require('./settings')
const { clampZoomLevel } = settingsModule
const { ChatService, sanitizeFileName } = require('./chat-service')
const { MessageStore } = require('./message-store')
const { GlassController } = require('./glass')
const tray = require('./tray')

const APP_VERSION = require('../../package.json').version
process.env.TCPCHAT_VERSION = APP_VERSION

/**
 * Launched by the login entry rather than by hand.
 *
 * The entry passes this flag, so a normal launch still opens the window while a
 * startup one goes straight to the tray.
 */
const START_IN_TRAY = process.argv.includes('--hidden')

const IS_DEV = process.argv.includes('--dev')
/** `--selftest [--out=<dir>]` seeds fixtures and captures screenshots, then exits. */
const SELFTEST_OUT = (() => {
  const flag = process.argv.find((a) => a === '--selftest' || a.startsWith('--selftest='))
  if (!flag) return null
  const explicit = process.argv.find((a) => a.startsWith('--out='))
  if (explicit) return explicit.slice('--out='.length)
  return path.join(process.cwd(), 'selftest-output')
})()
const SHELL_RADIUS = 12

// ---------------------------------------------------------------------------
// Custom protocol for locally cached attachments
// ---------------------------------------------------------------------------

protocol.registerSchemesAsPrivileged([
  {
    scheme: 'tcpcache',
    privileges: { standard: true, secure: true, supportFetchAPI: true, stream: true },
  },
])

const MIME_BY_EXT = {
  '.png': 'image/png',
  '.jpg': 'image/jpeg',
  '.jpeg': 'image/jpeg',
  '.gif': 'image/gif',
  '.bmp': 'image/bmp',
  '.webp': 'image/webp',
  '.ico': 'image/x-icon',
  '.svg': 'image/svg+xml',
  '.mp4': 'video/mp4',
  '.webm': 'video/webm',
  '.mkv': 'video/x-matroska',
  '.mov': 'video/quicktime',
  '.mp3': 'audio/mpeg',
  '.wav': 'audio/wav',
  '.ogg': 'audio/ogg',
  '.m4a': 'audio/mp4',
  '.flac': 'audio/flac',
  '.pdf': 'application/pdf',
  '.txt': 'text/plain; charset=utf-8',
}

/** Resolve a `tcpcache://local/<name>` URL to a file inside the cache directory. */
function resolveCachePath(requestUrl) {
  let name
  try {
    const url = new URL(requestUrl)
    if (url.hostname !== 'local') return null
    name = decodeURIComponent(url.pathname.replace(/^\/+/, ''))
  } catch {
    return null
  }
  if (!name) return null
  // Only ever serve a bare file name from the cache directory.
  const base = path.basename(name)
  if (base !== name || base === '.' || base === '..') return null
  const full = path.join(cacheDir(), base)
  const resolved = path.resolve(full)
  if (path.dirname(resolved) !== path.resolve(cacheDir())) return null
  return resolved
}

async function handleCacheRequest(request) {
  const file = resolveCachePath(request.url)
  if (!file) return new Response('Not found', { status: 404 })

  let stat
  try {
    stat = await fsp.stat(file)
  } catch {
    return new Response('Not cached', { status: 404 })
  }

  const type = MIME_BY_EXT[path.extname(file).toLowerCase()] ?? 'application/octet-stream'
  const range = request.headers.get('range')

  // Range support is what lets <audio>/<video> scrub instead of only playing
  // from the start.
  if (range) {
    const match = /bytes=(\d*)-(\d*)/.exec(range)
    if (match) {
      const start = match[1] === '' ? 0 : Number(match[1])
      const end = match[2] === '' ? stat.size - 1 : Math.min(Number(match[2]), stat.size - 1)
      if (Number.isFinite(start) && start <= end) {
        const stream = fs.createReadStream(file, { start, end })
        return new Response(streamToWeb(stream), {
          status: 206,
          headers: {
            'Content-Type': type,
            'Content-Length': String(end - start + 1),
            'Content-Range': `bytes ${start}-${end}/${stat.size}`,
            'Accept-Ranges': 'bytes',
            'Cache-Control': 'no-store',
          },
        })
      }
    }
  }

  return new Response(streamToWeb(fs.createReadStream(file)), {
    status: 200,
    headers: {
      'Content-Type': type,
      'Content-Length': String(stat.size),
      'Accept-Ranges': 'bytes',
      'Cache-Control': 'no-store',
    },
  })
}

function streamToWeb(stream) {
  return new ReadableStream({
    start(controller) {
      stream.on('data', (chunk) => controller.enqueue(new Uint8Array(chunk)))
      stream.on('end', () => controller.close())
      stream.on('error', (err) => controller.error(err))
    },
    cancel() {
      stream.destroy()
    },
  })
}

// ---------------------------------------------------------------------------
// Application state
// ---------------------------------------------------------------------------

/** @type {BrowserWindow|null} */
let mainWindow = null
/** @type {GlassController|null} */
let glass = null
/** @type {ChatService|null} */
let chat = null
/** @type {import('./settings').Settings} */
let settings = null
const store = new MessageStore()

let connection = { ok: false, message: '未连接', connected: false }
let statusText = ''
let quitting = false

/**
 * When this launch began. A message older than this is history, not an arrival.
 *
 * Messages carry the millisecond timestamp out of their file name, so age can be
 * read straight off the message instead of being inferred from how it got here.
 * That replaces the two earlier guards rather than adding to them: a cached
 * message and one fetched by the first sync are both simply older than the
 * process, and a message that genuinely arrives while the first sync is running
 * is newer than it and still raises a toast — which the previous "nothing before
 * the first sync" rule got wrong.
 *
 * The comparison is strict, so it inherits the sender's clock: a machine running
 * slow enough would have its new messages read as old and go unannounced.
 */
const startedAt = Date.now()

/** Bring the window back from the tray, or from minimised. */
function showMainWindow() {
  if (!mainWindow || mainWindow.isDestroyed()) return
  if (mainWindow.isMinimized()) mainWindow.restore()
  mainWindow.show()
  mainWindow.focus()
}

/** Create the tray icon the first time it is needed. */
function ensureTray() {
  if (tray.exists()) return
  tray.install({
    iconPath: path.join(__dirname, '..', 'renderer', 'assets', 'icon.png'),
    onShow: showMainWindow,
    onQuit: () => {
      quitting = true
      app.quit()
    },
  })
}

/**
 * Keep the system's startup entry in step with the setting.
 *
 * `--hidden` is what tells the next launch to come up in the tray. In a packaged
 * build `process.execPath` is the app itself; in development it is Electron, and
 * the app path has to be passed too or the entry would start an empty Electron.
 */
function applyLoginItem(current) {
  try {
    app.setLoginItemSettings({
      openAtLogin: current.launchAtLogin === true,
      path: process.execPath,
      args: app.isPackaged ? ['--hidden'] : [app.getAppPath(), '--hidden'],
    })
  } catch (err) {
    // A refused startup entry must not take the app with it.
    log.warn('could not update the login item', err)
  }
}

/**
 * Push a message to the renderer.
 *
 * Every background producer funnels through here — the sync loop, the glass
 * panel's luminance callbacks, the backdrop capture — and any of them can still
 * be running while the window is tearing down. An unguarded `send` then raises
 * "Render frame was disposed before WebFrameMain could be accessed" once per
 * event, which floods stderr during shutdown.
 */
function send(channel, payload) {
  if (quitting || !mainWindow || mainWindow.isDestroyed()) return
  const contents = mainWindow.webContents
  if (!contents || contents.isDestroyed() || contents.isCrashed()) return
  try {
    contents.send(channel, payload)
  } catch {
    // The frame can be disposed between the checks above and the send itself.
  }
}

/** Push a fresh cipher/connection summary to the renderer. */
function pushStats() {
  if (!chat) return
  send('cipher-state', {
    encryptionEnabled: chat.encryptionEnabled,
    passwordCount: chat.passwordCount,
    sendPasswordNumber: chat.sendPasswordNumber,
    undecryptableCount: chat.undecryptableCount,
    pollSeconds: chat.pollSeconds,
    chatFolder: chat.chatFolder,
    lastSyncTime: chat.lastSyncTime,
    messageCount: store.size,
  })
}

function rendererState() {
  return {
    messages: store.list(),
    connection,
    status: statusText,
    settings: settings.toRenderer(),
    glass: glassStatusWithMode(),
    stats: {
      encryptionEnabled: chat ? chat.encryptionEnabled : false,
      passwordCount: chat ? chat.passwordCount : 0,
      undecryptableCount: chat ? chat.undecryptableCount : 0,
      pollSeconds: chat ? chat.pollSeconds : settings.pollSeconds,
      chatFolder: settings.chatFolder,
      lastSyncTime: chat ? chat.lastSyncTime : null,
      messageCount: store.size,
    },
    platform: process.platform,
    versions: { app: APP_VERSION, electron: process.versions.electron },
  }
}

// ---------------------------------------------------------------------------
// Chat service wiring
// ---------------------------------------------------------------------------

function wireChat(service) {
  service.on('message-added', (msg) => {
    store.upsert(msg)
    send('message-added', msg)

    /*
     * Only a message that has just arrived is worth a toast.
     *
     * The test is the message's own timestamp against the time this process
     * started, which covers every way an old message can reach this handler — the
     * cache replay at startup, a re-read after a fingerprint changed, and the
     * first sync of a profile with no cache, which fetches the whole history and
     * delivers every message through the same event a new one uses.
     *
     * The focus guard alone suppresses none of that, because the window is never
     * in front during startup.
     *
     * A message that would not decrypt never reaches this handler: the service
     * holds it back before emitting (see ChatService._decrypt). Nothing is left to
     * filter here, and no toast can be raised for a message that is not in the
     * list.
     */
    const isSelf = settings.nickname !== '' && msg.from === settings.nickname
    const focused = mainWindow && !mainWindow.isDestroyed() && mainWindow.isFocused()
    const arrivedAt = Date.parse(msg.time)
    const isNew = Number.isFinite(arrivedAt) && arrivedAt >= startedAt
    if (!isSelf && !focused && isNew && settings.notifyOnMessage !== false) {
      notifyMessage(msg)
    }
    pushStats()
  })

  service.on('message-removed', (name) => {
    if (store.remove(name)) send('message-removed', name)
    pushStats()
  })

  service.on('status', (text) => {
    statusText = text
    send('status', text)
    // The tray is the only thing visible when the window is hidden, so the
    // connection state has to live there too.
    tray.setStatus(text)
  })

  service.on('synced', (at) => {
    send('synced', at)
    pushStats()
  })

  service.on('error', (message) => {
    log.warn('chat error:', message)
    send('error', message)
  })

  service.on('diag', (message) => {
    if (IS_DEV) log.info('[diag]', message)
    send('diag', message)
  })

  service.on('cipher-state', () => pushStats())
}

function notifyMessage(msg) {
  try {
    if (!Notification.isSupported()) return
    const body = (msg.text || msg.attach?.name || '').toString()
    const title = msg.from || '(匿名)'
    const notification = new Notification({
      title,
      body: body.length > 140 ? `${body.slice(0, 140)}…` : body || '(空消息)',
      silent: false,
    })
    notification.on('click', () => {
      if (!mainWindow || mainWindow.isDestroyed()) return
      if (mainWindow.isMinimized()) mainWindow.restore()
      mainWindow.show()
      mainWindow.focus()
    })
    notification.show()
  } catch (err) {
    // A suppressed notification must never affect receiving messages.
    log.warn('notification failed', err)
  }
  try {
    if (mainWindow && !mainWindow.isDestroyed()) mainWindow.flashFrame(true)
  } catch {
    /* flashing is best-effort */
  }
}

// ---------------------------------------------------------------------------
// Window
// ---------------------------------------------------------------------------

function createWindow() {
  const saved = settings.windowBounds
  const display = screen.getPrimaryDisplay()
  const workArea = display.workArea

  const width = saved?.width ?? Math.min(1180, workArea.width - 80)
  const height = saved?.height ?? Math.min(780, workArea.height - 80)
  const x = Number.isFinite(saved?.x) ? saved.x : Math.round(workArea.x + (workArea.width - width) / 2)
  const y = Number.isFinite(saved?.y) ? saved.y : Math.round(workArea.y + (workArea.height - height) / 2)

  const win = new BrowserWindow({
    x,
    y,
    width,
    height,
    minWidth: 680,
    minHeight: 480,
    frame: false,
    /*
     * Transparent: the native glass panel is pinned directly below this window
     * and supplies the backdrop, so the window itself must only draw the surface
     * layer. Toggling glass therefore never recreates the window.
     */
    transparent: true,
    backgroundColor: '#00000000',
    roundedCorners: false,
    show: false,
    resizable: true,
    maximizable: true,
    fullscreenable: true,
    title: 'TCP Chat',
    icon: path.join(__dirname, '..', 'renderer', 'assets', 'icon.png'),
    webPreferences: {
      preload: path.join(__dirname, '..', 'preload', 'index.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
      spellcheck: false,
      /*
       * Required, not a preference.
       *
       * Removing this to save idle CPU was tried and reverted: with the window
       * hidden at startup the renderer never got a frame, and on show only the
       * glass panel was there — a native window with nothing drawn under it. The
       * renderer has to keep running while the window is hidden.
       */
      backgroundThrottling: false,
    },
  })

  win.once('ready-to-show', () => {
    /*
     * Started by the login entry: come up in the tray and leave the screen alone.
     *
     * Showing the window on every boot is the reason people turn startup entries
     * off again. The window is built either way so the renderer is live and the
     * app is genuinely receiving; it is just not put in front of anyone.
     */
    if (START_IN_TRAY) ensureTray()
    else win.show()
    if (IS_DEV) win.webContents.openDevTools({ mode: 'detach' })
  })

  /*
   * Zoom is remembered the way DSH Desktop remembers it.
   *
   * `zoom-changed` is Chromium's own announcement that the zoom moved — Ctrl+wheel
   * over the window raises it without the renderer being involved at all — so
   * listening here is what keeps a gesture, the Ctrl +/- keys and the settings
   * slider from each having their own idea of the current level.
   *
   * The restore runs on every load rather than once at startup, because that is
   * when a web contents has a zoom to set: setting it earlier is discarded by the
   * navigation.
   */
  win.webContents.on('zoom-changed', () => {
    if (win.isDestroyed() || win.webContents.isDestroyed()) return
    const level = win.webContents.getZoomLevel()
    if (!Number.isFinite(level)) return
    settings.zoomLevel = clampZoomLevel(level)
    settings.save()
  })

  win.webContents.on('did-finish-load', () => {
    if (win.isDestroyed() || win.webContents.isDestroyed()) return
    const level = clampZoomLevel(Number(settings.zoomLevel))
    if (level !== 0) win.webContents.setZoomLevel(level)
  })

  /*
   * The glass follows the window's visibility, wherever it comes from.
   *
   * Bound to the window's own events rather than applied at each call site that
   * hides or shows it: closing to the tray, minimising, the tray menu and
   * anything added later all take these paths, and the glass being left on
   * screen by itself is the failure when one of them is missed.
   */
  win.on('show', applyGlass)
  win.on('restore', applyGlass)
  win.on('hide', applyGlass)
  win.on('minimize', applyGlass)

  win.on('focus', () => {
    try {
      win.flashFrame(false)
    } catch {
      /* ignore */
    }
  })

  const emitWindowState = () => {
    send('window-state', {
      maximized: win.isMaximized(),
      fullScreen: win.isFullScreen(),
    })
  }
  for (const evt of ['maximize', 'unmaximize', 'enter-full-screen', 'leave-full-screen', 'resize']) {
    win.on(evt, emitWindowState)
  }

  const persistBounds = debounce(() => {
    if (!settings || win.isDestroyed()) return
    if (win.isMaximized() || win.isFullScreen() || win.isMinimized()) return
    settings.update({ windowBounds: win.getBounds() })
  }, 500)
  win.on('resize', persistBounds)
  win.on('move', persistBounds)

  /*
   * Closing hides the window and leaves the app running in the tray.
   *
   * The polling, the cache and the decryption all live here in the main process,
   * so nothing has to be kept alive artificially — the window is the only thing
   * that goes away. Quitting is still reachable: the tray menu, or anything that
   * sets `quitting`, and then this lets the close through.
   */
  win.on('close', (event) => {
    if (quitting) return
    event.preventDefault()
    win.hide()
    ensureTray()
  })

  win.on('closed', () => {
    if (mainWindow === win) mainWindow = null
  })

  // Keep navigation inside the app; anything else goes to the system browser.
  win.webContents.setWindowOpenHandler(({ url }) => {
    if (/^https?:/i.test(url)) shell.openExternal(url).catch(() => {})
    return { action: 'deny' }
  })
  win.webContents.on('will-navigate', (event, url) => {
    if (!url.startsWith('file://')) {
      event.preventDefault()
      if (/^https?:/i.test(url)) shell.openExternal(url).catch(() => {})
    }
  })

  win.loadFile(path.join(__dirname, '..', 'renderer', 'index.html'))
  return win
}

function debounce(fn, ms) {
  let timer = null
  return (...args) => {
    if (timer) clearTimeout(timer)
    timer = setTimeout(() => {
      timer = null
      fn(...args)
    }, ms)
  }
}

// ---------------------------------------------------------------------------
// IPC
// ---------------------------------------------------------------------------

function registerIpc() {
  // ---- settings ----
  ipcMain.handle('settings:get', () => settings.toRenderer())

  ipcMain.handle('settings:update', async (_e, patch) => {
    const before = {
      nwUrl: settings.nwUrl,
      nwPassword: settings.nwPassword,
      chatFolder: settings.chatFolder,
      nickname: settings.nickname,
      cryptoPasswords: settings.cryptoPasswords.join('\u0000'),
      sendPasswordIndex: settings.sendPasswordIndex,
      glassEnabled: settings.glassEnabled,
    }
    const candidate = new settingsModule.Settings({ ...settings.toJSON(), ...(patch ?? {}) })
    settings = candidate
    settings.save()

    const connectionChanged =
      before.nwUrl !== settings.nwUrl ||
      before.nwPassword !== settings.nwPassword ||
      before.chatFolder !== settings.chatFolder
    const cryptoChanged =
      before.cryptoPasswords !== settings.cryptoPasswords.join('\u0000') ||
      before.sendPasswordIndex !== settings.sendPasswordIndex

    if (chat) {
      chat.settings = settings
      if (connectionChanged) {
        store.clear()
        send('messages-reset', store.list())
        await chat.reload()
        await connect()
      } else if (cryptoChanged) {
        chat.nickname = settings.nickname
        store.clear()
        send('messages-reset', store.list())
        await chat.applyCryptoPasswords(settings.cryptoPasswords, settings.sendPasswordIndex)
      } else {
        chat.nickname = settings.nickname
      }
    }

    if (glass) {
      const lookChanged = settings.glassQuality !== before.glassQuality
      if (settings.glassEnabled !== before.glassEnabled) applyGlass()
      else if (lookChanged) glass.setQuality(settings.glassQuality)
    }

    applyLoginItem(settings)
    send('settings-changed', settings.toRenderer())
    pushStats()
    return settings.toRenderer()
  })

  ipcMain.handle('settings:test', async (_e, candidate) => {
    const probe = new settingsModule.Settings({ ...settings.toJSON(), ...(candidate ?? {}) })
    const { NwClient } = require('./nw')
    try {
      const nw = new NwClient({ baseUrl: probe.nwUrl, password: probe.nwPassword })
      const entries = await nw.propFind(probe.chatFolder, 0)
      if (entries.length > 0) return { ok: true, message: '已连接，聊天目录可用' }
      return {
        ok: false,
        message: `连上了，但目录里什么都没有：${probe.chatFolder}`,
      }
    } catch (err) {
      return { ok: false, message: `连接失败: ${err.message}` }
    }
  })

  // ---- chat ----
  ipcMain.handle('chat:state', () => rendererState())

  ipcMain.handle('chat:reconnect', async () => {
    await connect()
    return rendererState()
  })

  ipcMain.handle('chat:send-text', async (_e, { text, quote } = {}) => {
    if (!chat) return { ok: false, error: '服务未就绪' }
    const msg = await chat.sendText(text, quote ?? null)
    if (!msg) return { ok: false, error: '消息为空' }
    store.upsert(msg)
    send('message-added', msg)
    pushStats()
    return { ok: !msg.status, message: msg, error: msg.status ?? null }
  })

  ipcMain.handle('chat:send-files', async (_e, { paths, kind, durationMs } = {}) => {
    if (!chat) return { ok: false, error: '服务未就绪' }
    const results = []
    for (const p of Array.isArray(paths) ? paths : []) {
      const msg = await chat.sendFile(p, kind ?? 0, durationMs ?? 0)
      if (msg) {
        store.upsert(msg)
        send('message-added', msg)
      }
      results.push({ path: p, ok: Boolean(msg) })
    }
    pushStats()
    return { ok: results.every((r) => r.ok), results }
  })

  ipcMain.handle('chat:send-voice', async (_e, { bytes, seconds, durationMs } = {}) => {
    if (!chat) return { ok: false, error: '服务未就绪' }
    try {
      const filename = `语音 ${Number(seconds) || 0}秒.wav`
      const target = path.join(cacheDir(), sanitizeFileName(filename))
      await fsp.writeFile(target, Buffer.from(bytes))
      const msg = await chat.sendFile(target, 5, Number(durationMs) || 0)
      if (!msg) return { ok: false, error: '语音发送失败' }
      store.upsert(msg)
      send('message-added', msg)
      pushStats()
      return { ok: true, message: msg }
    } catch (err) {
      return { ok: false, error: err.message }
    }
  })

  ipcMain.handle('chat:delete', async (_e, { remoteName } = {}) => {
    if (!chat) return { ok: false, error: '服务未就绪' }
    const msg = store.get(remoteName)
    if (!msg) return { ok: false, error: '消息不存在' }
    if (settings.nickname === '' || msg.from !== settings.nickname) {
      return { ok: false, error: '只能撤回自己的消息' }
    }
    const ok = await chat.deleteMessage(msg)
    return ok ? { ok: true } : { ok: false, error: '撤回失败' }
  })

  /** Download + decrypt on demand; the renderer then loads it via `tcpcache:`. */
  ipcMain.handle('chat:ensure-attachment', async (_e, { remoteName } = {}) => {
    if (!chat) return { ok: false, error: '服务未就绪' }
    const msg = store.get(remoteName)
    if (!msg?.attach) return { ok: false, error: '没有附件' }
    const local = await chat.downloadAttachment(msg)
    if (!local) return { ok: false, error: '附件下载失败(密码不一致或网络问题)' }
    return {
      ok: true,
      path: local,
      url: `tcpcache://local/${encodeURIComponent(path.basename(local))}`,
      size: msg.attach.size,
    }
  })

  ipcMain.handle('chat:open-attachment', async (_e, { remoteName } = {}) => {
    if (!chat) return { ok: false, error: '服务未就绪' }
    const msg = store.get(remoteName)
    if (!msg?.attach) return { ok: false, error: '没有附件' }
    const local = await chat.downloadAttachment(msg)
    if (!local) return { ok: false, error: '附件下载失败' }
    const error = await shell.openPath(local)
    return error ? { ok: false, error } : { ok: true }
  })

  ipcMain.handle('chat:save-attachment', async (_e, { remoteName } = {}) => {
    if (!chat) return { ok: false, error: '服务未就绪' }
    const msg = store.get(remoteName)
    if (!msg?.attach) return { ok: false, error: '没有附件' }
    const local = await chat.downloadAttachment(msg)
    if (!local) return { ok: false, error: '附件下载失败' }

    const result = await dialog.showSaveDialog(mainWindow, {
      title: '另存为',
      defaultPath: msg.attach.name || path.basename(local),
    })
    if (result.canceled || !result.filePath) return { ok: false, canceled: true }
    try {
      await fsp.copyFile(local, result.filePath)
      return { ok: true, path: result.filePath }
    } catch (err) {
      return { ok: false, error: err.message }
    }
  })

  ipcMain.handle('chat:open-external', async (_e, { url } = {}) => {
    if (typeof url !== 'string' || !/^https?:/i.test(url)) {
      return { ok: false, error: '仅支持 http/https 链接' }
    }
    await shell.openExternal(url)
    return { ok: true }
  })

  // ---- glass ----
  ipcMain.handle('glass:status', () => glassStatusWithMode())

  ipcMain.handle('glass:set-enabled', async (_e, { enabled } = {}) => {
    settings.update({ glassEnabled: Boolean(enabled) })
    applyGlass()
    send('settings-changed', settings.toRenderer())
    return glassStatusWithMode()
  })

  ipcMain.handle('glass:set-quality', async (_e, { quality } = {}) => {
    settings.update({ glassQuality: Number(quality) || 0 })
    if (glass) glass.apply({ quality: settings.glassQuality })
    return glassStatusWithMode()
  })

  /** Rebuild the panel — used after a driver update or a display change. */
  ipcMain.handle('glass:retry', async () => {
    if (!glass) return glassStatusWithMode()
    settings.update({ glassEnabled: true })
    glass.retry()
    send('glass-state', glassStatusWithMode())
    send('settings-changed', settings.toRenderer())
    return glassStatusWithMode()
  })

  /**
   * Bubble rectangles, in CSS pixels relative to the window's content area.
   *
   * The renderer measures them because only it knows the layout; the main
   * process turns them into screen pixels and drives one glass panel each.
   */
  ipcMain.handle('glass:set-bubbles', async (_e, { rects } = {}) => {
    if (!glass) return 0
    glass.setBubbles(Array.isArray(rects) ? rects : [])
    return glass.bubblePanels.length
  })
  /** Addon counters, for the settings dialog. */
  ipcMain.handle('glass:stats', async () => (glass ? glass.stats() : null))

  // ---- plain-theme background ----
  //
  // The image travels as a data URL rather than over a custom scheme.
  //
  // The first attempt at this feature registered `appbg://` and served the file
  // from it; the request never completed. Switching to `tcpcache://`, which the
  // attachment cache uses, behaved identically — `img.complete` stayed false
  // forever — and the cause was never found. A data URL involves no scheme, no
  // protocol handler and no CSP entry, so there is nothing left to go wrong for
  // a picture that is read once and then sits behind the messages.
  /*
   * Image format from the leading bytes, not the file name.
   *
   * Windows' own cached wallpapers are `TranscodedWallpaper_<hash>` with no
   * extension, so keying off the name rejected every one of them with "cannot
   * read this file" — while the thumbnail beside it rendered fine, because that
   * goes through nativeImage and actually decodes. Two paths, two different
   * notions of what the file is; the magic number is the one that is not a guess.
   */
  function sniffImageMime(bytes) {
    if (bytes.length >= 8 && bytes[0] === 0x89 && bytes[1] === 0x50) return 'image/png'
    if (bytes.length >= 3 && bytes[0] === 0xff && bytes[1] === 0xd8 && bytes[2] === 0xff) {
      return 'image/jpeg'
    }
    if (bytes.length >= 6 && bytes.subarray(0, 3).toString('latin1') === 'GIF') return 'image/gif'
    if (bytes.length >= 2 && bytes[0] === 0x42 && bytes[1] === 0x4d) return 'image/bmp'
    if (
      bytes.length >= 12 &&
      bytes.subarray(0, 4).toString('latin1') === 'RIFF' &&
      bytes.subarray(8, 12).toString('latin1') === 'WEBP'
    ) {
      return 'image/webp'
    }
    return null
  }

  /**
   * Read the chosen image.
   *
   * Returns { url } on success, or { error } saying which of the ways it failed —
   * a single "cannot read this" for a missing file, an over-large one and an
   * unrecognised format is what let the extension bug hide behind a message that
   * was true of none of them.
   */
  async function readBackground() {
    const file = settings.plainBackgroundPath
    if (!file) return { error: '还没有选择图片' }

    let stat
    try {
      stat = await fsp.stat(file)
    } catch {
      return { error: `文件找不到了：${path.basename(file)}` }
    }
    if (!stat.isFile()) return { error: '这个路径不是文件' }
    /*
     * A ceiling, because this crosses IPC as base64 and a 40 MB photo would be a
     * 55 MB string. Anything above this is not a background, it is a mistake.
     */
    if (stat.size > 24 * 1024 * 1024) {
      return { error: `图片 ${Math.round(stat.size / 1048576)} MB，超过 24 MB 上限` }
    }

    let bytes
    try {
      bytes = await fsp.readFile(file)
    } catch (err) {
      return { error: `读不了这个文件：${err.code || err.message}` }
    }

    const mime = sniffImageMime(bytes)
    if (!mime) return { error: '这不是 PNG / JPEG / GIF / BMP / WebP 图片' }
    return { url: `data:${mime};base64,${bytes.toString('base64')}` }
  }

  ipcMain.handle('app:background', async () => {
    const read = await readBackground()
    return {
      url: read.url ?? null,
      path: read.url ? settings.plainBackgroundPath : null,
      fit: settings.plainBackgroundFit,
    }
  })

  /*
   * A small data URL for a picture, for the preview and the recent row.
   *
   * Through nativeImage rather than CSS scaling: the settings panel would
   * otherwise hold a dozen full-size photos decoded at once, and it only needs
   * enough pixels to recognise which picture is which.
   */
  function thumbnail(file, width) {
    try {
      const image = nativeImage.createFromPath(file)
      if (image.isEmpty()) return null
      return image.resize({ width, quality: 'good' }).toDataURL()
    } catch {
      return null
    }
  }

  /*
   * The two places Windows 11 itself keeps pictures for this.
   *
   * Asking someone to go and find a wallpaper when the machine already has a
   * folder full of them is a worse first run than opening on that folder, and
   * the recent strip can start out holding what Windows' own strip holds.
   */
  const WINDOWS_WALLPAPER_ROOT = path.join(
    process.env.windir || 'C:\\Windows',
    'Web',
    'Wallpaper',
  )
  const WINDOWS_RECENT_WALLPAPERS = path.join(
    app.getPath('appData'),
    'Microsoft',
    'Windows',
    'Themes',
    'TranscodedWallpaperCache',
  )

  /**
   * The desktop's own recent strip, in its own order.
   *
   * Windows shows the default wallpaper of each built-in theme — one picture per
   * folder under Web\Wallpaper, the first by name. Spotlight is skipped: it
   * feeds the rotating-spotlight feature rather than being a theme, and Windows
   * does not list it here either.
   *
   * Derived rather than listed, so a machine with different themes gets its own
   * set instead of five paths that may not exist.
   */
  function windowsThemeDefaults() {
    try {
      const out = []
      for (const name of fs.readdirSync(WINDOWS_WALLPAPER_ROOT).sort()) {
        if (name.toLowerCase() === 'spotlight') continue
        const dir = path.join(WINDOWS_WALLPAPER_ROOT, name)
        if (!fs.statSync(dir).isDirectory()) continue
        const first = fs
          .readdirSync(dir)
          .filter((n) => /\.(jpe?g|png)$/i.test(n))
          .sort((a, b) =>
            a.localeCompare(b, undefined, { numeric: true, sensitivity: 'base' }),
          )[0]
        if (first) out.push(path.join(dir, first))
      }
      return out
    } catch {
      return []
    }
  }

  /** Windows' cached wallpapers, newest first. */
  function windowsRecentWallpapers(limit) {
    try {
      return fs
        .readdirSync(WINDOWS_RECENT_WALLPAPERS)
        .filter((n) => n.startsWith('TranscodedWallpaper_') && !n.endsWith('.meta'))
        .map((n) => path.join(WINDOWS_RECENT_WALLPAPERS, n))
        .map((p) => ({ path: p, time: fs.statSync(p).mtimeMs }))
        .sort((a, b) => b.time - a.time)
        .slice(0, limit)
        .map((e) => e.path)
    } catch {
      return []
    }
  }

  /**
   * The recent list with thumbnails attached, skipping anything since deleted.
   *
   * Your own picks come first, then the desktop's set — appended, not substituted.
   *
   * An earlier version replaced the desktop pictures the moment you chose one of
   * your own, so the row you had just been looking at vanished and the picture you
   * picked sat alone. Keeping both means the strip only ever grows, and the stock
   * wallpapers stay reachable without going back through the file dialog.
   */
  function recentWithThumbs() {
    const picks = settings.plainBackgroundRecent
    const stock = windowsThemeDefaults()
    const fallback = stock.length ? stock : windowsRecentWallpapers(6)

    /*
     * Compare on a normalised key, not the raw string.
     *
     * Windows paths are case-insensitive and the separators differ depending on
     * where a path came from — the picker and readdir do not have to agree. A
     * plain includes() let the same wallpaper appear twice, once at the front
     * because it had just been chosen and once in place among the stock ones.
     */
    const key = (p) => p.replace(/\//g, '\\').toLowerCase()
    const seen = new Set(picks.map(key))
    const files = [...picks]
    for (const file of fallback) {
      const k = key(file)
      if (seen.has(k)) continue
      seen.add(k)
      files.push(file)
    }

    const out = []
    for (const file of files) {
      const thumb = thumbnail(file, 160)
      if (thumb) out.push({ path: file, name: path.basename(file), thumb })
    }
    return out
  }

  ipcMain.handle('app:background-recents', async () => ({
    recents: recentWithThumbs(),
    preview: settings.plainBackgroundPath ? thumbnail(settings.plainBackgroundPath, 320) : null,
    fit: settings.plainBackgroundFit,
  }))

  /** Remember a picture, newest first and without duplicates. */
  function rememberBackground(file) {
    settings.plainBackgroundRecent = [
      file,
      ...settings.plainBackgroundRecent.filter((p) => p !== file),
    ].slice(0, 8)
  }

  ipcMain.handle('app:pick-background', async () => {
    /*
     * Open where Windows keeps its wallpapers, so the first thing on screen is
     * the same set the desktop's own picker offers. Falls back to the folder of
     * whatever is in use, then to Pictures.
     */
    const startDir =
      (settings.plainBackgroundPath && path.dirname(settings.plainBackgroundPath)) ||
      (fs.existsSync(WINDOWS_WALLPAPER_ROOT) ? WINDOWS_WALLPAPER_ROOT : null) ||
      app.getPath('pictures')
    const result = await dialog.showOpenDialog(mainWindow, {
      title: '选择背景图片',
      defaultPath: startDir,
      properties: ['openFile'],
      filters: [{ name: '图片', extensions: ['png', 'jpg', 'jpeg', 'webp', 'gif', 'bmp'] }],
    })
    if (result.canceled || !result.filePaths?.length) return { ok: false, canceled: true }
    return useBackground(result.filePaths[0])
  })

  /** Choose one of the recent pictures without going through the file dialog. */
  ipcMain.handle('app:use-background', async (_e, { file } = {}) => {
    if (typeof file !== 'string' || file === '') return { ok: false, error: '没有选中图片' }
    return useBackground(file)
  })

  async function useBackground(file) {
    const previous = settings.plainBackgroundPath
    settings.plainBackgroundPath = file
    const read = await readBackground()
    if (!read.url) {
      settings.plainBackgroundPath = previous
      return { ok: false, error: read.error }
    }
    rememberBackground(file)
    settings.save()
    send('settings-changed', settings.toRenderer())
    return { ok: true, url: read.url, path: file }
  }

  ipcMain.handle('app:set-background-fit', async (_e, { fit } = {}) => {
    settings.plainBackgroundFit = fit
    settings.save()
    send('settings-changed', settings.toRenderer())
    return { ok: true, fit: settings.plainBackgroundFit }
  })

  ipcMain.handle('app:clear-background', async () => {
    settings.plainBackgroundPath = null
    settings.save()
    send('settings-changed', settings.toRenderer())
    return { ok: true }
  })

  // ---- files ----
  ipcMain.handle('files:pick', async (_e, { kind } = {}) => {
    const filters =
      kind === 'image'
        ? [{ name: '图片', extensions: ['png', 'jpg', 'jpeg', 'gif', 'bmp', 'webp', 'ico', 'tif', 'tiff'] }]
        : kind === 'audio'
          ? [{ name: '音频', extensions: ['mp3', 'wav', 'ogg', 'flac', 'm4a', 'aac', 'wma'] }]
          : [{ name: '所有文件', extensions: ['*'] }]
    const result = await dialog.showOpenDialog(mainWindow, {
      title: '选择要发送的文件',
      properties: kind === 'any' ? ['openFile', 'multiSelections'] : ['openFile', 'multiSelections'],
      filters,
    })
    return result.canceled ? [] : result.filePaths
  })

  ipcMain.handle('files:save-voice', async (_e, { bytes, seconds } = {}) => {
    try {
      const filename = sanitizeFileName(`语音 ${Number(seconds) || 0}秒.wav`)
      const target = path.join(cacheDir(), filename)
      await fsp.writeFile(target, Buffer.from(bytes))
      return { ok: true, path: target }
    } catch (err) {
      return { ok: false, error: err.message }
    }
  })

  // ---- window ----
  ipcMain.handle('window:state', () => ({
    maximized: Boolean(mainWindow && mainWindow.isMaximized()),
    fullScreen: Boolean(mainWindow && mainWindow.isFullScreen()),
  }))

  ipcMain.handle('window:minimize', () => {
    mainWindow?.minimize()
  })

  ipcMain.handle('window:toggle-maximize', () => {
    if (!mainWindow) return false
    if (mainWindow.isMaximized()) mainWindow.unmaximize()
    else mainWindow.maximize()
    return mainWindow.isMaximized()
  })

  ipcMain.handle('window:close', () => {
    mainWindow?.close()
  })

  /*
   * Screenshot the window, glass included.
   *
   * Two layers, because no single capture can see both. capturePage reads the
   * renderer and is the only thing that shows the application; the glass panel is
   * a separate window excluded from every capture path — the addon says it has to
   * be, or the glass captures itself — and the only way at its pixels is the
   * addon's own readback.
   *
   * So: the page over the glass, alpha-composited. Where the page is opaque the
   * page wins; where it is transparent the glass shows through, which is exactly
   * what the window looks like on screen.
   *
   * Reading the screen instead was tried and abandoned: the window is under
   * 'dda-only' and desktopCapturer is desktop duplication, so both layers are
   * absent from it and the shot is of whatever is behind the window.
   */
  ipcMain.handle('window:capture', async () => {
    if (!mainWindow || mainWindow.isDestroyed()) {
      return { ok: false, message: '窗口不可用' }
    }
    try {
      const page = await mainWindow.webContents.capturePage()
      if (page.isEmpty()) return { ok: false, message: '截图为空' }
      const size = page.getSize()

      const backdrop = glass?.readPanel?.() ?? null
      if (!backdrop) {
        // No glass, or no readback: the page on its own, which is what the
        // button did before and is still the right answer when glass is off.
        return { ok: true, png: page.toPNG().toString('base64'), width: size.width, height: size.height }
      }

      /*
       * Both sides are BGRA at 4 bytes a pixel. The page is straight alpha, the
       * glass is premultiplied, so the glass term is added as it stands rather
       * than multiplied again.
       *
       * The two are not guaranteed to be the same size — the panel is the
       * window's client area in physical pixels and the page is the viewport at
       * the same scale, which has matched every time it has been checked — so the
       * backdrop is sampled only where it overlaps and treated as absent outside.
       */
      const out = Buffer.alloc(size.width * size.height * 4)
      const pagePixels = page.toBitmap()
      const bw = backdrop.width
      const bh = backdrop.height
      const glassPixels = backdrop.pixels

      for (let y = 0; y < size.height; y += 1) {
        for (let x = 0; x < size.width; x += 1) {
          const i = (y * size.width + x) * 4
          const a = pagePixels[i + 3] / 255
          const inv = 1 - a
          const inside = x < bw && y < bh
          for (let c = 0; c < 3; c += 1) {
            const under = inside ? glassPixels[(y * bw + x) * 4 + c] : 0
            out[i + c] = Math.min(255, Math.round(pagePixels[i + c] * a + under * inv))
          }
          out[i + 3] = 255
        }
      }

      const composed = nativeImage.createFromBitmap(out, {
        width: size.width,
        height: size.height,
      })
      return {
        ok: true,
        png: composed.toPNG().toString('base64'),
        width: size.width,
        height: size.height,
      }
    } catch (err) {
      log.warn('screenshot failed', err)
      return { ok: false, message: String(err?.message || err) }
    }
  })

  /*
   * The clipboard write, through the one route that works here.
   *
   * Neither obvious API does. This build's `clipboard` module has no writeImage
   * and no readImage — it is the web clipboard API wearing Electron's name, and
   * navigator.clipboard.write is refused by the permission layer. copyImageAt is
   * a real Electron method on webContents, it writes an image to the system
   * clipboard natively, and it takes a point in the page: whatever image is
   * rendered there is what gets copied.
   *
   * So the renderer puts the shot on screen for the duration of the call. It is
   * positioned under the composer, fully opaque — Chromium skips painting
   * anything it thinks is invisible, and an unrendered image has nothing to
   * copy — and removed as soon as this returns.
   */
  ipcMain.handle('window:copy-image-at', (_e, { x, y } = {}) => {
    if (!mainWindow || mainWindow.isDestroyed()) return false
    try {
      mainWindow.webContents.copyImageAt(Math.round(x), Math.round(y))
      return true
    } catch (err) {
      log.warn('copyImageAt failed', err)
      return false
    }
  })

  ipcMain.handle('window:get-bounds', () => (mainWindow ? mainWindow.getBounds() : null))

  /*
   * Zoom, in Chromium zoom levels, applied and remembered here.
   *
   * Levels rather than a factor because they are Chromium's own unit and because
   * Ctrl+wheel already produces one: the renderer is told about it instead of
   * recomputing a factor, so the keyboard, the menu and the gesture cannot
   * disagree. Applying it on the web contents — rather than calling
   * setZoomFactor in the frame — is what lets the zoom survive a reload.
   */
  ipcMain.handle('window:set-zoom-level', (_e, { level } = {}) => {
    const clamped = clampZoomLevel(Number(level))
    if (mainWindow && !mainWindow.isDestroyed()) {
      mainWindow.webContents.setZoomLevel(clamped)
    }
    settings.zoomLevel = clamped
    return clamped
  })

  ipcMain.handle('window:get-zoom-level', () =>
    mainWindow && !mainWindow.isDestroyed() ? mainWindow.webContents.getZoomLevel() : 0,
  )

  /** Absolute bounds in DIP, used by the DOM resize handles. */
  ipcMain.handle('window:set-bounds', (_e, bounds = {}) => {
    if (!mainWindow || mainWindow.isMaximized() || mainWindow.isFullScreen()) return null
    const display = screen.getDisplayMatching(bounds)
    const area = display.workArea
    const minW = 680
    const minH = 480
    const width = Math.max(minW, Math.min(Math.round(bounds.width ?? 0), area.width))
    const height = Math.max(minH, Math.min(Math.round(bounds.height ?? 0), area.height))
    // Never let the title bar escape above the work area.
    const x = Math.round(bounds.x ?? mainWindow.getBounds().x)
    const y = Math.round(bounds.y ?? mainWindow.getBounds().y)
    const clampedY = Math.max(area.y, Math.min(y, area.y + area.height - 48))
    mainWindow.setBounds({ x, y: clampedY, width, height })
    return mainWindow.getBounds()
  })

  // ---- app ----
  ipcMain.handle('app:open-data-dir', () => {
    shell.openPath(ensureDataDir())
  })

  ipcMain.handle('app:open-log', () => {
    shell.openPath(log.file)
  })

  ipcMain.handle('app:flash', () => {
    try {
      mainWindow?.flashFrame(true)
    } catch {
      /* ignore */
    }
  })

  ipcMain.handle('app:copy-text', (_e, { text } = {}) => {
    clipboard.writeText(String(text ?? ''))
    return true
  })

  /** Windows' microphone privacy page — the equivalent of the C# build's jump to settings. */
  ipcMain.handle('app:open-mic-settings', async () => {
    if (process.platform !== 'win32') return { ok: false, error: '只支持 Windows' }
    try {
      await shell.openExternal('ms-settings:privacy-microphone')
      return { ok: true }
    } catch (err) {
      return { ok: false, error: err.message }
    }
  })
}

/**
 * Permission policy. Only the microphone and the local font list are granted, and
 * only to our own renderer: everything else (geolocation, notifications from web
 * content, USB, serial, …) is denied, and a denied permission never reaches the
 * DOM.
 *
 * clipboard-sanitized-write was granted here while the screenshot button went
 * through navigator.clipboard. It does not any more — copyImageAt is a native
 * webContents call and needs no permission — so the grant went with it.
 */
function installPermissionHandlers(session) {
  const allowed = new Set(['media', 'audioCapture', 'local-fonts'])

  session.setPermissionRequestHandler((contents, permission, callback) => {
    const isOurs = contents === mainWindow?.webContents
    callback(isOurs && allowed.has(permission))
  })

  session.setPermissionCheckHandler((contents, permission) => {
    const isOurs = contents === mainWindow?.webContents
    return isOurs && allowed.has(permission)
  })
}

// ---------------------------------------------------------------------------
// Glass
// ---------------------------------------------------------------------------

/**
 * The visual mode the renderer should paint.
 *
 *   on  — the native panel is supplying the backdrop
 *   off — plain DSH theme, opaque
 */
function glassMode() {
  if (!glass) return 'off'
  const status = glass.status()
  return status.requested && status.supported ? 'on' : 'off'
}

function glassStatusWithMode() {
  const status = glass
    ? glass.status()
    : { enabled: false, supported: false, reason: 'not ready' }
  return { ...status, mode: glassMode() }
}

function applyGlass() {
  if (!glass) return
  // The controller reads this when it builds or re-tunes the panel.
  glass.quality = settings.glassQuality
  /*
   * A panel is only meaningful underneath a window.
   *
   * The glass is a separate native window pinned below ours, so creating it while
   * ours is hidden puts a pane of glass on screen with nothing behind it — which
   * is what starting in the tray did. And it is not just a rectangle in the wrong
   * place: the panel re-renders from desktop duplication for as long as it
   * exists, measured at about three quarters of a core, for something nobody
   * asked to see.
   *
   * So the window's own visibility decides. The window is created either way when
   * starting in the tray, which is what keeps the renderer live and the app
   * genuinely receiving; it is the glass that has no business being there.
   */
  const wanted = settings.glassEnabled && windowVisible()
  const status = glass.apply(wanted)
  if (wanted && !status.supported) {
    log.warn('liquid glass requested but unavailable:', status.reason)
  }
  send('glass-state', glassStatusWithMode())
  send('settings-changed', settings.toRenderer())
}

/** True when the main window is on screen. */
function windowVisible() {
  return Boolean(mainWindow) && !mainWindow.isDestroyed() && mainWindow.isVisible()
}



// ---------------------------------------------------------------------------
// Boot
// ---------------------------------------------------------------------------

async function connect() {
  if (!chat) return
  connection = { ok: false, message: '正在连接…', connected: false }
  send('connection', connection)
  const result = await chat.initialize()
  connection = { ok: result.ok, message: result.message, connected: result.ok }
  send('connection', connection)
  if (result.ok) {
    await chat.start()
  } else {
    send('error', result.message)
  }
  pushStats()
}

async function bootstrap() {
  installCrashHandlers()
  ensureDataDir()
  settings = settingsModule.load()

  protocol.handle('tcpcache', handleCacheRequest)

  mainWindow = createWindow()

  nativeTheme.themeSource =
    settings.theme === 1 ? 'light' : settings.theme === 2 ? 'dark' : 'system'
  nativeTheme.on('updated', () => {
    send('settings-changed', settings.toRenderer())
  })

  registerIpc()
  installPermissionHandlers(mainWindow.webContents.session)

  /*
   * Diagnostic: force the window to the top so a background-capture dump is
   * unambiguous. Without this, a window that happens to be behind another one
   * produces a "clean" dump that proves nothing about whether this window is
   * excluded from the capture.
   */
  if (process.env.TCPCHAT_GLASS_TOPMOST) {
    mainWindow.setAlwaysOnTop(true, 'screen-saver')
    log.info('diagnostic: window forced always-on-top')
  }
  glass = new GlassController(mainWindow, {
    // Repaint-driven luminance from the panel, used for adaptive text contrast.
    // Nothing is pushed while the desktop is static.
    onLuma: (bands) => send('glass-luma', bands),
  })
  applyGlass()

  chat = new ChatService(settings)
  wireChat(chat)
  await chat.applyCryptoPasswords(settings.cryptoPasswords, settings.sendPasswordIndex)

  mainWindow.webContents.once('did-finish-load', () => {
    send('settings-changed', settings.toRenderer())
    pushStats()
    if (SELFTEST_OUT) {
      const { runSelfTest } = require('./selftest')
      void runSelfTest({
        win: mainWindow,
        outDir: SELFTEST_OUT,
        settings,
        chatService: chat,
        send,
        glassStats: () => glass?.stats() ?? null,
        glassControls: {
          setEnabled: (enabled) => {
            settings.update({ glassEnabled: enabled })
            applyGlass()
          },
        },
      })
      return
    }
    void connect()
  })

  mainWindow.on('closed', () => {
    // Mark first so in-flight background work stops pushing to a dead frame.
    quitting = true
    glass?.dispose()
    glass = null
    chat?.dispose()
    chat = null
  })
}

// Single instance: a second launch focuses the existing window instead.
const gotLock = app.requestSingleInstanceLock()
if (!gotLock) {
  app.quit()
} else {
  app.on('second-instance', () => {
    // Also brings it back from the tray, which a plain show() would not undo.
    showMainWindow()
  })

  /*
   * Must match the appId the installer registers, or toasts cannot find their way
   * home.
   *
   * Windows attributes a toast to an AppUserModelID and, when one is clicked,
   * resolves that ID back to a registered application. The Start Menu shortcut
   * carries the appId from electron-builder.yml, so announcing a different name
   * here left the ID unmatched — and an unmatched toast falls back to the
   * executable that raised it, which for a packaged Electron app is a bare
   * electron.exe with no app path. Clicking a message opened Electron's own
   * welcome window instead of this one.
   */
  app.setAppUserModelId('cn.zhaohans.tcpchat')
  app
    .whenReady()
    .then(bootstrap)
    .then(() => {
      /*
       * Re-assert the startup entry on every launch.
       *
       * An installed build moves between versions, and the path recorded in the
       * entry is the one that has to exist. Writing it again costs nothing and
       * means an upgrade cannot leave a startup entry pointing at a folder that
       * is no longer there.
       */
      if (settings) applyLoginItem(settings)
    })
    .catch((err) => {
      log.error('bootstrap failed', err)
      dialog.showErrorBox('TCP Chat 启动失败', String(err?.stack || err))
      app.quit()
    })
}

app.on('window-all-closed', () => {
  quitting = true
  app.quit()
})

app.on('before-quit', () => {
  quitting = true
  try {
    chat?.dispose()
  } catch {
    /* ignore */
  }
  try {
    glass?.dispose()
  } catch {
    /* nothing to release */
  }
})

app.on('activate', () => {
  if (BrowserWindow.getAllWindows().length === 0 && !quitting) {
    bootstrap().catch((err) => log.error('re-bootstrap failed', err))
  }
})

module.exports = { APP_VERSION }
