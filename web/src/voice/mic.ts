import { encodeWav } from './wav'

/**
 * An always-on microphone with voice activity detection ("microphone on"): it finds where an utterance starts and ends
 * so the conversation can run hands-free, and it reports when the user speaks while the agent is talking (barge-in).
 *
 * Detection is energy based with an adaptive noise floor: simple, fast and good enough for a close microphone. Browser
 * echo cancellation keeps the agent's own voice mostly out of the signal; while the agent speaks the thresholds are
 * raised and more sustained speech is required, so a stray word does not interrupt it.
 */

const FRAME_MS = 20
const PREROLL_FRAMES = 20 // 400 ms kept from before the speech started, so the first word is not clipped
const START_FRAMES = 5 // 100 ms above the threshold starts an utterance
const START_FRAMES_WHILE_PLAYING = 12 // 240 ms of sustained speech interrupts the agent
const END_SILENCE_MS = 700
const MIN_UTTERANCE_MS = 350
const MAX_UTTERANCE_MS = 30_000
const MIN_THRESHOLD = 0.012

const WORKLET = `
class PcmCapture extends AudioWorkletProcessor {
  constructor() { super(); this.size = Math.round(sampleRate * ${FRAME_MS} / 1000); this.buf = new Float32Array(this.size); this.n = 0 }
  process(inputs) {
    const ch = inputs[0] && inputs[0][0]
    if (!ch) return true
    for (let i = 0; i < ch.length; i++) {
      this.buf[this.n++] = ch[i]
      if (this.n === this.size) { this.port.postMessage(this.buf.slice()); this.n = 0 }
    }
    return true
  }
}
registerProcessor('pcm-capture', PcmCapture)
`

export interface MicCallbacks {
  /** 0..1, about every 20 ms: for the avatar. */
  onLevel(level: number): void
  /** The user started speaking. `duringPlayback` is true when the agent was talking (barge-in). */
  onSpeechStart(duringPlayback: boolean): void
  onUtterance(wav: Blob, durationMs: number): void
  /** The sound was too short to be speech (a click, a cough): nothing will be sent. */
  onAbort?(): void
}

export class MicSession {
  private ctx: AudioContext
  private stream: MediaStream
  private node: AudioWorkletNode
  private source: MediaStreamAudioSourceNode

  private preroll: Float32Array[] = []
  private utterance: Float32Array[] = []
  private speaking = false
  private above = 0
  private silentMs = 0
  private floor = 0.004
  private playback = false
  private sensitivity = 1
  private cb: MicCallbacks

  private constructor(ctx: AudioContext, stream: MediaStream, node: AudioWorkletNode, source: MediaStreamAudioSourceNode, cb: MicCallbacks) {
    this.cb = cb
    this.ctx = ctx
    this.stream = stream
    this.node = node
    this.source = source
    node.port.onmessage = (e: MessageEvent<Float32Array>) => this.frame(e.data)
  }

  static async start(cb: MicCallbacks, sensitivity = 1): Promise<MicSession> {
    const stream = await navigator.mediaDevices.getUserMedia({
      audio: { channelCount: 1, echoCancellation: true, noiseSuppression: true, autoGainControl: true },
    })
    const ctx = new AudioContext()
    const url = URL.createObjectURL(new Blob([WORKLET], { type: 'text/javascript' }))
    try {
      await ctx.audioWorklet.addModule(url)
    } finally {
      URL.revokeObjectURL(url)
    }
    const source = ctx.createMediaStreamSource(stream)
    const node = new AudioWorkletNode(ctx, 'pcm-capture')
    source.connect(node) // not connected to the speakers: we only listen
    if (ctx.state === 'suspended') await ctx.resume()
    const session = new MicSession(ctx, stream, node, source, cb)
    session.sensitivity = sensitivity
    return session
  }

  setSensitivity(value: number) {
    this.sensitivity = value
  }

  /** Tell the detector the agent is speaking (or not): speech then has to be louder and longer to count as an interruption. */
  setPlaybackActive(active: boolean) {
    this.playback = active
  }

  stop() {
    this.node.port.onmessage = null
    this.source.disconnect()
    this.node.disconnect()
    this.stream.getTracks().forEach((t) => t.stop())
    void this.ctx.close()
  }

  private threshold(): number {
    const base = Math.max(MIN_THRESHOLD, this.floor * 4) / this.sensitivity
    return this.playback ? base * 1.8 : base
  }

  private frame(samples: Float32Array) {
    let sum = 0
    for (let i = 0; i < samples.length; i++) sum += samples[i] * samples[i]
    const rms = Math.sqrt(sum / samples.length)
    this.cb.onLevel(Math.min(1, rms * 8))

    const threshold = this.threshold()
    const loud = rms >= threshold

    if (!this.speaking) {
      if (!loud) this.floor = Math.max(0.002, this.floor * 0.99 + rms * 0.01) // learn the room noise only between utterances
      this.preroll.push(samples)
      if (this.preroll.length > PREROLL_FRAMES) this.preroll.shift()
      this.above = loud ? this.above + 1 : 0
      if (this.above >= (this.playback ? START_FRAMES_WHILE_PLAYING : START_FRAMES)) {
        this.speaking = true
        this.silentMs = 0
        this.utterance = [...this.preroll]
        this.preroll = []
        this.cb.onSpeechStart(this.playback)
      }
      return
    }

    this.utterance.push(samples)
    this.silentMs = rms >= threshold * 0.6 ? 0 : this.silentMs + FRAME_MS
    const length = this.utterance.length * FRAME_MS
    if (this.silentMs >= END_SILENCE_MS || length >= MAX_UTTERANCE_MS) this.finish(length)
  }

  private finish(length: number) {
    const spoken = length - this.silentMs
    const frames = this.utterance
    this.utterance = []
    this.speaking = false
    this.above = 0
    this.silentMs = 0
    if (spoken < MIN_UTTERANCE_MS) {
      this.cb.onAbort?.() // a click or a cough, not speech
      return
    }
    this.cb.onUtterance(encodeWav(frames, this.ctx.sampleRate), length)
  }
}
