'use strict'

/**
 * Self-test mode: `electron . --selftest [--out=<dir>]`
 *
 * Seeds a fixed set of messages that exercises every rendering path (plain text,
 * Markdown, the Luogu extensions, LaTeX, attachments, encryption states), then
 * captures PNGs of the window in each appearance so a change can be reviewed
 * without a live server. Mirrors the C# build's "自测模式", which likewise
 * subscribes to diagnostics only when explicitly requested.
 *
 * Always exits the process when finished.
 */

const fs = require('fs')
const path = require('path')
const { app } = require('electron')

const { log } = require('./logger')

const NICKNAME = '自测用户'

/** Message fixtures, oldest first. */
function fixtures() {
  const base = Date.UTC(2026, 1, 14, 2, 0, 0)
  const at = (minutes) => new Date(base + minutes * 60000).toISOString()
  const name = (minutes, suffix) => `msg_${base + minutes * 60000}_${suffix}.json`

  const messages = [
    {
      minutes: 0,
      from: '张三',
      text: '早上好，今天的题单发一下？\n另外 `3 - 2 = 1`、`1.5 倍`、`a_b_c` 应该原样显示。',
    },
    {
      minutes: 1,
      from: NICKNAME,
      text: '好，**洛谷题单** 在这里：\n\n- [x] P1001 A+B Problem\n- [ ] P1002 过河卒\n- [ ] P1003 铺地毯',
    },
    {
      minutes: 2,
      from: '张三',
      text:
        ':::info[提示]\n这是一个默认折叠的提示框，点标题展开。\n\n:::warning{open}\n嵌套的警告框，默认展开。\n:::\n:::',
    },
    {
      minutes: 3,
      from: '李四',
      text:
        '```cpp line-numbers lines=3-4\n#include <iostream>\nusing namespace std;\nint main() {\n    cout << "Hello" << endl;\n    return 0;\n}\n```',
    },
    {
      minutes: 4,
      from: NICKNAME,
      text: '行内公式 $E = mc^2$，块级：\n\n$$\\sum_{i=1}^{n} \\frac{1}{i^2} = \\frac{\\pi^2}{6}$$\n\n还有 `\\frac{a}{b}` 这种在代码里的不该排版。',
    },
    {
      minutes: 5,
      from: '李四',
      text:
        '::cute-table{tuack}\n| 题目 | 难度 | 算法 |\n| :---: | :---: | :---: |\n| P1001 | 入门 | 模拟 |\n| ^ | 普及- | 高精度 |\n| P1002 | 普及 | DP |\n| P1003 | ^ | 差分 |',
    },
    {
      minutes: 6,
      from: '张三',
      text: '> [!NOTE]\n> 这是 GitHub 风格的提示块。\n\n> 普通引用块。\n\n==高亮==、H~2~O、x^2^、~~删除~~、:smile:、脚注[^1]\n\n[^1]: 这是脚注内容。',
    },
    {
      minutes: 7,
      from: NICKNAME,
      text: '图片和文件消息：',
      attach: { name: '题目截图.png', kind: 2, size: 187 * 1024 },
    },
    {
      minutes: 8,
      from: '李四',
      text: '',
      attach: { name: '题解.pdf', kind: 1, size: 2 * 1024 * 1024 + 512 * 1024 },
    },
    {
      minutes: 9,
      from: NICKNAME,
      text: '',
      attach: { name: '语音 0秒.wav', kind: 5, size: 64000, durationMs: 7400 },
    },
    {
      minutes: 10,
      from: '王五',
      text: '这条是加密消息。',
      enc: 'AESGCM1:AAECAwQFBgcICQoLZGVhZGJlZWZkZWFkYmVlZg==',
    },
    {
      minutes: 11,
      from: '王五',
      text: '',
      decryptFailed: true,
      enc: 'AESGCM1:deadbeefdeadbeefdeadbeef',
    },
    {
      minutes: 12,
      from: NICKNAME,
      text: '这是一条**发送失败**的消息示例。',
      status: '发送失败 (403 Forbidden)',
    },
  ]

  return messages.map((m) => {
    const id = `${base + m.minutes * 60000}_${(m.minutes * 2654435761 % 0xffffffff)
      .toString(16)
      .padStart(8, '0')
      .slice(0, 8)}`
    return {
      v: m.enc ? 2 : 1,
      id,
      from: m.from,
      time: at(m.minutes),
      text: m.text ?? '',
      quote: m.quote ?? null,
      attach: m.attach
        ? {
            name: m.attach.name,
            path: `nw集训/学生资料临存/tcp_chat/att_${base}_x_${m.attach.name}`,
            size: m.attach.size,
            kind: m.attach.kind,
            durationMs: m.attach.durationMs ?? 0,
          }
        : null,
      enc: m.enc ?? null,
      decryptFailed: m.decryptFailed === true,
      status: m.status ?? null,
      remoteName: name(m.minutes, id.split('_')[1]),
      isSelf: m.from === NICKNAME,
    }
  })
}

async function shoot(win, file) {
  const image = await win.webContents.capturePage()
  fs.writeFileSync(file, image.toPNG())
  return fs.statSync(file).size
}

/**
 * @param {object} context
 * @param {import('electron').BrowserWindow} context.win
 * @param {string} context.outDir
 * @param {object} context.settings
 * @param {(channel: string, payload: unknown) => void} context.send
 * @param {{ setEnabled: (v: boolean) => void }} context.glassControls
 * @param {() => object|null} context.glassStats
 * @param {object} [context.chatService]
 */
async function runSelfTest({ win, outDir, settings, send, glassControls, glassStats, chatService }) {
  const results = []
  const record = (step, detail) => {
    results.push({ step, detail })
    log.info(`[selftest] ${step}: ${detail}`)
  }

  /*
   * `--fresh` reproduces a first launch: no nickname, no messages. The renderer
   * is expected to show the "set a nickname" hint and open the settings dialog
   * on its own, which is what this captures.
   */
  const fresh = process.argv.includes('--fresh')

  /*
   * `--flicker` measures backdrop stability in the real app: capture the window
   * repeatedly and report the mean absolute pixel difference between consecutive
   * frames. A backdrop that swaps its image without a decode barrier flashes the
   * transparent window through, which shows up as large alternating jumps.
   */
  const flicker = process.argv.includes('--flicker')

  /*
   * `--glass-shot` renders the app over whatever is behind it and writes one
   * full-window capture. Used together with scripts/desktop-pattern.js so the
   * refraction can be compared against a known background.
   */
  const glassShot = process.argv.includes('--glass-shot')

  try {
    fs.mkdirSync(outDir, { recursive: true })

    if (glassShot) {
      const messages = fixtures()
      settings.nickname = NICKNAME
      if (chatService) chatService.nickname = NICKNAME
      send('settings-changed', settings.toRenderer())
      send('connection', { ok: true, message: '已连接', connected: true })
      send('messages-reset', [])
      for (const msg of messages) send('message-added', msg)

      // Let the capture loop deliver a few frames.
      await new Promise((resolve) => setTimeout(resolve, 5000))

      const info = await win.webContents.executeJavaScript(`(() => {
        const canvas = document.getElementById('glass')
        const cs = (el) => {
          if (!el) return null
          const s = getComputedStyle(el)
          return { bg: s.backgroundColor, display: s.display, z: s.zIndex, op: s.opacity }
        }
        const rect = canvas ? canvas.getBoundingClientRect() : null
        return {
          mode: document.body.dataset.glass,
          luma: document.body.dataset.luma ?? null,
          canvas: canvas ? { w: canvas.width, h: canvas.height } : null,
          canvasRect: rect ? { w: Math.round(rect.width), h: Math.round(rect.height) } : null,
          renderFrames: window.__glassRenderFrames ? window.__glassRenderFrames() : -1,
          html: cs(document.documentElement),
          body: cs(document.body),
          app: cs(document.getElementById('app')),
          glass: cs(canvas),
          frame: window.__glassFrameInfo ?? null,
        }
      })()`)
      record('glass info', JSON.stringify(info))

      const file = path.join(outDir, 'glass-over-pattern.png')
      const image = await win.webContents.capturePage()
      fs.writeFileSync(file, image.toPNG())

      // Read the GL output directly, bypassing page compositing, once per stage
      // so a broken image can be attributed to the blur or to the lens.
      const probe = await win.webContents.executeJavaScript(
        'window.__glassProbe ? window.__glassProbe() : null',
      )
      console.log('probe :', JSON.stringify(probe))
      const sentHash = await win.webContents.executeJavaScript('window.__glassFrameInfo ?? null')
      console.log('received:', JSON.stringify(sentHash))
      for (const stage of ['source', 'blur', 'lens']) {
        await win.webContents.executeJavaScript(`window.__glassStage = '${stage}'`)
        await new Promise((resolve) => setTimeout(resolve, 400))
        const dataUrl = await win.webContents.executeJavaScript(
          'window.__glassSnapshot ? window.__glassSnapshot() : null',
        )
        if (typeof dataUrl === 'string' && dataUrl.startsWith('data:image/png')) {
          const glFile = path.join(outDir, `glass-stage-${stage}.png`)
          fs.writeFileSync(
            glFile,
            Buffer.from(dataUrl.slice('data:image/png;base64,'.length), 'base64'),
          )
          console.log(`stage ${stage}:`, glFile)
        } else {
          console.log(`stage ${stage} failed:`, String(dataUrl).slice(0, 120))
        }
      }
      await win.webContents.executeJavaScript("window.__glassStage = 'lens'")
      console.log('\n===== GLASS SHOT =====')
      console.log('state :', JSON.stringify(info))
      console.log('saved :', file)
      app.exit(0)
      return
    }

    if (flicker) {
      const messages = fixtures()
      settings.nickname = NICKNAME
      if (chatService) chatService.nickname = NICKNAME
      send('settings-changed', settings.toRenderer())
      send('connection', { ok: true, message: '已连接', connected: true })
      send('messages-reset', [])
      for (const msg of messages) send('message-added', msg)

      // Give the watchdog time to decide, and the backdrop time to start.
      await new Promise((resolve) => setTimeout(resolve, 6000))

      const mode = await win.webContents.executeJavaScript(
        `document.body.dataset.glass`,
      )
      record('mode', mode)

      const frames = Number((process.argv.find((a) => a.startsWith('--frames=')) || '').slice(9)) || 40
      const interval =
        Number((process.argv.find((a) => a.startsWith('--interval=')) || '').slice(11)) || 35

      const buffers = []
      const layers = []
      for (let i = 0; i < frames; i += 1) {
        buffers.push((await win.webContents.capturePage()).toBitmap())
        if (i % 8 === 0) {
          layers.push(
            await win.webContents.executeJavaScript(`(() => {
              const imgs = [...document.querySelectorAll('#backdrop img')]
              return {
                shown: document.querySelectorAll('#backdrop img.show').length,
                luma: document.body.dataset.luma ?? null,
                glass: document.body.dataset.glass,
                srcs: imgs.map((el) => (el.currentSrc || el.src || '').length),
              }
            })()`),
          )
        }
        await new Promise((resolve) => setTimeout(resolve, interval))
      }

      const diffs = []
      for (let i = 1; i < buffers.length; i += 1) {
        const a = buffers[i - 1]
        const b = buffers[i]
        if (a.length !== b.length) continue
        let sum = 0
        let n = 0
        for (let k = 0; k < a.length; k += 64) {
          sum += Math.abs(a[k] - b[k])
          n += 1
        }
        diffs.push(Number((sum / n).toFixed(2)))
      }
      const mean = diffs.length ? diffs.reduce((x, y) => x + y, 0) / diffs.length : 0
      const max = diffs.length ? Math.max(...diffs) : 0
      const moving = diffs.filter((d) => d > 2).length
      record('sampling', `${frames} frames @ ${interval}ms`)
      record('flicker diffs', JSON.stringify(diffs))
      record('flicker mean/max', `${mean.toFixed(2)} / ${max.toFixed(2)}`)
      record('frames changed', `${moving} of ${diffs.length}`)
      record('layer states', JSON.stringify(layers))

      fs.writeFileSync(
        path.join(outDir, 'selftest.json'),
        JSON.stringify({ mode, diffs, mean, max, moving, results }, null, 2),
        'utf8',
      )
      console.log('\n===== FLICKER PROBE =====')
      console.log('glass mode      :', mode)
      console.log('sampling        :', frames, 'frames @', interval, 'ms')
      console.log('frame diffs     :', JSON.stringify(diffs))
      console.log('mean / max      :', mean.toFixed(2), '/', max.toFixed(2))
      console.log('frames changed  :', moving, 'of', diffs.length)
      console.log('layer states    :', JSON.stringify(layers))
      app.exit(0)
      return
    }

    if (fresh) {
      settings.nickname = ''
      if (chatService) chatService.nickname = ''
      send('settings-changed', settings.toRenderer())
      send('connection', { ok: false, message: '未连接', connected: false })
      send('messages-reset', [])
      await new Promise((resolve) => setTimeout(resolve, 1800))
      const report = await win.webContents.executeJavaScript(`(() => {
        const modal = document.querySelector('.modal')
        return {
          settingsOpen: Boolean(modal),
          heading: modal ? (modal.querySelector('.modal-header span') || {}).textContent : null,
          fields: modal ? modal.querySelectorAll('.field').length : 0,
          emptyText: document.getElementById('empty-text')?.textContent ?? null,
          nicknameValue: document.getElementById('set-nickname')?.value ?? null,
        }
      })()`)
      record('first-run', JSON.stringify(report))
      record('shot first-run', `${await shoot(win, path.join(outDir, '00-first-run.png'))} bytes`)
      fs.writeFileSync(
        path.join(outDir, 'selftest.json'),
        JSON.stringify({ mode: 'fresh', results }, null, 2),
        'utf8',
      )
      console.log('\n===== SELFTEST OK (fresh) =====')
      for (const item of results) console.log(`${item.step}: ${item.detail}`)
      app.exit(report.settingsOpen ? 0 : 1)
      return
    }

    // The renderer decides "self" by comparing against the configured nickname,
    // so the fixture author has to be the current user for that path to render.
    settings.nickname = NICKNAME
    if (chatService) chatService.nickname = NICKNAME

    const messages = fixtures()
    send('settings-changed', settings.toRenderer())
    send('cipher-state', {
      encryptionEnabled: true,
      passwordCount: 1,
      sendPasswordNumber: 1,
      undecryptableCount: 1,
      pollSeconds: settings.pollSeconds,
      chatFolder: settings.chatFolder,
      lastSyncTime: Date.now(),
      messageCount: messages.length,
    })
    send('connection', { ok: true, message: '已连接', connected: true })
    send('status', `自测模式：已载入 ${messages.length} 条示例消息`)
    send('messages-reset', [])
    for (const msg of messages) send('message-added', msg)

    record('seed', `${messages.length} messages dispatched`)

    // Let layout, fonts and KaTeX settle before capturing.
    await new Promise((resolve) => setTimeout(resolve, 2200))

    const domReport = await win.webContents.executeJavaScript(`(() => {
      const list = document.getElementById('msg-list')
      const bg = (el) => (el ? getComputedStyle(el).backgroundColor : null)
      const selfBubble = list.querySelector('.msg.self .bubble')
      const otherBubble = list.querySelector('.msg:not(.self) .bubble')
      // Surfaces are resolved through var() chains; a broken chain silently
      // yields rgba(0,0,0,0), which is exactly the bug these fields catch.
      const transparent = (value) => !value || value === 'rgba(0, 0, 0, 0)' || value === 'transparent'
      return {
        messages: list.querySelectorAll('.msg').length,
        bubbles: list.querySelectorAll('.bubble').length,
        days: list.querySelectorAll('.msg-day').length,
        katex: list.querySelectorAll('.katex').length,
        katexDisplay: list.querySelectorAll('.katex-display').length,
        boxes: list.querySelectorAll('.box').length,
        codeBlocks: list.querySelectorAll('.code-block').length,
        lineNumbers: list.querySelectorAll('.code-block .gutter .ln').length,
        highlighted: list.querySelectorAll('.code-block .ln.hl').length,
        tables: list.querySelectorAll('table').length,
        tuack: list.querySelectorAll('table.tuack').length,
        rowspan: list.querySelectorAll('td[rowspan], th[rowspan]').length,
        alerts: list.querySelectorAll('.alert').length,
        marks: list.querySelectorAll('mark').length,
        taskItems: list.querySelectorAll('.task-list-item').length,
        footnotes: list.querySelectorAll('.footnotes').length,
        voice: list.querySelectorAll('.voice').length,
        fileCards: list.querySelectorAll('.file-card').length,
        placeholders: list.querySelectorAll('.bubble.is-placeholder').length,
        failed: list.querySelectorAll('.bubble.is-failed').length,
        selfBubbles: list.querySelectorAll('.msg.self').length,
        glassAttr: document.body.dataset.glass,
        dark: document.body.hasAttribute('data-ds-dark-theme'),
        canvasBg: bg(list),
     /*
      * The window surface. In glass mode the #app element must be fully
      * transparent: the native panel behind it is the background. An opaque
      * value here means the glass is hidden behind a flat sheet, which is what
      * a white window in glass mode turns out to be.
      *
      * No backticks in this comment: the whole probe is a template literal, and
      * a stray backtick would close it early.
      */
     appBg: bg(document.getElementById('app')),
     plainBg: document.body.dataset.plainBg || '',
     // naturalWidth stays 0 when a backdrop never loads, which is exactly
     // how the first attempt at this feature failed.
     plainBgProbe: (() => {
       const l = document.getElementById('plain-bg');
       const i = document.getElementById('plain-bg-img');
       if (!l || !i) return 'missing';
       const ls = getComputedStyle(l); const r = i.getBoundingClientRect();
       return [ls.display, ls.zIndex, ls.position, l.hidden ? 'hidden' : 'shown',
               Math.round(r.width) + 'x' + Math.round(r.height),
               i.complete ? 'complete' : 'loading',
               (i.getAttribute('src') || '').slice(0, 24)].join(' | ');
     })(),
        selfBubbleBg: bg(selfBubble),
        otherBubbleBg: bg(otherBubble),
        selfTextColor: selfBubble ? getComputedStyle(selfBubble).color : null,
        /*
         * Adaptive contrast: the tint set follows the *backdrop* brightness, not
         * the UI theme, so the text colour has to move with it. Read both states
         * deterministically rather than relying on whatever the live luma is.
         */
        lumaContrast: (() => {
          const bubble = list.querySelector('.msg:not(.self) .bubble')
          if (!bubble) return null
          const read = (v) => {
            if (v) document.body.dataset.luma = v
            else delete document.body.dataset.luma
            const cs = getComputedStyle(bubble)
            return { color: cs.color, bg: cs.backgroundColor }
          }
          const out = { dark: read('dark'), bright: read('bright'), neutral: read('') }
          delete document.body.dataset.luma
          return out
        })(),
        /* Software-backdrop state: whether a frame landed, and how it was laid out. */
        backdrop: (() => {
          const layers = [...document.querySelectorAll('#backdrop .layer')]
          return {
            luma: document.body.dataset.luma ?? null,
            shown: layers.filter((l) => l.classList.contains('show')).length,
            count: layers.length,
            bg: layers.map((l) => (l.style.backgroundImage || '').slice(0, 24)),
            size: layers.map((l) => l.style.backgroundSize || ''),
            pos: layers.map((l) => l.style.backgroundPosition || ''),
          }
        })(),
        businessPrimary: getComputedStyle(document.body).getPropertyValue('--dsw-alias-state-business-primary').trim(),
        token: getComputedStyle(document.body).getPropertyValue('--dsw-alias-label-primary').trim(),
        contentFont: getComputedStyle(document.documentElement).getPropertyValue('--dsh-content-font-size').trim(),
        /*
         * The bubbles stay opaque; the list is deliberately transparent when a
         * backdrop is on, which is the whole point of having one. Stated as the
         * two cases rather than one, so this does not read as a failure every
         * time the feature it is checking is in use.
         */
        surfacesOk:
          !transparent(bg(selfBubble)) &&
          !transparent(bg(otherBubble)) &&
          (document.body.dataset.plainBg === 'on'
            ? transparent(bg(list))
            : !transparent(bg(list))),
      }
    })()`)
    record('dom', JSON.stringify(domReport))

    const shots = [
      { name: '01-glass-light', dark: false, glass: true },
      { name: '02-glass-dark', dark: true, glass: true },
      { name: '03-plain-light', dark: false, glass: false },
      { name: '04-plain-dark', dark: true, glass: false },
    ]

    for (const shot of shots) {
      glassControls.setEnabled(shot.glass)
      await win.webContents.executeJavaScript(
        `document.body.toggleAttribute('data-ds-dark-theme', ${shot.dark});` +
          `document.body.dataset.luma=''; void 0;`,
      )
      await new Promise((resolve) => setTimeout(resolve, 700))
      const file = path.join(outDir, `${shot.name}.png`)
      const size = await shoot(win, file)
      record(`shot ${shot.name}`, `${file} (${(size / 1024).toFixed(0)} kB)`)
    }

    // The window only shows part of a long list, so walk the scroll offset to
    // cover every rendering path (containers, code, maths, tables, alerts).
    await win.webContents.executeJavaScript(`document.body.removeAttribute('data-ds-dark-theme'); void 0`)
    for (const offset of [0, 520, 1040, 1560]) {
      await win.webContents.executeJavaScript(`(() => {
        const list = document.getElementById('msg-list')
        list.scrollTop = ${offset}
        return list.scrollTop
      })()`)
      await new Promise((resolve) => setTimeout(resolve, 420))
      const label = `07-plain-scroll-${String(offset).padStart(4, '0')}`
      record(`shot ${label}`, `${await shoot(win, path.join(outDir, `${label}.png`))} bytes`)
    }

    // Search and settings dialog round-trip, so the interactive paths are covered.
    await win.webContents.executeJavaScript(`(() => {
      document.getElementById('searchbar').classList.remove('hidden')
      const input = document.getElementById('search-input')
      input.value = '洛谷'
      input.dispatchEvent(new Event('input', { bubbles: true }))
      return true
    })()`)
    await new Promise((resolve) => setTimeout(resolve, 700))
    const searchReport = await win.webContents.executeJavaScript(`(() => {
      const list = document.getElementById('msg-list')
      return {
        hits: list.querySelectorAll('.msg.hit').length,
        current: list.querySelectorAll('.msg.current').length,
        marks: list.querySelectorAll('mark.find').length,
        count: document.getElementById('search-count').textContent,
      }
    })()`)
    record('search', JSON.stringify(searchReport))
    record('shot 05-search', `${await shoot(win, path.join(outDir, '05-search.png'))} bytes`)

    await win.webContents.executeJavaScript(`(() => {
      document.getElementById('search-close').click()
      document.getElementById('btn-settings').click()
      return true
    })()`)
    await new Promise((resolve) => setTimeout(resolve, 900))
    const settingsReport = await win.webContents.executeJavaScript(`(() => {
      const modal = document.querySelector('.modal')
      return {
        open: Boolean(modal),
        groups: modal ? modal.querySelectorAll('.settings-group').length : 0,
        fields: modal ? modal.querySelectorAll('.field').length : 0,
        buttons: modal ? modal.querySelectorAll('button').length : 0,
      }
    })()`)
    // The About line sits at the bottom of a scrolling dialog and never fits in a
    // screenshot, so read the value the renderer actually holds instead.
    settingsReport.version = await win.webContents.executeJavaScript('window.tcpchat.versions.app')
  // The background card: preview, recent strip and fit mode.
  settingsReport.background = await win.webContents.executeJavaScript(
    "(() => { const p = document.getElementById('set-bg-preview'); const r = document.getElementById('set-bg-recent'); const f = document.getElementById('set-bg-fit'); return [p ? (p.hidden ? 'preview-hidden' : 'preview-shown') : 'no-preview', r ? r.querySelectorAll('img').length + ' recents' : 'no-strip', f ? f.value + '/' + f.options.length : 'no-fit'].join(' | ') })()",
  )
    record('settings', JSON.stringify(settingsReport))
    record('shot 06-settings', `${await shoot(win, path.join(outDir, '06-settings.png'))} bytes`)

    /*
     * The background card on its own.
     *
     * It sits far enough down the settings dialog that the full-height shot
     * never reaches it, and it is the one part of that dialog whose whole value
     * is visual — a thumbnail strip that renders empty tells you nothing from a
     * count of its children.
     */
    const bgRect = await win.webContents.executeJavaScript(
      "(() => { const f = document.getElementById('set-bg-preview'); if (!f) return null; const card = f.closest('.field'); if (!card) return null; card.scrollIntoView({ block: 'center' }); const r = card.getBoundingClientRect(); return { x: Math.round(r.x), y: Math.round(r.y), width: Math.round(r.width), height: Math.round(r.height), vh: window.innerHeight } })()",
    )
    if (bgRect && bgRect.width > 0 && bgRect.height > 0) {
      const image = await win.webContents.capturePage({
        x: Math.max(0, bgRect.x - 8),
        y: Math.max(0, bgRect.y - 8),
        width: bgRect.width + 16,
        height: bgRect.height + 16,
      })
      fs.writeFileSync(path.join(outDir, '08-background-card.png'), image.toPNG())
      const size = image.getSize()
      record('shot 08-background-card', `${size.width}x${size.height}`)
    } else {
      record('shot 08-background-card', 'card not found')
    }

    fs.writeFileSync(
      path.join(outDir, 'selftest.json'),
      JSON.stringify({ nickname: NICKNAME, results }, null, 2),
      'utf8',
    )

    console.log('\n===== SELFTEST OK =====')
    for (const item of results) console.log(`${item.step}: ${item.detail}`)
    app.exit(0)
  } catch (err) {
    log.error('[selftest] failed', err)
    console.error('\n===== SELFTEST FAILED =====')
    console.error(err?.stack || String(err))
    app.exit(1)
  }
}

module.exports = { runSelfTest, fixtures, NICKNAME }
