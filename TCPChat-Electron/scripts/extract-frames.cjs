'use strict'

/*
 * Extract frames from a video using Electron's own decoder.
 *
 * There is no ffmpeg on this machine, but Chromium ships an H.264 decoder and
 * Electron is already a dependency, so a hidden window with a <video> is enough:
 * seek to a time, draw the frame to a canvas, read it back as PNG.
 *
 * Usage: electron scripts/extract-frames.cjs <video> <outDir> [count]
 */

const fs = require('fs')
const path = require('path')
const { app, BrowserWindow } = require('electron')

const [videoArg, outDirArg, countArg] = process.argv.slice(2)
const count = Number(countArg) || 12

if (!videoArg || !outDirArg) {
  console.error('usage: electron extract-frames.cjs <video> <outDir> [count]')
  app.exit(2)
}

const videoUrl = `file:///${path.resolve(videoArg).replace(/\\/g, '/').replace(/ /g, '%20')}`

app.disableHardwareAcceleration()

app.whenReady().then(async () => {
  const win = new BrowserWindow({
    show: false,
    width: 640,
    height: 400,
    // A data: page cannot load a file:// subresource, so the page is a real
    // file and security is off. This only ever reads a local video.
    webPreferences: { webSecurity: false, backgroundThrottling: false },
  })

  const html = `<!doctype html><html><body style="margin:0;background:#000">
    <video id="v" src="${videoUrl}" preload="auto" muted></video>
    </body></html>`

  const page = path.join(require('os').tmpdir(), 'extract-frames.html')
  fs.writeFileSync(page, html)
  await win.loadFile(page)

  const meta = await win.webContents.executeJavaScript(`
    new Promise((resolve, reject) => {
      const v = document.getElementById('v')
      const done = () => resolve({ w: v.videoWidth, h: v.videoHeight, d: v.duration })
      if (v.readyState >= 1) done()
      else { v.onloadedmetadata = done; v.onerror = () => reject(new Error('load failed')) }
      setTimeout(() => reject(new Error('metadata timeout')), 15000)
    })
  `)
  console.log(`video ${meta.w}x${meta.h} duration=${meta.d.toFixed(2)}s`)

  fs.mkdirSync(outDirArg, { recursive: true })

  for (let i = 0; i < count; i++) {
    const t = (meta.d * i) / count
    const b64 = await win.webContents.executeJavaScript(`
      new Promise((resolve, reject) => {
        const v = document.getElementById('v')
        const grab = () => {
          const c = document.createElement('canvas')
          c.width = v.videoWidth; c.height = v.videoHeight
          c.getContext('2d').drawImage(v, 0, 0)
          resolve(c.toDataURL('image/png').split(',')[1])
        }
        v.onseeked = grab
        v.currentTime = ${t}
        setTimeout(() => reject(new Error('seek timeout')), 8000)
      })
    `)
    const file = path.join(outDirArg, `frame-${String(i).padStart(2, '0')}-t${t.toFixed(2)}.png`)
    fs.writeFileSync(file, Buffer.from(b64, 'base64'))
    console.log(`  ${path.basename(file)} (${Math.round(fs.statSync(file).size / 1024)} kB)`)
  }

  app.exit(0)
})
