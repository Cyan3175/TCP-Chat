/**
 * Small DOM helpers. The renderer builds elements imperatively (no framework)
 * so the whole view layer stays auditable and cheap to update.
 */

export const $ = (selector, scope = document) => scope.querySelector(selector)
export const $$ = (selector, scope = document) => [...scope.querySelectorAll(selector)]

/**
 * Create an element.
 * @param {string} tag
 * @param {object} [props]  `class`, `text`, `html`, `dataset`, `style`, `on*` handlers
 * @param {(Node|string|null|undefined|false)[]} [children]
 */
export function h(tag, props = null, children = []) {
  const el = document.createElement(tag)
  if (props) {
    for (const [key, value] of Object.entries(props)) {
      if (value === null || value === undefined || value === false) continue
      if (key === 'class') el.className = value
      else if (key === 'text') el.textContent = value
      else if (key === 'html') el.innerHTML = value
      else if (key === 'dataset') Object.assign(el.dataset, value)
      else if (key === 'style') Object.assign(el.style, value)
      else if (key.startsWith('on') && typeof value === 'function') {
        el.addEventListener(key.slice(2).toLowerCase(), value)
      } else if (value === true) el.setAttribute(key, '')
      else el.setAttribute(key, String(value))
    }
  }
  for (const child of [].concat(children)) {
    if (child === null || child === undefined || child === false) continue
    el.append(child instanceof Node ? child : document.createTextNode(String(child)))
  }
  return el
}

export function clear(el) {
  while (el.firstChild) el.removeChild(el.firstChild)
}

export function show(el, visible = true) {
  el?.classList.toggle('hidden', !visible)
}

/** `1536` -> `1.5 KB`, matching the C# `Human`/`SizeText` formatting. */
export function humanSize(bytes) {
  const n = Number(bytes) || 0
  if (n < 1024) return `${n} B`
  if (n < 1024 * 1024) return `${Math.trunc(n / 1024)} KB`
  if (n < 1024 * 1024 * 1024) return `${Math.trunc(n / (1024 * 1024))} MB`
  return `${Math.trunc(n / (1024 * 1024 * 1024))} GB`
}

export function clamp(value, min, max) {
  return Math.min(max, Math.max(min, value))
}

/** `754000` -> `12:34`, `3725000` -> `1:02:05`. */
export function formatClock(ms) {
  const total = Math.max(0, Math.floor((Number(ms) || 0) / 1000))
  const h = Math.floor(total / 3600)
  const m = Math.floor((total % 3600) / 60)
  const s = total % 60
  const pad = (n) => String(n).padStart(2, '0')
  return h > 0 ? `${h}:${pad(m)}:${pad(s)}` : `${m}:${pad(s)}`
}

/** Speech-bubble duration label: `7"` under a minute, `1:05` above. */
export function formatDuration(ms) {
  const total = Math.max(0, Number(ms) || 0) / 1000
  return total >= 60 ? formatClock(ms) : `${Math.trunc(total)}"`
}

export function formatTime(iso) {
  const date = iso ? new Date(iso) : new Date()
  if (Number.isNaN(date.getTime())) return ''
  const pad = (n) => String(n).padStart(2, '0')
  return `${pad(date.getHours())}:${pad(date.getMinutes())}:${pad(date.getSeconds())}`
}

export function formatDay(iso) {
  const date = iso ? new Date(iso) : new Date()
  if (Number.isNaN(date.getTime())) return ''
  const today = new Date()
  const sameDay = (a, b) =>
    a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth() && a.getDate() === b.getDate()
  const yesterday = new Date(today.getTime() - 86400000)
  if (sameDay(date, today)) return '今天'
  if (sameDay(date, yesterday)) return '昨天'
  return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(
    date.getDate(),
  ).padStart(2, '0')}`
}

export function dayKey(iso) {
  const date = iso ? new Date(iso) : new Date()
  if (Number.isNaN(date.getTime())) return ''
  return `${date.getFullYear()}-${date.getMonth()}-${date.getDate()}`
}

/** Copy text via the main process (the renderer has no clipboard access). */
export async function copyText(text) {
  try {
    await window.tcpchat.app.copyText(text)
    return true
  } catch {
    return false
  }
}

/** Debounce a function by `ms`. */
export function debounce(fn, ms) {
  let timer = null
  return (...args) => {
    if (timer) clearTimeout(timer)
    timer = setTimeout(() => {
      timer = null
      fn(...args)
    }, ms)
  }
}
