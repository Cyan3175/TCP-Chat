'use strict'

/**
 * Data-directory resolution, mirroring the C# build's layout exactly so the two
 * applications can share one profile:
 *
 *   %LOCALAPPDATA%\TCPChat\settings.json      settings
 *   %LOCALAPPDATA%\TCPChat\crash.log          crash / diagnostic log
 *   %LOCALAPPDATA%\TCPChat\cache\             attachment + voice cache
 *   %LOCALAPPDATA%\TCPChat\cache\msgs_*.json  per-folder message cache
 *
 * The 10.0–10.6 directory name was `TCPChat10`; its contents are migrated once
 * on first run and the old directory is removed, same as `AppSettings.MigrateLegacyDataDir`.
 */

const fs = require('fs')
const os = require('os')
const path = require('path')

const DIR_NAME = 'TCPChat'
const LEGACY_DIR_NAME = 'TCPChat10'

function localAppData() {
  return process.env.LOCALAPPDATA || path.join(os.homedir(), 'AppData', 'Local')
}

/** Test hooks (same names the C# unit tests use) so runs can be redirected. */
function dataDir() {
  const override = process.env.TCPCHAT10_TEST_DATADIR
  if (override && override.length > 0) return override
  return path.join(localAppData(), DIR_NAME)
}

function legacyDataDir() {
  const override = process.env.TCPCHAT10_TEST_LEGACYDIR
  if (override && override.length > 0) return override
  return path.join(localAppData(), LEGACY_DIR_NAME)
}

function settingsFile() {
  const override = process.env.TCPCHAT10_TEST_SETTINGS
  if (override && override.length > 0) return override
  return path.join(dataDir(), 'settings.json')
}

let migrated = false

/**
 * 10.7 moved the profile from `%LOCALAPPDATA%\TCPChat10` to `%LOCALAPPDATA%\TCPChat`.
 * Copy settings + attachment cache across once, then drop the old directory.
 * Never throws: a failed migration just means we keep running from defaults.
 */
function migrateLegacyDataDir() {
  if (migrated) return
  migrated = true
  try {
    const oldDir = legacyDataDir()
    const newDir = dataDir()
    if (path.resolve(oldDir).toLowerCase() === path.resolve(newDir).toLowerCase()) return
    if (!fs.existsSync(oldDir)) return

    fs.mkdirSync(newDir, { recursive: true })

    const oldSettings = path.join(oldDir, 'settings.json')
    const newSettings = path.join(newDir, 'settings.json')
    if (fs.existsSync(oldSettings) && !fs.existsSync(newSettings)) {
      fs.copyFileSync(oldSettings, newSettings)
    }

    const oldCache = path.join(oldDir, 'cache')
    const newCache = path.join(newDir, 'cache')
    if (fs.existsSync(oldCache)) {
      fs.mkdirSync(newCache, { recursive: true })
      for (const entry of fs.readdirSync(oldCache, { withFileTypes: true })) {
        if (!entry.isFile()) continue
        const dest = path.join(newCache, entry.name)
        if (fs.existsSync(dest)) continue
        try {
          fs.copyFileSync(path.join(oldCache, entry.name), dest)
        } catch {
          /* a single unreadable cache file must not abort the migration */
        }
      }
    }

    try {
      fs.rmSync(oldDir, { recursive: true, force: true })
    } catch {
      /* leaving the old directory behind is harmless */
    }
  } catch {
    /* running from defaults is fine */
  }
}

/** `%LOCALAPPDATA%\TCPChat`, created on demand. */
function ensureDataDir() {
  migrateLegacyDataDir()
  const dir = dataDir()
  fs.mkdirSync(dir, { recursive: true })
  return dir
}

/** Attachment / voice / message-cache directory, created on demand. */
function cacheDir() {
  const dir = path.join(ensureDataDir(), 'cache')
  fs.mkdirSync(dir, { recursive: true })
  return dir
}

function logFile() {
  return path.join(ensureDataDir(), 'crash.log')
}

module.exports = {
  DIR_NAME,
  LEGACY_DIR_NAME,
  dataDir,
  legacyDataDir,
  ensureDataDir,
  cacheDir,
  settingsFile,
  logFile,
  migrateLegacyDataDir,
}
