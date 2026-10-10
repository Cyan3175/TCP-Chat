/**
 * The rule that a message which will not decrypt is never shown.
 *
 *   node tests/hidden-messages.test.mjs
 *
 * A body that will not decrypt — the other side used a different password, or the
 * ciphertext was renamed or edited — is left out of the list entirely: no empty
 * bubble and no placeholder saying it cannot be read. It is still counted, and the
 * ciphertext stays in the local cache so a corrected password brings it back.
 *
 * That is a property of the chat service, so the service is driven here against a
 * stubbed transport and a stubbed local cache. Both ways a message can reach the
 * list are covered without a server: the cache replay at startup, and a fetch
 * during a sync round.
 */

import { createRequire } from 'node:module'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const require = createRequire(import.meta.url)
const here = path.dirname(fileURLToPath(import.meta.url))

// Keep the service's log away from the real profile.
process.env.TCPCHAT10_TEST_DATADIR = fs.mkdtempSync(path.join(os.tmpdir(), 'tcpchat-hidden-'))

const { ChatService } = require(path.join(here, '..', 'src', 'main', 'chat-service.js'))

let passed = 0
const failures = []

function check(name, actual, expected) {
  if (actual === expected) {
    passed += 1
    return true
  }
  failures.push({ name, actual, expected })
  return false
}

const FOLDER = '/chat'
const TIME = Date.UTC(2026, 9, 8, 12, 0, 0)
const NAME = `msg_${TIME}_deadbeef.json`
const BODY = {
  v: 2,
  id: 'deadbeef',
  from: '王五',
  time: new Date(TIME).toISOString(),
  text: '',
  quote: null,
  attach: null,
  enc: 'AESGCM1:AAECAwQFBgcICQoL',
}

/** Settings whose history window is wide enough that nothing expires mid-test. */
function makeSettings() {
  return {
    nwUrl: 'http://example.invalid',
    nwPassword: 'n/a',
    chatFolder: FOLDER,
    nickname: '我',
    pollSeconds: 3,
    historyDays: 36500,
    cryptoPasswords: [],
    sendPasswordIndex: 0,
  }
}

/** A cipher that never decrypts, or always returns `payload`. */
function stubCipher(payload) {
  return {
    tryDecryptPayload: () => payload ?? null,
    enabled: true,
    passwordCount: 1,
    sendPasswordNumber: 1,
  }
}

/** The parts of MessageCache the service touches, holding at most one entry. */
function stubCache(json, print = null) {
  const items = new Map()
  const prints = new Map()
  if (json) items.set(NAME, json)
  if (print) prints.set(NAME, print)
  return {
    names: () => [...items.keys()].sort(),
    get: (name) => items.get(name) ?? null,
    put: (name, value) => items.set(name, value),
    fingerprint: (name) => prints.get(name) ?? null,
    setFingerprint: (name, value) => prints.set(name, value),
    pruneMissing: () => [],
    save: () => {},
    describe: () => `缓存 ${items.size} 条`,
    get count() {
      return items.size
    },
  }
}

/** A listing entry for NAME, as the transport would report it. */
function listingEntry(size = 120) {
  return { name: NAME, isCollection: false, length: size, lastModified: new Date(TIME), etag: '"1"' }
}

function watch(chat) {
  const added = []
  const removed = []
  const errors = []
  chat.on('message-added', (m) => added.push(m))
  chat.on('message-removed', (n) => removed.push(n))
  chat.on('diag', () => {})
  chat.on('error', (m) => errors.push(m))
  return { added, removed, errors }
}

/** A service reading one message, with the given cipher and cache. */
function makeChat(cipher, cache) {
  const chat = new ChatService(makeSettings())
  chat.cipher = cipher
  chat._cache = cache
  chat.dav = {
    propFind: async () => [listingEntry()],
    getString: async () => JSON.stringify(BODY),
  }
  return chat
}

// --- the cache replay at startup -------------------------------------------

{
  const chat = makeChat(stubCipher(null), stubCache(JSON.stringify(BODY)))
  const { added, errors } = watch(chat)
  chat.emitCachedMessages()
  check('replay: an unreadable message is not shown', added.length, 0)
  check('replay: it is still counted', chat.undecryptableCount, 1)
  check('replay: nothing failed', errors.length, 0)
}

{
  const chat = makeChat(
    stubCipher({ text: '你好', quote: null, attach: null, from: '王五' }),
    stubCache(JSON.stringify(BODY)),
  )
  const { added } = watch(chat)
  chat.emitCachedMessages()
  check('replay: a readable message is shown', added.length, 1)
  check('replay: with its decrypted body', added[0]?.text ?? null, '你好')
  check('replay: nothing counted as unreadable', chat.undecryptableCount, 0)
}

// --- a sync round ----------------------------------------------------------

{
  const cache = stubCache(null)
  const chat = makeChat(stubCipher(null), cache)
  const { added, removed, errors } = watch(chat)
  await chat.syncOnce()
  check('sync: an unreadable message is not shown', added.length, 0)
  check('sync: it is still counted', chat.undecryptableCount, 1)
  check('sync: the ciphertext is kept for a later password fix', cache.get(NAME), JSON.stringify(BODY))
  // Announced as gone even though nothing was shown for it: the same channel a
  // withdrawal uses, and the only one that can take down a stale copy by name.
  check('sync: the name is announced gone', removed.includes(NAME), true)
  check('sync: nothing failed', errors.length, 0)
}

{
  const cache = stubCache(null)
  const chat = makeChat(
    stubCipher({ text: '在', quote: null, attach: null, from: '王五' }),
    cache,
  )
  const { added } = watch(chat)
  await chat.syncOnce()
  check('sync: a readable message is shown', added.length, 1)
  check('sync: with its decrypted body', added[0]?.text ?? null, '在')
  check('sync: nothing counted as unreadable', chat.undecryptableCount, 0)
}

/*
 * A copy that stops being readable.
 *
 * A withdrawal rewrites the record in place, so the name stays the same and only
 * the fingerprint moves. When the rewritten file no longer decrypts, the copy
 * already in the list has to come down with it.
 */
{
  const cache = stubCache(JSON.stringify(BODY), '1:1')
  const chat = makeChat(stubCipher(null), cache)
  chat._seen.add(NAME)
  const { added, removed } = watch(chat)
  await chat.syncOnce()
  check('changed file: nothing is shown for it', added.length, 0)
  check('changed file: the copy already in the list is taken back', removed.includes(NAME), true)
}

// ---------------------------------------------------------------------------

if (failures.length === 0) {
  console.log(`hidden messages: ${passed} checks passed`)
  process.exit(0)
}

console.error(`hidden messages: ${failures.length} of ${passed + failures.length} checks failed`)
for (const f of failures) {
  console.error(`  ${f.name}\n    actual:   ${f.actual}\n    expected: ${f.expected}`)
}
process.exit(1)
