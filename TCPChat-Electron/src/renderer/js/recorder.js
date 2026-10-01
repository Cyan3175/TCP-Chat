/**
 * Microphone capture.
 *
 * Chromium records WebM/Opus, but the wire format has to stay playable by the C#
 * build — which classifies `.webm` as *video* and would show a black thumbnail
 * for an audio-only note. So a recording is decoded and re-encoded as 16-bit PCM
 * WAV before it is sent, matching the original `语音 N秒.wav` convention.
 *
 * A recording shorter than `MIN_MS` is discarded, as the C# build does.
 */

const MIN_MS = 700

let mediaRecorder = null
let audioContext = null
let stream = null
let chunks = []
let startedAt = 0

/** Encode an AudioBuffer as a 16-bit PCM WAV file. */
export function encodeWav(audioBuffer) {
  const channels = Math.min(2, audioBuffer.numberOfChannels)
  const frames = audioBuffer.length
  const sampleRate = audioBuffer.sampleRate
  const bytesPerSample = 2
  const blockAlign = channels * bytesPerSample
  const dataSize = frames * blockAlign

  const buffer = new ArrayBuffer(44 + dataSize)
  const view = new DataView(buffer)

  const writeAscii = (offset, text) => {
    for (let i = 0; i < text.length; i++) view.setUint8(offset + i, text.charCodeAt(i))
  }

  writeAscii(0, 'RIFF')
  view.setUint32(4, 36 + dataSize, true)
  writeAscii(8, 'WAVE')
  writeAscii(12, 'fmt ')
  view.setUint32(16, 16, true) // PCM chunk size
  view.setUint16(20, 1, true) // format = PCM
  view.setUint16(22, channels, true)
  view.setUint32(24, sampleRate, true)
  view.setUint32(28, sampleRate * blockAlign, true)
  view.setUint16(32, blockAlign, true)
  view.setUint16(34, 8 * bytesPerSample, true)
  writeAscii(36, 'data')
  view.setUint32(40, dataSize, true)

  const channelData = []
  for (let c = 0; c < channels; c++) channelData.push(audioBuffer.getChannelData(c))

  let offset = 44
  for (let i = 0; i < frames; i++) {
    for (let c = 0; c < channels; c++) {
      const sample = Math.max(-1, Math.min(1, channelData[c][i]))
      view.setInt16(offset, sample < 0 ? sample * 0x8000 : sample * 0x7fff, true)
      offset += 2
    }
  }
  return new Uint8Array(buffer)
}

async function blobToWav(blob) {
  const arrayBuffer = await blob.arrayBuffer()
  audioContext = audioContext ?? new (window.AudioContext || window.webkitAudioContext)()
  const decoded = await audioContext.decodeAudioData(arrayBuffer.slice(0))
  return encodeWav(decoded)
}

export const isRecording = () => mediaRecorder !== null

/**
 * Request the microphone and begin recording.
 * @returns {Promise<{ok: true} | {ok: false, error: string, denied: boolean}>}
 */
export async function startRecording() {
  if (mediaRecorder) return { ok: true }
  try {
    stream = await navigator.mediaDevices.getUserMedia({
      audio: { channelCount: 1, echoCancellation: true, noiseSuppression: true },
    })
  } catch (err) {
    const denied = err?.name === 'NotAllowedError' || err?.name === 'SecurityError'
    return {
      ok: false,
      denied,
      error: denied
        ? '没有麦克风权限。请在系统设置 → 隐私 → 麦克风里允许桌面应用访问。'
        : '没有找到可用的麦克风，或者它正被别的程序占用。',
    }
  }

  // Prefer a codec Chromium always supports; the container is irrelevant because
  // the audio is decoded and re-encoded as WAV before sending.
  const mimeType = ['audio/webm;codecs=opus', 'audio/webm', 'audio/ogg;codecs=opus'].find((type) =>
    window.MediaRecorder?.isTypeSupported?.(type),
  )

  try {
    mediaRecorder = new MediaRecorder(stream, mimeType ? { mimeType } : undefined)
  } catch {
    mediaRecorder = new MediaRecorder(stream)
  }

  chunks = []
  startedAt = performance.now()
  mediaRecorder.addEventListener('dataavailable', (event) => {
    if (event.data && event.data.size > 0) chunks.push(event.data)
  })
  mediaRecorder.start()
  return { ok: true }
}

/**
 * Stop and encode.
 * @returns {Promise<{ok: true, bytes: Uint8Array, durationMs: number, seconds: number}
 *                  | {ok: false, error: string}>}
 */
export async function stopRecording() {
  if (!mediaRecorder) return { ok: false, error: '没有正在进行的录音' }

  const recorder = mediaRecorder
  const durationMs = Math.round(performance.now() - startedAt)

  const blob = await new Promise((resolve) => {
    recorder.addEventListener('stop', () => {
      resolve(new Blob(chunks, { type: recorder.mimeType || 'audio/webm' }))
    })
    recorder.stop()
  })

  cleanup()

  if (durationMs < MIN_MS) {
    return { ok: false, error: '录音太短，已丢弃' }
  }

  try {
    const bytes = await blobToWav(blob)
    return { ok: true, bytes, durationMs, seconds: Math.round(durationMs / 1000) }
  } catch (err) {
    return { ok: false, error: `录音转码失败: ${err.message}` }
  }
}

/** Abort without producing anything. */
export function cancelRecording() {
  try {
    if (mediaRecorder && mediaRecorder.state !== 'inactive') mediaRecorder.stop()
  } catch {
    /* already stopped */
  }
  cleanup()
}

function cleanup() {
  mediaRecorder = null
  chunks = []
  if (stream) {
    for (const track of stream.getTracks()) track.stop()
    stream = null
  }
}

/** Open Windows' microphone privacy page, mirroring the C# build's behaviour. */
export async function openMicrophoneSettings() {
  try {
    await window.tcpchat.app.openMicSettings()
    return true
  } catch {
    return false
  }
}

/** Watch for the permission flipping to granted so recording can resume. */
export async function waitForMicrophonePermission(timeoutMs = 60000) {
  const started = Date.now()
  for (;;) {
    try {
      const status = await navigator.permissions.query({ name: 'microphone' })
      if (status.state === 'granted') return true
    } catch {
      // `microphone` is not queryable in every Chromium build; fall back to a probe.
      try {
        const probe = await navigator.mediaDevices.getUserMedia({ audio: true })
        for (const track of probe.getTracks()) track.stop()
        return true
      } catch {
        /* keep waiting */
      }
    }
    if (Date.now() - started > timeoutMs) return false
    await new Promise((resolve) => setTimeout(resolve, 1200))
  }
}
