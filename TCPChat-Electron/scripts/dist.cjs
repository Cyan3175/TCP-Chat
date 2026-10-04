'use strict'

/*
 * Package the app through electron-builder's API instead of its CLI.
 *
 * The CLI is unusable here: `electron-builder --version` works, but any command
 * that actually builds exits with
 *
 *   Unknown argument: <repo>\node_modules\electron-builder\cli.js
 *
 * so the build re-spawns itself with its own script path in the argument list.
 * Going straight to the API skips the argument parsing that trips over it.
 *
 * Usage: node scripts/dist.cjs [--unpacked]
 *   default     nsis + portable installers, plus release/win-unpacked
 *   --unpacked  stop after the unpacked directory (much faster, for checks)
 */

const path = require('path')

const { build, Platform, Arch } = require('electron-builder')

const unpackedOnly = process.argv.includes('--unpacked')

async function main() {
  const options = {
    targets: unpackedOnly
      ? Platform.WINDOWS.createTarget(undefined, Arch.x64)
      : Platform.WINDOWS.createTarget(['nsis', 'portable'], Arch.x64),
    publish: 'never',
    projectDir: path.resolve(__dirname, '..'),
  }

  const files = await build(options)
  for (const file of files) {
    console.log(`  built ${path.relative(process.cwd(), file)}`)
  }
}

main().catch((err) => {
  console.error('packaging failed:', err && err.message ? err.message : err)
  process.exit(1)
})