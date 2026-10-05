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
  nativeTheme,
  protocol,
  screen,
  shell,
} = require('electron')

const { log, installCrashHandlers } = require('./logger')
const { ensureDataDir, cacheDir } = require('./paths')
const settingsModule = require('./settings')
const { ChatService, sanitizeFileName } = require('./chat-service')
const { MessageStore } = require('./message-store')
const { GlassController } = require('./glass')

const APP_VERSION = require('../../package.json').version
process.env.TCPCHAT_VERSION = APP_VERSION

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

    // Only other people's messages are worth a toast, and only while the window
    // is not in front already.
    const isSelf = settings.nickname !== '' && msg.from === settings.nickname
    const focused = mainWindow && !mainWindow.isDestroyed() && mainWindow.isFocused()
    if (!isSelf && !focused) notifyMessage(msg)
    pushStats()
  })

  service.on('message-removed', (name) => {
    if (store.remove(name)) send('message-removed', name)
    pushStats()
  })

  service.on('status', (text) => {
    statusText = text
    send('status', text)
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
      backgroundThrottling: false,
    },
  })

  win.once('ready-to-show', () => {
    win.show()
    if (IS_DEV) win.webContents.openDevTools({ mode: 'detach' })
  })

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
      serverUrl: settings.serverUrl,
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
      before.serverUrl !== settings.serverUrl || before.chatFolder !== settings.chatFolder
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
      const lookChanged =
        settings.glassQuality !== before.glassQuality ||
        settings.glassBlurSigma !== before.glassBlurSigma
      if (settings.glassEnabled !== before.glassEnabled) applyGlass()
      else if (lookChanged) glass.setQuality(settings.glassQuality)
    }

    send('settings-changed', settings.toRenderer())
    pushStats()
    return settings.toRenderer()
  })

  ipcMain.handle('settings:test', async (_e, candidate) => {
    const probe = new settingsModule.Settings({ ...settings.toJSON(), ...(candidate ?? {}) })
    const { WebDavClient } = require('./webdav')
    try {
      const dav = new WebDavClient(probe.serverUrl)
      const ok = (await dav.propFind(probe.chatFolder, 0)).length > 0
      if (ok) return { ok: true, message: '已连接，聊天目录可用' }
      const serverOk = await dav.test()
      return {
        ok: false,
        message: serverOk
          ? `聊天目录不存在：${probe.chatFolder}`
          : '无法访问服务器(请检查网络与服务器地址)',
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
  const IMAGE_MIME = {
    '.png': 'image/png',
    '.jpg': 'image/jpeg',
    '.jpeg': 'image/jpeg',
    '.webp': 'image/webp',
    '.gif': 'image/gif',
    '.bmp': 'image/bmp',
  }

  /** Read the chosen image, or null when there is nothing usable. */
  async function readBackground() {
    const file = settings.plainBackgroundPath
    if (!file) return null
    const mime = IMAGE_MIME[path.extname(file).toLowerCase()]
    if (!mime) return null
    try {
      const stat = await fsp.stat(file)
      /*
       * A ceiling, because this crosses IPC as base64 and a 40 MB photo would be
       * a 55 MB string. Anything above this is not a background, it is a mistake.
       */
      if (!stat.isFile() || stat.size > 24 * 1024 * 1024) return null
      const bytes = await fsp.readFile(file)
      return `data:${mime};base64,${bytes.toString('base64')}`
    } catch {
      return null
    }
  }

  ipcMain.handle('app:background', async () => {
    const url = await readBackground()
    return { url, path: url ? settings.plainBackgroundPath : null }
  })

  ipcMain.handle('app:pick-background', async () => {
    const result = await dialog.showOpenDialog(mainWindow, {
      title: '选择背景图片',
      properties: ['openFile'],
      filters: [{ name: '图片', extensions: ['png', 'jpg', 'jpeg', 'webp', 'gif', 'bmp'] }],
    })
    if (result.canceled || !result.filePaths?.length) return { ok: false, canceled: true }
    const file = result.filePaths[0]
    const url = await (async () => {
      const previous = settings.plainBackgroundPath
      settings.plainBackgroundPath = file
      const read = await readBackground()
      if (!read) settings.plainBackgroundPath = previous
      return read
    })()
    if (!url) return { ok: false, error: '这个文件读不出来，或者超过了 24 MB' }
    settings.save()
    send('settings-changed', settings.toRenderer())
    return { ok: true, url, path: file }
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

  ipcMain.handle('window:get-bounds', () => (mainWindow ? mainWindow.getBounds() : null))

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
 * Permission policy. Only the microphone is granted, and only to our own
 * renderer: everything else (geolocation, notifications from web content, USB,
 * serial, …) is denied, and a denied permission never reaches the DOM.
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
  // The controller reads these when it builds or re-tunes the panel.
  glass.blurSigma = settings.glassBlurSigma
  glass.quality = settings.glassQuality
  const status = glass.apply(settings.glassEnabled)
  if (settings.glassEnabled && !status.supported) {
    log.warn('liquid glass requested but unavailable:', status.reason)
  }
  send('glass-state', glassStatusWithMode())
  send('settings-changed', settings.toRenderer())
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
    if (!mainWindow) return
    if (mainWindow.isMinimized()) mainWindow.restore()
    mainWindow.show()
    mainWindow.focus()
  })

  app.setAppUserModelId('TCPChat')
  app.whenReady().then(bootstrap).catch((err) => {
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
