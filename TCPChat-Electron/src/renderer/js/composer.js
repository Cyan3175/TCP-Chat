/**
 * Composer: message input, reply state and the send/attach/voice actions.
 *
 * Enter sends; Shift+Enter and Ctrl+Enter insert a newline. The textarea grows
 * with the content up to the same 170px ceiling the C# build uses, then scrolls.
 */

import { $, h, show, clear } from './dom.js'
import { showToast, confirmDialog } from './overlays.js'
import {
  startRecording,
  stopRecording,
  cancelRecording,
  isRecording,
  openMicrophoneSettings,
  waitForMicrophonePermission,
} from './recorder.js'

let reply = null
let busy = false
let onSendText = async () => {}
let onSendFiles = async () => {}
let onSendVoice = async () => {}
let onStatus = () => {}

const input = () => document.getElementById('composer-input')
const sendBtn = () => document.getElementById('btn-send')
const voiceBtn = () => document.getElementById('btn-voice')

export function configureComposer(handlers) {
  onSendText = handlers.onSendText ?? onSendText
  onSendFiles = handlers.onSendFiles ?? onSendFiles
  onSendVoice = handlers.onSendVoice ?? onSendVoice
  onStatus = handlers.onStatus ?? onStatus
}

/** Grow the textarea to fit, capped at the CSS max-height. */
function autoGrow() {
  const el = input()
  el.style.height = 'auto'
  const next = Math.min(el.scrollHeight, 170)
  el.style.height = `${Math.max(34, next)}px`
}

export function focusComposer() {
  input()?.focus()
}

export function getDraft() {
  return input()?.value ?? ''
}

/** Put text into the composer without sending it (used by "quote" flows). */
export function setDraft(text) {
  const el = input()
  if (!el) return
  el.value = text
  autoGrow()
  el.focus()
  el.setSelectionRange(el.value.length, el.value.length)
}

export function setReply(msg) {
  reply = msg ?? null
  const bar = document.getElementById('composer-reply')
  if (!reply) {
    show(bar, false)
    return
  }
  const preview = [reply.text?.trim(), reply.attach?.name ? `[附件] ${reply.attach.name}` : '']
    .filter(Boolean)
    .join(' ')
  document.getElementById('reply-text').textContent =
    `回复 ${reply.from || '(匿名)'}：${preview.slice(0, 240) || '(空消息)'}`
  show(bar, true)
  focusComposer()
}

export function getReply() {
  return reply
}

function setBusy(value) {
  busy = value
  const button = sendBtn()
  if (!button) return
  button.disabled = value
  button.textContent = value ? '发送中…' : '发送'
}

async function send() {
  if (busy) return
  const el = input()
  const text = el.value.trim()
  if (!text) return

  const quote = reply ? buildQuote(reply) : null
  setBusy(true)
  try {
    const result = await onSendText(text, quote)
    if (result?.ok) {
      el.value = ''
      autoGrow()
      setReply(null)
      onStatus('已发送')
    } else {
      onStatus(result?.error || '发送失败')
      showToast(result?.error || '发送失败', true)
    }
  } finally {
    setBusy(false)
    el.focus()
  }
}

/** Quote text stored on the wire: sender plus a trimmed excerpt of the body. */
function buildQuote(msg) {
  const body = (msg.text || msg.attach?.name || '').trim().slice(0, 600)
  return `${msg.from || '(匿名)'}: ${body}`
}

async function pickAndSend(kind) {
  const paths = await window.tcpchat.files.pick(kind)
  if (!paths.length) return
  onStatus(`正在发送 ${paths.length} 个文件…`)
  const result = await onSendFiles(paths)
  const failed = (result?.results ?? []).filter((r) => !r.ok).length
  onStatus(failed ? `${failed} 个文件发送失败` : '已发送')
  if (failed) showToast(`${failed} 个文件发送失败`, true)
}

let recordingTicker = null
let recordingStart = 0

function paintRecording(on) {
  const button = voiceBtn()
  if (!button) return
  if (on) {
    button.classList.add('recording')
    clear(button)
    button.append(h('span', { class: 'rec-dot' }), document.createTextNode(' 停止'))
    button.title = '点击停止并发送'
  } else {
    button.classList.remove('recording')
    button.textContent = '🎤'
    button.title = '语音消息'
  }
}

function startTimer() {
  recordingStart = performance.now()
  const button = voiceBtn()
  recordingTicker = setInterval(() => {
    if (!button || !isRecording()) return
    const seconds = Math.floor((performance.now() - recordingStart) / 1000)
    button.lastChild.textContent = ` 停止 ${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`
  }, 500)
}

function stopTimer() {
  if (recordingTicker) clearInterval(recordingTicker)
  recordingTicker = null
}

async function beginRecording() {
  const started = await startRecording()
  if (!started.ok) {
    onStatus(started.error)
    return { ok: false, ...started }
  }
  paintRecording(true)
  startTimer()
  onStatus('正在录音，再点一下语音按钮结束并发送')
  return { ok: true }
}

async function toggleVoice() {
  if (isRecording()) {
    paintRecording(false)
    stopTimer()
    const result = await stopRecording()
    if (!result.ok) {
      onStatus(result.error)
      if (result.error !== '录音太短，已丢弃') showToast(result.error, true)
      return
    }
    onStatus('正在发送语音…')
    const sent = await onSendVoice(result.bytes, result.seconds, result.durationMs)
    if (sent?.ok) onStatus(`语音已发送 (${result.seconds} 秒)`)
    else {
      onStatus(sent?.error || '语音发送失败')
      showToast(sent?.error || '语音发送失败', true)
    }
    return
  }

  let attempt = await beginRecording()
  if (attempt.ok) return

  if (!attempt.denied) {
    showToast(attempt.error, true)
    return
  }

  const open = await confirmDialog({
    title: '需要麦克风权限',
    message: `${attempt.error}\n\n要现在打开系统的麦克风隐私设置吗？`,
    confirmLabel: '打开设置',
  })
  if (!open) return

  await openMicrophoneSettings()
  onStatus('等待麦克风权限…')
  if (!(await waitForMicrophonePermission())) {
    onStatus('仍未获得麦克风权限')
    return
  }

  // Matching the C# build: once the user flips the switch and comes back,
  // recording starts by itself instead of asking for another click.
  onStatus('已获得麦克风权限，正在开始录音…')
  attempt = await beginRecording()
  if (!attempt.ok) showToast(attempt.error, true)
}

/** Attach all wiring to the DOM. Call once at startup. */
export function initComposer() {
  const el = input()

  el.addEventListener('keydown', (event) => {
    if (event.key !== 'Enter') return
    if (event.isComposing || event.keyCode === 229) return // IME composition
    if (event.shiftKey || event.ctrlKey || event.metaKey) return // newline
    event.preventDefault()
    void send()
  })

  el.addEventListener('input', autoGrow)

  // Paste as plain text: the input is for messages, not formatting.
  el.addEventListener('paste', (event) => {
    const text = event.clipboardData?.getData('text/plain')
    if (text === undefined) return
    event.preventDefault()
    const start = el.selectionStart
    const end = el.selectionEnd
    el.setRangeText(text, start, end, 'end')
    autoGrow()
  })

  // Files dropped anywhere in the window are sent as attachments.
  window.addEventListener('dragover', (event) => {
    event.preventDefault()
  })
  window.addEventListener('drop', async (event) => {
    event.preventDefault()
    const files = [...(event.dataTransfer?.files ?? [])]
    if (!files.length) return
    // The renderer has no filesystem path for a dropped File, so ask the user to
    // pick them explicitly instead of silently failing.
    showToast('请用「📎 文件」按钮选择要发送的文件')
  })

  sendBtn().addEventListener('click', () => void send())
  document.getElementById('reply-cancel').addEventListener('click', () => setReply(null))

  document.getElementById('btn-attach').addEventListener('click', () => void pickAndSend('any'))
  document.getElementById('btn-image').addEventListener('click', () => void pickAndSend('image'))
  voiceBtn().addEventListener('click', () => void toggleVoice())

  window.addEventListener('beforeunload', () => cancelRecording())
  autoGrow()
}

export { send as sendDraft, toggleVoice }
