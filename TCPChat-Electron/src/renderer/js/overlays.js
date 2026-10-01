/**
 * Floating overlays: context menu, toasts and the shared click-away handling.
 *
 * Menus are rendered as a single element appended to <body> so they never get
 * clipped by the message list's scroll container.
 */

import { h, clear } from './dom.js'

let activeMenu = null
let activeCleanup = null

/**
 * Open a context menu at the pointer.
 * @param {Array<{label?: string, action?: Function, separator?: boolean, disabled?: boolean, danger?: boolean}>} items
 * @param {MouseEvent} event
 */
export function openMenu(items, event) {
  closeMenu()

  const menu = h('div', { class: 'menu', role: 'menu' })
  for (const item of items) {
    if (!item) continue
    if (item.separator) {
      menu.append(h('div', { class: 'menu-sep' }))
      continue
    }
    const button = h('button', {
      class: `menu-item${item.danger ? ' danger' : ''}`,
      type: 'button',
      role: 'menuitem',
      text: item.label ?? '',
      disabled: item.disabled === true,
      style: item.disabled ? { opacity: '0.5', pointerEvents: 'none' } : null,
      onclick: () => {
        closeMenu()
        item.action?.()
      },
    })
    menu.append(button)
  }

  document.body.append(menu)

  // Keep the menu inside the window.
  const rect = menu.getBoundingClientRect()
  const x = Math.min(event.clientX, window.innerWidth - rect.width - 8)
  const y = Math.min(event.clientY, window.innerHeight - rect.height - 8)
  menu.style.left = `${Math.max(8, x)}px`
  menu.style.top = `${Math.max(8, y)}px`

  activeMenu = menu

  const onPointerDown = (e) => {
    if (menu.contains(e.target)) return
    closeMenu()
  }
  const onKey = (e) => {
    if (e.key === 'Escape') closeMenu()
  }
  const onBlur = () => closeMenu()

  // Defer so the click that opened the menu does not immediately close it.
  setTimeout(() => {
    window.addEventListener('pointerdown', onPointerDown, true)
    window.addEventListener('keydown', onKey, true)
    window.addEventListener('blur', onBlur)
  }, 0)

  activeCleanup = () => {
    window.removeEventListener('pointerdown', onPointerDown, true)
    window.removeEventListener('keydown', onKey, true)
    window.removeEventListener('blur', onBlur)
  }

  return menu
}

export function closeMenu() {
  if (activeCleanup) {
    activeCleanup()
    activeCleanup = null
  }
  if (activeMenu) {
    activeMenu.remove()
    activeMenu = null
  }
}

export const isMenuOpen = () => activeMenu !== null

/** Transient message in the bottom-right corner. */
export function showToast(message, bad = false, timeout = 4200) {
  const container = document.getElementById('toasts')
  if (!container) return
  const toast = h('div', { class: `toast${bad ? ' bad' : ''}`, text: message })
  container.append(toast)
  setTimeout(() => {
    toast.style.transition = 'opacity 180ms ease'
    toast.style.opacity = '0'
    setTimeout(() => toast.remove(), 200)
  }, timeout)
}

/** Simple confirm dialog styled with the app's own primitives. */
export function confirmDialog({ title, message, confirmLabel = '确定', danger = false }) {
  return new Promise((resolve) => {
    const mask = h('div', { class: 'modal-mask' })
    const done = (value) => {
      mask.remove()
      window.removeEventListener('keydown', onKey, true)
      resolve(value)
    }
    const onKey = (event) => {
      if (event.key === 'Escape') {
        event.stopPropagation()
        done(false)
      }
    }

    const dialog = h('div', { class: 'modal', style: { width: 'min(420px, calc(100vw - 48px))' } }, [
      h('div', { class: 'modal-header', text: title }),
      h('div', { class: 'modal-body' }, [h('div', { text: message })]),
      h('div', { class: 'modal-footer' }, [
        h('button', { class: 'btn', type: 'button', text: '取消', onclick: () => done(false) }),
        h('button', {
          class: `btn ${danger ? 'btn-danger' : 'btn-primary'}`,
          type: 'button',
          text: confirmLabel,
          onclick: () => done(true),
        }),
      ]),
    ])
    mask.append(dialog)
    mask.addEventListener('pointerdown', (event) => {
      if (event.target === mask) done(false)
    })
    window.addEventListener('keydown', onKey, true)
    document.body.append(mask)
    dialog.querySelector('.btn-primary, .btn-danger')?.focus()
  })
}

export { clear }
