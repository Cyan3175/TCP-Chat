'use strict'

/*
 * The tray icon.
 *
 * Closing the window hides it instead of ending the process, so polling carries
 * on and messages keep arriving while nothing is on screen. That is the whole
 * point: the app is a message board, and a board you have to leave open to
 * receive on is not much of one.
 *
 * Deliberately quiet. A tray icon and a tooltip, no balloons — the request was
 * to keep receiving, not to be interrupted.
 */

const { Tray, Menu, nativeImage } = require('electron')
const { log } = require('./logger')

/** @type {Tray | null} */
let tray = null

/**
 * Create the icon, or do nothing if one already exists.
 *
 * @param {object} options
 * @param {string} options.iconPath
 * @param {() => void} options.onShow
 * @param {() => void} options.onQuit
 */
function install({ iconPath, onShow, onQuit }) {
  if (tray) return tray

  try {
    const image = nativeImage.createFromPath(iconPath)
    if (image.isEmpty()) {
      log.warn(`tray: icon unreadable at ${iconPath}`)
      return null
    }
    tray = new Tray(image.resize({ width: 16, height: 16 }))
  } catch (err) {
    // A missing tray must not take the app with it: the window still works.
    log.warn('tray: could not create the icon', err)
    return null
  }

  tray.setToolTip('TCP Chat')
  tray.setContextMenu(
    Menu.buildFromTemplate([
      { label: '显示主窗口', click: () => onShow() },
      { type: 'separator' },
      { label: '退出', click: () => onQuit() },
    ]),
  )
  // Left click opens the window; the menu is on right click, which is the
  // Windows convention and what setContextMenu already provides.
  tray.on('click', () => onShow())
  log.info('tray: installed')
  return tray
}

/** The tooltip, for the status the status bar would be showing. */
function setStatus(text) {
  if (!tray) return
  try {
    tray.setToolTip(text ? `TCP Chat — ${text}` : 'TCP Chat')
  } catch {
    /* the icon may have gone away underneath us */
  }
}

function destroy() {
  if (!tray) return
  try {
    tray.destroy()
  } catch {
    /* already gone */
  }
  tray = null
}

function exists() {
  return tray !== null
}

module.exports = { install, setStatus, destroy, exists }
