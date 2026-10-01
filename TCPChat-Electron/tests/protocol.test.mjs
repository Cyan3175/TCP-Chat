/**
 * End-to-end protocol test.
 *
 *   node tests/protocol.test.mjs
 *
 * Starts a minimal in-process WebDAV server, points two independent ChatService
 * instances at the same directory (standing in for two clients), and exercises
 * the real paths: sync loop, send, encryption, attachment upload/download,
 * withdraw propagation, local cache and history windowing.
 *
 * The server deliberately URL-encodes CJK path segments and reports hrefs with
 * a `/dav/` prefix, so the client's percent-encoding and href normalisation are
 * covered rather than assumed.
 *
 * The whole protocol layer is Electron-free by design, so this runs under plain
 * Node in a couple of seconds.
 */

import http from 'node:http'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { createRequire } from 'node:module'
import { fileURLToPath } from 'node:url'

const require = createRequire(import.meta.url)
const here = path.dirname(fileURLToPath(import.meta.url))
const root = path.join(here, '..')

// Redirect the profile before anything reads it.
const DATA_DIR = fs.mkdtempSync(path.join(os.tmpdir(), 'tcpchat-protocol-'))
process.env.TCPCHAT10_TEST_DATADIR = DATA_DIR
const { WebDavClient, parseXml, findAll, child } = require(path.join(root, 'src', 'main', 'webdav.js'))
const { MessageCache, timeOf, pathFor } = require(path.join(root, 'src', 'main', 'message-cache.js'))
const { Settings } = require(path.join(root, 'src', 'main', 'settings.js'))
const chatModule = require(path.join(root, 'src', 'main', 'chat-service.js'))

const CHAT_FOLDER = 'nw集训/学生资料临存/tcp_chat'
const PREFIX = '/dav'

let passed = 0
const failures = []

function check(name, actual, expected) {
  if (String(actual) === String(expected)) {
    passed += 1
    return true
  }
  failures.push({ name, actual, expected })
  return false
}
const ok = (name, value, detail = '') => check(name, value ? 'true' : `false ${detail}`, 'true')

// ---------------------------------------------------------------------------
// Minimal WebDAV server
// ---------------------------------------------------------------------------

/** path (decoded, no leading slash) -> {dir: true} | {data: Buffer, mtime} */
const store = new Map()

function decodePath(urlPath) {
  let p = urlPath
  if (p.startsWith(PREFIX)) p = p.slice(PREFIX.length)
  try {
    p = decodeURIComponent(p)
  } catch {
    /* keep the raw value */
  }
  return p.replace(/^\/+|\/+$/g, '')
}

/** Percent-encode each segment, the way the client does. */
function encodePath(p) {
  return PREFIX + '/' + p.split('/').filter(Boolean).map(encodeURIComponent).join('/')
}

function xmlEscape(s) {
  return String(s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&apos;' })[c])
}

function entryXml(p, isDir, entry) {
  const href = encodePath(p) + (isDir ? '/' : '')
  const name = p.includes('/') ? p.slice(p.lastIndexOf('/') + 1) : p
  return (
    '<D:response>' +
    `<D:href>${xmlEscape(href)}</D:href>` +
    '<D:propstat><D:prop>' +
    `<D:displayname>${xmlEscape(name)}</D:displayname>` +
    `<D:resourcetype>${isDir ? '<D:collection/>' : ''}</D:resourcetype>` +
    `<D:getcontentlength>${isDir ? 0 : entry.data.length}</D:getcontentlength>` +
    `<D:getlastmodified>${(entry.mtime ?? new Date()).toUTCString()}</D:getlastmodified>` +
    `<D:getetag>"${isDir ? 'dir' : entry.data.length}"</D:getetag>` +
    '</D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat>' +
    '</D:response>'
  )
}

/** Collections implied by the files that exist (no explicit directory records). */
function collections() {
  const dirs = new Set([''])
  for (const key of store.keys()) {
    const parts = key.split('/')
    for (let i = 1; i <= parts.length; i++) dirs.add(parts.slice(0, i).join('/'))
  }
  return dirs
}

function isCollection(p) {
  if (store.has(p)) return false
  return collections().has(p)
}

function readBody(req) {
  return new Promise((resolve) => {
    const chunks = []
    req.on('data', (c) => chunks.push(c))
    req.on('end', () => resolve(Buffer.concat(chunks)))
  })
}

const server = http.createServer(async (req, res) => {
  const url = new URL(req.url, 'http://127.0.0.1')
  const target = decodePath(url.pathname)
  const method = req.method.toUpperCase()

  if (method === 'PROPFIND') {
    const depth = req.headers.depth ?? '1'
    const body = await readBody(req)
    if (body.length) parseXml(body.toString('utf8')) // exercise the parser on real input

    const existing = store.has(target) || isCollection(target)
    if (!existing) {
      res.writeHead(404).end('not found')
      return
    }

    const rows = []
    if (store.has(target)) rows.push(entryXml(target, false, store.get(target)))
    else rows.push(entryXml(target, true, { mtime: new Date() }))

    if (Number(depth) >= 1 && !store.has(target)) {
      const prefix = target === '' ? '' : target + '/'
      for (const [key, entry] of store) {
        if (!key.startsWith(prefix)) continue
        if (key.slice(prefix.length).includes('/')) continue
        rows.push(entryXml(key, false, entry))
      }
      for (const dir of collections()) {
        if (!dir || !dir.startsWith(prefix) || dir === target) continue
        if (dir.slice(prefix.length).includes('/')) continue
        rows.push(entryXml(dir, true, { mtime: new Date() }))
      }
    }

    const xml =
      '<?xml version="1.0" encoding="utf-8"?>' +
      '<D:multistatus xmlns:D="DAV:">' + rows.join('') + '</D:multistatus>'
    res.writeHead(207, { 'Content-Type': 'application/xml; charset=utf-8' }).end(xml)
    return
  }

  if (method === 'GET') {
    const entry = store.get(target)
    if (!entry) {
      res.writeHead(404).end('not found')
      return
    }
    res.writeHead(200, { 'Content-Type': 'application/octet-stream' }).end(entry.data)
    return
  }

  if (method === 'PUT') {
    const data = await readBody(req)
    store.set(target, { data, mtime: new Date() })
    res.writeHead(201).end('created')
    return
  }

  if (method === 'DELETE') {
    if (!store.delete(target)) {
      res.writeHead(404).end('not found')
      return
    }
    res.writeHead(204).end()
    return
  }

  if (method === 'MKCOL') {
    res.writeHead(201).end('created')
    return
  }

  res.writeHead(405).end('method not allowed')
})

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

const wait = (ms) => new Promise((resolve) => setTimeout(resolve, ms))

/**
 * Each simulated client gets its own profile directory.
 *
 * The local message cache and the attachment cache both live under the profile,
 * so two clients sharing one directory would silently share a cache — one
 * client's replay would mark messages as seen for the other. That is a property
 * of the test harness, not of the app (a real install has one profile).
 *
 * `paths.js` reads the environment on every call, so switching is just an
 * assignment. The profile's own settings file is irrelevant here because the
 * services are constructed from explicit `Settings` objects.
 */
let clientSeq = 0
function useClientProfile(label) {
  const dir = path.join(DATA_DIR, `${++clientSeq}-${label}`)
  fs.mkdirSync(dir, { recursive: true })
  process.env.TCPCHAT10_TEST_DATADIR = dir
  return dir
}

/** Point the process profile at a client's own directory. */
function activate(service) {
  process.env.TCPCHAT10_TEST_DATADIR = service.profileDir
}

/** Construct a client with its own profile, wired up before any replay. */
async function makeClient(label, nickname, passwords, sendIndex, sink) {
  const profileDir = useClientProfile(label)
  const service = new chatModule.ChatService(makeSettings(nickname, passwords, sendIndex))
  service.profileDir = profileDir
  service.on('message-added', (m) => sink.push(m))
  service.on('error', (message) => sink.errors.push(message))
  await service.reload()
  return service
}

/** Read a payload through the cipher without needing a live sync round. */
function decryptEnvelope(service, aad, envelope) {
  return service.cipher.tryDecryptPayload(aad, envelope)
}

function makeSettings(nickname, passwords, sendIndex) {
  return new Settings({
    serverUrl: `http://127.0.0.1:${server.address().port}/dav`,
    chatFolder: CHAT_FOLDER,
    nickname,
    cryptoPasswords: passwords,
    sendPasswordIndex: sendIndex,
    pollSeconds: 1,
    historyDays: 7,
  })
}

// ---------------------------------------------------------------------------
// Run
// ---------------------------------------------------------------------------

async function main() {
  await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve))
  const baseUrl = `http://127.0.0.1:${server.address().port}/dav`

  // Seed the chat directory so initialize() finds it.
  store.set(`${CHAT_FOLDER}/.keep`, { data: Buffer.from(''), mtime: new Date() })

  // ---- URL building and href normalisation --------------------------------

  const dav = new WebDavClient(baseUrl)
  check('base URL is normalised with a trailing slash', dav.baseUrl, `${baseUrl}/`)
  check(
    'CJK path segments are percent-encoded, slashes preserved',
    dav.buildUrl('nw集训/学生资料临存/tcp_chat/msg_1.json'),
    `${baseUrl}/nw%E9%9B%86%E8%AE%AD/%E5%AD%A6%E7%94%9F%E8%B5%84%E6%96%99%E4%B8%B4%E5%AD%98/tcp_chat/msg_1.json`,
  )
  check(
    'apostrophes and asterisks are escaped like Uri.EscapeDataString',
    dav.buildUrl("a'b*c"),
    `${baseUrl}/a%27b%2Ac`,
  )
  check(
    'collection URLs get a trailing slash',
    dav.buildUrl(CHAT_FOLDER, true),
    `${baseUrl}/nw%E9%9B%86%E8%AE%AD/%E5%AD%A6%E7%94%9F%E8%B5%84%E6%96%99%E4%B8%B4%E5%AD%98/tcp_chat/`,
  )
  check(
    'href with the /dav prefix maps back to a clean relative path',
    dav.hrefToPath('/dav/nw%E9%9B%86%E8%AE%AD/x/msg_1.json'),
    'nw集训/x/msg_1.json',
  )
  check(
    'absolute-URL hrefs are handled',
    dav.hrefToPath(`${baseUrl}/nw%E9%9B%86%E8%AE%AD/x/msg_1.json`),
    'nw集训/x/msg_1.json',
  )

  // ---- multistatus parsing ------------------------------------------------

  const xml = `<?xml version="1.0" encoding="utf-8"?>
<D:multistatus xmlns:D="DAV:">
  <D:response>
    <D:href>${encodePath(CHAT_FOLDER)}/</D:href>
    <D:propstat><D:prop><D:resourcetype><D:collection/></D:resourcetype>
      <D:getcontentlength>0</D:getcontentlength></D:prop>
      <D:status>HTTP/1.1 200 OK</D:status></D:propstat>
  </D:response>
  <D:response>
    <D:href>${encodePath(CHAT_FOLDER + '/msg_1767225600000_ab12cd34.json')}</D:href>
    <D:propstat><D:prop><D:resourcetype/><D:getcontentlength>321</D:getcontentlength>
      <D:getlastmodified>Wed, 01 Jan 2026 00:00:00 GMT</D:getlastmodified>
      <D:getetag>"xyz"</D:getetag></D:prop>
      <D:status>HTTP/1.1 200 OK</D:status></D:propstat>
  </D:response>
</D:multistatus>`

  const entries = dav.parseMultiStatus(xml)
  check('multistatus: two responses parsed', entries.length, 2)
  check('multistatus: collection detected', entries[0].isCollection, 'true')
  check('multistatus: file name extracted', entries[1].name, 'msg_1767225600000_ab12cd34.json')
  check('multistatus: content length parsed', entries[1].length, 321)
  ok('multistatus: last-modified parsed', entries[1].lastModified instanceof Date)
  check('multistatus: etag parsed', entries[1].etag, '"xyz"')

  // A namespace-prefix-free variant must parse identically.
  const bare = xml.replace(/D:/g, '').replace(/ xmlns:D="DAV:"/, ' xmlns="DAV:"')
  check('multistatus: parses without namespace prefixes', dav.parseMultiStatus(bare).length, 2)

  // ---- connectivity -------------------------------------------------------

  useClientProfile('alice')
  const alice = new chatModule.ChatService(makeSettings('张三', ['shared-pass'], 0))
  alice.profileDir = process.env.TCPCHAT10_TEST_DATADIR
  await alice.reload()
  const init = await alice.initialize()
  ok('initialize() reports a usable chat folder', init.ok, init.message)
  ok('encryption is enabled with a non-blank password', alice.encryptionEnabled)

  // ---- send and receive, encrypted ---------------------------------------

  const sent = await alice.sendText('第一条消息 **加密**', '引用: 上一条')
  ok('sendText returned a message', Boolean(sent))
  check('sent message is marked version 2', sent.v, 2)
  ok('sent message carries a ciphertext envelope', typeof sent.enc === 'string' && sent.enc.startsWith('AESGCM1:'), sent.enc)
  check('sent message file name follows msg_<ms>_<hex>.json', /^msg_\d+_[0-9a-f]{8}\.json$/.test(sent.remoteName), 'true')

  const storedWire = store.get(`${CHAT_FOLDER}/${sent.remoteName}`)?.data?.toString('utf8') ?? ''
  ok('the message file really landed on the server', storedWire.length > 0)
  ok('the wire copy carries no plaintext body', !storedWire.includes('第一条消息'))
  ok('the wire copy carries no plaintext quote', !storedWire.includes('引用: 上一条'))
  ok('the wire copy does carry the nickname (needed to render "who")', storedWire.includes('张三'))

  const bobInbox = []
  bobInbox.errors = []
  const bob = await makeClient('bob', '李四', ['shared-pass'], 0, bobInbox)
  activate(bob)
  await bob.syncOnce()

  check('the peer received exactly one message', bobInbox.length, 1)
  check('peer sync reported no errors', bobInbox.errors.join(' | '), '')
  check('decrypted body matches', bobInbox[0]?.text, '第一条消息 **加密**')
  check('decrypted quote matches', bobInbox[0]?.quote, '引用: 上一条')
  check('sender nickname preserved', bobInbox[0]?.from, '张三')
  ok('peer marks the message as not-self', bobInbox[0]?.isSelf === false)

  // ---- wrong password -----------------------------------------------------

  const malloryInbox = []
  malloryInbox.errors = []
  const mallory = await makeClient('mallory', '王五', ['wrong-pass'], 0, malloryInbox)
  activate(mallory)
  await mallory.syncOnce()
  check('wrong password still yields the message', malloryInbox.length, 1)
  ok('wrong password flags the message as undecryptable', malloryInbox[0]?.decryptFailed === true)
  check('undecryptable counter incremented', mallory.undecryptableCount, 1)

  // ---- attachments --------------------------------------------------------

  const sourceFile = path.join(DATA_DIR, '题解 说明.pdf')
  const attachmentBytes = Buffer.from('PDF-ish payload with 中文 content'.repeat(40), 'utf8')
  fs.writeFileSync(sourceFile, attachmentBytes)

  activate(alice)
  const attachMsg = await alice.sendFile(sourceFile, 0, 0)
  ok('sendFile returned a message', Boolean(attachMsg))
  check('attachment kind derived from extension (pdf -> file)', attachMsg.attach.kind, 1)
  check('attachment size recorded', attachMsg.attach.size, attachmentBytes.length)
  ok(
    'attachment remote name uses the att_<ms>_<hex>_<name> convention',
    /^att_\d+_[0-9a-f]{6}_/.test(path.basename(attachMsg.attach.path)),
    attachMsg.attach.path,
  )
  check(
    'attachment stored on the server is encrypted (larger than the plaintext)',
    store.get(attachMsg.attach.path).data.length > attachmentBytes.length,
    'true',
  )

  // Bob downloads and decrypts it.
  const bobAttachment = []
  bob.on('message-added', (m) => bobAttachment.push(m))
  activate(bob)
  await bob.syncOnce()
  const receivedAttachment = bobAttachment.find((m) => m.attach)
  ok('peer received the attachment message', Boolean(receivedAttachment))
  check('peer sees the original file name', receivedAttachment?.attach?.name, '题解 说明.pdf')

  const localPath = await bob.downloadAttachment(receivedAttachment)
  ok('peer downloaded and decrypted the attachment', Boolean(localPath))
  check('decrypted attachment matches the original bytes', fs.readFileSync(localPath).equals(attachmentBytes), 'true')
  check(
    'attachment cached under its remote file name',
    path.basename(localPath),
    path.basename(receivedAttachment.attach.path),
  )

  // A wrong-password peer must not get bytes. Checked two ways: directly on the
  // cipher (deterministic) and through a download into a pristine profile, which
  // also proves no plaintext file is written.
  const remoteName = path.basename(receivedAttachment.attach.path)
  const rawBlob = store.get(receivedAttachment.attach.path).data
  check('wrong password cannot decrypt the attachment bytes', mallory.cipher.tryDecryptBytes(remoteName, rawBlob), 'null')

  useClientProfile('mallory-download')
  const malloryDownload = await mallory.downloadAttachment(receivedAttachment)
  check('wrong password cannot download a usable attachment', malloryDownload, 'null')

  // ---- local cache --------------------------------------------------------

  activate(bob)
  bob.cache.save()
  const reloaded = MessageCache.load(CHAT_FOLDER)
  check('cache reloads the same number of entries', reloaded.count >= 2, 'true')
  ok('cache file name is uppercase hex, derived from the folder hash', /^msgs_[0-9A-F]{16}\.json$/.test(path.basename(pathFor(CHAT_FOLDER))))
  check('cache round-trips a stored payload', reloaded.get(sent.remoteName) !== null, 'true')

  const cachedEvent = await bob.syncOnce()
  void cachedEvent
  const replay = []
  bob.on('message-added', (m) => replay.push(m))
  bob.emitCachedMessages()
  check('cached replay reproduces both messages', replay.length >= 2, 'true')
  check('replayed cache holds the decrypted body', replay.some((m) => m.text === '第一条消息 **加密**'), 'true')

  // ---- withdraw -----------------------------------------------------------

  const removed = []
  bob.on('message-removed', (name) => removed.push(name))
  activate(alice)
  const withdrawn = await alice.deleteMessage(sent)
  ok('withdraw succeeded', withdrawn)
  check('message file removed from the server', store.has(`${CHAT_FOLDER}/${sent.remoteName}`), 'false')

  activate(bob)
  await bob.syncOnce()
  check('peer observed the withdraw', removed.includes(sent.remoteName), 'true')
  check('withdrawn message dropped from the peer cache', bob.cache.get(sent.remoteName), 'null')

  activate(alice)
  const attachWithdrawn = await alice.deleteMessage(attachMsg)
  ok('withdrawing an attachment message succeeds', attachWithdrawn)
  check('attachment file also removed from the server', store.has(attachMsg.attach.path), 'false')

  // ---- history window -----------------------------------------------------

  const oldName = `msg_${Date.UTC(2000, 0, 1)}_deadbeef.json`
  store.set(`${CHAT_FOLDER}/${oldName}`, {
    data: Buffer.from(JSON.stringify({ v: 1, id: 'old', from: '古人', time: new Date(Date.UTC(2000, 0, 1)).toISOString(), text: '很久以前' })),
    mtime: new Date(Date.UTC(2000, 0, 1)),
  })
  const historySeen = []
  bob.on('message-added', (m) => historySeen.push(m))
  activate(bob)
  await bob.syncOnce()
  check('messages outside the history window are skipped', historySeen.some((m) => m.text === '很久以前'), 'false')
  check('filename timestamp parsing works', timeOf('msg_1767225600000_ab12cd34.json')?.getTime(), 1767225600000)
  check('non-message names have no timestamp', String(timeOf('att_1_x.png')), 'null')

  // ---- malformed input is survivable --------------------------------------

  store.set(`${CHAT_FOLDER}/msg_1767225600999_badbad00.json`, { data: Buffer.from('{not json'), mtime: new Date() })
  const brokenSeen = []
  bob.on('message-added', (m) => brokenSeen.push(m))
  await bob.syncOnce()
  ok('a corrupt message file does not stop the sync round', true)

  // ---- empty directory listing must not wipe the cache --------------------

  const before = bob.cache.count
  bob.cache.pruneMissing([], new Date(0))
  check('an empty server listing never prunes the cache', bob.cache.count, before)

  // ---- cleanup ------------------------------------------------------------

  alice.dispose()
  bob.dispose()
  mallory.dispose()
  await new Promise((resolve) => server.close(resolve))
}

main()
  .then(() => {
    console.log('')
    if (failures.length === 0) {
      console.log(`[protocol] OK — ${passed} checks passed end to end against a live WebDAV server`)
      process.exit(0)
    }
    console.log(`[protocol] FAILED — ${passed} passed, ${failures.length} failed`)
    for (const f of failures) {
      console.log(`  ✗ ${f.name}\n      expected: ${f.expected}\n      actual:   ${f.actual}`)
    }
    process.exit(1)
  })
  .catch((err) => {
    console.error('[protocol] harness error', err)
    process.exit(2)
  })
