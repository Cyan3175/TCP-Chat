'use strict'

/**
 * Authoritative, ordered message list held in the main process.
 *
 * The renderer is treated as a view: it boots by asking for the whole list and
 * then applies deltas. That keeps the UI correct across a renderer reload, and
 * means toggling the glass backdrop (which relayouts everything) never loses
 * scroll position or pending state.
 *
 * Ordering is by remote file name, which embeds the sender's millisecond
 * timestamp — the same chronological order the directory listing and the local
 * cache already use.
 */

class MessageStore {
  constructor() {
    /** @type {Map<string, object>} keyed by remote file name */
    this._byName = new Map()
    this._dirty = false
  }

  /** Insert or replace, returning true when the list actually changed. */
  upsert(msg) {
    const name = msg?.remoteName
    if (!name) return false
    const existing = this._byName.get(name)
    this._byName.set(name, { ...existing, ...msg, remoteName: name })
    this._dirty = true
    return true
  }

  remove(name) {
    const changed = this._byName.delete(name)
    if (changed) this._dirty = true
    return changed
  }

  get(name) {
    return this._byName.get(name) ?? null
  }

  get size() {
    return this._byName.size
  }

  /** All messages in chronological (file-name) order. */
  list() {
    return [...this._byName.values()].sort((a, b) =>
      a.remoteName < b.remoteName ? -1 : a.remoteName > b.remoteName ? 1 : 0,
    )
  }

  clear() {
    this._byName.clear()
    this._dirty = true
  }

  /** Mark every entry as no longer pending (used after a reconnect). */
  markAllSettled() {
    for (const msg of this._byName.values()) msg.pending = false
  }
}

module.exports = { MessageStore }
