#!/usr/bin/env node
'use strict'

/**
 * Build the vendored native glass addon.
 *
 *   npm run build:native
 *
 * The addon is a node-gyp project, and two things about building it are not
 * obvious:
 *
 *   * it must be built against **Electron's** headers, not Node's. Building
 *     against Node's produces a binary that loads and runs, but whose N-API
 *     return values are garbage: `createPanel` handed back a panel id of
 *     6.2e-317 (an integer's bit pattern read as a double), so `Int32Value()`
 *     returned 0 and every id-taking call silently did nothing.
 *   * Node 24's `common.gypi` pins `msbuild_toolset: ClangCL`, so the ClangCL
 *     component has to be present or MSBuild fails with MSB8020. Node-gyp also
 *     still imports `distutils`, which Python 3.12 removed — `setuptools`
 *     supplies it.
 *
 * Set PYTHON to a real interpreter if the Microsoft Store stub is on PATH.
 */

const { spawnSync, execFileSync } = require('child_process')
const fs = require('fs')
const path = require('path')

const root = path.join(__dirname, '..')
const moduleDir = path.join(root, 'vendor', 'electron-liquid-glass')
// Read the version rather than require() it: this file is an ES module.
const electronVersion = JSON.parse(
  fs.readFileSync(path.join(root, 'node_modules', 'electron', 'package.json'), 'utf8'),
).version

if (!fs.existsSync(moduleDir)) {
  console.error(`native module source missing: ${moduleDir}`)
  process.exit(1)
}

/**
 * Find a usable Python 3, ignoring the Microsoft Store stub.
 *
 * The list used to name Python312 and Python311 outright, which meant it stopped
 * working the moment either was replaced — it did, when 3.12 was removed and 3.15
 * installed. The minor version is not something this script should have an
 * opinion about, so it now looks for whatever Python3* directories exist and
 * takes the newest, and falls back to the launcher.
 */
function findPython() {
  if (process.env.PYTHON && fs.existsSync(process.env.PYTHON)) return process.env.PYTHON

  const roots = [
    path.join(process.env.LOCALAPPDATA || '', 'Programs', 'Python'),
    'C:\\',
    path.join(process.env.ProgramFiles || 'C:\\Program Files', 'Python'),
  ]

  const found = []
  for (const root of roots) {
    let entries = []
    try {
      entries = fs.readdirSync(root, { withFileTypes: true })
    } catch {
      continue // not installed there, which is the normal case for most of them
    }
    for (const entry of entries) {
      if (!entry.isDirectory() || !/^Python3\d*$/i.test(entry.name)) continue
      const exe = path.join(root, entry.name, 'python.exe')
      if (fs.existsSync(exe)) found.push({ exe, version: Number((entry.name.match(/\d+$/) || [0])[0]) })
    }
  }
  if (found.length) {
    // Newest first: a machine can hold several, and the addon should build with
    // the one that is actually current.
    found.sort((a, b) => b.version - a.version)
    return found[0].exe
  }

  // The launcher, last: it is on PATH by default and knows where everything is.
  for (const candidate of ['py']) {
    try {
      execFileSync(candidate, ['-3', '-c', 'pass'], { stdio: 'ignore' })
      return candidate
    } catch {
      /* no launcher */
    }
  }
  return null
}

const python = findPython()
if (!python) {
  console.error('No Python 3 found. Install it and set PYTHON to the interpreter path.')
  process.exit(1)
}

const args = [
  require.resolve('node-gyp/bin/node-gyp.js'),
  'rebuild',
  `--target=${electronVersion}`,
  '--dist-url=https://electronjs.org/headers',
  '--arch=x64',
]

console.log(`> building native glass addon for Electron ${electronVersion}`)
console.log(`  cwd: ${moduleDir}`)
console.log(`  python: ${python}`)

// Run node-gyp's entry script with the current Node rather than going through
// npx: npx.cmd needs a shell on Windows, and the extra layer only obscures the
// failure when the build does not go through.
const result = spawnSync(process.execPath, args, {
  cwd: moduleDir,
  stdio: 'inherit',
  env: { ...process.env, PYTHON: python },
})

if (result.status !== 0) {
  console.error('\nnative build failed')
  process.exit(result.status ?? 1)
}

const built = path.join(moduleDir, 'build', 'Release', 'liquid_glass.node')
if (!fs.existsSync(built)) {
  console.error(`build reported success but ${built} is missing`)
  process.exit(1)
}
console.log(`\nbuilt ${built} (${Math.round(fs.statSync(built).size / 1024)} KB)`)
