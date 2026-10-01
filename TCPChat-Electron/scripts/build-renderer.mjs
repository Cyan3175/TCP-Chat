/**
 * Renderer build.
 *
 * Bundles `src/renderer/js/main.js` into `src/renderer/dist/app.js` and copies
 * KaTeX's stylesheet plus its fonts next to it. The stylesheets in
 * `src/renderer/styles/` are authored as plain CSS and linked directly, so they
 * need no build step.
 *
 * Usage:
 *   node scripts/build-renderer.mjs [--watch]
 */

import * as esbuild from 'esbuild'
import fs from 'node:fs'
import path from 'node:path'
import { execFileSync } from 'node:child_process'
import { fileURLToPath } from 'node:url'

const here = path.dirname(fileURLToPath(import.meta.url))
const root = path.join(here, '..')
const outDir = path.join(root, 'src', 'renderer', 'dist')
const watch = process.argv.includes('--watch')

/**
 * Chromium major bundled with the installed Electron.
 *
 * Electron ships no machine-readable Chromium version — `dist/version` holds the
 * Electron version only, and the credits page has none — so this is a table.
 * Probing the runtime would be exact but needs a child process with piped stdio,
 * which hangs in some sandboxed environments.
 *
 * The table failing loudly is the point: an unknown Electron must not silently
 * inherit a stale target, because a target *below* the runtime ships needlessly
 * down-levelled code and one *above* it can emit syntax the runtime lacks.
 * Add the pair when bumping Electron, or set ELECTRON_CHROME=<major>.
 */
const CHROMIUM_BY_ELECTRON = {
  33: 130,
  44: 152,
}

const ELECTRON_CHROME = (() => {
  if (process.env.ELECTRON_CHROME) return String(process.env.ELECTRON_CHROME)

  let electronVersion
  try {
    electronVersion = JSON.parse(
      fs.readFileSync(path.join(root, 'node_modules', 'electron', 'package.json'), 'utf8'),
    ).version
  } catch {
    throw new Error('electron is not installed — run `npm install` first')
  }

  const major = Number(String(electronVersion).split('.')[0])
  const chrome = CHROMIUM_BY_ELECTRON[major]
  if (!chrome) {
    throw new Error(
      `no Chromium version recorded for Electron ${electronVersion}. ` +
        `Add \`${major}: <chromium-major>\` to CHROMIUM_BY_ELECTRON in this file, ` +
        `or run with ELECTRON_CHROME=<major>.`,
    )
  }
  return String(chrome)
})()

fs.mkdirSync(outDir, { recursive: true })

/** KaTeX ships its own CSS + fonts; copy them so the app needs no CDN. */
function copyKatex() {
  const katexDist = path.join(root, 'node_modules', 'katex', 'dist')
  if (!fs.existsSync(katexDist)) {
    console.warn('[build] katex not installed yet — skipping its stylesheet')
    return
  }
  const dest = path.join(outDir, 'katex')
  fs.mkdirSync(path.join(dest, 'fonts'), { recursive: true })
  fs.copyFileSync(path.join(katexDist, 'katex.min.css'), path.join(dest, 'katex.min.css'))
  const fontsDir = path.join(katexDist, 'fonts')
  if (fs.existsSync(fontsDir)) {
    for (const file of fs.readdirSync(fontsDir)) {
      if (!/\.(woff2?|ttf)$/i.test(file)) continue
      fs.copyFileSync(path.join(fontsDir, file), path.join(dest, 'fonts', file))
    }
  }
  console.log(`[build] copied KaTeX stylesheet + fonts -> ${path.relative(root, dest)}`)
}

const options = {
  entryPoints: [path.join(root, 'src', 'renderer', 'js', 'main.js')],
  outfile: path.join(outDir, 'app.js'),
  bundle: true,
  format: 'iife',
  /*
   * Matches Electron's bundled Chromium (see ELECTRON_CHROME below). Keeping it
   * exact means esbuild only down-levels what the runtime genuinely lacks,
   * instead of emulating features Chromium already has natively.
   */
  target: [`chrome${ELECTRON_CHROME}`],
  platform: 'browser',
  sourcemap: true,
  logLevel: 'info',
  legalComments: 'none',
  // Keep the bundle readable in DevTools without shipping unminified sources.
  minify: !watch,
  define: { 'process.env.NODE_ENV': watch ? '"development"' : '"production"' },
}

copyKatex()

if (watch) {
  const ctx = await esbuild.context(options)
  await ctx.watch()
  console.log('[build] watching renderer sources…')
} else {
  await esbuild.build(options)
  const size = fs.statSync(path.join(outDir, 'app.js')).size
  console.log(`[build] renderer bundle: ${(size / 1024).toFixed(1)} kB`)
}
