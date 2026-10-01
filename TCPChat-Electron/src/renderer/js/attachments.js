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

/** Voice note: round play button, static waveform bars, duration. */
function renderVoice(attach, remoteName, openMenu) {
  let audio = null
  let playing = false
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
  }

  button.addEventListener('click', async () => {
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
        await audio.play()
        playing = true
        button.textContent = '❚❚'
      }
    } catch (err) {
      stop()
      showToast(err.message || '语音播放失败', true)
    }
  })

  return h(
    'div',
    {
      class: 'voice',
      oncontextmenu: (event) => {
        event.preventDefault()
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
