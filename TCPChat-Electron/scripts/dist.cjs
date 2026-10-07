#!/usr/bin/env node
'use strict'

/*
 * Package the app, no matter which `node` the shell happens to hand us.
 *
 * `node` on this machine resolves to a harness shim
 * (%APPDATA%\dsh-desktop\harness\.desktop-bin\node.cmd) that starts Electron,
 * not Node.js. Electron rewrites process.argv, which is fatal for electron-
 * builder: its CLI sees its own script path as a positional argument and exits
 * with
 *
 *   Unknown argument: <repo>\node_modules\electron-builder\cli.js
 *
 * The API path fails differently — the asar step reports
 * `Invalid package ...\resources\default_app.asar` — but it is the same cause:
 * the process is not the Node the tooling expects.
 *
 * So this re-executes itself under a real interpreter before doing anything, and
 * only then drives electron-builder. Run it directly with the real Node and the
 * re-exec is skipped.
 *
 * Usage: node scripts/dist.cjs
 *   Builds the targets in electron-builder.yml: nsis + portable, plus
 *   release/win-unpacked.
 *
 * There is no unpacked-only mode: electron-builder falls back to the
 * configured targets whenever createTarget is given an empty list, and its
 * API has no `dir` option. A flag that silently built everything anyway
 * would be worse than not having one.
 */

const fs = require('fs')
const path = require('path')
const { spawnSync } = require('child_process')

const PROJECT_DIR = path.resolve(__dirname, '..')

/**
 * A Node.js that is not the Electron shim.
 *
 * Checked in order of how likely each is to be the real thing; the shim is
 * rejected by path because it lives under the harness directory.
 */
function findRealNode() {
  const candidates = []

  const programFiles = process.env.ProgramFiles || 'C:\\Program Files'
  const programFilesX86 = process.env['ProgramFiles(x86)'] || 'C:\\Program Files (x86)'
  candidates.push(path.join(programFiles, 'nodejs', 'node.exe'))
  candidates.push(path.join(programFilesX86, 'nodejs', 'node.exe'))

  // Anything named node.exe on PATH except the shim.
  const where = spawnSync('where.exe', ['node.exe'], { encoding: 'utf8', windowsHide: true })
  if (where.status === 0 && where.stdout) {
    for (const line of where.stdout.split(/\r?\n/)) {
      const trimmed = line.trim()
      if (!trimmed) continue
      if (/dsh-desktop|DSH Desktop/i.test(trimmed)) continue
      candidates.push(trimmed)
    }
  }

  for (const candidate of candidates) {
    try {
      if (!fs.statSync(candidate).isFile()) continue
    } catch {
      continue
    }
    // Must be Node, not Electron. `-p` is the cheapest way to ask.
    const probe = spawnSync(candidate, ['-p', 'process.versions.electron || "node"'], {
      encoding: 'utf8',
      windowsHide: true,
    })
    if (probe.status === 0 && probe.stdout.trim() === 'node') return candidate
  }

  return null
}

function reexecUnderRealNode() {
  const real = findRealNode()
  if (!real) {
    console.error(
      'Could not find a real Node.js.\n' +
        '`node` on PATH is the harness Electron shim, and Electron rewrites\n' +
        'process.argv, which electron-builder cannot cope with.\n' +
        'Install Node.js, or run this script with one explicitly.',
    )
    process.exit(1)
  }

  console.log(`  running under ${real}`)
  const result = spawnSync(
    real,
    ['--use-system-ca', __filename, ...process.argv.slice(2)],
    { stdio: 'inherit', windowsHide: true },
  )
  process.exit(result.status === null ? 1 : result.status)
}

// ---------------------------------------------------------------------------

if (process.versions.electron) {
  // We are the shim. Hand off and get out of the way.
  reexecUnderRealNode()
}

// From here on we are a real Node, so electron-builder behaves.
const { build, Platform, Arch } = require('electron-builder')


async function main() {
  const files = await build({
    // Installer only. The portable target is deliberately not here.
    //
    // Removing it from electron-builder.yml is not enough: this list is what
    // electron-builder is actually asked for, and it wins. Leaving 'portable' in
    // kept producing a 107 MB executable under the target's default name — with
    // no "portable" in the filename, so it did not look like one.
    targets: Platform.WINDOWS.createTarget(
      ['nsis'],
      Arch.x64,
    ),
    publish: 'never',
    projectDir: PROJECT_DIR,
  })

  for (const file of files) {
    console.log(`  built ${path.relative(PROJECT_DIR, file)}`)
  }
}

main().catch((err) => {
  console.error('packaging failed:', err && err.message ? err.message : err)
  process.exit(1)
})
