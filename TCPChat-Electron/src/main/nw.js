'use strict'

/*
 * A transfer timeout that scales with how much has to move.
 *
 * Every request here is bounded by `AbortSignal.timeout`, which measures the whole
 * request rather than idleness. A fixed number is therefore a size limit in
 * disguise: 60 seconds is generous for a message file and impossible for a 100 MB
 * attachment, so the attachment fails with an abort that reads like a network
 * error and looks like the app refusing to open it.
 *
 * The allowance is a floor plus a per-megabyte rate. The rate is deliberately slow
 * — around 330 KB/s — because it is there to notice a connection that has died,
 * not to police a slow one, and a real transfer is bounded by the server anyway.
 */
const TRANSFER_FLOOR_SECONDS = 30
const SECONDS_PER_MEGABYTE = 3

function transferTimeoutSeconds(bytes) {
  const mb = Math.max(0, Number(bytes) || 0) / 1048576
  return TRANSFER_FLOOR_SECONDS + Math.ceil(mb) * SECONDS_PER_MEGABYTE
}

/*
 * Client for the file-browsing service at nw.zhaohans.cn.
 *
 * That service is not WebDAV. It is a small JSON-and-HTML API over what appears
 * to be the same storage the WebDAV server used to expose, and it is where the
 * messages live now: the WebDAV host is abandoned and answers 502.
 *
 * It implements the same surface as WebDavClient — propFind / getBytes /
 * getString / put / putText / remove / test — so ChatService can hold either one.
 * Nothing else is shared: one speaks XML multistatus over bare URLs, the other
 * speaks JSON and HTML over a session cookie and identifies files by backslash
 * path.
 *
 * The protocol, and how each piece was established, is written up in
 * docs/nw-api.md.
 */

const path = require('path')
const { log } = require('./logger')

const USER_AGENT = `TCPChat/${require('../../package.json').version} (Electron)`

/** The session cookie lasts a day; refresh it a little before that. */
const SESSION_MS = 23 * 60 * 60 * 1000

class NwError extends Error {
  constructor(message, status) {
    super(message)
    this.name = 'NwError'
    this.status = status
  }
}

class NwClient {
  /**
   * @param {object} options
   * @param {string} options.baseUrl   e.g. `https://nw.zhaohans.cn`
   * @param {string} options.password  the download/upload password
   */
  constructor({ baseUrl, password }) {
    const trimmed = String(baseUrl ?? '').trim().replace(/\/+$/g, '')
    if (!/^https?:\/\//i.test(trimmed)) {
      throw new NwError(`Invalid server URL: ${baseUrl}`)
    }
    this.baseUrl = trimmed
    this.password = String(password ?? '')
    this._cookie = ''
    this._authedAt = 0
    this.lastPutStatus = ''
    /** True once a withdraw has been written as a tombstone. Surfaced in diagnostics. */
    this.supportsDelete = false
  }

  /** A directory as the API wants it: forward slashes, no leading or trailing one. */
  static _asDir(p) {
    return String(p ?? '')
      .replace(/\\/g, '/')
      .split('/')
      .filter((s) => s.length > 0)
      .join('/')
  }

  /**
   * A file as the API wants it: backslashes, and the whole path rather than the
   * name. Passing only the name answers 403, which is what made this look
   * unreachable at first.
   */
  static _asFile(p) {
    return this._asDir(p).replace(/\//g, '\\')
  }

  // -------------------------------------------------------------------------
  // Session
  // -------------------------------------------------------------------------

  async _auth() {
    if (this._cookie && Date.now() - this._authedAt < SESSION_MS) return
    let resp
    try {
      resp = await fetch(`${this.baseUrl}/nw/auth`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'User-Agent': USER_AGENT },
        body: JSON.stringify({ password: this.password, isSuperAuth: false }),
        signal: AbortSignal.timeout(20000),
      })
    } catch (err) {
      throw new NwError(`连不上 ${this.baseUrl}：${err.message}`)
    }
    const body = await resp.text()
    if (!resp.ok) throw new NwError(`认证失败 (${resp.status})`, resp.status)
    let parsed
    try {
      parsed = JSON.parse(body)
    } catch {
      throw new NwError('认证返回的不是 JSON，这个地址可能不是 nw 服务')
    }
    if (!parsed.success) throw new NwError(parsed.error || '密码错误')

    const cookies = resp.headers.getSetCookie ? resp.headers.getSetCookie() : []
    const session = cookies.map((c) => c.split(';')[0]).find((c) => c.startsWith('session_id='))
    if (!session) throw new NwError('认证成功但没有拿到会话 cookie')
    this._cookie = session
    this._authedAt = Date.now()
    log.info('nw: authenticated')
  }

  /** One request, re-authenticating once if the session has lapsed. */
  async _request(url, init = {}, timeoutSeconds = 25) {    await this._auth()
    const send = () =>
      fetch(url, {
        ...init,
        headers: { 'User-Agent': USER_AGENT, Cookie: this._cookie, ...(init.headers ?? {}) },
        signal: AbortSignal.timeout(timeoutSeconds * 1000),
      })

    let resp = await send()
    if (resp.status === 401 || resp.status === 403) {
      // The session may simply have expired underneath us; one retry is cheap
      // and saves a whole poll cycle.
      this._cookie = ''
      this._authedAt = 0
      await this._auth()
      resp = await send()
    }
    return resp
  }

  // -------------------------------------------------------------------------
  // The WebDavClient surface
  // -------------------------------------------------------------------------

  buildUrl(p) {
    return `${this.baseUrl}/nw/download?file=${encodeURIComponent(NwClient._asFile(p))}`
  }

  /**
   * List a directory.
   *
   * There is no JSON listing for files — /nw/api/folders-public returns folders
   * only — so the HTML page is the only source. Each row carries the full
   * backslash path of its file, which is what `download` needs later, so the
   * paths are taken from there rather than rebuilt from names.
   */
  async propFind(dirPath, _depth = 1) {
    const dir = NwClient._asDir(dirPath)
    const url = `${this.baseUrl}/nw/?dir=${encodeURIComponent(dir)}`

    let resp
    try {
      resp = await this._request(url, {}, 30)
    } catch (err) {
      throw new NwError(`列目录失败 ${dir}: ${err.message}`)
    }
    if (!resp.ok) throw new NwError(`列目录失败 ${dir} -> ${resp.status}`, resp.status)

    const html = await resp.text()
    if (!/代码仓|file-row|nw\//.test(html)) {
      throw new NwError('返回的不是 nw 的目录页面，这个地址可能已经变了')
    }

    /*
     * Each file row, whole.
     *
     * Split per row rather than scanned twice for paths and then for metadata:
     * the date and the size belong to the file named in the same row, and two
     * independent passes would pair them by position, which is exactly the kind
     * of assumption that survives until a row renders slightly differently.
     *
     * A row looks like:
     *   <div class="file-row" onclick="showAuthModal('...encoded path...')">
     *     <div class="file-name">msg_1791.json</div>
     *     <div class="file-meta">
     *       <span>📅 2026/10/05 11:00</span> <span>📦 98 B</span>
     *     </div>
     */
    const entries = []
    const seen = new Set()

    const rowPattern = /<div class="file-row"[\s\S]*?(?=<div class="file-row"|<\/body>|$)/g
    for (const match of html.matchAll(rowPattern)) {
      const chunk = match[0]

      const pathMatch = /showAuthModal\('([^']+)'\)/.exec(chunk)
      if (!pathMatch) continue
      let full = pathMatch[1]
      try {
        full = decodeURIComponent(full)
      } catch {
        /* leave it as it came */
      }
      const name = full.split('\\').pop()
      if (!name || seen.has(name)) continue
      seen.add(name)

      /*
       * The fingerprint. This is what makes it possible to notice a file that
       * changed rather than one that appeared: a withdraw rewrites the record in
       * place, so the name stays the same and only these move.
       *
       * Both are as coarse as the page renders them - minutes, and a size
       * rounded to one decimal in KB and up - so a change inside the same minute
       * that lands in the same size bucket is missed. That is the price of the
       * only fingerprint this service offers, and it is enough for a withdrawal,
       * which takes a message of some hundreds of bytes down to a short record.
       */
      const dateMatch = /📅\s*(\d{4})\/(\d{2})\/(\d{2})\s+(\d{2}):(\d{2})/.exec(chunk)
      const lastModified = dateMatch
        ? new Date(
            Number(dateMatch[1]),
            Number(dateMatch[2]) - 1,
            Number(dateMatch[3]),
            Number(dateMatch[4]),
            Number(dateMatch[5]),
          )
        : null

      const sizeMatch = /📦\s*([\d.]+)\s*(B|KB|MB|GB)/.exec(chunk)
      const scale = { B: 1, KB: 1024, MB: 1024 ** 2, GB: 1024 ** 3 }
      const length = sizeMatch
        ? Math.round(Number(sizeMatch[1]) * (scale[sizeMatch[2]] ?? 1))
        : 0

      entries.push({
        href: full,
        path: full.replace(/\\/g, '/'),
        name,
        isCollection: false,
        length,
        lastModified,
        etag: '',
      })
    }

    // Directories: navigateDir('/nw/?dir=<encoded forward-slash path>')
    for (const m of html.matchAll(/navigateDir\('\/nw\/\?dir=([^']+)'\)/g)) {
      let target = m[1]
      try {
        target = decodeURIComponent(target)
      } catch {
        /* leave it as it came */
      }
      const sub = NwClient._asDir(target).split('/').pop()
      if (!sub || seen.has(sub)) continue
      seen.add(sub)
      entries.push({
        href: target,
        path: `${dir}/${sub}`,
        name: sub,
        isCollection: true,
        length: 0,
        lastModified: null,
        etag: '',
      })
    }

    return entries
  }

  async getBytes(filePath, timeoutSeconds = 60) {
    const resp = await this._request(this.buildUrl(filePath), {}, timeoutSeconds)
    if (!resp.ok) throw new NwError(`读取失败 ${filePath} -> ${resp.status}`, resp.status)
    return Buffer.from(await resp.arrayBuffer())
  }

  async getString(filePath, timeoutSeconds = 25) {
    const resp = await this._request(this.buildUrl(filePath), {}, timeoutSeconds)
    if (!resp.ok) throw new NwError(`读取失败 ${filePath} -> ${resp.status}`, resp.status)
    return await resp.text()
  }

  /**
   * Write a file, replacing one of the same name.
   *
   * The bytes are sent as a Blob rather than a stream so Node sets the multipart
   * length itself; the API has no chunked path.
   */
  async put(filePath, data, _contentType = 'application/octet-stream', timeoutSeconds = null) {
    const full = NwClient._asDir(filePath)
    const dir = full.includes('/') ? full.slice(0, full.lastIndexOf('/')) : ''
    const name = full.slice(full.lastIndexOf('/') + 1)
    const bytes = Buffer.isBuffer(data) ? data : Buffer.from(data)

    const form = new FormData()
    form.append('password', this.password)
    form.append('dir', dir)
    form.append('file', new Blob([bytes]), name)

    const resp = await this._request(
      `${this.baseUrl}/nw/upload`,
      { method: 'POST', body: form },
      timeoutSeconds ?? transferTimeoutSeconds(bytes.length),
    )
    const text = await resp.text()
    this.lastPutStatus = `${resp.status} ${text.slice(0, 120)}`

    let parsed = null
    try {
      parsed = JSON.parse(text)
    } catch {
      /* fall through to the status check below */
    }
    if (!resp.ok || !parsed?.success) {
      log.warn(`nw: upload ${full} failed -> ${this.lastPutStatus}`)
      return false
    }
    /*
     * The server says which of the two it did. Worth surfacing rather than
     * discarding: writing a tombstone over a message that is not there, or
     * creating one where a replace was intended, both look identical otherwise.
     */
    if (parsed.overwritten) log.info(`nw: replaced ${full}`)
    return true
  }

  async putText(filePath, text) {
    return this.put(filePath, Buffer.from(text, 'utf8'), 'application/json; charset=utf-8', 25)
  }

  /**
   * Withdraw, by overwriting the file with a tombstone.
   *
   * This API has no delete and the operator confirms the UI cannot remove a file
   * either, so the message stays where it is and is marked instead. Reading it
   * back gives a record with no text, which both sides render as withdrawn.
   *
   * The C# build is retired, so the shape of that record is ours to choose; it
   * keeps `v` and `id` so an older reader still finds a well-formed message.
   */
  async remove(filePath, _timeoutSeconds = 25) {
    const full = NwClient._asDir(filePath)
    const name = full.slice(full.lastIndexOf('/') + 1)
    const idMatch = /^msg_([^.]*)\.json$/i.exec(name)
    const tombstone = {
      v: 2,
      id: idMatch ? idMatch[1] : name,
      time: new Date().toISOString(),
      tombstone: true,
    }
    const ok = await this.put(filePath, Buffer.from(JSON.stringify(tombstone), 'utf8'))
    if (ok) log.info(`nw: withdrew ${name} (tombstone)`)
    return ok
  }

  /** Is a message record a tombstone rather than something to show? */
  static isTombstone(parsed) {
    return Boolean(parsed) && parsed.tombstone === true
  }

  async exists(filePath) {
    try {
      const resp = await this._request(this.buildUrl(filePath), {}, 20)
      return resp.ok
    } catch {
      return false
    }
  }

  /** The folder must already exist; this API cannot create one. */
  async ensureCollection(dirPath) {
    const entries = await this.propFind(dirPath, 0)
    return entries.length > 0
  }

  async test() {
    try {
      await this._auth()
      return true
    } catch {
      return false
    }
  }
}

module.exports = { NwClient, NwError, transferTimeoutSeconds }
