'use strict'

/**
 * Local settings, persisted to `%LOCALAPPDATA%\TCPChat\settings.json`.
 *
 * The JSON schema is byte-compatible with the C# build's `AppSettings`: same
 * property names, same defaults, same normalisation pass. A user can therefore
 * point either application at the same profile and keep their passwords,
 * folders and glass preferences.
 */

const fs = require('fs')
const path = require('path')
const { settingsFile, ensureDataDir } = require('./paths')
const { log } = require('./logger')

const DEFAULT_CHAT_FOLDER = 'nw集训/学生资料临存/tcp_chat'
/** 11.2 and earlier defaulted to the shared folder itself; migrated once. */
const LEGACY_SHARED_CHAT_FOLDER = 'nw集训/学生资料临存'
/** 10.0/10.1 defaulted to a `聊天` subfolder; migrated once. */
const LEGACY_DEFAULT_CHAT_FOLDER = 'nw集训/学生资料临存/聊天'
/*
 * The only address the messages live on. dev.zhaohans.cn was the WebDAV host;
 * it is abandoned and answers 502, so there is nothing left to choose between.
 */
const DEFAULT_NW_URL = 'https://nw.zhaohans.cn'

const ZOOM_MIN = 0.6
const ZOOM_MAX = 2.4
const ZOOM_STEP = 0.1
/** Base font size for message content, in CSS pixels at zoom 1. */
const BASE_FONT_SIZE = 14

const POLL_MIN = 1
const POLL_MAX = 120
const HISTORY_MIN = 1
const HISTORY_MAX = 365

/*
 * Fit modes for the chosen background, as the CSS object-fit keyword. These
 * mirror what Windows offers for the desktop picture, which is the mental
 * model anyone picking a backdrop already has.
 */
const FITS = ['cover', 'contain', 'fill', 'none', 'repeat']

/** How many recently used pictures to remember. */
const RECENT_MAX = 8

function clamp(value, min, max, fallback) {
  const n = typeof value === 'number' ? value : Number(value)
  if (!Number.isFinite(n)) return fallback
  return Math.min(max, Math.max(min, n))
}

/** Same rounding the C# `UiZoom.Clamp` performs. */
function clampZoom(level) {
  if (!Number.isFinite(level) || level <= 0) return 1
  const clamped = Math.min(ZOOM_MAX, Math.max(ZOOM_MIN, level))
  return Math.round(clamped * 100) / 100
}

function defaults() {
  return {
    folderMigratedV113: false,
    nwUrl: DEFAULT_NW_URL,
    nwPassword: '',
    chatFolder: DEFAULT_CHAT_FOLDER,
    nickname: '',
    pollSeconds: 3,
    historyDays: 7,
    autoScroll: true,
    theme: 0, // 0 = follow system, 1 = light, 2 = dark
    fontFamily: '',
    cryptoPasswords: [],
    sendPasswordIndex: 0,
    cryptoPassword: '',
    glassEnabled: true,
    glassQuality: 60,
    /*
     * Backdrop blur strength, in CSS pixels of Gaussian sigma. 0 selects the
     * renderer's sharp path (the lens samples the full-resolution desktop crop).
     *
     * This is not decoration: blur is what stops a window *behind* the app from
     * reading as a second UI stacked on ours. At 0 the backdrop is pixel-sharp
     * and text behind the window shows through at full contrast, which looks
     * exactly like a doubled image.
     */
    glassBlurSigma: 0,

    /*
     * Plain-theme background: null, or the absolute path of an image the user
     * picked.
     *
     * Not the desktop wallpaper. That was the first attempt and it never
     * rendered — the request for the image simply never completed, through a
     * dedicated scheme and through tcpcache both. Letting someone choose a
     * picture sidesteps that, and it is the better feature anyway: a backdrop
     * you picked reads better behind messages than whatever happens to be on
     * the desktop.
     */
    plainBackgroundPath: null,

    /*
     * How the picture meets the window, mirroring the desktop's own fit modes.
     * Stored as the CSS object-fit keyword so nothing has to translate it.
     */
    plainBackgroundFit: 'cover',

    /** Recently chosen pictures, newest first. Paths only; thumbnails are built on demand. */
    plainBackgroundRecent: [],

    zoom: 1,
    // Electron-only additions (ignored by the C# build).
    windowBounds: null,
    glassTint: null,
  }
}

class Settings {
  constructor(raw) {
    Object.assign(this, defaults())
    if (raw && typeof raw === 'object') {
      for (const key of Object.keys(defaults())) {
        if (raw[key] !== undefined && raw[key] !== null) this[key] = raw[key]
      }
    }
    this.normalize()
  }

  /** Missing/invalid values fall back, and old default folders migrate once. */
  normalize() {
    if (typeof this.nwUrl !== 'string' || !/^https?:\/\//i.test(this.nwUrl.trim())) {
      this.nwUrl = DEFAULT_NW_URL
    }
    this.nwUrl = this.nwUrl.trim().replace(/\/+$/g, '')
    if (typeof this.nwPassword !== 'string') this.nwPassword = ''

    if (typeof this.chatFolder !== 'string' || this.chatFolder.trim() === '') {
      this.chatFolder = DEFAULT_CHAT_FOLDER
    }
    this.chatFolder = this.chatFolder.trim().replace(/^\/+|\/+$/g, '')

    if (!this.folderMigratedV113) {
      this.folderMigratedV113 = true
      if (
        this.chatFolder === LEGACY_DEFAULT_CHAT_FOLDER ||
        this.chatFolder === LEGACY_SHARED_CHAT_FOLDER
      ) {
        this.chatFolder = DEFAULT_CHAT_FOLDER
      }
    }

    if (typeof this.nickname !== 'string') this.nickname = ''
    this.pollSeconds = Math.round(clamp(this.pollSeconds, POLL_MIN, POLL_MAX, 3))
    this.historyDays = Math.round(clamp(this.historyDays, HISTORY_MIN, HISTORY_MAX, 7))
    this.autoScroll = this.autoScroll !== false
    this.theme = [0, 1, 2].includes(Number(this.theme)) ? Number(this.theme) : 0
    if (typeof this.fontFamily !== 'string') this.fontFamily = ''
    this.glassEnabled = this.glassEnabled !== false

    this.glassQuality = Math.round(clamp(this.glassQuality, 0, 100, 60))
    this.glassBlurSigma = clamp(this.glassBlurSigma, 0, 20, 0)
    // Anything that is not a usable path reads as "no background chosen".
    if (typeof this.plainBackgroundPath !== 'string' || this.plainBackgroundPath === '') {
      this.plainBackgroundPath = null
    }
    this.plainBackgroundFit = FITS.includes(this.plainBackgroundFit) ? this.plainBackgroundFit : 'cover'
    this.plainBackgroundRecent = Array.isArray(this.plainBackgroundRecent)
      ? this.plainBackgroundRecent.filter((p) => typeof p === 'string' && p !== '').slice(0, RECENT_MAX)
      : []
    this.zoom = clampZoom(typeof this.zoom === 'number' ? this.zoom : 1)

    // 11.2 moved from a single password to an ordered list. Blank entries are
    // meaningful: a blank entry means "send in the clear" while the remaining
    // passwords still take part in decryption.
    const cleaned = []
    for (const raw of Array.isArray(this.cryptoPasswords) ? this.cryptoPasswords : []) {
      const p = typeof raw === 'string' ? raw.trim() : ''
      if (!cleaned.includes(p)) cleaned.push(p)
    }
    this.cryptoPasswords = cleaned
    if (this.cryptoPasswords.length === 0 && typeof this.cryptoPassword === 'string' &&
        this.cryptoPassword.trim() !== '') {
      this.cryptoPasswords.push(this.cryptoPassword.trim())
    }
    this.sendPasswordIndex =
      this.cryptoPasswords.length === 0
        ? 0
        : Math.round(clamp(this.sendPasswordIndex, 0, this.cryptoPasswords.length - 1, 0))
    // Older builds read this field; keep it in sync for downgrades.
    this.cryptoPassword = this.sendPassword

    if (this.windowBounds && typeof this.windowBounds === 'object') {
      const b = this.windowBounds
      this.windowBounds = {
        x: Number.isFinite(b.x) ? b.x : undefined,
        y: Number.isFinite(b.y) ? b.y : undefined,
        width: Math.max(680, clamp(b.width, 680, 10000, 1107)),
        height: Math.max(480, clamp(b.height, 480, 10000, 744)),
      }
    } else {
      this.windowBounds = null
    }
    return this
  }

  /** Password used to encrypt outgoing messages ('' = send in the clear). */
  get sendPassword() {
    if (this.cryptoPasswords.length === 0) return ''
    return this.cryptoPasswords[this.sendPasswordIndex] ?? ''
  }

  /** Send password first, then the rest of the list, de-duplicated. */
  get decryptCandidates() {
    const list = []
    const send = this.sendPassword
    if (send.length > 0) list.push(send)
    for (const p of this.cryptoPasswords) {
      if (p.length > 0 && !list.includes(p)) list.push(p)
    }
    return list
  }

  /** Whether outgoing messages will be encrypted at all. */
  get encryptionEnabled() {
    return this.sendPassword.length > 0
  }

  toJSON() {
    const out = {}
    // Preserve the C# field order so diffs between the two apps stay readable.
    for (const key of Object.keys(defaults())) out[key] = this[key]
    return out
  }

  save() {
    try {
      ensureDataDir()
      fs.writeFileSync(settingsFile(), JSON.stringify(this.toJSON(), null, 2), 'utf8')
      return true
    } catch (err) {
      log.error('settings save failed', err)
      return false
    }
  }

  /** Shallow patch + re-normalise + persist. Returns the settings. */
  update(patch) {
    if (patch && typeof patch === 'object') {
      for (const key of Object.keys(defaults())) {
        if (patch[key] !== undefined) this[key] = patch[key]
      }
    }
    this.normalize()
    this.save()
    return this
  }

  /** The subset shipped to the renderer (never exposes more than it needs). */
  toRenderer() {
    return {
      nwUrl: this.nwUrl,
      chatFolder: this.chatFolder,
      // The password itself never leaves the main process; the dialog only needs
      // to know whether one is set, so the field can show a placeholder.
      hasNwPassword: this.nwPassword !== '',
      nickname: this.nickname,
      pollSeconds: this.pollSeconds,
      historyDays: this.historyDays,
      autoScroll: this.autoScroll,
      theme: this.theme,
      fontFamily: this.fontFamily,
      cryptoPasswords: this.cryptoPasswords.slice(),
      sendPasswordIndex: this.sendPasswordIndex,
      encryptionEnabled: this.encryptionEnabled,
      glassEnabled: this.glassEnabled,
      glassQuality: this.glassQuality,
      glassBlurSigma: this.glassBlurSigma,
      zoom: this.zoom,
    }
  }
}

function load() {
  const file = settingsFile()
  try {
    if (fs.existsSync(file)) {
      return new Settings(JSON.parse(fs.readFileSync(file, 'utf8')))
    }
  } catch (err) {
    log.error('settings load failed, using defaults', err)
  }
  return new Settings(null)
}

module.exports = {
  Settings,
  load,
  defaults,
  clampZoom,
  DEFAULT_CHAT_FOLDER,
  DEFAULT_NW_URL,
  LEGACY_SHARED_CHAT_FOLDER,
  LEGACY_DEFAULT_CHAT_FOLDER,
  ZOOM_MIN,
  ZOOM_MAX,
  ZOOM_STEP,
  BASE_FONT_SIZE,
  POLL_MIN,
  POLL_MAX,
  HISTORY_MIN,
  HISTORY_MAX,
}
