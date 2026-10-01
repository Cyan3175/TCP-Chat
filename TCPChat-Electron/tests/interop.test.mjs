/**
 * Wire-compatibility test: the Electron implementation vs. the real C# build.
 *
 *   node tests/interop.test.mjs
 *
 * The harness in `interop-csharp/` compiles the *unmodified* `MessageCrypto.cs`
 * and `ChatMessage.cs` from `TCPChat10/` into a small console app. This test
 * drives it with a batch of operations and cross-checks every result against
 * `src/main/crypto.js`, so compatibility is proven against the shipped code
 * rather than against a re-reading of the algorithm.
 *
 * Requires the .NET SDK. Skips (exit 0) with a notice when it is unavailable.
 */

import { execFileSync } from 'node:child_process'
import { createRequire } from 'node:module'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const require = createRequire(import.meta.url)
const here = path.dirname(fileURLToPath(import.meta.url))
const root = path.join(here, '..')

const crypto = require(path.join(root, 'src', 'main', 'crypto.js'))

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

function checkTrue(name, value, detail = '') {
  return check(name, value ? 'true' : `false ${detail}`, 'true')
}

// ---------------------------------------------------------------------------
// Locate the C# harness
// ---------------------------------------------------------------------------

const harnessDir = path.join(here, 'interop-csharp')
const candidates = [
  path.join(harnessDir, 'bin', 'Release', 'net10.0', 'interop.dll'),
  path.join(harnessDir, 'bin', 'Debug', 'net10.0', 'interop.dll'),
]
const harness = candidates.find((p) => fs.existsSync(p))

if (!harness) {
  console.log('[interop] C# harness not built.')
  console.log('[interop] run:  dotnet build -c Release tests/interop-csharp')
  console.log('[interop] SKIPPED')
  process.exit(0)
}

/** Run a batch of operations through the C# harness. */
function runCsharp(ops) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'tcpchat-interop-'))
  const req = path.join(dir, 'request.json')
  const res = path.join(dir, 'response.json')
  fs.writeFileSync(req, JSON.stringify({ ops }), 'utf8')
  try {
    execFileSync('dotnet', [harness, req, res], { stdio: ['ignore', 'ignore', 'inherit'] })
    return JSON.parse(fs.readFileSync(res, 'utf8')).results
  } finally {
    fs.rmSync(dir, { recursive: true, force: true })
  }
}

// ---------------------------------------------------------------------------
// Fixtures
// ---------------------------------------------------------------------------

const SALT_SEED = 'nw集训/学生资料临存/tcp_chat'
const PASSWORD = 'hunter2-密码'
const AAD = 'msg_1767225600000_ab12cd34.json'

const ops = [
  { op: 'key', password: PASSWORD, saltSeed: SALT_SEED },
  { op: 'key', password: 'simple', saltSeed: SALT_SEED },
  { op: 'key', password: 'x', saltSeed: 'other/folder' },
  { op: 'enc', password: PASSWORD, saltSeed: SALT_SEED, aad: AAD, text: '你好，世界 🌍 line2\nline3' },
  { op: 'encb', password: PASSWORD, saltSeed: SALT_SEED, aad: 'att_1_abc.png', b64: Buffer.from([0, 1, 2, 250, 251, 252, 253]).toString('base64') },
]

const results = runCsharp(ops)
const byOp = {}
results.forEach((r, i) => {
  byOp[`${r.op}#${i}`] = r
})

// --- PBKDF2 derivation ------------------------------------------------------

check(
  'PBKDF2 key (UTF-8 password + CJK folder salt) matches C#',
  crypto.deriveKeySync(PASSWORD, SALT_SEED).toString('hex'),
  results[0].key,
)
check(
  'PBKDF2 key (ascii password) matches C#',
  crypto.deriveKeySync('simple', SALT_SEED).toString('hex'),
  results[1].key,
)
check(
  'PBKDF2 key (short password, different folder) matches C#',
  crypto.deriveKeySync('x', 'other/folder').toString('hex'),
  results[2].key,
)

// --- C# encrypts, Node decrypts --------------------------------------------

const csharpEnvelope = results[3].envelope
checkTrue('C# produced an AESGCM1 envelope', typeof csharpEnvelope === 'string' && csharpEnvelope.startsWith('AESGCM1:'))

const nodeKey = crypto.deriveKeySync(PASSWORD, SALT_SEED)
check(
  'Node decrypts a C#-encrypted body',
  crypto.tryDecryptText(nodeKey, AAD, csharpEnvelope),
  '你好，世界 🌍 line2\nline3',
)
check(
  'Node rejects a C# envelope under the wrong AAD',
  String(crypto.tryDecryptText(nodeKey, 'msg_other.json', csharpEnvelope)),
  'null',
)
check(
  'Node rejects a C# envelope under the wrong password',
  String(crypto.tryDecryptText(crypto.deriveKeySync('wrong', SALT_SEED), AAD, csharpEnvelope)),
  'null',
)

// --- Node encrypts, C# decrypts --------------------------------------------

const nodeEnvelope = crypto.encryptText(nodeKey, AAD, '反向验证 reverse check ✓')
const reverse = runCsharp([
  { op: 'dec', password: PASSWORD, saltSeed: SALT_SEED, aad: AAD, envelope: nodeEnvelope },
  { op: 'dec', password: PASSWORD, saltSeed: SALT_SEED, aad: 'msg_wrong.json', envelope: nodeEnvelope },
])
check('C# decrypts a Node-encrypted body', reverse[0].text, '反向验证 reverse check ✓')
check('C# rejects a Node envelope under the wrong AAD', String(reverse[1].text), 'null')

// --- Attachment bytes -------------------------------------------------------

const blob = Buffer.from([0, 1, 2, 250, 251, 252, 253])
const csharpBlob = Buffer.from(results[4].b64, 'base64')
check('Node decrypts C#-encrypted attachment bytes', crypto.tryDecryptBytes(nodeKey, 'att_1_abc.png', csharpBlob).toString('hex'), blob.toString('hex'))

const nodeBlob = crypto.encryptBytes(nodeKey, 'att_1_abc.png', blob)
const blobCheck = runCsharp([
  { op: 'decb', password: PASSWORD, saltSeed: SALT_SEED, aad: 'att_1_abc.png', b64: nodeBlob.toString('base64') },
])
check('C# decrypts Node-encrypted attachment bytes', Buffer.from(blobCheck[0].b64, 'base64').toString('hex'), blob.toString('hex'))

// --- Full payload envelope (what actually lands in `enc`) -------------------

const payloadSpec = {
  op: 'payload',
  password: PASSWORD,
  saltSeed: SALT_SEED,
  aad: AAD,
  from: '张三',
  text: '带引用的正文 **bold**',
  quote: '李四: 上一条',
  attach: { name: '语音 0秒.wav', path: `${SALT_SEED}/att_9_zzz.wav`, size: 64000, kind: 5, dur: 7400 },
}

const payloadResult = runCsharp([payloadSpec])[0]
checkTrue('C# produced a payload envelope', typeof payloadResult.envelope === 'string')

const decoded = crypto.parsePayload(crypto.tryDecryptText(nodeKey, AAD, payloadResult.envelope))
check('Node decrypts a C# payload: from', decoded?.from, '张三')
check('Node decrypts a C# payload: text', decoded?.text, '带引用的正文 **bold**')
check('Node decrypts a C# payload: quote', decoded?.quote, '李四: 上一条')
check('Node decrypts a C# payload: attachment name', decoded?.attach?.name, '语音 0秒.wav')
check('Node decrypts a C# payload: attachment path', decoded?.attach?.path, `${SALT_SEED}/att_9_zzz.wav`)
check('Node decrypts a C# payload: attachment size', String(decoded?.attach?.size), '64000')
check('Node decrypts a C# payload: attachment kind', String(decoded?.attach?.kind), '5')
check('Node decrypts a C# payload: attachment duration', String(decoded?.attach?.durationMs), '7400')

// --- Node payload -> C# MessageCipher (password list + send index) ----------

async function nodePayloadEnvelope() {
  const cipher = await crypto.MessageCipher.create(['old-pass', PASSWORD, ''], 1, SALT_SEED)
  return cipher.encryptPayload(AAD, '自测用户', '来自 Node 的消息', '引用内容', {
    name: '题解.pdf',
    path: `${SALT_SEED}/att_1_x_题解.pdf`,
    size: 2621440,
    kind: 1,
    durationMs: 0,
  })
}

const nodePayload = await nodePayloadEnvelope()
const unpayload = runCsharp([
  {
    op: 'unpayload',
    saltSeed: SALT_SEED,
    aad: AAD,
    envelope: nodePayload,
    passwords: ['old-pass', PASSWORD, ''],
    sendIndex: 1,
  },
])
const p = unpayload[0].payload
checkTrue('C# MessageCipher decrypts a Node payload', p !== null)
check('C# reads Node payload: from', p?.from, '自测用户')
check('C# reads Node payload: text', p?.text, '来自 Node 的消息')
check('C# reads Node payload: quote', p?.quote, '引用内容')
check('C# reads Node payload: attachment name', p?.attach?.name, '题解.pdf')
check('C# reads Node payload: attachment size', String(p?.attach?.size), '2621440')
check('C# reads Node payload: attachment kind', String(p?.attach?.kind), '1')

// --- Password-list semantics ------------------------------------------------

// A blank entry means "send in the clear" while other passwords still decrypt.
const blankSend = await crypto.MessageCipher.create(['old-pass', PASSWORD, ''], 2, SALT_SEED)
check('blank send entry disables encryption', String(blankSend.enabled), 'false')
check('blank send entry still allows decryption', String(blankSend.passwordCount), '2')
check('sendPasswordNumber is 0 when sending in the clear', String(blankSend.sendPasswordNumber), '0')

const encryptedSend = await crypto.MessageCipher.create(['old-pass', PASSWORD, ''], 1, SALT_SEED)
check('non-blank send entry enables encryption', String(encryptedSend.enabled), 'true')
check('sendPasswordNumber reflects the chosen index', String(encryptedSend.sendPasswordNumber), '2')

// ---------------------------------------------------------------------------
// Report
// ---------------------------------------------------------------------------

console.log('')
if (failures.length === 0) {
  console.log(`[interop] OK — ${passed} checks passed against the C# build`)
  process.exit(0)
}
console.log(`[interop] FAILED — ${passed} passed, ${failures.length} failed`)
for (const f of failures) {
  console.log(`  ✗ ${f.name}\n      expected: ${f.expected}\n      actual:   ${f.actual}`)
}
process.exit(1)
