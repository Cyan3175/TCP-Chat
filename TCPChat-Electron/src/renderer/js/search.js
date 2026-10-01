/**
 * Message search (Ctrl+F).
 *
 * Enter jumps to the next hit, Shift+Enter to the previous one, Escape exits.
 * The footer shows live match progress. Hits are marked on the bubbles; the
 * current one gets the accent ring. Matching happens against body, quote,
 * nickname, attachment name and timestamp — the same fields the C# build covers.
 */

import { $, show, debounce } from './dom.js'
import { matchesQuery, highlightInMessage, scrollToMessage } from './messages.js'

let getMessages = () => []
let hits = []
let current = -1
let query = ''

const bar = () => document.getElementById('searchbar')
const inputEl = () => document.getElementById('search-input')
const countEl = () => document.getElementById('search-count')

export function configureSearch(handlers) {
  getMessages = handlers.getMessages ?? getMessages
}

export const searchQuery = () => query
export const hasHits = () => hits.length > 0

function paintCount() {
  const el = countEl()
  if (!el) return
  if (!query) el.textContent = ''
  else if (hits.length === 0) el.textContent = '无匹配'
  else el.textContent = `${current + 1}/${hits.length}`
}

function clearMarks() {
  for (const el of document.querySelectorAll('.msg.hit, .msg.current')) {
    el.classList.remove('hit', 'current')
    highlightInMessage(el, '')
  }
  document.querySelectorAll('mark.find').forEach((mark) => mark.replaceWith(document.createTextNode(mark.textContent)))
}

/** Recompute hits for the current query. */
function recompute({ keepCurrent = false } = {}) {
  const previousName = hits[current]?.remoteName
  clearMarks()
  hits = []
  current = -1

  const needle = query.trim()
  if (!needle) {
    paintCount()
    return
  }

  for (const msg of getMessages()) {
    if (!matchesQuery(msg, needle)) continue
    hits.push(msg)
    const el = document.querySelector(`.msg[data-name="${window.CSS.escape(msg.remoteName)}"]`)
    if (el) {
      el.classList.add('hit')
      highlightInMessage(el, needle)
    }
  }

  if (!hits.length) {
    paintCount()
    return
  }

  if (keepCurrent && previousName) {
    const index = hits.findIndex((m) => m.remoteName === previousName)
    current = index >= 0 ? index : 0
  } else {
    current = 0
  }
  focusCurrent()
}

function focusCurrent() {
  document.querySelectorAll('.msg.current').forEach((el) => el.classList.remove('current'))
  const hit = hits[current]
  if (!hit) {
    paintCount()
    return
  }
  const el = document.querySelector(`.msg[data-name="${window.CSS.escape(hit.remoteName)}"]`)
  el?.classList.add('current')
  scrollToMessage(hit.remoteName)
  paintCount()
}

function step(delta) {
  if (!hits.length) return
  current = (current + delta + hits.length) % hits.length
  focusCurrent()
}

export function openSearch() {
  show(bar(), true)
  const el = inputEl()
  el.focus()
  el.select()
}

export function closeSearch() {
  show(bar(), false)
  query = ''
  const el = inputEl()
  if (el) el.value = ''
  clearMarks()
  hits = []
  current = -1
  paintCount()
}

export function isSearchOpen() {
  return !bar().classList.contains('hidden')
}

/** Re-run the active query — used when the message list changes. */
export const refreshSearch = debounce(() => {
  if (!query) return
  recompute({ keepCurrent: true })
}, 120)

/** Called after a full re-render so highlights survive. */
export function reapplySearch() {
  if (query) recompute({ keepCurrent: true })
}

export function initSearch() {
  const el = inputEl()

  el.addEventListener(
    'input',
    debounce(() => {
      query = el.value
      recompute()
    }, 140),
  )

  el.addEventListener('keydown', (event) => {
    if (event.key === 'Enter') {
      event.preventDefault()
      step(event.shiftKey ? -1 : 1)
    } else if (event.key === 'Escape') {
      event.preventDefault()
      closeSearch()
    }
  })

  document.getElementById('search-next').addEventListener('click', () => step(1))
  document.getElementById('search-prev').addEventListener('click', () => step(-1))
  document.getElementById('search-close').addEventListener('click', () => closeSearch())
  document.getElementById('btn-search').addEventListener('click', () => {
    if (isSearchOpen()) closeSearch()
    else openSearch()
  })
}
