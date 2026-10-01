'use strict'

/**
 * Diagnostic logging. Everything lands in `%LOCALAPPDATA%\TCPChat\crash.log`
 * (the same file the C# build writes), and is additionally echoed to stderr when
 * `TCPCHAT_VERBOSE` is set.
 *
 * Logging must never itself become a failure path. Two guards make that true:
 *
 *   * every write is wrapped — a closed stderr pipe (the parent process went
 *     away) raises EPIPE, and an unguarded throw inside the `uncaughtException`
 *     handler re-enters the handler, which writes again, which throws again:
 *     an unbounded loop that filled a 2.5 GB log file in seconds.
 *   * a re-entrancy flag stops a failure inside the logger from recursing.
 */

const fs = require('fs')
const { logFile } = require('./paths')

/*
 * A closed stdout/stderr pipe (the launching shell exited) raises EPIPE
 * asynchronously as an `error` event on the stream — not as a throw from
 * `write()`. Unhandled, that becomes an `uncaughtException`, whose handler logs,
 * which writes to the same broken pipe, which raises again.
 *
 * Swallowing the event once here removes the whole class of failure.
 */
for (const stream of [process.stdout, process.stderr]) {
  try {
    stream.on('error', () => {})
  } catch {
    /* not a stream in some embedders */
  }
}

let stream = null
let opening = false
const pending = []
/** Set while a write is in flight, so a failure inside it cannot recurse. */
let writing = false
let disabled = false

function openStream() {
  if (stream || opening) return stream
  opening = true
  try {
    stream = fs.createWriteStream(logFile(), { flags: 'a' })
    // The stream outlives the process if it errors; never let it raise.
    stream.on('error', () => {
      disabled = true
      stream = null
    })
    for (const line of pending.splice(0)) {
      try {
        stream.write(line)
      } catch {
        break
      }
    }
  } catch {
    stream = null
    disabled = true
  } finally {
    opening = false
  }
  return stream
}

function stamp() {
  const d = new Date()
  const pad = (n, w = 2) => String(n).padStart(w, '0')
  return (
    `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ` +
    `${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}.${pad(d.getMilliseconds(), 3)}`
  )
}

function format(args) {
  return args
    .map((a) => {
      if (a instanceof Error) return `${a.name}: ${a.message}\n${a.stack || ''}`
      if (typeof a === 'string') return a
      try {
        return JSON.stringify(a)
      } catch {
        return String(a)
      }
    })
    .join(' ')
}

function write(level, args, options = {}) {
  if (writing || disabled) return
  writing = true
  try {
    const line = `[${stamp()}] [${level}] ${format(args)}\n`

    const s = openStream()
    if (s) {
      try {
        s.write(line)
      } catch {
        disabled = true
        stream = null
      }
    } else if (pending.length < 512) {
      // Keep a bounded buffer: an unwritable location must not grow unbounded.
      pending.push(line)
    }

    // Crash reports go to the file only: echoing them to a terminal that just
    // went away is what produced the original feedback loop.
    if (process.env.TCPCHAT_VERBOSE && !options.fileOnly) {
      try {
        process.stderr.write(line)
      } catch {
        // Covered by the stream error handler above; file logging continues.
      }
    }
  } catch {
    disabled = true
  } finally {
    writing = false
  }
}

const log = {
  info: (...args) => write('info', args),
  warn: (...args) => write('warn', args),
  error: (...args) => write('error', args),
  /** Log and swallow: used on paths where a failure must not break the feature. */
  guard(label, fn) {
    try {
      return fn()
    } catch (err) {
      write('error', [`${label} failed`, err])
      return undefined
    }
  },
  file: logFile,
}

/**
 * Route unhandled failures into the log instead of killing the session.
 *
 * These handlers are intentionally minimal: they must not throw, or Node will
 * invoke them again for the new failure. A repeated failure is counted and then
 * dropped, so a hot loop cannot spin the process.
 */
function installCrashHandlers() {
  let recent = 0
  let windowStart = Date.now()

  const report = (kind, value) => {
    const now = Date.now()
    if (now - windowStart > 5000) {
      windowStart = now
      recent = 0
    }
    recent += 1
    if (recent > 20) return // Something is looping; stay quiet rather than spin.
    write('fatal', [kind, value instanceof Error ? value : String(value)], { fileOnly: true })
  }

  process.on('uncaughtException', (err) => report('uncaughtException', err))
  process.on('unhandledRejection', (reason) => report('unhandledRejection', reason))
}

module.exports = { log, installCrashHandlers }
