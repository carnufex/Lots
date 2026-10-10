/** Encodes mono float samples (-1..1) as a 16-bit PCM WAV file. */
export function encodeWav(frames: Float32Array[], sampleRate: number): Blob {
  const samples = frames.reduce((n, f) => n + f.length, 0)
  const buffer = new ArrayBuffer(44 + samples * 2)
  const view = new DataView(buffer)
  const text = (offset: number, s: string) => {
    for (let i = 0; i < s.length; i++) view.setUint8(offset + i, s.charCodeAt(i))
  }
  text(0, 'RIFF')
  view.setUint32(4, 36 + samples * 2, true)
  text(8, 'WAVE')
  text(12, 'fmt ')
  view.setUint32(16, 16, true)
  view.setUint16(20, 1, true) // PCM
  view.setUint16(22, 1, true) // mono
  view.setUint32(24, sampleRate, true)
  view.setUint32(28, sampleRate * 2, true)
  view.setUint16(32, 2, true)
  view.setUint16(34, 16, true)
  text(36, 'data')
  view.setUint32(40, samples * 2, true)

  let offset = 44
  for (const frame of frames) {
    for (let i = 0; i < frame.length; i++) {
      const s = Math.max(-1, Math.min(1, frame[i]))
      view.setInt16(offset, s < 0 ? s * 0x8000 : s * 0x7fff, true)
      offset += 2
    }
  }
  return new Blob([buffer], { type: 'audio/wav' })
}
