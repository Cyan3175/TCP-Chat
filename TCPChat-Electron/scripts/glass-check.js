'use strict'

/**
 * Native glass self-check.
 *
 *   npm run test:glass
 *
 * Answers the only question that matters for this feature: can this machine
 * actually put a glass backdrop on screen? It builds a panel over a small
 * animated window and reports what the addon captured.
 *
 * Exit codes:
 *   0  the panel captured and rendered real desktop pixels
 *   3  the addon loaded but produced no frames (static desktop, or a display
 *      state it cannot duplicate) — the app falls back to the plain theme
 *   4  the native addon could not be loaded at all
 *
 * The distinction matters: an unsupported machine is not a bug in the app, and
 * the app degrades rather than showing a black backdrop.
 */

const path = require('path')
const { app, BrowserWindow, screen } = require('electron')

const glass = require(path.join(__dirname, '..', 'vendor', 'electron-liquid-glass'))

const wait = (ms) => new Promise((resolve) => setTimeout(resolve, ms))

app.whenReady().then(async () => {
  if (!glass.isSupported || !glass.isSupported()) {
    console.error('FAIL: native addon unavailable (isSupported() === false)')
    app.exit(4)
    return
  }
  console.log('isSupported()    : true')

  const display = screen.getPrimaryDisplay()
  const dpr = display.scaleFactor
  const area = display.workArea

  /*
   * Desktop Duplication only delivers a frame when the desktop presents one, so
   * a driver window that repaints is part of the test rather than decoration.
   * It is created first so the panel, which is inserted directly below its
   * anchor, ends up above it.
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
      encodeURIComponent(`<!doctype html><body style="margin:0;overflow:hidden">
      <div id="p" style="position:absolute;inset:0;background:
        radial-gradient(60% 60% at 25% 25%, #ff9d3c 0%, transparent 60%),
        radial-gradient(60% 60% at 75% 35%, #2aa3ef 0%, transparent 60%),
        linear-gradient(120deg,#fdf1e6,#eef2fb)"></div>
      <script>let i=0;setInterval(()=>{i++;
        document.getElementById('p').style.transform='translateX('+Math.sin(i/8)*40+'px)'
      },33)</script></body>`),
  )

  const host = new BrowserWindow({ x: area.x + 8, y: area.y + 8, width: 200, height: 120, show: false, frame: false })
  await host.loadURL('data:text/html,<body style="margin:0;background:#123"></body>')

  const pane = {
    x: Math.round((area.x + Math.round(area.width * 0.4)) * dpr),
    y: Math.round((area.y + Math.round(area.height * 0.35)) * dpr),
    width: Math.round(area.width * 0.34 * dpr),
    height: Math.round(area.height * 0.34 * dpr),
  }

  const samples = []
  const panel = glass.createPanel({
    ...pane,
    dpr,
    cornerRadius: Math.round(12 * dpr),
    blurSigma: 6 * dpr,
    displacementScale: 70 * dpr,
    aberrationIntensity: 2,
    saturation: 1.4,
    excludeFromCapture: true,
    anchorWindow: host,
    lumaBands: [{ id: 0, x: 0, y: 0, width: pane.width, height: pane.height }],
    onLuma: (bands) => {
      const b = bands && (bands['0'] ?? Object.values(bands)[0])
      if (b) samples.push(b)
    },
  })
  if (!panel) {
    console.error('FAIL: createPanel returned null')
    app.exit(4)
    return
  }
  console.log('panel created    :', JSON.stringify(pane))
  panel.show(0)
  await wait(6000)

  const stats = glass._native && glass._native._stats ? glass._native._stats() : {}
  const mean = samples.length
    ? {
        r: Math.round(samples.reduce((a, s) => a + s.r, 0) / samples.length),
        g: Math.round(samples.reduce((a, s) => a + s.g, 0) / samples.length),
        b: Math.round(samples.reduce((a, s) => a + s.b, 0) / samples.length),
      }
    : null

  console.log('frames acquired  :', stats.framesAcquired ?? '?')
  console.log('renders          :', stats.renders ?? '?')
  console.log('luma samples     :', samples.length, 'mean', JSON.stringify(mean))

  panel.hide(0)
  panel.destroy()
  host.destroy()
  driver.destroy()
  glass.shutdown()

  const ok = (stats.renders ?? 0) > 0 && samples.length > 0
  console.log(ok ? '\nRESULT: native glass working' : '\nRESULT: no frames captured')
  app.exit(ok ? 0 : 3)
})
