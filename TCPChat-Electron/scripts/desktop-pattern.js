'use strict'

/**
 * A stand-in desktop for checking the glass.
 *
 *   npx electron scripts/desktop-pattern.js [--hold=30000]
 *
 * Draws the same kind of content as the reference screenshot — a soft gradient,
 * large type, saturated blocks, a card — so the rim refraction can be compared
 * against a known image instead of whatever happens to be on screen. Run this
 * first, then launch the app over it.
 */

const { app, BrowserWindow, screen } = require('electron')

const hold = Number((process.argv.find((a) => a.startsWith('--hold=')) || '').slice(7)) || 30000

const HTML = `<!doctype html>
<html><head><meta charset="utf-8"><style>
  html, body { margin: 0; height: 100%; overflow: hidden; }
  body {
    font: 600 15px/1.5 "Segoe UI", system-ui, sans-serif;
    background:
      radial-gradient(120% 90% at 8% 4%, #f7d9e6 0%, transparent 55%),
      radial-gradient(110% 90% at 96% 10%, #ffe6c2 0%, transparent 50%),
      radial-gradient(120% 120% at 50% 110%, #cfe3f7 0%, transparent 60%),
      linear-gradient(120deg, #f3e7f5 0%, #eef2fb 45%, #fdf1e6 100%);
    color: #2a2f36;
  }
  .row { position: absolute; top: 9%; right: 4%; display: flex; gap: 22px; }
  .tile { width: 108px; height: 108px; border-radius: 22px;
          box-shadow: 0 10px 26px rgb(0 0 0 / 14%); }
  .t1 { background: #e8483c; } .t2 { background: #24c06a; }
  .t3 { background: #2aa3ef; } .t4 { background: #f5c518; }
  .t5 { background: #8e44d0; }
  .card { position: absolute; left: 5%; top: 17%; width: 430px; padding: 26px 28px;
          border-radius: 26px; background: #fff;
          box-shadow: 0 18px 44px rgb(0 0 0 / 12%); }
  .card h2 { margin: 0 0 14px; font-size: 25px; }
  .card ul { margin: 0; padding: 0; list-style: none; }
  .card li { padding: 9px 0 9px 26px; position: relative; font-weight: 500; }
  .card li::before { content: ''; position: absolute; left: 0; top: 16px;
                     width: 11px; height: 11px; border-radius: 50%; }
  .c1::before { background: #6c5ce7; } .c2::before { background: #00b894; }
  .c3::before { background: #e84393; } .c4::before { background: #e17055; }
  .big { position: absolute; left: 6%; bottom: 9%; font-size: 88px; font-weight: 800;
         letter-spacing: -2px; color: #232a33; }
  .caption { position: absolute; left: 6%; bottom: 4%; font-size: 22px; font-weight: 600;
             color: #4a5560; }
</style></head><body>
  <div class="row">
    <div class="tile t1"></div><div class="tile t2"></div>
    <div class="tile t3"></div><div class="tile t4"></div><div class="tile t5"></div>
  </div>
  <div class="card">
    <h2>今日待办</h2>
    <ul>
      <li class="c1">整理会议纪要</li>
      <li class="c2">回复邮件</li>
      <li class="c3">预订周三出差酒店</li>
      <li class="c4">更新项目里程碑表</li>
    </ul>
  </div>
  <div class="big">LIQUID GLASS 0123</div>
  <div class="caption">实时折射桌面内容 · 边缘位移 + 色散</div>
</body></html>`

app.whenReady().then(() => {
  const display = screen.getPrimaryDisplay()
  const win = new BrowserWindow({
    x: display.bounds.x,
    y: display.bounds.y,
    width: display.bounds.width,
    height: display.bounds.height,
    frame: false,
    show: true,
    focusable: true,
  })
  win.loadURL(`data:text/html;charset=utf-8,${encodeURIComponent(HTML)}`)
  console.log('pattern window covering', JSON.stringify(display.bounds))
  setTimeout(() => app.exit(0), hold)
})
