/**
 * Settings dialog.
 *
 * Field set and semantics mirror the C# build's settings, including the
 * ordered password list where a blank entry means "send in the clear" while the
 * remaining passwords still take part in decryption.
 */

import { $, h, clear, show, clamp } from './dom.js'
import { showToast, confirmDialog } from './overlays.js'

let current = null
let pickers = {}
let onChange = async () => {}

export function configureSettings(handlers) {
  onChange = handlers.onChange ?? onChange
}

/** Common CJK-capable families offered when the font list is unavailable. */
const FALLBACK_FONTS = [
  'Microsoft YaHei',
  'Microsoft YaHei UI',
  'SimSun',
  'SimHei',
  'KaiTi',
  'FangSong',
  'DengXian',
  'Segoe UI',
  'Consolas',
  'JetBrains Mono',
  'Cascadia Mono',
  'Source Han Sans SC',
  'Noto Sans SC',
]

/** Ask Chromium for the installed font families; [] when unavailable. */
async function queryFontFamilies() {
  try {
    if (typeof window.queryLocalFonts !== 'function') return []
    const fonts = await window.queryLocalFonts()
    return [...new Set(fonts.map((f) => f.family))].sort((a, b) => a.localeCompare(b))
  } catch {
    return []
  }
}

function field(label, control, hint) {
  return h('label', { class: 'field' }, [
    h('div', { class: 'field-label', text: label }),
    control,
    hint ? h('div', { class: 'caption', text: hint }) : null,
  ])
}

function textInput(id, value, extra = {}) {
  return h('input', {
    class: 'input',
    id,
    type: 'text',
    value: value ?? '',
    autocomplete: 'off',
    spellcheck: 'false',
    ...extra,
  })
}

function numberInput(id, value, min, max, step = 1) {
  return h('input', {
    class: 'input',
    id,
    type: 'number',
    value: String(value ?? ''),
    min: String(min),
    max: String(max),
    step: String(step),
  })
}

function switchRow(id, checked, label) {
  return h('label', { class: 'switch' }, [
    h('input', { type: 'checkbox', id, checked: checked === true }),
    h('span', { class: 'switch-track' }),
    h('span', { text: label }),
  ])
}

function buildDialog(state) {
  const body = h('div', { class: 'modal-body' })

  // ---- connection ----
  body.append(
    h('div', { class: 'settings-group' }, [
      h('div', { class: 'settings-group-title', text: '连接' }),
      /*
       * The address, the password and the chat folder are not shown.
       *
       * There is one service, one password and one folder now — the WebDAV host
       * they used to be a choice against is gone — so three editable boxes only
       * offered ways to break a working install. They keep their values; the
       * folder and address are shown read-only by the status bar, which is where
       * you would look to confirm what you are connected to anyway.
       */
      field('昵称', textInput('set-nickname', state.nickname), '显示在消息上，仅作为身份标识'),
      h('div', { class: 'settings-row' }, [
        h('button', { class: 'btn', id: 'set-test', type: 'button', text: '测试连接' }),
        h('span', { class: 'caption', id: 'set-test-result', text: '' }),
      ]),
    ]),
  )

  // ---- sync ----
  body.append(
    h('div', { class: 'settings-group' }, [
      h('div', { class: 'settings-group-title', text: '同步' }),
      field(
        '同步周期（秒）',
        numberInput('set-poll', state.pollSeconds, 1, 120),
        '1–120 秒，改完立即生效。填 1 就是实时——服务没有推送通道，轮询到秒级已经是它能让的最快速度。',
      ),
      field(
        '历史天数',
        numberInput('set-history', state.historyDays, 1, 365),
        '1–365 天，只加载最近这么多天的消息',
      ),
      switchRow('set-autoscroll', state.autoScroll, '新消息自动滚动到底部'),
      switchRow(
        'set-notify',
        state.notifyOnMessage !== false,
        '新消息弹通知（只在窗口不在前面时）',
      ),
      switchRow(
        'set-launch',
        state.launchAtLogin === true,
        '开机时自动启动（直接进托盘，不弹窗口）',
      ),
    ]),
  )

  // ---- encryption ----
  const passwordList = h('textarea', {
    class: 'textarea',
    id: 'set-passwords',
    rows: '4',
    spellcheck: 'false',
    placeholder: '每行一个密码；留空行 = 明文发送',
  })
  passwordList.value = (state.cryptoPasswords ?? []).join('\n')

  const sendSelect = h('select', { class: 'select', id: 'set-send-index' })

  const refreshSendSelect = () => {
    const lines = passwordList.value.split('\n')
    const previous = sendSelect.value
    clear(sendSelect)
    if (lines.length === 0) {
      sendSelect.append(h('option', { value: '0', text: '（不加密）' }))
      sendSelect.disabled = true
      return
    }
    sendSelect.disabled = false
    lines.forEach((line, index) => {
      const label = line.trim() === '' ? '（明文发送）' : `第 ${index + 1} 把 · ${'•'.repeat(Math.min(8, line.trim().length))}`
      sendSelect.append(h('option', { value: String(index), text: label }))
    })
    sendSelect.value = previous && Number(previous) < lines.length ? previous : '0'
  }
  passwordList.addEventListener('input', refreshSendSelect)

  body.append(
    h('div', { class: 'settings-group' }, [
      h('div', { class: 'settings-group-title', text: '端到端加密' }),
      field('密码列表', passwordList, '收发双方要填完全一样的密码；留空行表示用明文发送，其余密码仍用于解开旧消息'),
      field('默认用哪一个发送', sendSelect, '加密只用选中的这一把，其余只用来解开老消息'),
    ]),
  )

  // ---- appearance ----
  const fontInput = h('input', {
    class: 'input',
    id: 'set-font',
    type: 'text',
    list: 'font-list',
    value: state.fontFamily ?? '',
    placeholder: '留空 = 系统默认字体',
    autocomplete: 'off',
  })
  const fontList = h('datalist', { id: 'font-list' })
  for (const family of FALLBACK_FONTS) fontList.append(h('option', { value: family }))

  const fontPreview = h('div', {
    class: 'font-preview',
    id: 'set-font-preview',
    text: '中文示例 Aa Bb 0123 — The quick brown fox',
  })
  const applyPreview = () => {
    fontPreview.style.fontFamily = fontInput.value.trim() || 'var(--dsw-font-family)'
  }
  fontInput.addEventListener('input', applyPreview)

  const themeSelect = h('select', { class: 'select', id: 'set-theme' }, [
    h('option', { value: '0', text: '跟随系统' }),
    h('option', { value: '1', text: '浅色' }),
    h('option', { value: '2', text: '深色' }),
  ])
  themeSelect.value = String(state.theme ?? 0)

  /*
   * Orthogonal to the theme, not another entry in it.
   *
   * The theme decides the palette; this decides shape and how code is coloured.
   * Folding them into one list would give options that each change two unrelated
   * things, and you would pick "dark DeepSeek" instead of "dark" plus "DeepSeek".
   */
  const uiSelect = h('select', { class: 'select', id: 'set-ui' }, [
    h('option', { value: 'default', text: '默认' }),
    h('option', { value: 'deepseek', text: 'DeepSeek' }),
    h('option', { value: 'edge', text: 'Edge（Fluent 2）' }),
    h('option', { value: 'glass', text: '液态玻璃' }),
  ])
  /*
   * One control for "what does it look like", rather than a style list plus a
   * separate switch.
   *
   * Glass is not a fourth style the way the other three are — it replaces the
   * surface with the native panel, and the styles underneath stop being visible.
   * Presenting it as a peer of them is honest about that: you are choosing one
   * appearance, and glass is one of the choices. The stored settings stay as they
   * were, `uiStyle` plus `glassEnabled`, and this reads and writes both.
   */
  uiSelect.value = state.glassEnabled ? 'glass' : ['deepseek', 'edge'].includes(state.uiStyle) ? state.uiStyle : 'default'

  const zoomRange = h('input', {
    class: 'slider',
    id: 'set-zoom',
    type: 'range',
    min: '60',
    max: '240',
    step: '10',
    value: String(Math.round((state.zoom ?? 1) * 100)),
  })
  const zoomLabel = h('span', { class: 'caption', id: 'set-zoom-label', text: `${Math.round((state.zoom ?? 1) * 100)}%` })
  zoomRange.addEventListener('input', () => {
    zoomLabel.textContent = `${zoomRange.value}%`
  })

  body.append(
    h('div', { class: 'settings-group' }, [
      h('div', { class: 'settings-group-title', text: '外观' }),
      field('界面字体', h('div', {}, [fontInput, fontList]), '下拉选择本机字体，也可以直接输入字体名；留空使用系统默认'),
      fontPreview,
      field('主题', themeSelect),
      field('界面风格', uiSelect, '默认是产品外观；DeepSeek 用胶囊按钮和 Atom One 代码配色；Edge 用 Fluent 2 的中性灰、更紧的圆角和微软蓝'),
      h('div', { class: 'field' }, [
        h('div', { class: 'field-label', text: '缩放' }),
        h('div', { class: 'settings-row' }, [zoomRange, zoomLabel]),
        h('div', { class: 'caption', text: '也可以随时用 Ctrl + / Ctrl - / Ctrl 0 / Ctrl + 滚轮' }),
      ]),
    ]),
  )

  // ---- liquid glass ----
  const qualityRange = h('input', {
    class: 'slider',
    id: 'set-glass-quality',
    type: 'range',
    min: '0',
    max: '100',
    step: '1',
    value: String(state.glassQuality ?? 60),
  })
  const qualityLabel = h('span', {
    class: 'caption',
    id: 'set-glass-quality-label',
    text: String(state.glassQuality ?? 60),
  })
  qualityRange.addEventListener('input', () => {
    qualityLabel.textContent = qualityRange.value
  })

  const blurRange = h('input', {
    class: 'slider',
    id: 'set-glass-blur',
    type: 'range',
    min: '0',
    max: '12',
    step: '0.5',
    value: String(state.glassBlurSigma ?? 0),
  })
  const blurLabel = h('span', {
    class: 'caption',
    id: 'set-glass-blur-label',
    text: String(state.glassBlurSigma ?? 0),
  })
  blurRange.addEventListener('input', () => {
    blurLabel.textContent = blurRange.value
  })

  const glassStatus = h('span', { class: 'caption', id: 'set-glass-status', text: '' })

  body.append(
    h('div', { class: 'settings-group' }, [
      h('div', { class: 'settings-group-title', text: '液态玻璃' }),
      /*
       * No switch here: turning glass on and off is now the 液态玻璃 entry in
       * 界面风格, and two controls for one setting is a way to leave them
       * disagreeing. What is left below only matters once it is on.
       */
      h('div', {
        class: 'caption',
        text: '开关在「外观 → 界面风格」里选「液态玻璃」。下面几项只在开启后起作用。',
      }),
      h('div', { class: 'field' }, [
        h('div', { class: 'field-label', text: '背景模糊' }),
        h('div', { class: 'settings-row' }, [blurRange, blurLabel]),
        h('div', {
          class: 'caption',
          text: '不是装饰：模糊是让背后的窗口不变成"第二层界面"的机制。拖到 0 背景完全清晰，背后窗口里的文字会以全对比度透出来，看起来就是重影。',
        }),
      ]),
      h('div', { class: 'field' }, [
        h('div', { class: 'field-label', text: '玻璃质量' }),
        h('div', { class: 'settings-row' }, [qualityRange, qualityLabel]),
        h('div', { class: 'caption', text: '质量越高，模糊半径、边缘位移与色散越强' }),
      ]),
      h('div', { class: 'field' }, [
        h('div', { class: 'field-label', text: '普通主题背景' }),
        /*
         * Laid out like the desktop's own background page: a preview of what is
         * set, the pictures used recently, a way to reach a new one, and the fit
         * mode. That is the shape anyone who has changed a wallpaper already
         * knows, so none of it needs explaining.
         */
        h('div', { class: 'bg-preview-frame' }, [
          h('img', { class: 'bg-preview', id: 'set-bg-preview', alt: '' }),
          h('div', { class: 'bg-preview-empty', id: 'set-bg-empty', text: '未选择背景' }),
        ]),
        h('div', { class: 'bg-recent-head', text: '最近使用的图像' }),
        h('div', { class: 'bg-recent', id: 'set-bg-recent' }),
        h('div', { class: 'settings-row' }, [
          h('button', { class: 'btn', id: 'set-bg-pick', type: 'button', text: '选择图片…' }),
          h('button', { class: 'btn', id: 'set-bg-clear', type: 'button', text: '清除' }),
        ]),
        h('div', { class: 'field' }, [
          h('div', { class: 'field-label', text: '适应模式' }),
          h(
            'select',
            { class: 'input bg-fit', id: 'set-bg-fit' },
            [
              ['cover', '填充'],
              ['contain', '适应'],
              ['fill', '拉伸'],
              ['none', '居中'],
              ['repeat', '平铺'],
            ].map(([value, label]) => h('option', { value, text: label })),
          ),
        ]),
        h('div', {
          class: 'caption',
          text: '只在关闭液态玻璃时显示。图片原样显示，不加任何叠加；消息气泡自己有底色，所以文字仍然看得清。',
        }),
      ]),
      h('div', { class: 'settings-row' }, [
        h('button', { class: 'btn', id: 'set-glass-retry', type: 'button', text: '重建玻璃面板' }),
        h('span', {
          class: 'caption',
          text: '分辨率或显示器变化后可以点这个重新贴合，不用重启',
        }),
      ]),
      h('div', { class: 'settings-row' }, [glassStatus]),
    ]),
  )

  // ---- about ----
  const versions = window.tcpchat.versions
  body.append(
    h('div', { class: 'settings-group' }, [
      h('div', { class: 'settings-group-title', text: '关于' }),
      h('div', { class: 'caption' }, [
        `TCP Chat ${versions.app} · Electron ${versions.electron} · Chromium ${versions.chrome}`,
      ]),
      h('div', { class: 'settings-row' }, [
        h('button', { class: 'btn', id: 'set-open-data', type: 'button', text: '打开数据目录' }),
        h('button', { class: 'btn', id: 'set-open-log', type: 'button', text: '查看日志' }),
      ]),
      h('div', { class: 'caption', text: '设置与日志都在 %LOCALAPPDATA%\\TCPChat\\，与 C# 版共用同一份配置文件' }),
    ]),
  )

  pickers = {
    /*
     * No nwUrl, nwPassword or folder picker: those three have no input any more.
     * They are left out of the patch entirely rather than sent back unchanged, so
     * `settings:update` keeps the values it already holds.
     */
    nickname: () => document.getElementById('set-nickname').value,
    poll: () => clamp(Number(document.getElementById('set-poll').value) || 3, 1, 120),
    history: () => clamp(Number(document.getElementById('set-history').value) || 7, 1, 365),
    autoScroll: () => document.getElementById('set-autoscroll').checked,
    launchAtLogin: () => document.getElementById('set-launch').checked,
    notifyOnMessage: () => document.getElementById('set-notify').checked,
    passwords: () => passwordList.value.split('\n').map((line) => line.trim()),
    sendIndex: () => Number(sendSelect.value) || 0,
    font: () => fontInput.value.trim(),
    theme: () => Number(themeSelect.value) || 0,
    uiStyle: () => (['deepseek', 'edge'].includes(uiSelect.value) ? uiSelect.value : 'default'),
    zoom: () => clamp(Number(zoomRange.value) / 100, 0.6, 2.4),
    /*
     * Glass comes from the same picker, so the two cannot disagree.
     *
     * Choosing it leaves uiStyle alone rather than resetting it: switching glass
     * off again should land back on the style that was underneath, not on 默认.
     */
    glass: () => uiSelect.value === 'glass',
    glassQuality: () => clamp(Number(qualityRange.value) || 0, 0, 100),
    glassBlurSigma: () => clamp(Number(blurRange.value) || 0, 0, 12),
  }

  refreshSendSelect()
  sendSelect.value = String(clamp(state.sendPasswordIndex ?? 0, 0, Math.max(0, (state.cryptoPasswords ?? []).length - 1)))
  applyPreview()

  const dialog = h('div', { class: 'modal' }, [
    h('div', { class: 'modal-header' }, [
      h('span', { text: '设置' }),
      h('span', { style: { flex: '1' } }),
      h('button', { class: 'btn btn-ghost btn-icon', type: 'button', text: '✕', id: 'set-close' }),
    ]),
    body,
    h('div', { class: 'modal-footer' }, [
      h('button', { class: 'btn', type: 'button', text: '取消', id: 'set-cancel' }),
      h('button', { class: 'btn btn-primary', type: 'button', text: '保存', id: 'set-save' }),
    ]),
  ])


  return dialog
}

function collect() {
  const passwords = pickers.passwords().map((p) => p.trim())
  // A trailing blank line is an editing artefact, not an intended "clear" entry.
  while (passwords.length > 1 && passwords[passwords.length - 1] === '') passwords.pop()

  const patch = {
    nickname: pickers.nickname(),
    pollSeconds: pickers.poll(),
    historyDays: pickers.history(),
    autoScroll: pickers.autoScroll(),
    launchAtLogin: pickers.launchAtLogin(),
    notifyOnMessage: pickers.notifyOnMessage(),
    cryptoPasswords: passwords,
    sendPasswordIndex: pickers.sendIndex(),
    fontFamily: pickers.font(),
    theme: pickers.theme(),
    uiStyle: pickers.uiStyle(),
    zoom: pickers.zoom(),
    glassEnabled: pickers.glass(),
    glassQuality: pickers.glassQuality(),
    glassBlurSigma: pickers.glassBlurSigma(),
  }

  return patch
}

export async function openSettings(state) {
  current = state
  const dialog = buildDialog(state)
  const mask = h('div', { class: 'modal-mask' }, [dialog])
  document.body.append(mask)

  // Populate the full system font list asynchronously so opening the dialog
  // never waits on it.
  void queryFontFamilies().then((families) => {
    if (!families.length) return
    const datalist = dialog.querySelector('#font-list')
    if (!datalist) return
    for (const family of families) datalist.append(h('option', { value: family }))
  })

  const close = () => {
    mask.remove()
    window.removeEventListener('keydown', onKey, true)
  }
  const onKey = (event) => {
    if (event.key === 'Escape') {
      event.stopPropagation()
      close()
    }
  }
  window.addEventListener('keydown', onKey, true)

  dialog.querySelector('#set-close').addEventListener('click', close)
  dialog.querySelector('#set-cancel').addEventListener('click', close)
  mask.addEventListener('pointerdown', (event) => {
    if (event.target === mask) close()
  })

  dialog.querySelector('#set-open-data').addEventListener('click', () => window.tcpchat.app.openDataDir())
  dialog.querySelector('#set-open-log').addEventListener('click', () => window.tcpchat.app.openLog())

  const testResult = dialog.querySelector('#set-test-result')
  dialog.querySelector('#set-test').addEventListener('click', async () => {
    testResult.textContent = '正在测试…'
    testResult.style.color = ''
    // Nothing to pass: the address, password and folder are no longer editable,
    // so the main process tests what it already has.
    const result = await window.tcpchat.settings.test()
    testResult.textContent = result.message
    testResult.style.color = result.ok ? 'var(--dsw-alias-state-success-primary)' : 'var(--dsw-alias-state-error-primary)'
  })

  const glassStatus = dialog.querySelector('#set-glass-status')
  const describe = (glass) => {
    if (!glass.supported) return `原生玻璃不可用：${glass.reason}`
    if (glass.active) return '原生玻璃面板正在运行'
    if (glass.requested) return '已请求开启，正在创建面板…'
    return '原生玻璃可用，当前已关闭'
  }
  const glass = await window.tcpchat.glass.status()
  glassStatus.textContent = describe(glass)
  glassStatus.style.color =
    glass.supported && (!glass.requested || glass.active)
      ? ''
      : 'var(--dsw-alias-state-warn-label)'
  // Rebuild the panel without restarting (display or driver changes).
  dialog.querySelector('#set-glass-retry').addEventListener('click', async () => {
    glassStatus.textContent = '正在重建玻璃面板…'
    glassStatus.style.color = ''
    const after = await window.tcpchat.glass.retry()
    glassStatus.textContent = describe(after)
    glassStatus.style.color = after.active === false && after.supported ? 'var(--dsw-alias-state-warn-label)' : ''
  })

  /*
   * The backdrop is applied the moment it is chosen rather than on Save, because
   * the point of picking a picture is seeing whether you like it behind your
   * messages. It also means Cancel cannot un-choose it, so the card shows what is
   * in use and offers Clear.
   */
  const bgPreview = dialog.querySelector('#set-bg-preview')
  const bgEmpty = dialog.querySelector('#set-bg-empty')
  const bgRecents = dialog.querySelector('#set-bg-recent')
  const bgFit = dialog.querySelector('#set-bg-fit')

  function paintBackground({ preview, recents, fit }) {
    if (bgPreview) {
      // Remove the attribute rather than setting src to '' — an empty src
      // resolves to the page URL and the browser tries to decode the document.
      if (preview) bgPreview.src = preview
      else bgPreview.removeAttribute('src')
    }
    /*
     * The .hidden class, not the [hidden] attribute.
     *
     * Both elements carry an explicit display — block and flex — and an explicit
     * display beats the UA stylesheet's [hidden] { display: none }. The class in
     * base.css is marked !important and is what the rest of the app already uses;
     * the attribute silently did nothing here, which is how "未选择背景" ended up
     * printed across a picture that was plainly selected.
     */
    bgPreview?.classList.toggle('hidden', !preview)
    bgEmpty?.classList.toggle('hidden', Boolean(preview))
    if (bgFit && fit) bgFit.value = fit
    if (!bgRecents) return
    clear(bgRecents)
    if (!recents?.length) {
      bgRecents.append(h('div', { class: 'caption', text: '还没有用过图片' }))
      return
    }
    for (const item of recents) {
      bgRecents.append(
        h('button', {
          class: 'bg-recent-item',
          type: 'button',
          title: item.name,
          onclick: async () => {
            const result = await window.tcpchat.app.useBackground(item.path)
            if (!result?.ok) {
              showToast(result?.error || '这张图片用不了', true)
              return
            }
            paintBackground(await window.tcpchat.app.backgroundRecents())
          },
        }, [h('img', { src: item.thumb, alt: item.name })]),
      )
    }
  }

  async function refreshBackground() {
    paintBackground(await window.tcpchat.app.backgroundRecents())
  }

  void refreshBackground()

  dialog.querySelector('#set-bg-pick').addEventListener('click', async () => {
    const result = await window.tcpchat.app.pickBackground()
    if (result?.canceled) return
    if (!result?.ok) {
      showToast(result?.error || '这张图片用不了', true)
      return
    }
    await refreshBackground()
  })

  dialog.querySelector('#set-bg-clear').addEventListener('click', async () => {
    await window.tcpchat.app.clearBackground()
    await refreshBackground()
  })

  bgFit?.addEventListener('change', async () => {
    await window.tcpchat.app.setBackgroundFit(bgFit.value)
  })


  dialog.querySelector('#set-save').addEventListener('click', async () => {
    const patch = collect()

    /*
     * Changing the folder abandons the current board: warn first.
     *
     * Checked against `in patch` rather than by comparing values. The folder and
     * the address no longer have inputs, so they are absent from the patch, and
     * an absent key read as `undefined` compares unequal to the saved string —
     * which would have raised this dialog on every single save.
     */
    const boardChanged = 'chatFolder' in patch && patch.chatFolder !== current.chatFolder
    if (boardChanged) {
      const proceed = await confirmDialog({
        title: '切换聊天目录',
        message: `将从「${current.chatFolder}」切换到「${patch.chatFolder}」。\n当前列表会清空并重新同步，本地缓存按目录分开保存，不会串消息。`,
        confirmLabel: '切换',
      })
      if (!proceed) return
    }

    const updated = await onChange(patch)
    if (updated) {
      showToast('设置已保存')
      close()
    } else {
      showToast('保存失败', true)
    }
  })
}
