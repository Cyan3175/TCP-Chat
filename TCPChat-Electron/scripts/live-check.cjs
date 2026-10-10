'use strict'

/**
 * Live read-only check against the real WebDAV server.
 *
 *   node scripts/live-check.cjs
 *
 * Runs the production sync pipeline (PROPFIND -> filter -> GET -> parse) against
 * the configured server and reports what it found. Nothing is written, deleted
 * or modified: the only requests are PROPFIND and GET.
 *
 * This is the strongest available compatibility evidence short of two clients:
 * the messages being parsed were written by the C# build.
 */

process.env.TCPCHAT10_TEST_DATADIR =
  process.env.TCPCHAT10_TEST_DATADIR || require('path').join(require('os').tmpdir(), 'tcpchat-live')

const { WebDavClient } = require('../src/main/webdav.js')
const { Settings } = require('../src/main/settings.js')
const { ChatService } = require('../src/main/chat-service.js')

const line = (label, value) => console.log(`${label.padEnd(26)}: ${value}`)

async function main() {
  const settings = new Settings({})
  line('server', settings.serverUrl)
  line('folder', settings.chatFolder)

  const dav = new WebDavClient(settings.serverUrl)

  // ---- raw listing --------------------------------------------------------
  const entries = await dav.propFind(settings.chatFolder, 1)
  const files = entries.filter((e) => !e.isCollection)
  const messages = files
    .filter((e) => /^msg_/i.test(e.name) && /\.json$/i.test(e.name))
    .sort((a, b) => (a.name < b.name ? -1 : 1))
  const attachments = files.filter((e) => /^att_/i.test(e.name))

  line('entries', entries.length)
  line('attachments', attachments.length)
  line('message files', messages.length)

  if (messages.length === 0) {
    console.log('\nNo messages on the server yet — nothing further to verify.')
    return 0
  }

  // ---- parse real messages written by the C# client -----------------------
  const newest = messages[messages.length - 1]
  const oldest = messages[0]
  line('newest message', `${newest.name} (${newest.length} B, ${newest.lastModified?.toISOString()})`)
  line('oldest message', `${oldest.name} (${oldest.length} B, ${oldest.lastModified?.toISOString()})`)

  const { parseMessage } = require('../src/main/chat-service.js')
  let parsedOk = 0
  let encrypted = 0
  let malformed = 0
  const sample = []

  for (const entry of [newest, oldest, ...messages.slice(-6)]) {
    const text = await dav.getString(`${settings.chatFolder}/${entry.name}`, 25)
    const msg = parseMessage(text)
    if (!msg) {
      malformed += 1
      console.log(`  ! could not parse ${entry.name}`)
      continue
    }
    parsedOk += 1
    if (msg.enc) encrypted += 1
    if (sample.length < 3) {
      sample.push({
        name: entry.name,
        v: msg.v,
        from: msg.from,
        time: msg.time,
        enc: msg.enc ? `${msg.enc.slice(0, 14)}…(${msg.enc.length} chars)` : null,
        text: msg.text ? `${msg.text.slice(0, 30)}…` : '',
        attach: msg.attach ? { name: msg.attach.name, size: msg.attach.size, kind: msg.attach.kind } : null,
      })
    }
  }

  line('parsed', `${parsedOk} ok, ${malformed} malformed`)
  line('encrypted', encrypted)
  console.log('\nsample parsed messages:')
  for (const s of sample) console.log('  ', JSON.stringify(s))

  // ---- full pipeline through ChatService (read-only) ----------------------
  const chat = new ChatService(settings)
  const received = []
  const errors = []
  const diags = []
  chat.on('message-added', (m) => received.push(m))
  chat.on('error', (m) => errors.push(m))
  chat.on('diag', (m) => diags.push(m))

  await chat.applyCryptoPasswords([], 0)
  const init = await chat.initialize()
  line('initialize()', `${init.ok} — ${init.message}`)
  await chat.syncOnce()

  line('messages synced', received.length)
  line('decrypt failures', chat.undecryptableCount)
  line('with attachments', received.filter((m) => m.attach).length)
  line('senders seen', [...new Set(received.map((m) => m.from))].join(', ').slice(0, 120))
  if (errors.length) line('errors', errors.slice(0, 3).join(' | '))
  console.log('\nsync diagnostics:')
  for (const d of diags.slice(-8)) console.log('  ', d)

  chat.dispose()
  return received.length > 0 ? 0 : 1
}

main()
  .then((code) => {
    console.log(code === 0 ? '\nLIVE CHECK OK' : '\nLIVE CHECK: no messages synced')
    process.exit(code)
  })
  .catch((err) => {
    console.error('\nLIVE CHECK FAILED:', err.message)
    process.exit(2)
  })
