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

const { spawnSync } = require('child_process')
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

/** Find a usable Python 3, ignoring the Microsoft Store stub. */
function findPython() {
  if (process.env.PYTHON && fs.existsSync(process.env.PYTHON)) return process.env.PYTHON
  const candidates = [
    path.join(process.env.LOCALAPPDATA || '', 'Programs', 'Python', 'Python312', 'python.exe'),
    path.join(process.env.LOCALAPPDATA || '', 'Programs', 'Python', 'Python311', 'python.exe'),
    'C:\\Python312\\python.exe',
    'C:\\Python311\\python.exe',
  ]
  for (const candidate of candidates) {
    if (candidate && fs.existsSync(candidate)) return candidate
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
