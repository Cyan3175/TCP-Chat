'use strict'

/**
 * Chat core: a WebDAV directory used as a shared message board.
 *
 *   send    = PUT one `msg_*.json`
 *   receive = periodic `PROPFIND Depth: 1` plus GET for names not seen yet
 *
 * One message per file, so concurrent writers never collide (WebDAV has no
 * atomic append). File names carry a millisecond timestamp, which makes the
 * directory listing sort chronologically for free.
 *
 * Wire format is identical to the C# build, so both clients share one board:
 *   msg_<unixMs>_<8 hex>.json
 *   att_<unixMs>_<6 hex>_<sanitised original name>
 */

const crypto = require('crypto')
const fs = require('fs')
const fsp = require('fs/promises')
const path = require('path')
const { EventEmitter } = require('events')

const { combine } = require('./webdav')
const { NwClient } = require('./nw')
const { MessageCache, timeOf } = require('./message-cache')
const { MessageCipher } = require('./crypto')
const { cacheDir } = require('./paths')
const { log } = require('./logger')

/**
 * The transport.
 *
 * One address, one protocol. There were two for a while — a WebDAV host with the
 * nw service as a spare — but the WebDAV host is abandoned and answers 502, so
 * the choice is gone and NwClient is what the messages are read and written with.
 */
function makeClient(settings) {
  return new NwClient({ baseUrl: settings.nwUrl, password: settings.nwPassword })
}

/** How many message files are fetched concurrently during a sync round. */
const FETCH_CONCURRENCY = 4

const IMAGE_EXT = new Set(['.jpg', '.jpeg', '.png', '.gif', '.bmp', '.webp', '.ico', '.tif', '.tiff'])
const VIDEO_EXT = new Set(['.mp4', '.mkv', '.avi', '.mov', '.webm', '.wmv', '.flv', '.m4v'])
const AUDIO_EXT = new Set(['.mp3', '.wav', '.ogg', '.flac', '.m4a', '.aac', '.wma'])

const CONTENT_TYPES = {
  '.png': 'image/png',
  '.jpg': 'image/jpeg',
  '.jpeg': 'image/jpeg',
  '.gif': 'image/gif',
  '.bmp': 'image/bmp',
  '.webp': 'image/webp',
  '.mp4': 'video/mp4',
  '.mp3': 'audio/mpeg',
  '.wav': 'audio/wav',
  '.pdf': 'application/pdf',
  '.zip': 'application/zip',
  '.txt': 'text/plain; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
}

/** Attachment kinds, matching the C# enum. */
const KIND_FILE = 1
const KIND_IMAGE = 2
const KIND_VIDEO = 3
const KIND_AUDIO = 4
const KIND_VOICE = 5

/** Characters Windows forbids in a file name. */
const INVALID_FILE_CHARS = /["<>|:*?\\/\u0000-\u001f]/g

/** Replace dangerous characters and cap the length. Keeps the tail so the extension survives. */
function sanitizeFileName(name) {
  const cleaned = String(name ?? '').replace(INVALID_FILE_CHARS, '_')
  const capped = cleaned.length > 80 ? cleaned.slice(cleaned.length - 80) : cleaned
  return capped.trim() === '' ? 'file' : capped
}

/** Guess 1=file 2=image 3=video 4=audio from an extension. */
function guessKind(ext) {
  const e = String(ext ?? '').toLowerCase()
  if (IMAGE_EXT.has(e)) return KIND_IMAGE
  if (VIDEO_EXT.has(e)) return KIND_VIDEO
  if (AUDIO_EXT.has(e)) return KIND_AUDIO
  return KIND_FILE
}

function guessContentType(ext) {
  return CONTENT_TYPES[String(ext ?? '').toLowerCase()] ?? 'application/octet-stream'
}

/** `1536` -> `1 KB`, matching the C# formatting (integer division). */
function human(bytes) {
  const n = Number(bytes) || 0
  if (n < 1024) return `${n} B`
  if (n < 1024 * 1024) return `${Math.trunc(n / 1024)} KB`
  if (n < 1024 * 1024 * 1024) return `${Math.trunc(n / (1024 * 1024))} MB`
  return `${Math.trunc(n / (1024 * 1024 * 1024))} GB`
}

/** Serialise a chat message for the wire, matching System.Text.Json + WhenWritingNull. */
function serializeMessage(msg) {
  const out = {
    v: msg.v ?? 1,
    id: msg.id ?? '',
    from: msg.from ?? '',
    time: msg.time ?? new Date().toISOString(),
    text: msg.text ?? '',
  }
  if (msg.quote !== null && msg.quote !== undefined) out.quote = msg.quote
  if (msg.attach !== null && msg.attach !== undefined) {
    out.attach = {
      name: msg.attach.name ?? '',
      path: msg.attach.path ?? '',
      size: msg.attach.size ?? 0,
      kind: msg.attach.kind ?? 1,
      dur: msg.attach.durationMs ?? 0,
    }
  }
  if (msg.enc !== null && msg.enc !== undefined) out.enc = msg.enc
  return JSON.stringify(out)
}

/** Parse a server-side message file into the internal shape. Returns null if unusable. */
function parseMessage(json) {
  let raw
  try {
    raw = JSON.parse(json)
  } catch {
    return null
  }
  if (!raw || typeof raw !== 'object') return null

  const attach = raw.attach && typeof raw.attach === 'object'
    ? {
        name: typeof raw.attach.name === 'string' ? raw.attach.name : '',
        path: typeof raw.attach.path === 'string' ? raw.attach.path : '',
        size: Number(raw.attach.size) || 0,
        kind: Number(raw.attach.kind) || 1,
        durationMs: Number(raw.attach.dur) || 0,
      }
    : null

  const time = (() => {
    const t = raw.time
    if (typeof t === 'string') {
      const d = new Date(t)
      if (!Number.isNaN(d.getTime())) return d.toISOString()
    }
    return new Date().toISOString()
  })()

  return {
    v: Number(raw.v) || 1,
    id: typeof raw.id === 'string' ? raw.id : '',
    from: typeof raw.from === 'string' ? raw.from : '',
    time,
    text: typeof raw.text === 'string' ? raw.text : '',
    quote: typeof raw.quote === 'string' ? raw.quote : null,
    attach,
    enc: typeof raw.enc === 'string' && raw.enc ? raw.enc : null,
    /*
     * A withdrawn message.
     *
     * The service the messages live on cannot delete a file, so a withdraw
     * overwrites the record in place with {"tombstone":true} and leaves it there.
     * Reading it back therefore yields a well-formed message with no text, which
     * would render as an empty bubble; the flag is what distinguishes "withdrawn"
     * from "arrived blank". Callers must drop it rather than show it.
     */
    tombstone: raw.tombstone === true,
  }
}

class ChatService extends EventEmitter {
  /**
   * @param {import('./settings').Settings} settings
   */
  constructor(settings) {
    super()
    this.settings = settings
    this.nickname = settings.nickname
    this.dav = makeClient(settings)
    /** @type {MessageCipher} */
    this.cipher = new MessageCipher([], 0, false)
    this._cache = null
    this._seen = new Set()
    this._deleted = new Set()
    this._undecryptable = 0
    this._timer = null
    this._running = false
    this._stopping = false
    this.lastSyncTime = null
  }

  get chatFolder() {
    return this.settings.chatFolder
  }

  get pollSeconds() {
    const n = Number(this.settings.pollSeconds) || 3
    return Math.min(120, Math.max(1, Math.round(n)))
  }

  get isRunning() {
    return this._running
  }

  get encryptionEnabled() {
    return this.cipher.enabled
  }

  get passwordCount() {
    return this.cipher.passwordCount
  }

  get sendPasswordNumber() {
    return this.cipher.sendPasswordNumber
  }

  get undecryptableCount() {
    return this._undecryptable
  }

  /** Lazily opened; the cache is per chat folder. */
  get cache() {
    if (!this._cache) this._cache = MessageCache.load(this.chatFolder)
    return this._cache
  }

  /** Salt seed is the chat folder alone: both sides read the same directory. */
  get cryptoSaltSeed() {
    return String(this.chatFolder ?? '').replace(/^\/+|\/+$/g, '')
  }

  _diag(message) {
    this.emit('diag', message)
  }

  // -------------------------------------------------------------------------
  // Key material
  // -------------------------------------------------------------------------

  /** (Re)build the cipher and forget what has been read so the next round re-examines everything. */
  async applyCryptoPasswords(passwords, sendIndex) {
    this.cipher = await MessageCipher.create(passwords, sendIndex, this.cryptoSaltSeed)
    this._seen.clear()
    this._deleted.clear()
    this._undecryptable = 0
    // Re-run the cache through the new keys right away: the UI has just been
    // cleared, and waiting for a full directory re-download would be needless.
    this.emitCachedMessages()
    this._emitCipherState()
  }

  /** Rebuild the cipher from the current settings (server URL / folder changes included). */
  async reload() {
    this.dav = makeClient(this.settings)
    this.nickname = this.settings.nickname
    this._cache = null
    await this.applyCryptoPasswords(
      this.settings.cryptoPasswords,
      this.settings.sendPasswordIndex,
    )
  }

  _emitCipherState() {
    this.emit('cipher-state', {
      encryptionEnabled: this.encryptionEnabled,
      passwordCount: this.passwordCount,
      sendPasswordNumber: this.sendPasswordNumber,
    })
  }

  // -------------------------------------------------------------------------
  // Connect
  // -------------------------------------------------------------------------

  /**
   * Probe connectivity and confirm the chat folder is usable.
   * The folder is deliberately *not* created: a typo should report "folder does
   * not exist" rather than silently litter someone's share.
   */
  async initialize() {
    try {
      const started = Date.now()
      if (!this.cipher.passwordCount && this.settings.cryptoPasswords.length > 0) {
        await this.applyCryptoPasswords(
          this.settings.cryptoPasswords,
          this.settings.sendPasswordIndex,
        )
      }

      let folderOk = false
      try {
        folderOk = (await this.dav.propFind(this.chatFolder, 0)).length > 0
      } catch {
        folderOk = false
      }

      if (!folderOk) {
        let serverOk = false
        try {
          serverOk = (await this.dav.propFind('', 0)).length > 0
        } catch {
          serverOk = false
        }
        this._diag(`聊天目录不可用: ${this.chatFolder}`)
        return {
          ok: false,
          message: serverOk
            ? `聊天目录不存在：${this.chatFolder}`
            : '无法访问服务器(请检查网络与服务器地址)',
        }
      }

      this._diag(
        `连接就绪, 耗时 ${Date.now() - started} ms` +
          (this.encryptionEnabled ? ' (端到端加密已启用)' : ' (未加密)'),
      )
      return { ok: true, message: '已连接' }
    } catch (err) {
      return { ok: false, message: `连接失败: ${err.message}` }
    }
  }

  // -------------------------------------------------------------------------
  // Sending
  // -------------------------------------------------------------------------

  /** `<unixMs>_<8 hex>` — globally unique and chronologically sortable. */
  _newId() {
    return `${Date.now()}_${crypto.randomBytes(4).toString('hex')}`
  }

  /** Send a text message. Returns the local message object (with a status on failure). */
  async sendText(text, quote = null) {
    const trimmed = String(text ?? '').trim()
    if (trimmed.length === 0) return null
    return this._send(trimmed, quote, null)
  }

  /**
   * Upload a local file and post a message referencing it.
   * Images, video, audio and voice notes all travel this path.
   */
  async sendFile(localPath, kind = 0, durationMs = 0) {
    if (!fs.existsSync(localPath)) return null
    const stat = fs.statSync(localPath)
    const ext = path.extname(localPath)
    const originalName = path.basename(localPath)
    const safeName = sanitizeFileName(originalName)
    const remoteName = `att_${Date.now()}_${crypto.randomBytes(3).toString('hex')}_${safeName}`
    const remotePath = combine(this.chatFolder, remoteName)

    this.emit('status', `正在上传 ${safeName} (${human(stat.size)}) …`)
    try {
      let bytes = await fsp.readFile(localPath)
      if (this.cipher.enabled) {
        // The attachment body is encrypted too, with the file name as AAD so
        // neither the name nor the size leaks in the clear.
        const encrypted = this.cipher.encryptBytes(remoteName, bytes)
        if (!encrypted) {
          this.emit('error', '附件加密失败')
          return null
        }
        bytes = encrypted
      }
      if (!(await this.dav.put(remotePath, bytes, guessContentType(ext), 60))) {
        this.emit('error', `附件上传失败 (${this.dav.lastPutStatus}): ${safeName}`)
        return null
      }
    } catch (err) {
      this.emit('error', `附件上传失败: ${err.message}`)
      return null
    }

    const attach = {
      name: originalName,
      path: remotePath,
      size: stat.size,
      kind: kind > 0 ? kind : guessKind(ext),
      durationMs: Math.max(0, durationMs | 0),
    }
    this._diag(`附件已上传: ${remoteName} (${human(stat.size)}, kind=${attach.kind})`)
    this.emit('status', `已上传 ${safeName}`)
    return this._send('', null, attach)
  }

  /** Download an attachment into the local cache; null when it cannot be decrypted. */
  async downloadAttachment(msg) {
    const attach = msg?.attach
    if (!attach) return null
    const remoteFileName = path.basename(attach.path)
    const localPath = path.join(cacheDir(), remoteFileName)
    try {
      if (fs.existsSync(localPath) && fs.statSync(localPath).size === attach.size) {
        return localPath
      }
    } catch {
      /* fall through and re-download */
    }

    try {
      let bytes = await this.dav.getBytes(attach.path, 120)
      if (bytes === null) return null

      if (this.cipher.enabled) {
        const plain = this.cipher.tryDecryptBytes(remoteFileName, bytes)
        if (!plain) {
          this._undecryptable += 1
          this.emit('error', `附件解密失败(密码与发送方不一致): ${attach.name}`)
          return null
        }
        bytes = plain
      }
      await fsp.writeFile(localPath, bytes)
      return localPath
    } catch (err) {
      this._diag(`附件下载失败: ${err.message}`)
      return null
    }
  }

  /** Shared send path for text and attachment messages. */
  async _send(text, quote, attach) {
    const cleanQuote = typeof quote === 'string' && quote.trim() !== '' ? quote : null
    const local = {
      v: 1,
      id: this._newId(),
      from: this.nickname,
      time: new Date().toISOString(),
      text,
      quote: cleanQuote,
      attach,
      enc: null,
    }
    const name = `msg_${local.id}.json`
    local.remoteName = name
    local.pending = true
    // Mark as known up front so our own message is not fetched back this round.
    this._seen.add(name)

    try {
      let wire
      if (this.cipher.enabled) {
        local.enc = this.cipher.encryptPayload(name, this.nickname, text, cleanQuote, attach)
        local.v = 2
        // The wire copy carries no plaintext: body, quote and attachment
        // metadata all travel inside `enc`.
        wire = { v: 2, id: local.id, from: local.from, time: local.time, text: '', enc: local.enc }
      } else {
        wire = {
          v: 1,
          id: local.id,
          from: local.from,
          time: local.time,
          text,
          quote: cleanQuote,
          attach,
        }
      }

      const json = serializeMessage(wire)
      const ok = await this._putWithRetry(combine(this.chatFolder, name), json)
      if (ok) {
        local.pending = false
        local.status = null
        this.cache.put(name, json)
        this.cache.save()
      } else {
        local.status = `发送失败 (${this.dav.lastPutStatus})`
      }
    } catch (err) {
      local.status = `发送失败: ${err.message}`
    }
    return local
  }

  /**
   * Upload a message file, retrying twice.
   * The school WebDAV occasionally 5xx's or hangs a request, so a single failure
   * should not surface on the bubble.
   */
  async _putWithRetry(remotePath, json) {
    for (let attempt = 1; ; attempt++) {
      try {
        if (await this.dav.putText(remotePath, json)) return true
        if (attempt >= 3) return false
        this._diag(`PUT 第 ${attempt} 次失败 (${this.dav.lastPutStatus}), 重试…`)
      } catch (err) {
        if (attempt >= 3) {
          this._diag(`PUT 连续失败: ${err.message}`)
          return false
        }
        this._diag(`PUT 第 ${attempt} 次异常 (${err.message}), 重试…`)
      }
      await new Promise((resolve) => setTimeout(resolve, 500 * attempt))
    }
  }

  /** Withdraw one of our own messages, deleting its attachment as well. */
  async deleteMessage(msg) {
    if (!msg?.remoteName) return false
    try {
      const ok = await this.dav.remove(combine(this.chatFolder, msg.remoteName))
      if (!ok) return false
      if (msg.attach?.path) await this.dav.remove(msg.attach.path)
      this._deleted.add(msg.remoteName)
      this.cache.remove(msg.remoteName)
      this.cache.save()
      this.emit('message-removed', msg.remoteName)
      return true
    } catch (err) {
      this._diag(`撤回失败: ${err.message}`)
      return false
    }
  }

  // -------------------------------------------------------------------------
  // Polling
  // -------------------------------------------------------------------------

  /** Show cached messages first, then start the poll loop. */
  async start() {
    if (this._running) return
    this.emitCachedMessages()
    this._running = true
    this._stopping = false
    this._loop().catch((err) => {
      if (!this._stopping) this.emit('error', `轮询异常: ${err.message}`)
    })
  }

  stop() {
    this._stopping = true
    this._running = false
    if (this._timer) {
      clearTimeout(this._timer)
      this._timer = null
    }
  }

  async _loop() {
    // First round runs immediately; afterwards the period is measured from the
    // start of a round so a slow sync does not stretch the interval.
    await this.syncOnce()
    while (!this._stopping) {
      const started = Date.now()
      await this.syncOnce()
      const remaining = this.pollSeconds * 1000 - (Date.now() - started)
      await new Promise((resolve) => {
        this._timer = setTimeout(resolve, Math.max(0, remaining))
      })
      this._timer = null
    }
  }

  /** Replay locally cached messages, oldest first. */
  emitCachedMessages() {
    try {
      const cutoff = new Date(Date.now() - Math.max(1, this.settings.historyDays) * 86400000)
      let emitted = 0
      for (const name of this.cache.names()) {
        const time = timeOf(name)
        if (time && time < cutoff) continue

        const json = this.cache.get(name)
        if (!json) continue

        try {
          const msg = parseMessage(json)
          if (!msg) continue
          // Withdrawn while we were away: drop it rather than replay a blank.
          if (msg.tombstone) continue
          if (!msg.enc && !msg.attach && !msg.text?.trim() && !msg.quote?.trim()) continue

          if (msg.enc) this._decrypt(msg, name)
          msg.remoteName = name
          msg.isSelf = this.nickname !== '' && msg.from === this.nickname
          /*
           * Marked, so the window can tell a replay from an arrival.
           *
           * These are messages that were already here last time; the only new
           * thing about them is that the app has started. Without this they are
           * indistinguishable from a message that just came in, and every launch
           * raises a notification per cached message.
           */
          msg.fromCache = true
          this._seen.add(name)
          this.emit('message-added', msg)
          emitted += 1
        } catch {
          /* one bad entry must not stop the replay */
        }
      }
      if (emitted > 0) this._diag(`本地缓存: 直接显示 ${emitted} 条`)
    } catch (err) {
      this._diag(`读本地缓存失败: ${err.message}`)
    }
  }

  /** One sync round: list, find new names, download, parse, emit. */
  async syncOnce() {
    let entries
    try {
      entries = await this.dav.propFind(this.chatFolder, 1)
    } catch (err) {
      this.emit('error', `列目录失败: ${err.message}`)
      return
    }

    this.lastSyncTime = Date.now()
    const cutoff = new Date(Date.now() - Math.max(1, this.settings.historyDays) * 86400000)

    const files = entries
      .filter(
        (e) =>
          !e.isCollection &&
          /^msg_/i.test(e.name) &&
          /\.json$/i.test(e.name),
      )
      .filter((e) => e.lastModified === null || e.lastModified >= cutoff)
      .sort((a, b) => (a.name < b.name ? -1 : a.name > b.name ? 1 : 0))

    // A message that vanished from the server was withdrawn: drop it locally too.
    const gone = this.cache.pruneMissing(
      files.map((e) => e.name),
      cutoff,
    )
    for (const name of gone) {
      this._seen.delete(name)
      this.emit('message-removed', name)
      this._diag(`对方撤回, 本地移除 ${name}`)
    }

    /*
     * What to fetch: everything not already known, plus anything known whose
     * fingerprint has moved.
     *
     * The second half is the point. A withdraw cannot delete the file — the
     * service has no delete — so it rewrites the record in place. The name stays
     * the same, which means a plain "have I seen this name" test answers yes
     * forever and the cached copy is replayed on every launch: the other side
     * withdraws a message and it never goes away here. The size and mtime from
     * the listing are what tell the two apart.
     */
    const changed = new Set()
    for (const e of files) {
      if (!this._seen.has(e.name) && !this.cache.get(e.name)) continue
      const recorded = this.cache.fingerprint(e.name)
      const remote = MessageCache.fingerprintOf(e)
      // A name with no recorded fingerprint is not evidence of a change; it is
      // a cache written before fingerprints existed, and re-reading all of those
      // once is fine but calling them all changed is not.
      if (recorded && remote && recorded !== remote) changed.add(e.name)
    }

    const fresh = files.filter(
      (e) => (!this._seen.has(e.name) && !this._deleted.has(e.name)) || changed.has(e.name),
    )
    if (changed.size > 0) {
      this._diag(`比对: ${changed.size} 个文件在服务器上变了，重新读取`)
    }
    /*
     * Also to the log, not only to the status bar.
     *
     * The comparison is the one part of a sync whose correctness is invisible
     * when it works and silent when it does not: a fingerprint that never
     * differs looks exactly like a service where nothing ever changes. Having
     * the counts in the log is what makes "it fetched 196 the second time too"
     * something you can see.
     */
    log.info(
      `sync: listed=${entries.length} messages=${files.length} fetch=${fresh.length}` +
        ` changed=${changed.size} withdrawn=${gone.length} cached=${this.cache.count}`,
    )
    this._diag(
      `同步: 目录 ${entries.length} 项 / 消息 ${files.length} 个 / 待取 ${fresh.length} 个` +
        (changed.size > 0 ? ` (其中变化 ${changed.size} 个)` : '') +
        (gone.length > 0 ? ` / 撤回 ${gone.length} 个` : '') +
        ` / ${this.cache.describe()}`,
    )

    if (fresh.length === 0) {
      this.cache.save()
      this.emit('synced', this.lastSyncTime)
      return
    }

    const started = Date.now()
    // A handful of parallel GETs: dozens of history files in series would take
    // tens of seconds. A failed file stays unseen and is retried next round.
    await this._forEachLimited(fresh, FETCH_CONCURRENCY, async (entry) => {
      try {
        const fullPath = combine(this.chatFolder, entry.name)
        const text = await this.dav.getString(fullPath, 25)
        if (!text || !text.trim()) {
          this._seen.add(entry.name)
          return
        }
        const msg = parseMessage(text)
        if (!msg) {
          this._seen.add(entry.name)
          return
        }
        /*
         * Withdrawn by the other side.
         *
         * The record is still there — the transport cannot delete — so it keeps
         * coming back in every listing. Mark it seen and withdrawn so it is not
         * fetched or shown again, and tell the UI to drop it if it is already up.
         */
        if (msg.tombstone) {
          this._seen.add(entry.name)
          this._deleted.add(entry.name)
          this.cache.remove(entry.name)
          this.emit('message-removed', entry.name)
          this._diag(`对方撤回(墓碑), 本地移除 ${entry.name}`)
          return
        }
        // Drop messages with neither content nor attachment, so the list never
        // shows empty bubbles (this is what 10.0 attachment messages look like).
        if (!msg.enc && !msg.attach && !msg.text?.trim() && !msg.quote?.trim()) {
          this._diag(`跳过没有正文的消息(10.0 的附件消息?): ${entry.name}`)
          this._seen.add(entry.name)
          return
        }

        if (msg.enc) this._decrypt(msg, entry.name)

        this._seen.add(entry.name)
        this.cache.put(entry.name, text)
        // Record what the server said, so the next round can tell a file that
        // changed from one that is merely already known.
        this.cache.setFingerprint(entry.name, MessageCache.fingerprintOf(entry))
        msg.remoteName = entry.name
        msg.isSelf = this.nickname !== '' && msg.from === this.nickname
        this.emit('message-added', msg)
      } catch (err) {
        // A single corrupt file is reported and left unseen for a retry.
        this.emit('error', `读取 ${entry.name} 失败: ${err.message}`)
      }
    })

    this.cache.save()
    this._diag(`同步完成: ${fresh.length} 个文件, 耗时 ${Date.now() - started} ms`)
    this.emit('synced', this.lastSyncTime)
  }

  /** Bounded-concurrency map. */
  async _forEachLimited(items, limit, worker) {
    let cursor = 0
    const runners = new Array(Math.min(limit, items.length)).fill(null).map(async () => {
      for (;;) {
        const index = cursor++
        if (index >= items.length) return
        await worker(items[index])
      }
    })
    await Promise.all(runners)
  }

  /**
   * Decrypt a message body in place. The file name is the AAD, so a mismatched
   * password (or a renamed/tampered ciphertext) fails here and the UI shows a
   * placeholder rather than mojibake.
   */
  _decrypt(msg, remoteName) {
    const payload = this.cipher.tryDecryptPayload(remoteName, msg.enc)
    if (!payload) {
      this._undecryptable += 1
      msg.decryptFailed = true
      msg.text = ''
      msg.quote = null
      this._diag(`解密失败(密码不一致?): ${remoteName}`)
      return
    }
    msg.text = payload.text
    msg.quote = payload.quote
    msg.attach = payload.attach
    if (payload.from) msg.from = payload.from
  }

  dispose() {
    this.stop()
    this.cache.save()
  }
}

module.exports = {
  ChatService,
  sanitizeFileName,
  guessKind,
  guessContentType,
  human,
  serializeMessage,
  parseMessage,
  KIND_FILE,
  KIND_IMAGE,
  KIND_VIDEO,
  KIND_AUDIO,
  KIND_VOICE,
}
