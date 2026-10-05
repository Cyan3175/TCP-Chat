'use strict'

/**
 * Native liquid-glass backdrop.
 *
 * The panel is a DirectComposition window owned by the addon's worker thread: it
 * captures the desktop with DXGI Desktop Duplication, blurs it, refracts it
 * through a lens shader and composites the result directly below our window.
 * This class only does lifecycle — create, size, quality, teardown — and turns
 * the addon's luminance callbacks into events the renderer can use.
 *
 * The addon binary must be built from `vendor/electron-liquid-glass`. Building it
 * against Node's headers instead of Electron's silently corrupts N-API values
 * (panel ids come back as denormals, so every id-taking call no-ops); see
 * `npm run build:native`.
 */

const { screen } = require('electron')

const { log } = require('./logger')

/** Corner radius of the glass shell, in CSS pixels. */
const CORNER_RADIUS_CSS = 12

/** Matches the bubbles' own border-radius, so the panel traces the same shape. */
const BUBBLE_RADIUS_CSS = 8

/**
 * Ceiling on per-message panels. Each is a DirectComposition window and a shader
 * pass; a long scrollback should not turn into hundreds of them.
 */
const MAX_BUBBLE_PANELS = 24

/** Fade for a re-shown bubble panel: 0 snaps, which reads as a pop. */
const BUBBLE_FADE_MS = 90

/**
 * Map the quality slider (0..100) onto the shader's parameters.
 *
 * Everything here is in physical pixels, hence the dpr scaling: the addon's
 * geometry constants are display-independent, so a 2x display needs twice the
 * blur radius and edge displacement to look the same.
 */
function paramsForQuality(quality, dpr, blurSigmaCss = 0) {
  const q = Math.min(100, Math.max(0, quality)) / 100
  return {
    cornerRadius: Math.round(CORNER_RADIUS_CSS * dpr),
    /*
     * Blur is in physical pixels, so it scales with dpr like every other length
     * here. A sigma at or below half a pixel is not a blur, and the renderer
     * treats it as 0: the lens then samples the full-resolution desktop crop
     * directly instead of the half-resolution intermediate.
     *
     * The quality slider scales it rather than setting it, so the user's chosen
     * strength survives moving that slider.
     */
    blurSigma: Math.max(0, blurSigmaCss) * (0.6 + q * 0.8) * dpr,
    displacementScale: (40 + q * 70) * dpr,
    aberrationIntensity: 0.5 + q * 3,
    saturation: 1.15 + q * 0.5,
  }
}

/** Luminance bands, in panel-local physical pixels. */
function lumaBands(width, height, dpr) {
  const titleH = Math.round(38 * dpr)
  const composerH = Math.round(56 * dpr)
  const composerTop = Math.max(titleH, height - composerH)
  return [
    { id: 0, x: 0, y: 0, width, height: Math.max(1, titleH) },
    {
      id: 1,
      x: 0,
      y: titleH,
      width,
      height: Math.max(1, composerTop - titleH),
    },
    { id: 2, x: 0, y: composerTop, width, height: Math.max(1, height - composerTop) },
  ]
}

class GlassController {
  /**
   * @param {import('electron').BrowserWindow} window
   * @param {{onLuma?: (bands: object) => void}} options
   */
  constructor(window, options = {}) {
    this.window = window
    this._onLuma = options.onLuma ?? null

    this.enabled = false
    this.supported = false
    this.reason = 'not started'
    /**
     * Which capture policy actually took effect:
     *   'dda-only' — the window is dropped from Desktop Duplication only, so the
     *                panel does not capture itself and the app still shows up in
     *                screenshots. Windows 11 24H2+.
     *   'all'      — fallback: WDA hides the window from every capture path.
     *   'none'     — nothing applied; the panel will feed on itself.
     */
    this.capturePolicy = 'none'
    this.quality = 60
    /** Backdrop blur strength in CSS px of sigma. Set from settings before enable(). */
    this.blurSigma = 0

    /** @type {import('@hicccc77/electron-liquid-glass').GlassPanel|null} */
    this.panel = null
    /** @type {import('@hicccc77/electron-liquid-glass').GlassPanel[]} */
    this.bubblePanels = []
    this._lastBounds = null
    this._syncing = false

    try {
      // Required lazily so a missing or unusable binary degrades to the plain
      // theme instead of preventing the app from starting.
      this.glass = require('@hicccc77/electron-liquid-glass')
      this.supported = Boolean(this.glass && this.glass.isSupported && this.glass.isSupported())
      this.reason = this.supported
        ? 'ok'
        : 'native addon unavailable (needs Windows 10 2004+ and a local build)'
    } catch (err) {
      this.glass = null
      this.supported = false
      this.reason = `native addon failed to load: ${err.message}`
      log.warn('liquid glass unavailable —', err.message)
    }

    this._handleGeometry = () => this.syncBounds()
    window.on('move', this._handleGeometry)
    window.on('resize', this._handleGeometry)
    window.on('restore', this._handleGeometry)
    window.on('maximize', this._handleGeometry)
    window.on('unmaximize', this._handleGeometry)
    window.on('enter-full-screen', this._handleGeometry)
    window.on('leave-full-screen', this._handleGeometry)
    // Applying the exclusion while the window is still being created does not
    // stick: the readback says "all" while the panel keeps capturing the window.
    // Re-assert once it is actually on screen.
    window.on('show', () => {
      this._applyCapturePolicy()
      setTimeout(() => this._applyCapturePolicy(), 1200)
    })
  }

  /** True when the native backdrop is actually contributing pixels. */
  get active() {
    return Boolean(this.panel) && this.enabled
  }

  /**
   * The glass as it was last drawn, as straight BGRA.
   *
   * The panel is excluded from every capture path — the addon says it has to be,
   * or the glass captures itself — so this is the only way its pixels get out of
   * the process, and therefore the only way a screenshot can contain them.
   *
   * The readback runs on the addon's worker thread and blocks this call for a
   * few milliseconds. Returns null when the glass is off, so callers fall back to
   * whatever they would have done anyway.
   */
  readPanel() {
    if (!this.active) return null
    const native = this.glass?._native
    if (!native || typeof native.readPanel !== 'function') return null
    try {
      const result = native.readPanel(this.panel.id)
      if (!result || !result.ok || !result.pixels) return null
      return { width: result.width, height: result.height, pixels: result.pixels }
    } catch (err) {
      log.warn('glass readback failed', err)
      return null
    }
  }

  status() {
    return {
      enabled: this.active,
      requested: this.enabled,
      supported: this.supported,
      reason: this.reason,
      capturePolicy: this.capturePolicy,
    }
  }

  /** Physical-pixel rect for the panel: exactly the window's client area. */
  _physicalBounds() {
    const content = this.window.getContentBounds()
    try {
      // `dipToScreenRect` is Windows-only and correct across mixed-DPI desktops.
      if (typeof screen.dipToScreenRect === 'function') {
        return screen.dipToScreenRect(null, content)
      }
    } catch {
      /* fall through to the scale-factor approximation */
    }
    const dpr = screen.getDisplayMatching(content).scaleFactor || 1
    return {
      x: Math.round(content.x * dpr),
      y: Math.round(content.y * dpr),
      width: Math.round(content.width * dpr),
      height: Math.round(content.height * dpr),
    }
  }

  _dpr() {
    const content = this.window.getContentBounds()
    return screen.getDisplayMatching(content).scaleFactor || 1
  }

  /**
   * Take the host window out of this module's own capture path.
   *
   * Without this the panel captures the desktop *including our window*, which
   * sits directly above it — the glass refracts its own output and the result
   * creeps. `dda-only` is the right policy: it removes the window from DXGI
   * Desktop Duplication while leaving it visible to ordinary screenshots.
   *
   * `dda-only` needs Windows 11 24H2+; when it is unavailable we fall back to
   * `all`, which works everywhere but does hide the window from screenshots.
   */
  _applyCapturePolicy() {
    if (!this.glass?.setWindowCapturePolicy) {
      this.capturePolicy = 'none'
      return
    }
    try {
      if (this.glass.setWindowCapturePolicy(this.window, 'dda-only')) {
        this.capturePolicy = 'dda-only'
      } else if (this.glass.setWindowCapturePolicy(this.window, 'all')) {
        this.capturePolicy = 'all'
        log.warn(
          'liquid glass: selective DDA exclusion unavailable — the window is now ' +
            'hidden from all screen capture, not just Desktop Duplication',
        )
      } else {
        this.capturePolicy = 'none'
      }
      // Read it back. "Applied" and "in effect" are not the same thing, and a
      // silent failure here is exactly what a ghosted window looks like.
      this.capturePolicyReadback = this.glass.getWindowCapturePolicy
        ? this.glass.getWindowCapturePolicy(this.window)
        : 'unsupported'
      log.info(
        `liquid glass: window capture policy = ${this.capturePolicy} ` +
          `(readback: ${this.capturePolicyReadback})`,
      )
    } catch (err) {
      this.capturePolicy = 'none'
      log.warn('liquid glass capture policy failed —', err.message)
    }
  }

  /** Build the panel and show it. Returns false when the panel could not be made. */
  enable() {
    this.enabled = true
    if (!this.supported || this.panel) return this.active
    if (this.window.isDestroyed()) return false

    try {
      this._applyCapturePolicy()
      const bounds = this._physicalBounds()
      const dpr = this._dpr()
      this.panel = this.glass.createPanel({
        ...bounds,
        dpr,
        ...paramsForQuality(this.quality, dpr, this.blurSigma),
        // Must stay true: without it the panel captures itself and the image
        // converges on black.
        capturePolicy: this.capturePolicy,
        anchorWindow: this.window,
        lumaBands: lumaBands(bounds.width, bounds.height, dpr),
        onLuma: (bands) => {
          try {
            this._onLuma?.(bands)
          } catch (err) {
            log.warn('glass luma handler failed —', err.message)
          }
        },
      })
      if (!this.panel) {
        this.reason = 'createPanel returned null'
        log.warn('liquid glass panel was not created')
        return false
      }
      this._lastBounds = bounds
      this.panel.show(160)
      this.reason = 'ok'
      const created = paramsForQuality(this.quality, dpr, this.blurSigma)
      log.info(
        `liquid glass panel created ${JSON.stringify(bounds)} dpr=${dpr} ` +
          `q=${this.quality} blur=${created.blurSigma.toFixed(1)} capture=${this.capturePolicy}`,
      )
      return true
    } catch (err) {
      this.reason = `panel creation failed: ${err.message}`
      log.warn('liquid glass panel failed —', err.message)
      this.panel = null
      return false
    }
  }

  /** Release the panel and stop the backdrop. */
  disable() {
    this.enabled = false
    this._clearBubblePanels()
    const panel = this.panel
    this.panel = null
    this._lastBounds = null
    if (panel) {
      try {
        panel.hide(0)
      } catch {
        /* already gone */
      }
      try {
        panel.destroy()
      } catch (err) {
        log.warn('glass panel destroy failed —', err.message)
      }
    }
    return true
  }

  /*
   * —— Per-message glass ————————————————————————————————————————————————
   *
   * The DOM cannot give a bubble its own backdrop filter: `backdrop-filter`
   * samples Chromium's compositing surface, and the panel supplying the real
   * backdrop is a separate OS window underneath ours. So a bubble that should
   * have its own blur, dispersion and lensed rim needs its own panel.
   *
   * That is what the addon is built for: every panel renders its own rectangle
   * out of the *same* shared desktop mirror, so N bubbles still mean one
   * duplication session and one capture per desktop frame, not N.
   *
   * Panels are pooled by index and only ever repositioned, so scrolling costs
   * setBounds calls rather than a create/destroy storm.
   */

  /**
   * @param {Array<{x:number,y:number,width:number,height:number}>} rects
   *   Bubble rectangles in CSS pixels relative to the window's content area.
   */
  setBubbles(rects) {
    if (!this.supported || !this.panel || !this.enabled) {
      this._clearBubblePanels()
      return
    }
    const want = Array.isArray(rects) ? rects.slice(0, MAX_BUBBLE_PANELS) : []
    this._lastBubbleRects = want

    // Grow to the high-water mark; never shrink by destroying.
    //
    // Destroying surplus panels on every count change is what made the glass
    // flicker while scrolling: a bubble crossing the window edge changed the
    // count, the panel holding that slot was destroyed, and the replacement's
    // first frame is necessarily empty. Hiding keeps the panel — and its
    // swapchain contents — so re-showing it is instant.
    while (this.bubblePanels.length < want.length) {
      const panel = this._createBubblePanel()
      if (!panel) break
      this.bubblePanels.push(panel)
      log.info(`liquid glass: bubble panel pool grew to ${this.bubblePanels.length}`)
    }

    const content = this.window.getContentBounds()
    const dpr = this._dpr()
    const params = this._bubbleParams(dpr)
    // Keyed per panel: a batch can mix pills and bubbles, so a single global
    // "params changed" flag would push the wrong radius onto half of them.
    const paramsKey = JSON.stringify(params)

    // Match panels to bubbles by identity, not by position in the list.
    //
    // The renderer only offers bubbles that are entirely inside the window, so a
    // bubble scrolling past an edge renumbers everything after it. Assigning by
    // index therefore moved *every* panel onto a different bubble's rectangle on
    // a single scroll step — a full re-render of all of them — and while a bubble
    // hovered at an edge the mapping flipped back and forth every frame. That is
    // the twitching, and it is worst for tall bubbles because they straddle an
    // edge most of the time.
    //
    // A panel that already draws a key keeps it, so only the bubbles actually
    // entering and leaving the window change panels.
    const pools = this.bubblePanels
    for (const panel of pools) panel.__taken = false
    const held = new Map()
    for (const panel of pools) {
      if (panel.__key && !held.has(panel.__key)) held.set(panel.__key, panel)
    }

    const assignments = new Array(want.length).fill(null)
    for (let i = 0; i < want.length; i++) {
      const panel = held.get(want[i].key)
      if (panel && !panel.__taken) {
        panel.__taken = true
        assignments[i] = panel
      }
    }

    const spare = pools.filter((panel) => !panel.__taken)
    for (let i = 0; i < want.length; i++) {
      if (assignments[i]) continue
      const panel = spare.pop()
      if (!panel) break
      panel.__taken = true
      panel.__key = want[i].key
      assignments[i] = panel
    }

    for (const panel of pools) {
      if (!panel.__taken) {
        panel.__key = undefined
        panel.__rect = undefined
        if (panel.__visible) {
          try {
            panel.hide(0)
          } catch {
            /* already gone */
          }
          panel.__visible = false
        }
      }
    }

    for (let i = 0; i < want.length; i++) {
      const panel = assignments[i]
      if (!panel) break
      const r = want[i]
      // Remembered so _repositionBubblePanels can re-place this panel after a
      // window move without knowing its slot in the array.
      panel.__rect = r
      try {
        // Geometry and parameters first, *then* show.
        //
        // A fresh panel is 10x10 at (0,0). Showing it before it has been moved
        // puts a glass square in the window's top-left corner for a frame, which
        // is what "the glass starts out misplaced and settles later" is.
        //
        // Position changes are cheap (SetWindowPos); only a *size* change makes
        // the addon call ResizeBuffers, which discards the back buffer and
        // flashes. A given bubble's size is stable, so that path is rare.
        panel.setBounds(
          screen.dipToScreenRect(null, {
            x: Math.round(content.x + r.x),
            y: Math.round(content.y + r.y),
            width: Math.max(2, Math.round(r.width)),
            height: Math.max(2, Math.round(r.height)),
          }),
        )
        const wanted = r.pill ? this._bubbleParams(dpr, true) : params
        const wantedKey = r.pill ? `${paramsKey}|pill` : paramsKey
        if (panel.__paramsKey !== wantedKey) {
          panel.setParams(wanted)
          panel.__paramsKey = wantedKey
        }
        if (!panel.__visible) {
          panel.show(BUBBLE_FADE_MS)
          panel.__visible = true
        }
      } catch (err) {
        log.warn('bubble panel update failed —', err.message)
      }
    }
  }

  _bubbleParams(dpr, pill = false) {
    // Same glass as the window, different shape. A pill's radius is half its
    // height and clamps there, so the pill case asks the panel for a radius it
    // will clamp identically rather than a fixed 8px.
    return {
      ...paramsForQuality(this.quality, dpr, this.blurSigma),
      cornerRadius: pill ? 999 : Math.round(BUBBLE_RADIUS_CSS * dpr),
    }
  }

  _createBubblePanel() {
    try {
      const dpr = this._dpr()
      const panel = this.glass.createPanel({
        x: 0,
        y: 0,
        width: 10,
        height: 10,
        dpr,
        ...this._bubbleParams(dpr),
        // Same policy as the shell: the bubble must not capture the shell it
        // sits on, or the two glass layers feed each other.
        capturePolicy: this.capturePolicy,
        anchorWindow: this.window,
      })
      if (!panel) return null
      // Left hidden: setBubbles() shows it once it has bounds, and only if a
      // bubble actually wants that slot. Showing here would flash a 10x10
      // panel at the window's top-left corner.
      panel.__visible = false
      return panel
    } catch (err) {
      log.warn('bubble panel creation failed —', err.message)
      return null
    }
  }

  _clearBubblePanels() {
    const panels = this.bubblePanels
    this.bubblePanels = []
    this._lastBubbleRects = []
    for (const panel of panels) {
      try {
        panel.hide(0)
        panel.destroy()
      } catch {
        /* already gone */
      }
    }
  }

  /** Applies the requested on/off state (the addon has no degraded state). */
  apply(enabled) {
    if (enabled && this.supported) return this.enable(), this.status()
    if (!enabled) this.disable()
    return this.status()
  }

  /** Re-assert position/size. Cheap: skipped when the rectangle is unchanged. */
  syncBounds() {
    const panel = this.panel
    if (!panel || !this.enabled || this._syncing) return
    if (this.window.isDestroyed()) return
    this._syncing = true
    try {
      // Cheap insurance: re-assert the exclusion while we are here anyway.
      this._applyCapturePolicy()
      const bounds = this._physicalBounds()
      const last = this._lastBounds
      const moved =
        !last ||
        last.x !== bounds.x ||
        last.y !== bounds.y ||
        last.width !== bounds.width ||
        last.height !== bounds.height
      if (moved) {
        this._lastBounds = bounds
        panel.setBounds(bounds)
        panel.anchor(this.window)
        panel.setLumaBands(lumaBands(bounds.width, bounds.height, this._dpr()))
      }
      /*
       * Bubble panels are positioned from content-relative rectangles, so a
       * window move leaves them exactly where they were: the rectangles did not
       * change, the window did. The renderer's signature check cannot see that
       * either — it compares the same content-relative numbers and correctly
       * concludes nothing moved — so the repositioning has to happen here, from
       * the rectangles we already hold.
       */
      this._repositionBubblePanels()
    } catch (err) {
      log.warn('glass syncBounds failed —', err.message)
    } finally {
      this._syncing = false
    }
  }

  /** Re-place the existing bubble panels from the last rectangles received. */
  _repositionBubblePanels() {
    if (!this.bubblePanels.length) return
    const content = this.window.getContentBounds()
    /*
     * Each panel carries the rectangle it was last assigned.
     *
     * This cannot index `_lastBubbleRects` by panel position any more: setBubbles
     * matches panels to bubbles by key, so a panel's slot in the array no longer
     * says which rectangle is its. A window move (maximise included) re-places
     * every panel from the rectangle it actually holds.
     */
    for (const panel of this.bubblePanels) {
      const r = panel.__rect
      if (!panel.__visible || !r) continue
      try {
        panel.setBounds(
          screen.dipToScreenRect(null, {
            x: Math.round(content.x + r.x),
            y: Math.round(content.y + r.y),
            width: Math.max(2, Math.round(r.width)),
            height: Math.max(2, Math.round(r.height)),
          }),
        )
      } catch {
        /* panel already gone */
      }
    }
  }

  /** Re-apply visual parameters (called when the quality slider moves). */
  setQuality(quality) {
    this.quality = Math.min(100, Math.max(0, Number(quality) || 0))
    if (!this.panel) return
    try {
      this.panel.setParams(paramsForQuality(this.quality, this._dpr(), this.blurSigma))
    } catch (err) {
      log.warn('glass setParams failed —', err.message)
    }
  }

  /** Rebuild the panel — a manual retry after fixing a driver or display issue. */
  retry() {
    if (!this.supported) return this.status()
    this.disable()
    this.enable()
    return this.status()
  }

  dispose() {
    this.disable()
  }

  /** Addon counters, for diagnostics. */
  stats() {
    try {
      return this.glass?._native?._stats?.() ?? null
    } catch {
      return null
    }
  }
}

module.exports = { GlassController, paramsForQuality, lumaBands, CORNER_RADIUS_CSS, MAX_BUBBLE_PANELS }
