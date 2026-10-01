/**
 * Message list rendering.
 *
 * Messages arrive from the main process in chronological (file-name) order. New
 * ones append, withdrawn ones are removed, and a full reset is used when the
 * chat folder, server or passwords change.
 *
 * Own messages are only editable in the ways the protocol allows: quote-reply,
 * copy, and withdraw (which also deletes the attachment server-side).
 */

import { $, h, clear, formatTime, formatDay, dayKey, copyText } from './dom.js'
import { renderMarkdown, toPlainText } from './markdown.js'
import { renderAttachment } from './attachments.js'
import { openMenu, showToast, confirmDialog } from './overlays.js'

const DECRYPT_FAILED_TEXT = '⚠ 无法解密（密码与发送方不一致，或密文被改动过）'

let onReply = () => {}
let onWithdraw = () => {}

/** Wire the callbacks the context menu needs. */
export function configureMessages(handlers) {
  onReply = handlers.onReply ?? onReply
  onWithdraw = handlers.onWithdraw ?? onWithdraw
}

const listEl = () => document.getElementById('msg-list')
const emptyEl = () => document.getElementById('empty-state')

function isSelfMessage(msg, nickname) {
  return Boolean(nickname) && msg.from === nickname
}

/** Copy payload for a message: body plus a readable form of its quote. */
function copyPayload(msg) {
  const parts = []
  if (msg.quote) parts.push(`> ${msg.quote.split('\n').join('\n> ')}`)
  if (msg.text) parts.push(msg.text)
  if (msg.attach) parts.push(`[附件] ${msg.attach.name}`)
  return parts.join('\n\n')
}

function buildMenuItems(msg, nickname) {
  const items = [
    { label: '引用回复', action: () => onReply(msg) },
    { label: '复制文本', action: () => copyText(copyPayload(msg)) },
  ]
  if (msg.attach) {
    items.push(
      { separator: true },
      { label: '打开附件', action: () => window.tcpchat.chat.openAttachment(msg.remoteName) },
      { label: '另存为…', action: () => window.tcpchat.chat.saveAttachment(msg.remoteName) },
    )
  }
  if (isSelfMessage(msg, nickname)) {
    items.push(
      { separator: true },
      {
        label: '撤回消息',
        danger: true,
        action: async () => {
          const ok = await confirmDialog({
            title: '撤回消息',
            message: '撤回会同时删除服务器上的消息文件（以及附件）。此操作无法撤销。',
            confirmLabel: '撤回',
            danger: true,
          })
          if (ok) onWithdraw(msg)
        },
      },
    )
  }
  return items
}

function buildHead(msg, nickname) {
  const head = h('div', { class: 'msg-head' }, [
    h('span', { class: 'who', text: isSelfMessage(msg, nickname) ? `${msg.from}（我）` : msg.from || '(匿名)' }),
    h('span', { class: 'time', text: formatTime(msg.time) }),
  ])
  if (msg.enc) head.append(h('span', { class: 'lock', text: '🔒', title: '端到端加密' }))
  if (msg.attach) head.append(h('span', { class: 'caption', text: `📎 ${msg.attach.name}` }))
  if (msg.status) {
    head.append(h('span', { class: 'state bad', text: msg.status }))
  } else if (msg.pending) {
    head.append(h('span', { class: 'state', text: '发送中…' }))
  }
  return head
}

/**
 * Build one message element.
 * @param {object} msg
 * @param {string} nickname current user's nickname
 */
export function renderMessage(msg, nickname) {
  const self = isSelfMessage(msg, nickname)
  const el = h('div', {
    class: `msg${self ? ' self' : ''}`,
    dataset: { name: msg.remoteName },
  })
  el.append(buildHead(msg, nickname))

  const openMenuHere = (event) => {
    event.preventDefault()
    openMenu(buildMenuItems(msg, nickname), event)
  }

  if (msg.decryptFailed) {
    el.append(h('div', { class: 'bubble is-placeholder', oncontextmenu: openMenuHere }, [DECRYPT_FAILED_TEXT]))
    return el
  }

  const bubble = h('div', { class: `bubble${msg.status ? ' is-failed' : ''}` })
  bubble.addEventListener('contextmenu', openMenuHere)

  if (msg.quote) {
    bubble.append(h('div', { class: 'quote', text: msg.quote, title: msg.quote }))
  }

  const body = h('div', { class: 'md' })
  const hasText = typeof msg.text === 'string' && msg.text.length > 0
  if (hasText) body.innerHTML = renderMarkdown(msg.text).html
  if (hasText || !msg.attach) bubble.append(body)

  if (msg.attach) {
    bubble.append(renderAttachment(msg.attach, msg.remoteName, (items, event) => openMenu(items, event)))
  }

  el.append(bubble)
  return el
}

function daySeparator(iso) {
  return h('div', { class: 'msg-day', dataset: { day: dayKey(iso) }, text: formatDay(iso) })
}

/**
 * Append a message, inserting a day separator when the date rolls over.
 * Reads only the list's last child, so appending stays O(1) — a full
 * `querySelectorAll` per message would make loading a long history quadratic.
 */
export function appendMessage(msg, nickname) {
  const list = listEl()
  emptyEl()?.classList.add('hidden')

  const key = dayKey(msg.time)
  const last = list.lastElementChild
  let lastKey = null
  if (last?.classList.contains('msg') || last?.classList.contains('msg-day')) {
    lastKey = last.dataset.day ?? null
  }
  if (lastKey !== key) list.append(daySeparator(msg.time))

  const el = renderMessage(msg, nickname)
  el.dataset.day = key
  list.append(el)
  return el
}

/** Replace an existing message in place (e.g. after a send failed or succeeded). */
export function replaceMessage(msg, nickname) {
  const existing = listEl().querySelector(`.msg[data-name="${cssEscape(msg.remoteName)}"]`)
  if (!existing) return appendMessage(msg, nickname)
  const el = renderMessage(msg, nickname)
  el.dataset.day = existing.dataset.day
  existing.replaceWith(el)
  return el
}

export function removeMessage(remoteName) {
  const el = listEl().querySelector(`.msg[data-name="${cssEscape(remoteName)}"]`)
  if (!el) return false
  const day = el.dataset.day
  const wasLastOfDay = el.previousElementSibling?.classList.contains('msg-day')
  el.remove()
  // Drop the separator if the day now has no messages.
  if (wasLastOfDay) {
    const next = listEl().querySelector(`.msg[data-day="${cssEscape(day)}"]`)
    if (!next) listEl().querySelector(`.msg-day[data-day="${cssEscape(day)}"]`)?.remove()
  }
  return true
}

function cssEscape(value) {
  return window.CSS?.escape ? window.CSS.escape(String(value)) : String(value).replace(/"/g, '\\"')
}

/** Full re-render from an ordered list. */
export function renderAll(messages, nickname) {
  const list = listEl()
  clear(list)
  if (!messages.length) {
    // Derived from state rather than set once, so a later reset cannot wipe the
    // first-run hint.
    list.append(
      buildEmptyState(
        nickname ? '还没有消息，说点什么吧' : '先在「设置」里填一个昵称，然后就可以开始聊天了',
      ),
    )
    return
  }
  emptyEl()?.classList.add('hidden')
  let currentDay = null
  for (const msg of messages) {
    const key = dayKey(msg.time)
    if (key !== currentDay) {
      list.append(h('div', { class: 'msg-day', dataset: { day: key }, text: formatDay(msg.time) }))
      currentDay = key
    }
    const el = renderMessage(msg, nickname)
    el.dataset.day = key
    list.append(el)
  }
}

function buildEmptyState(text = '还没有消息，说点什么吧') {
  return h('div', { class: 'empty-state', id: 'empty-state' }, [
    h('div', { class: 'big', text: '💬' }),
    h('div', { id: 'empty-text', text }),
  ])
}

/** Message shown while the chat folder or server is unusable. */
export function setEmptyText(text) {
  const list = listEl()
  if (list.querySelector('.msg')) return
  const existing = list.querySelector('.empty-state')
  if (existing) {
    existing.querySelector('#empty-text').textContent = text
  } else {
    list.append(buildEmptyState(text))
  }
}

export function messageCount() {
  return listEl().querySelectorAll('.msg').length
}

// ---------------------------------------------------------------------------
// Scrolling
// ---------------------------------------------------------------------------

/** True when the view is close enough to the bottom to auto-follow new messages. */
export function isAtBottom(slack = 48) {
  const list = listEl()
  return list.scrollHeight - list.scrollTop - list.clientHeight <= slack
}

export function scrollToBottom(smooth = false) {
  const list = listEl()
  list.scrollTo({ top: list.scrollHeight, behavior: smooth ? 'smooth' : 'auto' })
}

export function scrollToMessage(remoteName) {
  const el = listEl().querySelector(`.msg[data-name="${cssEscape(remoteName)}"]`)
  if (!el) return false
  el.scrollIntoView({ block: 'center', behavior: 'smooth' })
  return true
}

// ---------------------------------------------------------------------------
// Search highlighting (works over rendered Markdown text nodes)
// ---------------------------------------------------------------------------

/** Wrap every case-insensitive occurrence of `query` in `<mark class="find">`. */
export function highlightInMessage(el, query) {
  clearHighlights(el)
  if (!query) return 0
  const needle = query.toLowerCase()
  let hits = 0

  const walker = document.createTreeWalker(el, NodeFilter.SHOW_TEXT, {
    acceptNode(node) {
      if (!node.nodeValue || !node.nodeValue.trim()) return NodeFilter.FILTER_REJECT
      const parent = node.parentElement
      if (!parent) return NodeFilter.FILTER_REJECT
      // Never rewrite code blocks, scripts or existing highlights.
      if (parent.closest('script, style, mark.find')) return NodeFilter.FILTER_REJECT
      return node.nodeValue.toLowerCase().includes(needle)
        ? NodeFilter.FILTER_ACCEPT
        : NodeFilter.FILTER_REJECT
    },
  })

  const targets = []
  let node
  while ((node = walker.nextNode())) targets.push(node)

  for (const textNode of targets) {
    const text = textNode.nodeValue
    const lower = text.toLowerCase()
    const fragment = document.createDocumentFragment()
    let cursor = 0
    for (;;) {
      const at = lower.indexOf(needle, cursor)
      if (at === -1) break
      if (at > cursor) fragment.append(document.createTextNode(text.slice(cursor, at)))
      fragment.append(h('mark', { class: 'find', text: text.slice(at, at + needle.length) }))
      cursor = at + needle.length
      hits += 1
    }
    if (cursor < text.length) fragment.append(document.createTextNode(text.slice(cursor)))
    textNode.replaceWith(fragment)
  }
  return hits
}

export function clearHighlights(el) {
  for (const mark of el.querySelectorAll('mark.find')) {
    mark.replaceWith(document.createTextNode(mark.textContent))
  }
  el.normalize()
}

/** Plain-text search over the fields the user expects to match. */
export function matchesQuery(msg, query) {
  if (!query) return false
  const needle = query.toLowerCase()
  const haystack = [
    msg.decryptFailed ? '' : toPlainText(msg.text ?? ''),
    msg.quote ?? '',
    msg.from ?? '',
    msg.attach?.name ?? '',
    formatTime(msg.time),
    formatDay(msg.time),
  ]
    .join('\n')
    .toLowerCase()
  return haystack.includes(needle)
}

export { copyPayload, toPlainText }
