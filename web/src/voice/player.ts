/** Plays the agent's voice and exposes its level (for the avatar) and a quick stop (for barge-in). */
export class Player {
  private ctx: AudioContext | null = null
  private analyser: AnalyserNode | null = null
  private current: { el: HTMLAudioElement; url: string; done: () => void } | null = null
  private data: Uint8Array<ArrayBuffer> | null = null

  /** Create the audio context from a user gesture (turning the microphone on) so playback is allowed later. */
  prepare() {
    if (this.ctx) return
    this.ctx = new AudioContext()
    this.analyser = this.ctx.createAnalyser()
    this.analyser.fftSize = 512
    this.data = new Uint8Array(new ArrayBuffer(this.analyser.fftSize))
    this.analyser.connect(this.ctx.destination)
  }

  get playing(): boolean {
    return this.current !== null
  }

  /** 0..1 loudness of what is playing right now. */
  level(): number {
    if (!this.analyser || !this.data || !this.current) return 0
    this.analyser.getByteTimeDomainData(this.data)
    let sum = 0
    for (const v of this.data) {
      const x = (v - 128) / 128
      sum += x * x
    }
    return Math.min(1, Math.sqrt(sum / this.data.length) * 4)
  }

  /** Resolves when the audio has ended or was stopped. */
  async play(blob: Blob): Promise<void> {
    this.prepare()
    this.stop()
    if (this.ctx!.state === 'suspended') await this.ctx!.resume()
    const url = URL.createObjectURL(blob)
    const el = new Audio(url)
    const source = this.ctx!.createMediaElementSource(el)
    source.connect(this.analyser!)
    await new Promise<void>((resolve) => {
      const done = () => {
        if (this.current?.el === el) this.current = null
        source.disconnect()
        URL.revokeObjectURL(url)
        resolve()
      }
      this.current = { el, url, done }
      el.onended = done
      el.onerror = done
      el.play().catch(done)
    })
  }

  /** Stops immediately (a short fade avoids a click). */
  stop() {
    const c = this.current
    if (!c) return
    this.current = null
    const el = c.el
    const started = performance.now()
    const fade = () => {
      const t = (performance.now() - started) / 90
      if (t >= 1) {
        el.pause()
        c.done()
        return
      }
      el.volume = Math.max(0, 1 - t)
      requestAnimationFrame(fade)
    }
    fade()
  }

  dispose() {
    this.stop()
    void this.ctx?.close()
    this.ctx = null
  }
}
