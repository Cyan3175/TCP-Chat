'use strict'

/**
 * Local message cache — wire-compatible with the C# build's `MessageCache`.
 *
 *   %LOCALAPPDATA%\TCPChat\cache\msgs_<SHA256(chatFolder)[0..16] UPPERCASE>.json
 *   { "folder": "...", "savedAt": "...", "items": [ { "name": "...", "json": "..." } ] }
 *
 * The cached `json` is the *server's* copy — ciphertext included — so the cache
 * never holds more plaintext than the server does, and rotating the password
 * only means decrypting the same ciphertext differently.
 *
 * The file name hash must stay uppercase-hex to remain readable by the C# build,
 * so both applications share one cache per chat folder.
 */

const crypto = require('crypto')
const fs = require('fs')
const path = require('path')
const { cacheDir } = require('./paths')
const { log } = require('./logger')

/** `msgs_<16 uppercase hex chars>.json` for a chat folder. */
function pathFor(chatFolder) {
  const hash = crypto
    .createHash('sha256')
    .update(String(chatFolder ?? ''), 'utf8')
    .digest('hex')
    .slice(0, 16)
    .toUpperCase()
  return path.join(cacheDir(), `msgs_${hash}.json`)
}

/** Timestamp embedded in `msg_<unixMs>_xxxx.json`, or null if it is not a message file. */
function timeOf(name) {
  try {
    let s = String(name ?? '')
    if (!/^msg_/i.test(s)) return null
    s = s.slice(4)
    const underscore = s.indexOf('_')
    if (underscore > 0) s = s.slice(0, underscore)
    if (!/^\d+$/.test(s)) return null
    const ms = Number(s)
    if (!Number.isFinite(ms)) return null
    return new Date(ms)
  } catch {
    return null
  }
}

class MessageCache {
  constructor(folder, file) {
    this._folder = folder ?? ''
    this._path = file
    this._items = new Map()
    /*
     * name -> the server's size and mtime for that file, as a compact string.
     *
     * Kept beside the message rather than inside it so the stored JSON stays the
     * bytes the server sent. This is what makes a file that changed
     * distinguishable from one that is merely already known: a withdraw rewrites
     * the record in place, so its name never changes and only this moves.
     */
    this._prints = new Map()
    this._dirty = false
  }

  /** Load the cache for a chat folder. A corrupt or mismatched file reads as empty. */
  static load(chatFolder) {
    const file = pathFor(chatFolder)
    const cache = new MessageCache(chatFolder ?? '', file)
    try {
      if (!fs.existsSync(file)) return cache
      const parsed = JSON.parse(fs.readFileSync(file, 'utf8'))
      if (!parsed || typeof parsed !== 'object') return cache
      if (String(parsed.folder ?? '') !== String(chatFolder ?? '')) {
        // Folder changed under this hash: drop the contents and rewrite on save.
        cache._dirty = true
        return cache
      }
      for (const entry of Array.isArray(parsed.items) ? parsed.items : []) {
        if (entry && entry.name && entry.json) cache._items.set(entry.name, entry.json)
      }
      for (const entry of Array.isArray(parsed.prints) ? parsed.prints : []) {
        if (entry && entry.name && entry.print) cache._prints.set(entry.name, entry.print)
      }
    } catch (err) {
      log.warn('message cache unreadable, starting empty', err)
    }
    return cache
  }

  get dirty() {
    return this._dirty
  }

  get count() {
    return this._items.size
  }

  /** Cached message file names, sorted by name — which is to say by time. */
  names() {
    return [...this._items.keys()].sort()
  }

  get(name) {
    return this._items.has(name) ? this._items.get(name) : null
  }

  put(name, json) {
    if (!name || !json) return
    this._items.set(name, json)
    this._dirty = true
  }

  /** The recorded fingerprint for a name, or null when none was stored. */
  fingerprint(name) {
    return this._prints.has(name) ? this._prints.get(name) : null
  }

  /** Record what the server said about a file. */
  setFingerprint(name, print) {
    if (!name || !print) return
    if (this._prints.get(name) === print) return
    this._prints.set(name, print)
    this._dirty = true
  }

  /** `size:mtimeMs`, the comparable form of a listing entry. */
  static fingerprintOf(entry) {
    if (!entry) return ''
    const size = Number(entry.length) || 0
    const mtime = entry.lastModified instanceof Date ? entry.lastModified.getTime() : 0
    return `${size}:${mtime}`
  }

  remove(name) {
    if (!name) return
    this._prints.delete(name)
    if (this._items.delete(name)) this._dirty = true
  }

  clear() {
    if (this._items.size === 0) return
    this._items.clear()
    this._prints.clear()
    this._dirty = true
  }

  /**
   * Reconcile against the server listing and return names that disappeared.
   *
   * Two guards, both matching the C# build:
   *   * an empty `remoteNames` list is ignored — a server hiccup returning
   *     nothing must never wipe the local cache;
   *   * only names inside the history window count as withdrawn, older entries
   *     were never in sync scope to begin with.
   */
  pruneMissing(remoteNames, cutoff) {
    const remote = new Set([...remoteNames].map((n) => String(n).toLowerCase()))
    if (remote.size === 0) return []

    const gone = []
    for (const name of [...this._items.keys()]) {
      if (remote.has(name.toLowerCase())) continue
      const time = timeOf(name)
      if (!time || time < cutoff) continue
      this._items.delete(name)
      this._prints.delete(name)
      gone.push(name)
    }
    if (gone.length > 0) this._dirty = true
    return gone.sort()
  }

  /** Flush to disk via a temp file + rename so a crash cannot leave a torn cache. */
  save() {
    try {
      if (!this._dirty) return
      const payload = {
        folder: this._folder,
        savedAt: new Date().toISOString(),
        items: [...this._items.entries()].map(([name, json]) => ({ name, json })),
        prints: [...this._prints.entries()].map(([name, print]) => ({ name, print })),
      }
      this._dirty = false
      fs.mkdirSync(path.dirname(this._path), { recursive: true })
      const tmp = `${this._path}.tmp`
      fs.writeFileSync(tmp, JSON.stringify(payload), 'utf8')
      fs.renameSync(tmp, this._path)
    } catch (err) {
      // A failed write only costs a re-download next launch.
      log.warn('message cache save failed', err)
    }
  }

  describe() {
    return `本地缓存 ${this._items.size} 条`
  }
}

module.exports = { MessageCache, pathFor, timeOf }
