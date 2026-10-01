'use strict'

/**
 * Standalone harness for the native glass module.
 *
 *   npx electron scripts/native-probe.js
 *
 * Creates one panel over a known desktop area and lets the addon's own capture
 * loop run, with the C++ probes compiled in. All diagnostics go to stderr, so
 * run it with stderr captured.
 *
 * This deliberately touches nothing else in the app: it is the smallest thing
 * that exercises Desktop Duplication -> mirror -> shader, which is where the
 * black frames come from.
 */

const path = require('path')
const { app, BrowserWindow, screen } = require('electron')

const glass = require(path.join(__dirname, '..', 'vendor', 'electron-liquid-glass'))

const wait = (ms) => new Promise((resolve) => setTimeout(resolve, ms))

app.whenReady().then(async () => {
  console.log('isSupported:', glass.isSupported())
  if (!glass.isSupported()) {
    app.exit(2)
    return
  }

  const display = screen.getPrimaryDisplay()
  const dpr = display.scaleFactor
  const area = display.workArea

  /*
   * Desktop Duplication only hands over a frame when the desktop actually
   * presents one. On a static desktop `AcquireNextFrame` just times out, so the
   * probe drives a full-screen window that repaints continuously — otherwise it
   * measures nothing and reports zero frames.
   *
   * This is created *before* the host window on purpose: the panel is inserted
   * directly below its anchor window in z-order, so anything created later sits
   * on top of it and hides it.
   */
  const driver = new BrowserWindow({
    x: area.x,
    y: area.y,
    width: area.width,
    height: area.height,
    frame: false,
    show: true,
  })
  await driver.loadURL(
    'data:text/html;charset=utf-8,' +
      encodeURIComponent(`<!doctype html><html><body style="margin:0;overflow:hidden">
      <div id="p" style="position:absolute;inset:0;background:
        radial-gradient(60% 60% at 20% 20%, #ff9d3c 0%, transparent 60%),
        radial-gradient(60% 60% at 80% 30%, #2aa3ef 0%, transparent 60%),
        radial-gradient(70% 70% at 50% 90%, #24c06a 0%, transparent 60%),
        linear-gradient(120deg,#fdf1e6,#eef2fb)"></div>
      <div id="t" style="position:absolute;left:6%;bottom:8%;font:800 120px/1 Segoe UI,sans-serif;color:#232a33">LIQUID GLASS 0123</div>
      <script>
        let i = 0
        setInterval(() => {
          i++
          document.getElementById('p').style.transform = 'translateX(' + Math.sin(i/8)*40 + 'px)'
          document.getElementById('t').style.opacity = 0.6 + 0.4*Math.abs(Math.sin(i/10))
        }, 33)
      </script></body></html>`),
  )

  // A host window that owns the panel, created after the driver so the panel
  // ends up above it.
  const host = new BrowserWindow({
    x: area.x + 8,
    y: area.y + 8,
    width: 240,
    height: 140,
    show: false,
    frame: false,
  })
  await host.loadURL('data:text/html,<body style="margin:0;background:#123"></body>')


  const paneDip = {
    x: area.x + Math.round(area.width * 0.4),
    y: area.y + Math.round(area.height * 0.35),
    width: Math.round(area.width * 0.34),
    height: Math.round(area.height * 0.34),
  }
  const pane = {
    x: Math.round(paneDip.x * dpr),
    y: Math.round(paneDip.y * dpr),
    width: Math.round(paneDip.width * dpr),
    height: Math.round(paneDip.height * dpr),
  }

  const samples = []
  const panel = glass.createPanel({
    ...pane,
    dpr,
    cornerRadius: Math.round(12 * dpr),
    blurSigma: 6,
    displacementScale: 70,
    aberrationIntensity: 2,
    saturation: 1.4,
    capturePolicy: 'all',
    anchorWindow: host,
    lumaBands: [{ id: 0, x: 0, y: 0, width: pane.width, height: pane.height }],
    onLuma: (bands) => {
      const b = bands && (bands['0'] ?? Object.values(bands)[0])
      if (b) samples.push(b)
    },
  })
  if (!panel) {
    console.error('createPanel returned null')
    app.exit(1)
    return
  }
  console.log('panel.id returned by native:', panel.id, typeof panel.id)
  panel.show(0)
  console.log('panel', JSON.stringify(pane))

  await wait(6000)

  const recent = samples.slice(-40)
  const mean = recent.length
    ? {
        r: Math.round(recent.reduce((a, s) => a + s.r, 0) / recent.length),
        g: Math.round(recent.reduce((a, s) => a + s.g, 0) / recent.length),
        b: Math.round(recent.reduce((a, s) => a + s.b, 0) / recent.length),
      }
    : null
  const stats = glass._native && glass._native._stats ? glass._native._stats() : {}
  console.log('luma samples:', samples.length, 'mean:', JSON.stringify(mean))
  console.log('stats:', JSON.stringify(stats))

  panel.hide(0)
  panel.destroy()
  host.destroy()
  glass.shutdown()
  app.exit(0)
})
