#!/usr/bin/env node
'use strict'

/**
 * Launch Electron with a sanitised environment.
 *
 *   node scripts/run-electron.cjs <script-or-app-dir> [...args]
 *
 * Electron treats the mere *presence* of `ELECTRON_RUN_AS_NODE` as "behave as
 * plain Node", even when it is set to an empty string — which some shells and
 * CI environments do. The symptom is baffling: `require('electron')` returns the
 * path to the binary instead of the API, so the script dies with
 * "Cannot read properties of undefined (reading 'whenReady')" while clearly
 * running under Electron.
 *
 * Removing the variable here means `npm run test:glass` and `npm run selftest`
 * behave the same everywhere.
 */

const { spawn } = require('child_process')
const fs = require('fs')
const path = require('path')

const root = path.join(__dirname, '..')

const binary = (() => {
  try {
    // `electron` exports the path to the platform binary.
    const resolved = require(path.join(root, 'node_modules', 'electron'))
    return typeof resolved === 'string' ? resolved : null
  } catch {
    return null
  }
})()

if (!binary || !fs.existsSync(binary)) {
  console.error('Electron binary not found. Run `npm install` first.')
  process.exit(1)
}

const env = { ...process.env }
delete env.ELECTRON_RUN_AS_NODE

const args = process.argv.slice(2)
if (!args.length) {
  console.error('usage: node scripts/run-electron.cjs <script-or-app-dir> [...args]')
  process.exit(1)
}

const child = spawn(binary, args, { cwd: root, env, stdio: 'inherit' })
child.on('exit', (code, signal) => {
  if (signal) process.kill(process.pid, signal)
  else process.exit(code ?? 0)
})
