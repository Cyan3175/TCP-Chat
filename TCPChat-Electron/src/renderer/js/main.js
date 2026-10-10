/*
 * Renderer entry point.
 *
 * Owns the application state, wires the view modules together and runs the boot
 * sequence. The other modules in this folder are deliberately state-free: they
 * render what they are given and call back through the `configure*` hooks set up
 * in `boot()`.
 */

import {
  $,
  $$,
  h,
  clear,
  show,
  formatTime,
  clamp,
  copyText,
} from './dom.js'
import {
  configureMessages,
  appendMessage,
  removeMessage,
  renderAll,
  setRenderLimit,
  setEmptyText,
  isAtBottom,
  scrollToBottom,
} from './messages.js'
import {
  configureComposer,
  setReply,
  initComposer,
  sendDraft,
  focusComposer,
} from './composer.js'
import {
  configureSearch,
  searchQuery,
  openSearch,
  closeSearch,
  isSearchOpen,
  refreshSearch,
  reapplySearch,
  initSearch,
} from './search.js'
import { configureSettings, openSettings } from './settings-ui.js'
import { closeMenu, isMenuOpen, showToast } from './overlays.js'

/**
 * Zoom bounds. Mirrored from the main process (which clamps persisted values)
 * so the UI cannot drift from what the settings actually allow.
 */
const ZOOM_LEVEL_MIN = -3
const ZOOM_LEVEL_MAX = 3
const ZOOM_LEVEL_STEP = 0.5
/** 1.2 ** level, the same relation the main process uses. */
const zoomFactorForLevel = (level) => Math.pow(1.2, level)

/** Base font size for message content, in CSS pixels at zoom 1. */

/**
 * Everything the views read. Kept in one object so a view can be handed the
 * whole state without importing this module back (which would be a cycle).
 */
/*
 * How many messages are rendered at once, and how many more each 载入更早 adds.
 *
 * Every rendered message costs a Markdown parse, syntax highlighting and KaTeX,
 * and stays in the DOM. 200 covers several screens of scrollback, which is what
 * the window is for; the rest of the history is one click away and search reads
 * the whole list regardless.
 */
const RENDER_WINDOW = 100

const state = {
  /** Settings as the main process last reported them. */
  settings: {
    nwUrl: '',
    chatFolder: '',
    nickname: '',
    pollSeconds: 3,
    historyDays: 7,
    autoScroll: true,
    theme: 0,
    uiStyle: 'default',
    fontFamily: '',
    cryptoPasswords: [],
    sendPasswordIndex: 0,
    glassEnabled: true,
    glassQuality: 60,
    zoomLevel: 0,
  },
  stats: {},
  connection: { ok: false, message: '未连接', connected: false },
  glass: { enabled: false, requested: false, supported: false, reason: 'not ready', mode: 'off' },
  messages: [],
  /** How many of the newest messages are rendered. Raised by 载入更早. */
  renderLimit: RENDER_WINDOW,
  windowMaximized: false,
  searchOpen: false,
}

/* Liquid glass -------------------------------------------------------------- */

/**
 * Adaptive contrast.
 *
 * The native panel samples the blurred backdrop under three bands (title bar,
 * message list, composer) and pushes their mean colour whenever the content
 * underneath changes. A mean-luma threshold with hysteresis picks the tint set,
 * so the UI flips between "bright desktop / dark text" and "dark desktop / light
 * text" without strobing while something moves behind the window.
 */
let lumaMode = ''
function applyLuma(luma) {
  if (!Number.isFinite(luma)) return
  let next = lumaMode
  if (lumaMode === 'bright') next = luma < 132 ? '' : 'bright'
  else if (lumaMode === 'dark') next = luma > 88 ? '' : 'dark'
  else next = luma >= 150 ? 'bright' : luma <= 70 ? 'dark' : ''
  if (next === lumaMode) return
  lumaMode = next
  if (next) document.body.dataset.luma = next
  else delete document.body.dataset.luma
}

/**
 * Luminance bands from the addon, keyed by band id.
 *
 * Band 1 is the message list — the area whose readability actually matters.
 */
function handleLuma(bands) {
  if (!bands) return
  const list = Array.isArray(bands) ? bands : Object.values(bands)
  const band = list[1] ?? list[0]
  if (!band || !Number.isFinite(band.r)) return
  applyLuma(0.2126 * band.r + 0.7152 * band.g + 0.0722 * band.b)
}

/* ---------------------------------------------------------------------------
 * Native glass panels behind each message bubble
 * ------------------------------------------------------------------------ */

/**
 * Measure the bubbles and hand them to the main process, which gives each one
 * its own native glass panel.
 *
 * A DOM `backdrop-filter` cannot do this: it samples Chromium's compositing
 * surface, and the panel holding the real backdrop is a separate OS window
 * underneath ours, so the filter would only ever see transparency. The blur,
 * dispersion and lensed rim on a message therefore have to come from the same
 * machinery that draws the window's own glass.
 *
 * Rectangles are content-relative CSS pixels. Only bubbles *fully* inside the
 * window are sent: a clipped one would get a panel whose rounded rim traces the
 * wrong shape, which looks worse than no panel at all.
 */
const MAX_BUBBLE_PANELS = 24
let bubbleSyncHandle = 0
let bubbleSignature = ''

function collectBubbleRects() {
  const host = document.getElementById('app')
  if (!host) return []
  const hb = host.getBoundingClientRect()
  const rects = []
  /*
   * Day separators, not just bubbles. They are glass chips like any other
   * surface and looked wrong sitting on the panel without a backdrop of their
   * own. They are pill-shaped, so the panel gets a matching radius.
   */
  const targets = [...$$('.msg-list .bubble'), ...$$('.msg-list .msg-day')]
  for (const el of targets) {
    const r = el.getBoundingClientRect()
    if (r.width < 8 || r.height < 8) continue
    if (r.left < hb.left || r.top < hb.top || r.right > hb.right || r.bottom > hb.bottom) continue
    /*
     * Stable identity for the panel that draws this surface.
     *
     * Panels are matched to bubbles by this key, not by their position in the
     * list. Bubbles that are not entirely inside the window are dropped above,
     * so scrolling one past an edge renumbers everything after it — and an
     * index-based match then moves *every* panel onto a different bubble. See
     * GlassController.setBubbles.
     */
    const isDay = el.classList.contains('msg-day')
    const key = isDay ? `day:${el.dataset.day ?? ''}` : `msg:${el.closest('.msg')?.dataset.name ?? ''}`
    if (key.endsWith(':')) continue
    rects.push({
      x: Math.round(r.left - hb.left),
      y: Math.round(r.top - hb.top),
      width: Math.round(r.width),
      height: Math.round(r.height),
      key,
      // A pill clamps its radius to half the height; the panel has to match or
      // the glass corner pokes out past the chip.
      pill: isDay,
    })
    if (rects.length >= MAX_BUBBLE_PANELS) break
  }
  return rects
}

/*
 * Bubble panels track their bubbles live, one update per animation frame.
 *
 * Hiding them while the list moved was tried and removed: a hidden panel keeps
 * its back buffer, so re-showing it puts a frame of *pre-scroll* content at the
 * *post-scroll* position — one visible flash, which is exactly what it was meant
 * to prevent. The drift it was working around turned out to be the addon's
 * twice-a-second z-order re-assert and a polling timer, both since fixed.
 */

/** Coalesce to one sync per frame; skip when nothing actually moved. */
function syncBubblePanels() {
  if (bubbleSyncHandle) return
  bubbleSyncHandle = requestAnimationFrame(() => {
    bubbleSyncHandle = 0
    if (state.glass.mode !== 'on' || !window.tcpchat.glass.setBubbles) return
    const rects = collectBubbleRects()
    // The key is part of the signature: a repack that keeps every rectangle
    // identical still changes which panel draws which bubble.
    const signature = rects.map((r) => `${r.key}|${r.x},${r.y},${r.width},${r.height}`).join('|')
    if (signature === bubbleSignature) return
    bubbleSignature = signature
    window.tcpchat.glass.setBubbles(rects).catch((err) => {
      console.warn('[tcpchat] bubble panels failed', err.message)
    })
  })
}



/** Drop every bubble panel — used when the glass goes off or the layout resets. */
function clearBubblePanels() {
  bubbleSignature = ''
  window.tcpchat.glass.setBubbles?.([]).catch(() => {})
}

// ---------------------------------------------------------------------------
// Appearance
// ---------------------------------------------------------------------------

function prefersDark() {
  return window.matchMedia('(prefers-color-scheme: dark)').matches
}

/** DSH's own convention: dark mode is the `data-ds-dark-theme` attribute on body. */
function applyTheme() {
  const preference = state.settings?.theme ?? 0
  const dark = preference === 2 || (preference === 0 && prefersDark())
  document.body.toggleAttribute('data-ds-dark-theme', dark)
}

/**
 * The UI set, on the root element rather than the body.
 *
 * applyTheme puts the light/dark attribute on the body, and the two are meant to
 * combine — a UI set does not choose a palette, it chooses shape and code
 * colouring on top of whichever palette is in force. Keying it on the root keeps
 * them independent and still lets a rule name both together.
 */
function applyUiStyle() {
  const chosen = state.settings?.uiStyle
  const style = ['deepseek', 'edge'].includes(chosen) ? chosen : 'default'
  if (style === 'default') delete document.documentElement.dataset.ui
  else document.documentElement.dataset.ui = style
}

function applyFont() {
  const family = (state.settings?.fontFamily ?? '').trim()
  if (family) {
    document.documentElement.style.setProperty('--dsw-font-family', `"${family}", var(--dsw-font-family-fallback)`)
  } else {
    document.documentElement.style.removeProperty('--dsw-font-family')
  }
  /*
   * A flag, not another variable.
   *
   * The chosen family has to reach two places that deliberately pin a font —
   * code, which wants a monospace, and maths, which is drawn from KaTeX's own
   * faces. Both are written in the stylesheets as a first choice followed by
   * those specialist fonts, and they can only be switched on when a family has
   * actually been chosen. An attribute is enough to key that on, and it leaves
   * the default look untouched for anyone who has not picked one.
   */
  if (family) document.documentElement.dataset.customFont = 'on'
  else delete document.documentElement.dataset.customFont
}

/*
 * Plain-theme backdrop.
 *
 * The picture is the user's own, chosen in settings, and arrives as a data URL.
 * The first attempt at this feature served the desktop wallpaper over a custom
 * scheme and the image request never completed; a data URL has no scheme,
 * handler or CSP entry to get wrong.
 *
 * Only shown in the plain theme: under glass #app is a translucent tint over the
 * native panel, so the layer would read through and put the picture behind the
 * desktop mirror.
 */
let plainBackgroundUrl = null
let plainBackgroundFit = 'cover'

async function applyPlainBackground() {
  const layer = document.getElementById('plain-bg')
  const img = document.getElementById('plain-bg-img')
  if (!layer || !img) return

  if (plainBackgroundUrl === null) {
    try {
      const info = await window.tcpchat.app.background()
      plainBackgroundUrl = info?.url ?? ''
      plainBackgroundFit = info?.fit ?? 'cover'
    } catch {
      plainBackgroundUrl = ''
    }
  }

  const want = Boolean(plainBackgroundUrl) && document.body.dataset.glass !== 'on'
  if (want) {
    if (img.getAttribute('src') !== plainBackgroundUrl) img.src = plainBackgroundUrl
    // The fit mode is an object-fit keyword, so it goes straight through.
    img.style.objectFit = plainBackgroundFit
    img.style.objectPosition = 'center'
    layer.hidden = false
    document.body.dataset.plainBg = 'on'
  } else {
    layer.hidden = true
    document.body.dataset.plainBg = ''
  }
}

/** Forget the cached data URL so the next apply re-reads from the main process. */
function invalidatePlainBackground() {
  plainBackgroundUrl = null
}

function applyZoom() {
  const level = zoomLevel()
  /*
   * Zoom scales the whole UI, not just message text.
   *
   * This used to set --dsh-content-font-size, which only reached the rules that
   * happened to read it: the message body and the composer. The title bar, the
   * status bar and the buttons stayed put, so "zoom" meant two different things on
   * the same screen — and the composer, scaling its type while its height stayed an
   * inline pixel value, pushed its own placeholder out of the box.
   *
   * A zoom level moves the CSS pixel underneath everything instead, so every
   * dimension scales by the same amount and none of it can drift. The main process
   * applies it, on the web contents rather than this frame, which is also what
   * makes a reload come back at the zoom it was left at.
   */
  window.tcpchat.window.setZoomLevel(level)
  const label = document.getElementById('status-zoom')
  if (label) label.textContent = `${Math.round(zoomFactorForLevel(level) * 100)}%`
}

/** The stored zoom, clamped to what the menu offers. */
function zoomLevel() {
  const level = state.settings?.zoomLevel
  return clamp(typeof level === 'number' ? level : 0, ZOOM_LEVEL_MIN, ZOOM_LEVEL_MAX)
}

/** Step the zoom and persist it, mirroring Ctrl +/-/0 from the C# build. */
async function stepZoom(direction) {
  const currentLevel = zoomLevel()
  const next =
    direction === 0
      ? 0
      : clamp(
          Math.round((currentLevel + direction * ZOOM_LEVEL_STEP) * 100) / 100,
          ZOOM_LEVEL_MIN,
          ZOOM_LEVEL_MAX,
        )
  if (Math.abs(next - currentLevel) < 0.0001) return
  state.settings = { ...state.settings, zoomLevel: next }
  applyZoom()
  await window.tcpchat.settings.update({ zoomLevel: next })
}

// ---------------------------------------------------------------------------
// Status bar and connection indicator
// ---------------------------------------------------------------------------

function paintStatus() {
  const stats = state.stats ?? {}
  const text = document.getElementById('status-text')
  if (text) text.textContent = stats.statusText ?? ''

  const count = document.getElementById('status-count')
  if (count) count.textContent = `${state.messages.length} 条`

  const poll = document.getElementById('status-poll')
  if (poll) poll.textContent = `${stats.pollSeconds ?? state.settings?.pollSeconds ?? 3} 秒`

  const sync = document.getElementById('status-sync')
  if (sync) {
    sync.textContent = stats.lastSyncTime ? `同步于 ${formatTime(new Date(stats.lastSyncTime).toISOString())}` : '尚未同步'
  }

  const folder = document.getElementById('status-folder')
  if (folder) {
    folder.textContent = state.settings?.chatFolder ?? ''
    folder.title = state.settings?.chatFolder ?? ''
  }
}

function paintConnection() {
  const dot = document.getElementById('conn-dot')
  const text = document.getElementById('conn-text')
  const { ok, message } = state.connection
  if (dot) dot.className = `dot${ok ? ' ok' : ' bad'}`
  if (text) text.textContent = message || (ok ? '已连接' : '未连接')

  const encryption = state.stats?.encryptionEnabled
  const status = document.getElementById('conn-status')
  if (status) {
    status.title = [
      message,
      `目录：${state.settings?.chatFolder ?? ''}`,
      encryption ? `端到端加密已启用（第 ${state.stats.sendPasswordNumber} 把密码发送）` : '未加密（明文发送）',
    ].join('\n')
  }
}

/**
 * Tell the user once if the glass pipeline could not be built, and say so in the
 * status bar. Without a WebGL2 context the theme stays opaque, which is correct
 * but looks like the glass switch did nothing.
 */
let reportedGlassFailure = false
function noteGlassHealth() {
  const { supported, reason, mode } = state.glass
  if (supported) {
    reportedGlassFailure = false
    return
  }
  if (reportedGlassFailure) return
  reportedGlassFailure = true
  showToast(`液态玻璃不可用：${reason}`, true, 7000)
  state.stats = { ...(state.stats ?? {}), statusText: `⚠ ${reason}` }
  paintStatus()
  void mode
}

function paintToolbar() {
  paintConnection()
  paintStatus()
}

// ---------------------------------------------------------------------------
// Glass
// ---------------------------------------------------------------------------

/**
/**
 * Paint the mode the main process decided.
 *
 *   on  — the native panel is supplying the backdrop
 *   off — the opaque plain theme
 */
function applyGlassMode() {
  const mode = state.glass.mode ?? 'off'
  document.body.dataset.glass = mode === 'on' ? 'on' : 'off'
  if (mode !== 'on') {
    // No panel, no backdrop to adapt the text contrast to.
    delete document.body.dataset.luma
    lumaMode = ''
  }
  paintStatus()
  if (mode === 'on') syncBubblePanels()
  else clearBubblePanels()
  // The backdrop belongs to the plain theme, so the mode change decides it.
  applyPlainBackground()
}

// ---------------------------------------------------------------------------
// Message flow
// ---------------------------------------------------------------------------

function nickname() {
  return state.settings?.nickname ?? ''
}

/*
 * Identity of everything that building a bubble element depends on.
 *
 * upsertMessage tears the element down and rebuilds it, which is correct when
 * the message genuinely changed (decryption finished, send status moved) and
 * wrong when the *same* message arrives twice. It does arrive twice on every
 * send: the main process both emits `message-added` and returns the message, and
 * the renderer handles both. Without this the element is appended, removed and
 * appended again — the message visibly appears, vanishes, and reappears.
 */
function messageRenderKey(msg) {
  return [
    msg.remoteName ?? '',
    msg.text ?? '',
    msg.status ?? '',
    msg.quote?.text ?? '',
    msg.attach?.name ?? '',
    msg.attach?.size ?? '',
    msg.time ?? '',
  ].join('\u0000')
}

/** Insert a message, keeping the list ordered and the view sensibly scrolled. */
function upsertMessage(msg, { scroll = true } = {}) {
  syncBubblePanels()
  const index = state.messages.findIndex((m) => m.remoteName === msg.remoteName)
  const existing = index >= 0
  const key = messageRenderKey(msg)
  const unchanged = existing && state.messages[index].renderKey === key

  if (existing) {
    state.messages[index] = { ...state.messages[index], ...msg, renderKey: key }
  } else {
    msg.renderKey = key
    state.messages.push(msg)
    state.messages.sort((a, b) => (a.remoteName < b.remoteName ? -1 : a.remoteName > b.remoteName ? 1 : 0))
  }

  const stick = isAtBottom()
  // A second delivery of an identical message must not touch the DOM: removing
  // and re-appending is what the user sees as a blink.
  if (!unchanged) {
    if (existing) removeMessage(msg.remoteName)
    appendMessage(msg, nickname())
  }
  if (scroll && (stick || msg.isSelf || (nickname() && msg.from === nickname()))) {
    scrollToBottom(!existing)
  }
  if (searchQuery()) refreshSearch()
  paintStatus()
}

function dropMessage(remoteName) {
  syncBubblePanels()
  const index = state.messages.findIndex((m) => m.remoteName === remoteName)
  if (index >= 0) state.messages.splice(index, 1)
  removeMessage(remoteName)
  if (searchQuery()) refreshSearch()
  paintStatus()
}

function resetMessages(messages) {
  syncBubblePanels()
  state.messages = [...messages]
  state.renderLimit = RENDER_WINDOW
  setRenderLimit(state.renderLimit)
  renderAll(state.messages, nickname(), state.renderLimit)
  scrollToBottom(false)
  reapplySearch()
  paintStatus()
}

/**
 * Make sure a message is in the DOM, widening the render window if it is not.
 *
 * Search matches against the whole list but highlights and scrolls through the
 * DOM, so a hit older than the window has a count and no element to show. Widening
 * to exactly that message keeps the reveal working without rendering the history
 * the window exists to avoid.
 */
function ensureRendered(remoteName) {
  const index = state.messages.findIndex((m) => m.remoteName === remoteName)
  if (index < 0) return false
  const needed = state.messages.length - index
  if (needed <= state.renderLimit) return true
  state.renderLimit = needed
  setRenderLimit(state.renderLimit)
  renderAll(state.messages, nickname(), state.renderLimit)
  reapplySearch()
  return true
}

// ---------------------------------------------------------------------------
// Window chrome
// ---------------------------------------------------------------------------

function initTitlebar() {
  document.getElementById('btn-min').addEventListener('click', () => window.tcpchat.window.minimize())
  document.getElementById('btn-max').addEventListener('click', () => window.tcpchat.window.toggleMaximize())
  document.getElementById('btn-close').addEventListener('click', () => window.tcpchat.window.close())
  document.getElementById('btn-settings').addEventListener('click', () =>
    openSettings(state.settings ?? {}),
  )
  document.getElementById('btn-shot').addEventListener('click', () => void captureWindow())
}

/**
 * Copy a picture of the window to the clipboard.
 *
 * Neither obvious route works in this build. The main process's `clipboard` has
 * no writeImage — it is the web clipboard API under Electron's name, and it
 * wants ClipboardItem objects — and navigator.clipboard.write from here is
 * refused by the permission layer.
 *
 * What does work is webContents.copyImageAt: it writes an image to the system
 * clipboard natively and takes a point in the page. So the shot is put on screen
 * for the duration of that call and taken away again. It has to be genuinely
 * painted — Chromium skips anything it believes is invisible, and an unrendered
 * image has nothing to copy — so it sits behind the composer at full opacity
 * rather than at opacity 0.
 *
 * Guarded while a capture is in flight: capturePage takes a frame from the
 * compositor, so a second click before the first returns would queue another
 * shot of an unchanged window and a second toast claiming success.
 */
let capturing = false

async function captureWindow() {
  if (capturing) return
  capturing = true
  let holder = null
  try {
    const result = await window.tcpchat.window.capture()
    if (!result?.ok) {
      showToast(result?.message || '截图失败', true)
      return
    }

    // A blob URL, not a data URL: the page's CSP refuses data: images.
    const binary = atob(result.png)
    const bytes = new Uint8Array(binary.length)
    for (let i = 0; i < binary.length; i += 1) bytes[i] = binary.charCodeAt(i)
    const url = URL.createObjectURL(new Blob([bytes], { type: 'image/png' }))

    holder = h('img', { src: url, class: 'shot-copy-source' })
    document.body.append(holder)
    await new Promise((resolve, reject) => {
      holder.addEventListener('load', resolve, { once: true })
      holder.addEventListener('error', () => reject(new Error('图片装入页面失败')), { once: true })
    })

    /*
     * Floored, not rounded. The first version rounded, and with a 1x1 box at
     * bottom:0 the centre came to viewportHeight — one row past the bottom of the
     * page, where there is no image, so copyImageAt had nothing to take. That is
     * why the toast said success and nothing could be pasted.
     */
    const rect = holder.getBoundingClientRect()
    const ok = await window.tcpchat.window.copyImageAt({
      x: Math.floor(rect.left + rect.width / 2),
      y: Math.floor(rect.top + rect.height / 2),
    })
    if (!ok) {
      showToast('复制到剪贴板失败', true)
      return
    }
    showToast(`已复制到剪贴板（${result.width} × ${result.height}）`)
  } catch (err) {
    showToast(`复制到剪贴板失败：${err.message || err}`, true)
  } finally {
    if (holder) {
      URL.revokeObjectURL(holder.src)
      holder.remove()
    }
    capturing = false
  }
}

/** Frameless windows need their own resize affordances. */
function initResizeHandles() {
  for (const handle of $$('.rz')) {
    handle.addEventListener('pointerdown', async (event) => {
      if (event.button !== 0 || state.windowMaximized) return
      event.preventDefault()
      const start = await window.tcpchat.window.getBounds()
      if (!start) return

      const direction = handle.dataset.dir
      const originX = event.screenX
      const originY = event.screenY
      let frame = null
      let pending = null

      const flush = () => {
        frame = null
        if (!pending) return
        window.tcpchat.window.setBounds(pending)
        pending = null
      }

      const onMove = (moveEvent) => {
        const dx = moveEvent.screenX - originX
        const dy = moveEvent.screenY - originY
        const bounds = { x: start.x, y: start.y, width: start.width, height: start.height }
        if (direction.includes('e')) bounds.width = start.width + dx
        if (direction.includes('s')) bounds.height = start.height + dy
        if (direction.includes('w')) {
          bounds.x = start.x + dx
          bounds.width = start.width - dx
        }
        if (direction.includes('n')) {
          bounds.y = start.y + dy
          bounds.height = start.height - dy
        }
        pending = bounds
        if (frame === null) frame = requestAnimationFrame(flush)
      }

      const onUp = () => {
        window.removeEventListener('pointermove', onMove)
        window.removeEventListener('pointerup', onUp)
        if (frame !== null) cancelAnimationFrame(frame)
        flush()
      }

      window.addEventListener('pointermove', onMove)
      window.addEventListener('pointerup', onUp)
    })
  }
}

function initShortcuts() {
  window.addEventListener(
    'keydown',
    (event) => {
      const mod = event.ctrlKey || event.metaKey

      if (event.key === 'Escape') {
        if (isMenuOpen()) closeMenu()
        else if (isSearchOpen()) closeSearch()
        return
      }

      if (!mod) return

      if (event.key === 'f' || event.key === 'F') {
        event.preventDefault()
        openSearch()
        return
      }
      if (event.key === '=' || event.key === '+') {
        event.preventDefault()
        void stepZoom(1)
        return
      }
      if (event.key === '-' || event.key === '_') {
        event.preventDefault()
        void stepZoom(-1)
        return
      }
      if (event.key === '0') {
        event.preventDefault()
        void stepZoom(0)
      }
    },
    true,
  )

  // Ctrl + wheel zooms, matching the C# build.
  window.addEventListener(
    'wheel',
    (event) => {
      if (!event.ctrlKey) return
      event.preventDefault()
      void stepZoom(event.deltaY < 0 ? 1 : -1)
    },
    { passive: false },
  )
}

/** Code-block copy buttons and collapsible ::: containers. */
function initContentInteractions() {
  document.getElementById('msg-list').addEventListener('click', async (event) => {
    const copyButton = event.target.closest('[data-copy-code]')
    if (copyButton) {
      const block = copyButton.closest('.code-block')
      const code = block?.querySelector('.scroller > code')
      if (code) {
        const ok = await copyText(code.innerText)
        copyButton.textContent = ok ? '已复制' : '复制失败'
        setTimeout(() => {
          copyButton.textContent = '复制'
        }, 1400)
      }
      return
    }

    const title = event.target.closest('.box > .box-title')
    if (title) {
      const box = title.parentElement
      box.dataset.open = box.dataset.open === 'true' ? 'false' : 'true'
    }
  })
}

// ---------------------------------------------------------------------------
// Main-process subscriptions
// ---------------------------------------------------------------------------

function subscribe() {
  const api = window.tcpchat

  api.on('message-added', (msg) => upsertMessage(msg))

  api.on('message-removed', (remoteName) => dropMessage(remoteName))

  api.on('messages-reset', () => resetMessages([]))

  api.on('connection', (value) => {
    state.connection = value ?? state.connection
    paintConnection()
    if (!value?.ok && value?.message) setEmptyText(value.message)
  })

  api.on('status', (text) => {
    state.stats = { ...(state.stats ?? {}), statusText: text }
    paintStatus()
  })

  api.on('synced', (at) => {
    state.stats = { ...(state.stats ?? {}), lastSyncTime: at }
    paintStatus()
  })

  api.on('cipher-state', (stats) => {
    state.stats = { ...(state.stats ?? {}), ...stats }
    paintConnection()
    paintStatus()
  })

  api.on('error', (message) => {
    if (!message) return
    state.stats = { ...(state.stats ?? {}), statusText: message }
    paintStatus()
    showToast(message, true)
  })

  api.on('settings-changed', (settings) => {
    const previous = state.settings
    state.settings = settings
    applyTheme()
    applyUiStyle()
    applyFont()
    applyZoom()
    paintToolbar()
    // Picking or clearing a backdrop comes through here; drop the cached data
    // URL so the next apply reads the new one instead of reusing the old.
    if (previous?.plainBackgroundPath !== settings.plainBackgroundPath) {
      invalidatePlainBackground()
      applyPlainBackground()
    }
    // The fit mode is not in the settings snapshot the renderer keeps, so it is
    // read back with the image rather than compared here.
    if (previous?.plainBackgroundFit !== settings.plainBackgroundFit) {
      invalidatePlainBackground()
      applyPlainBackground()
    }
    if (previous && previous.chatFolder !== settings.chatFolder) resetMessages(state.messages)
  })

  api.on('glass-state', (glass) => {
    state.glass = glass ?? state.glass
    applyGlassMode()
  })

  api.on('glass-luma', handleLuma)

  api.on('window-state', (windowState) => {
    state.windowMaximized = Boolean(windowState?.maximized || windowState?.fullScreen)
    document.body.classList.toggle('maximized', state.windowMaximized)
  })
}

// ---------------------------------------------------------------------------
// Boot
// ---------------------------------------------------------------------------

async function boot() {
  const initial = await window.tcpchat.chat.state()

  state.settings = initial.settings
  state.stats = { ...(initial.stats ?? {}), statusText: initial.status ?? '' }
  state.connection = initial.connection ?? state.connection
  state.glass = initial.glass ?? state.glass

  applyTheme()
  applyUiStyle()
  applyFont()
  applyZoom()
  applyGlassMode()

  /*
   * Bubble panels track their bubbles, so anything that moves them must ask for
   * a re-sync. Scroll and resize happen continuously; the hooks in
   * upsertMessage/resetMessages/dropMessage cover structural changes.
   * `syncBubblePanels` coalesces to one sync per frame and returns early when
   * the geometry has not changed, so calling it liberally is cheap.
   */
  document.getElementById('msg-list')?.addEventListener('scroll', syncBubblePanels, { passive: true })
  window.addEventListener('resize', syncBubblePanels)
  /*
   * Late markdown reflow and image loads change bubble heights without a scroll
   * or resize. A polling timer was tried here and is the wrong tool twice over:
   * it costs a full measurement pass every second forever, and any tick that
   * produced a slightly different signature pushed a pointless setBounds storm
   * at panels that had not moved — which is flicker with nothing to explain it.
   * A ResizeObserver fires only when the layout genuinely changed.
   */
  if (typeof ResizeObserver === 'function') {
    const list = document.getElementById('msg-list')
    if (list) new ResizeObserver(() => syncBubblePanels()).observe(list)
  }

  configureComposer({
    onSendText: async (text, quote) => {
      const result = await window.tcpchat.chat.sendText(text, quote)
      if (result?.message) upsertMessage(result.message)
      return result
    },
    onSendFiles: (paths) => window.tcpchat.chat.sendFiles(paths, 0),
    onSendVoice: (bytes, seconds, durationMs) =>
      window.tcpchat.chat.sendVoice(bytes, seconds, durationMs),
    onStatus: (text) => {
      state.stats = { ...(state.stats ?? {}), statusText: text }
      paintStatus()
    },
  })

  configureMessages({
    onReply: (msg) => setReply(msg),
    onWithdraw: async (msg) => {
      const result = await window.tcpchat.chat.deleteMessage(msg.remoteName)
      if (!result?.ok) showToast(result?.error || '撤回失败', true)
    },
    onLoadOlder: () => {
      state.renderLimit += RENDER_WINDOW
      setRenderLimit(state.renderLimit)
      renderAll(state.messages, nickname(), state.renderLimit)
      // Keep the reader where they were. The button they pressed is what moved,
      // and jumping to the bottom would undo the point of pressing it.
      document.getElementById('load-older')?.scrollIntoView({ block: 'start' })
      reapplySearch()
    },
  })

  configureSearch({ getMessages: () => state.messages, ensureRendered })
  configureSettings({
    onChange: async (patch) => {
      const updated = await window.tcpchat.settings.update(patch)
      if (!updated) return null
      state.settings = updated
      applyTheme()
      applyUiStyle()
      applyFont()
      applyZoom()
      state.glass = await window.tcpchat.glass.status()
      applyGlassMode()
      paintToolbar()
      return updated
    },
  })

  initComposer()
  initSearch()
  initTitlebar()
  initResizeHandles()
  initShortcuts()
  initContentInteractions()

  subscribe()

  resetMessages(initial.messages ?? [])
  paintToolbar()

  const windowState = await window.tcpchat.window.state()
  state.windowMaximized = Boolean(windowState?.maximized)
  document.body.classList.toggle('maximized', state.windowMaximized)

  window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', () => {
    if ((state.settings?.theme ?? 0) === 0) applyTheme()
  })

  if (!state.glass.supported) {
    // Surfaced once so the user knows why the glass button is dimmed.
    console.info('[tcpchat] liquid glass unavailable:', state.glass.reason)
  }

  // First run: the C# build asks for a nickname before anything else, and the
  // whole app keys "is this mine?" off it, so the settings dialog opens itself
  // once there is no nickname yet.
  if (!(state.settings?.nickname ?? '').trim()) {
    setEmptyText('先设置一个昵称，然后就可以开始聊天了')
    setTimeout(() => {
      void openSettings(state.settings ?? {})
    }, 400)
  }
}

boot().catch((err) => {
  console.error('[tcpchat] renderer boot failed', err)
  const list = document.getElementById('msg-list')
  if (list) {
    clear(list)
    list.append(
      h('div', { class: 'empty-state' }, [
        h('div', { class: 'big', text: '⚠' }),
        h('div', { text: `界面启动失败：${err.message}` }),
      ]),
    )
  }
})

export { state, sendDraft, focusComposer, show }
