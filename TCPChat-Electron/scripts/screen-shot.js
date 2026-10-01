'use strict'

/**
 * Photograph a screen region through Windows Graphics Capture.
 *
 *   npx electron scripts/screen-shot.js --rect=x,y,w,h --out=file.png
 *
 * Ground truth for "what is actually on screen": WGC captures the composited
 * desktop, so it sees the WebGL canvas as the display server does, unlike
 * `capturePage` or `canvas.toDataURL` which read back through the renderer.
 */

const fs = require('fs')
const path = require('path')
const { app, desktopCapturer, screen } = require('electron')

const arg = (name, fallback) => {
  const hit = process.argv.find((a) => a.startsWith(`--${name}=`))
  return hit ? hit.slice(name.length + 3) : fallback
}

app.whenReady().then(async () => {
  const display = screen.getPrimaryDisplay()
  const dpr = display.scaleFactor
  const rectArg = arg('rect', '')
  const rect = rectArg
    ? (([x, y, w, h]) => ({ x, y, width: w, height: h }))(rectArg.split(',').map(Number))
    : {
        x: Math.round(display.bounds.x * dpr),
        y: Math.round(display.bounds.y * dpr),
        width: Math.round(display.bounds.width * dpr),
        height: Math.round(display.bounds.height * dpr),
      }

  const sources = await desktopCapturer.getSources({
    types: ['screen'],
    thumbnailSize: {
      width: Math.round(display.bounds.width * dpr),
      height: Math.round(display.bounds.height * dpr),
    },
  })
  if (!sources.length) {
    console.error('no screen source')
    app.exit(1)
    return
  }

  const image = sources[0].thumbnail
  const size = image.getSize()
  const x = Math.max(0, Math.round(rect.x))
  const y = Math.max(0, Math.round(rect.y))
  const width = Math.min(size.width - x, Math.round(rect.width))
  const height = Math.min(size.height - y, Math.round(rect.height))

  const out = arg('out', path.join(require('os').tmpdir(), 'screen-shot.png'))
  fs.writeFileSync(out, image.crop({ x, y, width, height }).toPNG())
  console.log(`captured ${width}x${height} at (${x},${y}) -> ${out}`)
  app.exit(0)
})
