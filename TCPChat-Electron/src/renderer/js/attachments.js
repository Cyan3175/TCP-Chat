/**
 * Attachment rendering: images, video (first-frame thumbnail + play badge),
 * audio, voice notes and generic file cards.
 *
 * Media is fetched on demand through the main process, which downloads and
 * decrypts into the local cache and hands back a `tcpcache:` URL. Fetching is
 * lazily triggered by an IntersectionObserver, so opening a long conversation
 * does not download every historical attachment at once.
 */

import { $, h, humanSize, formatDuration } from './dom.js'
import { showToast } from './overlays.js'

const KIND_FILE = 1
const KIND_IMAGE = 2
const KIND_VIDEO = 3
const KIND_AUDIO = 4
const KIND_VOICE = 5

/** remote file name -> resolved tcpcache URL (or a rejection marker). */
const urlCache = new Map()
const pending = new Map()

/** Resolve (downloading if needed) the local URL for a message's attachment. */
export function resolveAttachment(remoteName) {
  if (urlCache.has(remoteName)) return Promise.resolve(urlCache.get(remoteName))
  if (pending.has(remoteName)) return pending.get(remoteName)

  const promise = window.tcpchat.chat
    .ensureAttachment(remoteName)
    .then((result) => {
      if (!result?.ok) throw new Error(result?.error || '附件不可用')
      urlCache.set(remoteName, result.url)
      return result.url
    })
    .catch((err) => {
      urlCache.set(remoteName, null)
      throw err
    })
    .finally(() => {
      pending.delete(remoteName)
    })

  pending.set(remoteName, promise)
  return promise
}

/** Called after a re-download so a previously failed attachment can retry. */
export function invalidateAttachment(remoteName) {
  urlCache.delete(remoteName)
}

/** Placeholder shown until the observer triggers the real load. */
function lazyBox(className, label) {
  return h('div', { class: className }, [h('span', { class: 'caption', text: label })])
}

/** Swap a placeholder for real content once it scrolls into view. */
function lazy(placeholder, build) {
  if (!('IntersectionObserver' in window)) {
    build().catch(() => {})
    return placeholder
  }
  const observer = new IntersectionObserver(
    (entries) => {
      for (const entry of entries) {
        if (!entry.isIntersecting) continue
        observer.disconnect()
        build().catch((err) => {
          placeholder.replaceWith(
            h('div', { class: 'attach-progress' }, [
              h('span', { class: 'caption', text: err.message || '附件不可用' }),
            ]),
          )
        })
      }
    },
    { root: document.querySelector('.msg-list'), rootMargin: '240px 0px' },
  )
  observer.observe(placeholder)
  return placeholder
}

function fileIcon(name) {
  const ext = (name.match(/\.([^.]+)$/)?.[1] ?? '').toLowerCase()
  if (['zip', 'rar', '7z', 'tar', 'gz'].includes(ext)) return '🗜'
  if (['pdf'].includes(ext)) return '📕'
  if (['doc', 'docx'].includes(ext)) return '📘'
  if (['xls', 'xlsx', 'csv'].includes(ext)) return '📗'
  if (['ppt', 'pptx'].includes(ext)) return '📙'
  if (['txt', 'md', 'log'].includes(ext)) return '📄'
  if (['exe', 'msi'].includes(ext)) return '⚙'
  if (['cpp', 'c', 'h', 'hpp', 'py', 'js', 'ts', 'java', 'cs', 'go', 'rs'].includes(ext)) return '📜'
  return '📦'
}

function attachMenuItems(remoteName, attach, extra = []) {
  return [
    { label: '打开附件', action: () => openAttachment(remoteName) },
    { label: '另存为…', action: () => saveAttachment(remoteName) },
    ...extra,
    { separator: true },
    { label: `大小 ${humanSize(attach.size)}`, disabled: true },
  ]
}

async function openAttachment(remoteName) {
  const result = await window.tcpchat.chat.openAttachment(remoteName)
  if (!result?.ok) showToast(result?.error || '打开失败', true)
}

async function saveAttachment(remoteName) {
  const result = await window.tcpchat.chat.saveAttachment(remoteName)
  if (result?.ok) showToast(`已保存到 ${result.path}`)
  else if (!result?.canceled) showToast(result?.error || '保存失败', true)
}

/**
 * Build the attachment view for a message.
 * @param {object} attach    attachment descriptor from the wire
 * @param {string} remoteName owning message's remote file name
 * @param {(items: object[], event: MouseEvent) => void} openMenu
 */
export function renderAttachment(attach, remoteName, openMenu) {
  const kind = Number(attach.kind) || KIND_FILE
  const name = attach.name || '附件'

  if (kind === KIND_IMAGE) {
    const box = lazyBox('attach-progress', '正在加载图片…')
    return lazy(box, async () => {
      const url = await resolveAttachment(remoteName)
      const img = h('img', {
        class: 'attach-image',
        alt: name,
        title: name,
        src: url,
        onclick: () => $('.lightbox') ?? openLightbox(url, name),
        oncontextmenu: (event) => {
          event.preventDefault()
          // The bubble underneath listens for contextmenu too. Without stopping
          // propagation this menu opens and is instantly replaced by the message
          // menu, so "打开附件" is never reachable from the card.
          event.stopPropagation()
          openMenu(attachMenuItems(remoteName, attach), event)
        },
        onerror: () => {
          invalidateAttachment(remoteName)
          box.replaceWith(h('div', { class: 'caption', text: '图片加载失败' }))
        },
      })
      box.replaceWith(img)
    })
  }

  if (kind === KIND_VIDEO) {
    const box = lazyBox('attach-progress', '正在加载视频…')
    return lazy(box, async () => {
      const url = await resolveAttachment(remoteName)
      const wrap = h('div', { style: { position: 'relative', display: 'inline-block' } })
      // `#t=0.1` makes Chromium decode and show the first frame as a poster,
      // reproducing the C# build's first-frame thumbnail.
      const video = h('video', {
        class: 'attach-video',
        src: `${url}#t=0.1`,
        preload: 'metadata',
        playsinline: true,
        oncontextmenu: (event) => {
          event.preventDefault()
          // The bubble underneath listens for contextmenu too. Without stopping
          // propagation this menu opens and is instantly replaced by the message
          // menu, so "打开附件" is never reachable from the card.
          event.stopPropagation()
          openMenu(attachMenuItems(remoteName, attach), event)
        },
      })
      const badge = h('div', {
        style: {
          position: 'absolute',
          inset: '0',
          display: 'grid',
          placeItems: 'center',
          cursor: 'pointer',
          background: 'rgb(0 0 0 / 22%)',
          borderRadius: 'var(--dsw-radius-sm)',
          color: '#fff',
          fontSize: '26px',
          pointerEvents: 'auto',
        },
        text: '▶',
        onclick: () => {
          badge.remove()
          video.removeAttribute('src')
          video.src = url
          video.controls = true
          video.play().catch(() => {})
        },
      })
      wrap.append(video, badge)
      box.replaceWith(wrap)
    })
  }

  if (kind === KIND_VOICE) {
    return renderVoice(attach, remoteName, openMenu)
  }

  if (kind === KIND_AUDIO) {
    const box = lazyBox('attach-progress', '正在加载音频…')
    return lazy(box, async () => {
      const url = await resolveAttachment(remoteName)
      const row = h('div', { class: 'attach-audio' }, [
        h('span', { text: '🎵' }),
        h('audio', {
          src: url,
          controls: true,
          preload: 'metadata',
          oncontextmenu: (event) => {
            event.preventDefault()
          // The bubble underneath listens for contextmenu too: without this the
          // attachment menu opens and is instantly replaced by the message menu.
          event.stopPropagation()
            openMenu(attachMenuItems(remoteName, attach), event)
          },
        }),
      ])
      box.replaceWith(row)
    })
  }

  // Generic file card.
  const card = h(
    'div',
    {
      class: 'file-card',
      title: name,
      onclick: () => openAttachment(remoteName),
      oncontextmenu: (event) => {
        event.preventDefault()
          // The bubble underneath listens for contextmenu too: without this the
          // attachment menu opens and is instantly replaced by the message menu.
          event.stopPropagation()
        openMenu(attachMenuItems(remoteName, attach), event)
      },
    },
    [
      h('div', { class: 'ico', text: fileIcon(name) }),
      h('div', { class: 'meta' }, [
        h('div', { class: 'nm', text: name }),
        h('div', { class: 'sz', text: humanSize(attach.size) }),
      ]),
    ],
  )
  return card
}

/**
 * The one thing allowed to be playing.
 *
 * Three kinds of player end up here and no single mechanism covers all of them.
 * A voice note is an Audio object the app creates and never inserts into the
 * document, so it cannot be found by looking and its events never reach a
 * document listener. Audio and video attachments are real elements, so the
 * reverse is true for them. Both halves are handled below.
 */
let currentVoiceStop = null

/** Pause every audio/video element in the document except `keep`. */
function pauseDocumentMedia(keep) {
  for (const el of document.querySelectorAll('audio, video')) {
    if (el !== keep && !el.paused) el.pause()
  }
}

/*
 * A media element started: silence the voice note.
 *
 * Capture phase, so this runs before anything else reacts to the same play, and
 * unattached voice notes never appear here — their events do not reach the
 * document, which is what keeps this from stopping the note that just started.
 */
document.addEventListener(
  'play',
  (event) => {
    const el = event.target
    if (!(el instanceof HTMLMediaElement)) return
    if (currentVoiceStop) currentVoiceStop()
    pauseDocumentMedia(el)
  },
  true,
)

/** Voice note: round play button, static waveform bars, duration. */
function renderVoice(attach, remoteName, openMenu) {
  let audio = null
  let playing = false
  /*
   * Guards the whole handler, not just its last step.
   *
   * Resolving the attachment is a round trip, and until it finishes `audio` is
   * still null and `playing` is still false — so a second click during that
   * window takes the same branch as the first and builds a second Audio object.
   * The two then play over each other, and only one of them is reachable from the
   * button, so no amount of clicking afterwards stops the other.
   */
  let busy = false
  const bars = h(
    'div',
    { class: 'bars' },
    Array.from({ length: 16 }, (_, i) => {
      // Deterministic pseudo-waveform so a note looks the same on every render.
      const seed = ((attach.size || 1) + i * 2654435761) % 100
      return h('i', { style: { height: `${5 + (seed % 15)}px` } })
    }),
  )

  const button = h('button', { class: 'play', type: 'button', title: '播放', text: '▶' })

  const stop = () => {
    playing = false
    button.textContent = '▶'
    /*
     * Only give up the slot if it is still ours.
     *
     * stop() also runs from the pause and ended listeners, so when a second note
     * takes the slot the first one's pause fires and would otherwise clear the
     * registration the second one just made.
     */
    if (currentVoiceStop === stop) currentVoiceStop = null
  }

  button.addEventListener('click', async () => {
    if (busy) return
    busy = true
    try {
      if (!audio) {
        const url = await resolveAttachment(remoteName)
        audio = new Audio(url)
        audio.addEventListener('ended', stop)
        audio.addEventListener('pause', stop)
        audio.addEventListener('error', () => {
          stop()
          showToast('语音播放失败', true)
        })
      }
      if (playing) {
        audio.pause()
        stop()
      } else {
        // Silence whatever was playing before this one starts: another note, an
        // audio attachment, or a video.
        if (currentVoiceStop && currentVoiceStop !== stop) currentVoiceStop()
        pauseDocumentMedia(null)
        await audio.play()
        playing = true
        currentVoiceStop = stop
        button.textContent = '❚❚'
      }
    } catch (err) {
      stop()
      showToast(err.message || '语音播放失败', true)
    } finally {
      busy = false
    }
  })

  return h(
    'div',
    {
      class: 'voice',
      oncontextmenu: (event) => {
        event.preventDefault()
          // The bubble underneath listens for contextmenu too: without this the
          // attachment menu opens and is instantly replaced by the message menu.
          event.stopPropagation()
        openMenu(attachMenuItems(remoteName, attach), event)
      },
    },
    [button, bars, h('span', { class: 'dur', text: formatDuration(attach.durationMs) })],
  )
}

export function openLightbox(url, caption) {
  const img = h('img', { src: url, alt: caption })
  const box = h('div', { class: 'lightbox', onclick: () => box.remove() }, [
    img,
    h('button', {
      class: 'btn close',
      type: 'button',
      text: '✕',
      onclick: (event) => {
        event.stopPropagation()
        box.remove()
      },
    }),
  ])
  document.body.append(box)
  const onKey = (event) => {
    if (event.key === 'Escape') {
      box.remove()
      window.removeEventListener('keydown', onKey)
    }
  }
  window.addEventListener('keydown', onKey)
  return box
}

export { KIND_FILE, KIND_IMAGE, KIND_VIDEO, KIND_AUDIO, KIND_VOICE }
