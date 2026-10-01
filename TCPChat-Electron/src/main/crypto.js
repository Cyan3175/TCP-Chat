'use strict'

/**
 * End-to-end encryption of message bodies (AES-256-GCM), wire-compatible with
 * the C# build's `MessageCrypto` / `MessageCipher`.
 *
 * Contract (must not change without breaking interop with TCP Chat ≤ 11.7):
 *   * key    = PBKDF2-HMAC-SHA256(password, salt, 100_000, 32 bytes)
 *     salt   = SHA256(UTF8("TCPChat10/v2|crypto|" + chatFolder))[0..16]
 *              — fixed per chat folder, so one session derives a key once and a
 *                copy of the log moved to another folder stays unreadable.
 *   * envelope = "AESGCM1:" + base64(nonce(12) || ciphertext || tag(16)) in the
 *                message JSON's `enc` field.
 *   * AAD    = the remote *file name* (e.g. `msg_1712345678901_ab12cd34.json`),
 *              so a renamed or relocated ciphertext fails authentication.
 *   * payload = UTF8 JSON `{from, text, quote, attach}` with nulls omitted,
 *              escaping equivalent to System.Text.Json's UnsafeRelaxedJsonEscaping.
 *   * attachments are encrypted as raw `nonce || ciphertext || tag` with the
 *     attachment file name as AAD.
 *
 * Nickname and timestamp stay in the clear — receivers need them to render who
 * sent what and when.
 */

const crypto = require('crypto')

const PREFIX = 'AESGCM1:'
const SALT_LENGTH = 16
const NONCE_LENGTH = 12
const TAG_LENGTH = 16
const KEY_LENGTH = 32
const ITERATIONS = 100_000
const SALT_LABEL = 'TCPChat10/v2|crypto|'

/** Derived keys are cached: PBKDF2 with 100k iterations is deliberately slow. */
const keyCache = new Map()

function cacheKey(password, saltSeed) {
  return `${saltSeed}\u0000${password}`
}

function deriveKeySync(password, saltSeed) {
  const material = Buffer.from(SALT_LABEL + (saltSeed ?? ''), 'utf8')
  const salt = crypto.createHash('sha256').update(material).digest().subarray(0, SALT_LENGTH)
  return crypto.pbkdf2Sync(password ?? '', salt, ITERATIONS, KEY_LENGTH, 'sha256')
}

/**
 * Derive (or reuse) the 32-byte key for a password in a chat folder.
 * Async so the 100k-iteration derivation never blocks the UI thread; the result
 * is memoised for the lifetime of the process.
 */
function deriveKey(password, saltSeed) {
  const id = cacheKey(password, saltSeed)
  const cached = keyCache.get(id)
  if (cached) return Promise.resolve(cached)
  return new Promise((resolve, reject) => {
    const material = Buffer.from(SALT_LABEL + (saltSeed ?? ''), 'utf8')
    const salt = crypto.createHash('sha256').update(material).digest().subarray(0, SALT_LENGTH)
    crypto.pbkdf2(password ?? '', salt, ITERATIONS, KEY_LENGTH, 'sha256', (err, key) => {
      if (err) return reject(err)
      keyCache.set(id, key)
      resolve(key)
    })
  })
}

/** Encrypt text into a `AESGCM1:` envelope. */
function encryptText(key, aad, plaintext) {
  const nonce = crypto.randomBytes(NONCE_LENGTH)
  const cipher = crypto.createCipheriv('aes-256-gcm', key, nonce, { authTagLength: TAG_LENGTH })
  cipher.setAAD(Buffer.from(aad, 'utf8'))
  const ct = Buffer.concat([cipher.update(Buffer.from(plaintext, 'utf8')), cipher.final()])
  const tag = cipher.getAuthTag()
  return PREFIX + Buffer.concat([nonce, ct, tag]).toString('base64')
}

/** Encrypt bytes into raw `nonce || ciphertext || tag` (attachments, voice notes). */
function encryptBytes(key, aad, plain) {
  const nonce = crypto.randomBytes(NONCE_LENGTH)
  const cipher = crypto.createCipheriv('aes-256-gcm', key, nonce, { authTagLength: TAG_LENGTH })
  cipher.setAAD(Buffer.from(aad, 'utf8'))
  const ct = Buffer.concat([cipher.update(plain), cipher.final()])
  return Buffer.concat([nonce, ct, cipher.getAuthTag()])
}

/** Decrypt a `AESGCM1:` envelope; returns null when the password or file name is wrong. */
function tryDecryptText(key, aad, envelope) {
  if (!envelope || typeof envelope !== 'string') return null
  if (!envelope.startsWith(PREFIX)) return null
  let buf
  try {
    buf = Buffer.from(envelope.slice(PREFIX.length), 'base64')
  } catch {
    return null
  }
  if (buf.length < NONCE_LENGTH + TAG_LENGTH) return null
  try {
    const nonce = buf.subarray(0, NONCE_LENGTH)
    const tag = buf.subarray(buf.length - TAG_LENGTH)
    const ct = buf.subarray(NONCE_LENGTH, buf.length - TAG_LENGTH)
    const decipher = crypto.createDecipheriv('aes-256-gcm', key, nonce, { authTagLength: TAG_LENGTH })
    decipher.setAAD(Buffer.from(aad, 'utf8'))
    decipher.setAuthTag(tag)
    return Buffer.concat([decipher.update(ct), decipher.final()]).toString('utf8')
  } catch {
    return null
  }
}

/** Decrypt raw attachment bytes; returns null when authentication fails. */
function tryDecryptBytes(key, aad, blob) {
  if (!blob || blob.length < NONCE_LENGTH + TAG_LENGTH) return null
  try {
    const nonce = blob.subarray(0, NONCE_LENGTH)
    const tag = blob.subarray(blob.length - TAG_LENGTH)
    const ct = blob.subarray(NONCE_LENGTH, blob.length - TAG_LENGTH)
    const decipher = crypto.createDecipheriv('aes-256-gcm', key, nonce, { authTagLength: TAG_LENGTH })
    decipher.setAAD(Buffer.from(aad, 'utf8'))
    decipher.setAuthTag(tag)
    return Buffer.concat([decipher.update(ct), decipher.final()])
  } catch {
    return null
  }
}

/**
 * Serialise a crypto payload the way System.Text.Json does with
 * `DefaultIgnoreCondition = WhenWritingNull`. Field order matters for byte-level
 * parity with the C# build, so it is written out explicitly rather than relying
 * on object key insertion order elsewhere in the code base.
 */
function serializePayload({ from, text, quote, attach }) {
  const out = { from: from ?? '', text: text ?? '' }
  if (quote !== null && quote !== undefined) out.quote = quote
  if (attach !== null && attach !== undefined) {
    const a = { name: attach.name ?? '', path: attach.path ?? '', size: attach.size ?? 0 }
    // `kind` and `dur` always travel: the C# reader treats 0 as "file".
    a.kind = attach.kind ?? 1
    a.dur = attach.durationMs ?? 0
    out.attach = a
  }
  return JSON.stringify(out)
}

/**
 * Normalise a decrypted payload into the internal attachment shape.
 * Tolerates the C# numeric types arriving as either numbers or strings.
 */
function parsePayload(json) {
  if (!json) return null
  let raw
  try {
    raw = JSON.parse(json)
  } catch {
    return null
  }
  if (!raw || typeof raw !== 'object') return null
  const out = {
    from: typeof raw.from === 'string' ? raw.from : '',
    text: typeof raw.text === 'string' ? raw.text : '',
    quote: typeof raw.quote === 'string' ? raw.quote : null,
    attach: null,
  }
  const a = raw.attach
  if (a && typeof a === 'object') {
    out.attach = {
      name: typeof a.name === 'string' ? a.name : '',
      path: typeof a.path === 'string' ? a.path : '',
      size: Number(a.size) || 0,
      kind: Number(a.kind) || 1,
      durationMs: Number(a.dur) || 0,
    }
  }
  return out
}

/**
 * One session's key material. Mirrors `MessageCipher`: the send password's key
 * is tried first, then every other non-empty password in list order.
 *
 * A blank entry in the list is meaningful — selecting it means "send in the
 * clear" while the remaining passwords still decrypt older ciphertext.
 */
class MessageCipher {
  constructor(keys, sendIndex, sendEnabled) {
    this._keys = keys
    this._sendIndex = sendIndex
    this._sendEnabled = sendEnabled
  }

  /**
   * @param {string[]} passwords password list (blank entries allowed)
   * @param {number} sendIndex  index into `passwords` used for encryption
   * @param {string} saltSeed   chat folder
   */
  static async create(passwords, sendIndex, saltSeed) {
    const all = (Array.isArray(passwords) ? passwords : []).map((p) =>
      typeof p === 'string' ? p.trim() : '',
    )
    if (all.length === 0) return new MessageCipher([], 0, false)

    const idx = Math.min(Math.max(0, sendIndex | 0), all.length - 1)
    const send = all[idx]
    const keys = []
    let sendEnabled = false
    if (send.length > 0) {
      keys.push(await deriveKey(send, saltSeed))
      sendEnabled = true
    }
    for (let i = 0; i < all.length; i++) {
      if (i === idx || all[i].length === 0) continue
      keys.push(await deriveKey(all[i], saltSeed))
    }
    return new MessageCipher(keys, idx, sendEnabled)
  }

  /** True when outgoing messages are encrypted. */
  get enabled() {
    return this._sendEnabled
  }

  /** 1-based number of the password used for sending (0 = encryption off). */
  get sendPasswordNumber() {
    return this._sendEnabled ? this._sendIndex + 1 : 0
  }

  /** How many passwords take part in decryption. */
  get passwordCount() {
    return this._keys.length
  }

  /** Encrypt body + quote + attachment metadata; null when encryption is off. */
  encryptPayload(aad, from, text, quote, attach) {
    if (!this._sendEnabled) return null
    return encryptText(
      this._keys[0],
      aad,
      serializePayload({ from, text, quote, attach }),
    )
  }

  /** Encrypt attachment bytes; null when encryption is off. */
  encryptBytes(aad, plain) {
    if (!this._sendEnabled) return null
    return encryptBytes(this._keys[0], aad, plain)
  }

  /** Try every password against an attachment blob. */
  tryDecryptBytes(aad, blob) {
    for (const key of this._keys) {
      const plain = tryDecryptBytes(key, aad, blob)
      if (plain) return plain
    }
    return null
  }

  /** Try every password against a `enc` envelope. */
  tryDecryptPayload(aad, envelope) {
    if (this._keys.length === 0) return null
    for (const key of this._keys) {
      const json = tryDecryptText(key, aad, envelope)
      if (json) {
        const payload = parsePayload(json)
        if (payload) return payload
      }
    }
    return null
  }
}

module.exports = {
  PREFIX,
  ITERATIONS,
  deriveKey,
  deriveKeySync,
  encryptText,
  encryptBytes,
  tryDecryptText,
  tryDecryptBytes,
  serializePayload,
  parsePayload,
  MessageCipher,
  _clearKeyCache: () => keyCache.clear(),
}
