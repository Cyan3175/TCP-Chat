'use strict'

/**
 * Minimal WebDAV client (PROPFIND / GET / PUT / DELETE / MKCOL), matching the
 * behaviour of the C# build's `WebDavClient` under Node's global `fetch`.
 *
 * A focused XML reader is used instead of a general parser: DAV multistatus is a
 * small, regular grammar, and reading it by local name keeps us independent of
 * whatever namespace prefix a particular server decides to emit (D:, d:, lp1:,
 * or none at all).
 */

const { log } = require('./logger')
const DAV_NS = 'DAV:'
// Derived, not written down: a literal here goes stale silently, and this
// one had been advertising 12.0.0 while the app was on 12.2.
const USER_AGENT = `TCPChat/${require('../../package.json').version} (Electron)`

/**
 * Did the request fail to reach a host at all?
 *
 * Distinguished from an HTTP error because only this counts as "that server is
 * not available" — the case failover exists for. A 404 means something answered.
 */
function isConnectionError(err) {
  const code = err?.cause?.code || err?.code || ''
  if (code === 'ENOTFOUND' || code === 'ECONNREFUSED' || code === 'ECONNRESET') return true
  if (code === 'ETIMEDOUT' || code === 'EAI_AGAIN' || code === 'EHOSTUNREACH') return true
  if (code === 'UND_ERR_CONNECT_TIMEOUT' || code === 'UND_ERR_SOCKET') return true
  // Our own timeout, or anything the TLS layer refused to complete.
  if (err?.name === 'AbortError' || err?.name === 'TimeoutError') return true
  if (/fetch failed|socket hang up|network|certificate/i.test(String(err?.message ?? ''))) {
    return true
  }
  return false
}


// ---------------------------------------------------------------------------
// XML
// ---------------------------------------------------------------------------

const NAMED_ENTITIES = {
  amp: '&',
  lt: '<',
  gt: '>',
  quot: '"',
  apos: "'",
  nbsp: '\u00a0',
}

function decodeEntities(text) {
  if (!text || text.indexOf('&') === -1) return text
  return text.replace(/&(#x?[0-9a-fA-F]+|[a-zA-Z][a-zA-Z0-9]*);/g, (match, body) => {
    if (body[0] === '#') {
      const isHex = body[1] === 'x' || body[1] === 'X'
      const code = parseInt(isHex ? body.slice(2) : body.slice(1), isHex ? 16 : 10)
      if (!Number.isFinite(code) || code < 0 || code > 0x10ffff) return match
      try {
        return String.fromCodePoint(code)
      } catch {
        return match
      }
    }
    const named = NAMED_ENTITIES[body.toLowerCase()]
    return named === undefined ? match : named
  })
}

/** Local name of a possibly prefixed tag: `D:getcontentlength` -> `getcontentlength`. */
function localName(qualified) {
  const colon = qualified.indexOf(':')
  return (colon === -1 ? qualified : qualified.slice(colon + 1)).toLowerCase()
}

/**
 * Parse an XML document into a lightweight element tree.
 * Each node: `{ name, qualified, attrs, children, text }`.
 * Self-closing tags, comments, processing instructions and CDATA are handled.
 */
function parseXml(source) {
  const root = { name: '#document', qualified: '#document', attrs: {}, children: [], text: '' }
  if (typeof source !== 'string' || source.length === 0) return root

  const stack = [root]
  let i = 0
  const len = source.length

  while (i < len) {
    const lt = source.indexOf('<', i)
    if (lt === -1) {
      appendText(stack[stack.length - 1], source.slice(i))
      break
    }
    if (lt > i) appendText(stack[stack.length - 1], source.slice(i, lt))

    // <!-- comment -->, <![CDATA[...]]>, <?pi?>, <!DOCTYPE ...>
    if (source.startsWith('<!--', lt)) {
      const end = source.indexOf('-->', lt + 4)
      i = end === -1 ? len : end + 3
      continue
    }
    if (source.startsWith('<![CDATA[', lt)) {
      const end = source.indexOf(']]>', lt + 9)
      const chunk = end === -1 ? source.slice(lt + 9) : source.slice(lt + 9, end)
      appendText(stack[stack.length - 1], chunk, true)
      i = end === -1 ? len : end + 3
      continue
    }
    if (source.startsWith('<?', lt)) {
      const end = source.indexOf('?>', lt + 2)
      i = end === -1 ? len : end + 2
      continue
    }
    if (source.startsWith('<!', lt)) {
      const end = source.indexOf('>', lt + 2)
      i = end === -1 ? len : end + 1
      continue
    }

    const gt = findTagEnd(source, lt)
    if (gt === -1) {
      appendText(stack[stack.length - 1], source.slice(lt))
      break
    }

    const raw = source.slice(lt + 1, gt)
    if (raw.startsWith('/')) {
      if (stack.length > 1) stack.pop()
      i = gt + 1
      continue
    }

    const selfClosing = raw.endsWith('/')
    const body = selfClosing ? raw.slice(0, -1) : raw
    const { qualified, attrs } = parseTag(body)
    const node = {
      name: localName(qualified),
      qualified,
      attrs,
      children: [],
      text: '',
    }
    stack[stack.length - 1].children.push(node)
    if (!selfClosing) stack.push(node)
    i = gt + 1
  }

  return root
}

/** Find the `>` that closes a tag, skipping any inside quoted attribute values. */
function findTagEnd(source, start) {
  let quote = null
  for (let i = start + 1; i < source.length; i++) {
    const ch = source[i]
    if (quote) {
      if (ch === quote) quote = null
      continue
    }
    if (ch === '"' || ch === "'") {
      quote = ch
      continue
    }
    if (ch === '>') return i
  }
  return -1
}

function parseTag(body) {
  const attrs = {}
  let i = 0
  while (i < body.length && !/\s/.test(body[i])) i++
  const qualified = body.slice(0, i)
  const attrRe = /([^\s=/]+)\s*=\s*("([^"]*)"|'([^']*)')/g
  attrRe.lastIndex = i
  let m
  while ((m = attrRe.exec(body)) !== null) {
    attrs[localName(m[1])] = decodeEntities(m[3] !== undefined ? m[3] : m[4])
  }
  return { qualified, attrs }
}

function appendText(node, chunk, raw = false) {
  if (!chunk) return
  node.text += raw ? chunk : decodeEntities(chunk)
}

/** Depth-first search by local name. */
function findAll(node, name, out = []) {
  for (const child of node.children) {
    if (child.name === name) out.push(child)
    findAll(child, name, out)
  }
  return out
}

/** First direct child with the given local name. */
function child(node, name) {
  if (!node) return null
  for (const c of node.children) if (c.name === name) return c
  return null
}

/** First descendant (self included) with the given local name. */
function first(node, name) {
  if (!node) return null
  if (node.name === name) return node
  for (const c of node.children) {
    const hit = first(c, name)
    if (hit) return hit
  }
  return null
}

// ---------------------------------------------------------------------------
// URL helpers
// ---------------------------------------------------------------------------

/**
 * Percent-encode one path segment the way `Uri.EscapeDataString` does: every
 * byte outside the RFC 3986 unreserved set is escaped, including `!'()*`
 * (which `encodeURIComponent` leaves alone).
 */
function encodeSegment(segment) {
  return encodeURIComponent(segment).replace(
    /[!'()*]/g,
    (c) => '%' + c.charCodeAt(0).toString(16).toUpperCase(),
  )
}

/** `a/b` + `c` -> `a/b/c`, tolerating leading/trailing slashes on either side. */
function combine(a, b) {
  const left = (a ?? '').replace(/^\/+|\/+$/g, '')
  const right = (b ?? '').replace(/^\/+|\/+$/g, '')
  if (!left) return right
  if (!right) return left
  return `${left}/${right}`
}

class WebDavError extends Error {
  constructor(message, status) {
    super(message)
    this.name = 'WebDavError'
    this.status = status
  }
}

class WebDavClient {
  /**
   * @param {string} baseUrl     server root, e.g. `https://dev.zhaohans.cn`
   * @param {string} fallbackUrl second root to use when the first is unusable,
   *                             or null for none. Left null the client behaves
   *                             exactly as before.
   */
  constructor(baseUrl, fallbackUrl = null) {
    const trimmed = String(baseUrl ?? '').trim()
    if (!/^https?:\/\//i.test(trimmed)) {
      throw new WebDavError(`Invalid server URL: ${baseUrl}`)
    }
    this.baseUrl = trimmed.replace(/\/+$/g, '') + '/'
    /** Status line of the most recent PUT, surfaced to the user on failure. */
    this.lastPutStatus = ''
    const basePath = (() => {
      try {
        return decodeURIComponent(new URL(this.baseUrl).pathname).replace(/^\/+|\/+$/g, '')
      } catch {
        return ''
      }
    })()
    this._basePath = basePath

    /*
     * Failover.
     *
     * Two hosts, one of which is regularly the one that is down: they resolve to
     * the same address and only one vhost is serving at a time. Retrying the dead
     * one on every poll would cost a round trip — or a 25 second timeout — each
     * time, so once one host has been found unusable the other is used from then
     * on, with an occasional probe back so a recovered host is picked up without
     * a restart.
     */
    const fallback = String(fallbackUrl ?? '').trim().replace(/\/+$/g, '')
    this.fallbackUrl = fallback && /^https?:\/\//i.test(fallback) ? fallback + '/' : null
    this._onFallback = false
    this._probePrimaryAt = 0
    /** Set when a request was served by the fallback, for the status line. */
    this.usingFallback = false
  }

  /** How long to stay on the fallback before testing the primary again. */
  static get PRIMARY_PROBE_MS() {
    return 60000
  }

  /** A status that means the host is up but cannot serve: try the other one. */
  static _hostDown(status) {
    return status === 502 || status === 503 || status === 504
  }

  /**
   * Full URL for a server-relative path.
   * @param {string} path     e.g. `nw集训/学生资料临存/tcp_chat/msg_1_a.json`
   * @param {boolean} isDir   append the trailing slash collections require
   */
  buildUrl(path, isDir = false) {
    const segments = String(path ?? '')
      .replace(/\\/g, '/')
      .split('/')
      .filter((s) => s.length > 0)
    let url = this.baseUrl
    url += segments.map(encodeSegment).join('/')
    if (isDir && segments.length > 0) url += '/'
    return url
  }

  /** Map a multistatus `href` back to a server-relative, decoded, slash-trimmed path. */
  hrefToPath(href) {
    let s = String(href ?? '')
    if (/^https?:\/\//i.test(s)) {
      try {
        s = new URL(s).pathname
      } catch {
        /* keep the raw value; the trim below still yields something usable */
      }
    }
    try {
      s = decodeURIComponent(s)
    } catch {
      /* malformed escapes: match the server's literal href instead of throwing */
    }
    s = s.replace(/^\/+|\/+$/g, '')
    if (this._basePath) {
      if (s.toLowerCase() === this._basePath.toLowerCase()) return ''
      const prefix = this._basePath + '/'
      if (s.toLowerCase().startsWith(prefix.toLowerCase())) s = s.slice(prefix.length)
    }
    return s
  }

  async _fetch(url, init, timeoutSeconds) {
    const send = async (target) => {
      const controller = new AbortController()
      const timer = setTimeout(() => controller.abort(), timeoutSeconds * 1000)
      try {
        return await fetch(target, {
          ...init,
          signal: controller.signal,
          redirect: 'follow',
          headers: { 'User-Agent': USER_AGENT, ...(init && init.headers) },
        })
      } finally {
        clearTimeout(timer)
      }
    }

    if (!this.fallbackUrl) return send(url)

    // While on the fallback, only touch the primary when it is time to probe.
    const onBackup = this._onFallback && Date.now() < this._probePrimaryAt
    const primary = onBackup ? this.fallbackUrl : this.baseUrl
    const other = onBackup ? this.baseUrl : this.fallbackUrl
    const swap = (target) => target.replace(primary, other)

    /*
     * Fail over on a transport error or a gateway status, and on nothing else.
     *
     * A 401 or a 404 means the host answered: the other one would answer the
     * same way, and swapping would only hide a wrong password or a missing folder
     * behind a second, identical failure.
     */
    let first
    try {
      first = await send(url)
      if (!WebDavClient._hostDown(first.status)) {
        if (onBackup) {
          // The primary answered after all; stay on it.
          this._onFallback = false
          this.usingFallback = false
        }
        return first
      }
    } catch (err) {
      if (!isConnectionError(err)) throw err
    }

    const second = await send(swap(url)).catch((err) => {
      // Neither host works. Report the one we were asked for, not the spare.
      throw err
    })
    this._onFallback = !onBackup
    this.usingFallback = this._onFallback
    this._probePrimaryAt = this._onFallback ? Date.now() + WebDavClient.PRIMARY_PROBE_MS : 0
    if (this._onFallback) {
      log.info(`webdav: ${primary} unusable, using ${other} instead`)
    }
    return second
  }

  /**
   * PROPFIND. `depth` 0 queries the resource itself, 1 its direct children.
   * A missing collection yields an empty list rather than an error — servers
   * disagree on whether that is 404, 409 or 500.
   */
  async propFind(path, depth = 1) {
    const body =
      '<?xml version="1.0" encoding="utf-8"?>' +
      '<D:propfind xmlns:D="DAV:"><D:prop>' +
      '<D:displayname/><D:resourcetype/><D:getcontentlength/><D:getlastmodified/><D:getetag/>' +
      '</D:prop></D:propfind>'

    let resp
    try {
      resp = await this._fetch(
        this.buildUrl(path, true),
        {
          method: 'PROPFIND',
          headers: { Depth: String(depth), 'Content-Type': 'application/xml; charset=utf-8' },
          body,
        },
        25,
      )
    } catch (err) {
      throw new WebDavError(`PROPFIND ${path} failed: ${err.message}`)
    }

    if (resp.status === 404) return []
    if (resp.status === 207) {
      return this.parseMultiStatus(await resp.text())
    }
    if (resp.ok) return []
    throw new WebDavError(`PROPFIND ${path} -> ${resp.status} ${resp.statusText}`, resp.status)
  }

  /** Parse a 207 Multi-Status body into entries. */
  parseMultiStatus(xml) {
    const list = []
    if (!xml || !xml.trim()) return list
    let doc
    try {
      doc = parseXml(xml)
    } catch (err) {
      log.warn('multistatus parse failed', err)
      return list
    }

    for (const response of findAll(doc, 'response')) {
      const hrefEl = child(response, 'href')
      if (!hrefEl) continue
      const href = hrefEl.text.trim()

      const prop = first(response, 'prop')
      const resourceType = child(prop, 'resourcetype')
      const isCollection = resourceType ? first(resourceType, 'collection') !== null : false

      const lenText = child(prop, 'getcontentlength')?.text.trim() ?? ''
      const length = Number.parseInt(lenText, 10)

      const lmText = child(prop, 'getlastmodified')?.text.trim() ?? ''
      let lastModified = null
      if (lmText) {
        const parsed = new Date(lmText)
        if (!Number.isNaN(parsed.getTime())) lastModified = parsed
      }

      let path = this.hrefToPath(href)
      let name = path.includes('/') ? path.slice(path.lastIndexOf('/') + 1) : path
      if (!name && path) {
        const trimmed = path.replace(/\/+$/g, '')
        name = trimmed.slice(trimmed.lastIndexOf('/') + 1)
      }

      list.push({
        href,
        path,
        name,
        isCollection,
        length: Number.isFinite(length) ? length : 0,
        lastModified,
        etag: child(prop, 'getetag')?.text.trim() ?? '',
      })
    }
    return list
  }

  /**
   * GET raw bytes.
   * @returns {Promise<Buffer|null>} null on 404 (a withdrawn message/attachment)
   */
  async getBytes(path, timeoutSeconds = 60) {
    const resp = await this._fetch(this.buildUrl(path, false), { method: 'GET' }, timeoutSeconds)
    if (resp.status === 404) return null
    if (!resp.ok) {
      throw new WebDavError(`GET ${path} -> ${resp.status} ${resp.statusText}`, resp.status)
    }
    return Buffer.from(await resp.arrayBuffer())
  }

  /** GET text, stripping a UTF-8 BOM if the server added one. */
  async getString(path, timeoutSeconds = 25) {
    const bytes = await this.getBytes(path, timeoutSeconds)
    if (bytes === null) return null
    let buf = bytes
    if (buf.length >= 3 && buf[0] === 0xef && buf[1] === 0xbb && buf[2] === 0xbf) {
      buf = buf.subarray(3)
    }
    return buf.toString('utf8')
  }

  /** PUT bytes. Records the status line for display on failure. */
  async put(path, data, contentType = 'application/octet-stream', timeoutSeconds = 60) {
    const payload = Buffer.isBuffer(data) ? data : Buffer.from(data)
    const resp = await this._fetch(
      this.buildUrl(path, false),
      {
        method: 'PUT',
        headers: { 'Content-Type': contentType, 'Content-Length': String(payload.length) },
        body: payload,
      },
      timeoutSeconds,
    )
    this.lastPutStatus = `${resp.status} ${resp.statusText}`.trim()
    return resp.ok
  }

  putText(path, text) {
    return this.put(
      path,
      Buffer.from(text, 'utf8'),
      'application/json; charset=utf-8',
      60,
    )
  }

  async remove(path, timeoutSeconds = 25) {
    const resp = await this._fetch(this.buildUrl(path, false), { method: 'DELETE' }, timeoutSeconds)
    return resp.ok
  }

  /** MKCOL; returns false when the collection already exists (not an error). */
  async mkcol(path) {
    const resp = await this._fetch(this.buildUrl(path, true), { method: 'MKCOL' }, 25)
    return resp.ok
  }

  /** Create every level of a collection path. */
  async ensureCollection(path) {
    const parts = String(path ?? '')
      .replace(/\\/g, '/')
      .split('/')
      .filter(Boolean)
    let current = ''
    for (const part of parts) {
      current = combine(current, part)
      await this.mkcol(current)
    }
  }

  async exists(path) {
    try {
      const entries = await this.propFind(path, 0)
      return entries.length > 0
    } catch {
      return false
    }
  }

  /** Cheap connectivity probe against the server root. */
  async test() {
    try {
      await this.propFind('', 0)
      return true
    } catch {
      return false
    }
  }
}

module.exports = { WebDavClient, WebDavError, combine, encodeSegment, parseXml, findAll, child, first }
