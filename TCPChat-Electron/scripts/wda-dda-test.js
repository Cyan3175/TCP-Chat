'use strict'

/**
 * Does SetWindowDisplayAffinity actually remove a window from DXGI Desktop
 * Duplication?
 *
 *   node scripts/run-electron.cjs scripts/wda-dda-test.js
 *
 * This matters because the two exclusion mechanisms are not interchangeable:
 * `dda-only` (SetWindowCaptureAffinity / WCA_EXCLUDED_FROM_DDA) is documented as
 * the one that stops this module's own DXGI input, while `all`
 * (SetWindowDisplayAffinity / WDA_EXCLUDEFROMCAPTURE) is the widely available
 * fallback. On Windows builds where SetWindowCaptureAffinity is not exported,
 * `all` is the only tool left — so the question "does `all` stop DDA?" decides
 * whether the ghosting can be fixed at all on such a machine.
 *
 * Method: paint a host window a colour that cannot occur naturally (pure
 * magenta), point a glass panel at it, and read back what the panel's own DXGI
 * capture pipeline sees under that rect. Magenta means DDA sees the window;
 * anything else means the exclusion worked.
 *
 * Exit 0 when DDA is successfully excluded, 1 when the window is still captured,
 * 2 when the test could not run.
 */

const path = require('path')
const { app, BrowserWindow, screen } = require('electron')

const glass = require(path.join(__dirname, '..', 'vendor', 'electron-liquid-glass'))

const wait = (ms) => new Promise((resolve) => setTimeout(resolve, ms))
const MAGENTA = { r: 255, g: 0, b: 255 }
const TRANSPARENT = process.argv.includes('--transparent')

/** Distance from pure magenta; a captured magenta window lands near zero. */
function magentaDistance(sample) {
  return Math.hypot(sample.r - MAGENTA.r, sample.g - MAGENTA.g, sample.b - MAGENTA.b)
}

app.whenReady().then(async () => {
  if (!glass.isSupported || !glass.isSupported()) {
    console.error('SKIP: native addon unavailable')
    app.exit(2)
    return
  }

  const display = screen.getPrimaryDisplay()
  const dpr = display.scaleFactor
  const area = display.workArea

  // A driver window keeps the desktop presenting frames; DDA delivers nothing on
  // a static desktop. Created first so the panel ends up above it.
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
      <div id="p" style="position:absolute;inset:0;background:#101820"></div>
      <script>let i=0;setInterval(()=>{i++;document.getElementById('p').style.opacity=
        0.85+0.15*Math.abs(Math.sin(i/10))},33)</script></body>`),
  )

  // The probe target: a large, unmistakable magenta window.
  const target = new BrowserWindow({
    x: area.x + Math.round(area.width * 0.3),
    y: area.y + Math.round(area.height * 0.3),
    width: Math.round(area.width * 0.4),
    height: Math.round(area.height * 0.4),
    frame: false,
    show: true,
    /*
     * Transparent, like the real app window. This is the variable under test:
     * an Electron transparent window is composited differently from an opaque
     * one, and display affinity may not behave the same way on it.
     */
    transparent: TRANSPARENT,
    backgroundColor: TRANSPARENT ? '#00000000' : '#ff00ff',
  })
  await target.loadURL(
    'data:text/html;charset=utf-8,' +
      encodeURIComponent(
        '<body style="margin:0;background:' +
          (TRANSPARENT ? 'rgba(255,0,255,0.92)' : '#ff00ff') +
          '"></body>',
      ),
  )
  target.setAlwaysOnTop(true)

  const bounds = target.getBounds()
  const pane = {
    x: Math.round((bounds.x + 8) * dpr),
    y: Math.round((bounds.y + 8) * dpr),
    width: Math.round((bounds.width - 16) * dpr),
    height: Math.round((bounds.height - 16) * dpr),
  }

  let band = null
  const panel = glass.createPanel({
    ...pane,
    dpr,
    blurSigma: 0,
    displacementScale: 0,
    aberrationIntensity: 0,
    saturation: 1,
    excludeFromCapture: true,
    anchorWindow: target,
    lumaBands: [{ id: 0, x: 0, y: 0, width: pane.width, height: pane.height }],
    onLuma: (b) => {
      const s = b && (b['0'] ?? Object.values(b)[0])
      if (s) band = s
    },
  })
  if (!panel) {
    console.error('SKIP: createPanel returned null')
    app.exit(2)
    return
  }
  panel.show(0)

  // Baseline: with no policy applied, DDA must see the magenta window.
  await wait(2500)
  const baseline = band ? { ...band } : null
  console.log('no policy      :', JSON.stringify(baseline))

  // Now the policy under test.
  const applied = glass.setWindowCapturePolicy(target, 'all')
  console.log('setWindowCapturePolicy(target, "all") ->', applied)
  await wait(3500)

  // Average a few pushes so a single stale frame cannot decide the verdict.
  const after = []
  for (let i = 0; i < 6; i++) {
    if (band) after.push({ ...band })
    await wait(400)
  }
  const mean = after.length
    ? {
        r: Math.round(after.reduce((a, s) => a + s.r, 0) / after.length),
        g: Math.round(after.reduce((a, s) => a + s.g, 0) / after.length),
        b: Math.round(after.reduce((a, s) => a + s.b, 0) / after.length),
      }
    : null
  console.log('after WDA "all":', JSON.stringify(mean), `(${after.length} samples)`)

  panel.hide(0)
  panel.destroy()
  target.destroy()
  driver.destroy()
  glass.shutdown()

  if (!baseline || !mean) {
    console.log('\nRESULT: inconclusive — no luma arrived')
    app.exit(2)
    return
  }

  const before = magentaDistance(baseline)
  const now = magentaDistance(mean)
  console.log(`magenta distance: before=${before.toFixed(0)} after=${now.toFixed(0)}`)

  if (before > 120) {
    console.log('\nRESULT: inconclusive — the baseline never saw the window either')
    app.exit(2)
  } else if (now > 120) {
    console.log('\nRESULT: WDA DOES exclude the window from DDA')
    app.exit(0)
  } else {
    console.log('\nRESULT: WDA does NOT exclude the window from DDA')
    app.exit(1)
  }
})
