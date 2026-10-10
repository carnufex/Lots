import { useEffect, useRef } from 'react'

export type AvatarState = 'off' | 'listening' | 'hearing' | 'thinking' | 'speaking'

const COLORS: Record<AvatarState, [string, string]> = {
  off: ['#2a3039', '#1a1f26'],
  listening: ['#6cc4b2', '#2f6f63'],
  hearing: ['#8ee6a8', '#3f9d63'],
  thinking: ['#e0be6a', '#8d6f2a'],
  speaking: ['#7fb2ff', '#3a63b8'],
}

/**
 * The agent's presence in the conversation, drawn on a canvas: it breathes while listening, swells with your voice while you
 * speak, circles while it thinks and ripples with its own voice while it talks. `level` is read every frame (0..1) so the
 * animation follows the audio without re-rendering React.
 */
export default function Avatar({ state, level, size = 168 }: { state: AvatarState; level: { current: number }; size?: number }) {
  const canvas = useRef<HTMLCanvasElement>(null)
  const stateRef = useRef(state)
  useEffect(() => {
    stateRef.current = state
  }, [state])

  useEffect(() => {
    const el = canvas.current
    if (!el) return
    const dpr = window.devicePixelRatio || 1
    el.width = size * dpr
    el.height = size * dpr
    const g = el.getContext('2d')!
    g.scale(dpr, dpr)
    const reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches
    let smooth = 0
    let raf = 0

    const draw = (t: number) => {
      const s = stateRef.current
      const [c1, c2] = COLORS[s]
      const cx = size / 2
      const cy = size / 2
      const base = size * 0.27
      smooth = smooth * 0.72 + (reduced ? 0 : level.current) * 0.28
      g.clearRect(0, 0, size, size)

      // soft halo
      const halo = g.createRadialGradient(cx, cy, base * 0.6, cx, cy, size * 0.5)
      halo.addColorStop(0, c1 + (s === 'off' ? '22' : '55'))
      halo.addColorStop(1, c1 + '00')
      g.fillStyle = halo
      g.fillRect(0, 0, size, size)

      let r = base
      if (!reduced) {
        if (s === 'listening') r = base * (1 + 0.035 * Math.sin(t / 900))
        if (s === 'hearing') r = base * (1 + 0.28 * smooth)
      }

      // ripples
      if (!reduced && (s === 'listening' || s === 'hearing' || s === 'speaking')) {
        const rings = s === 'listening' ? 2 : 3
        for (let i = 0; i < rings; i++) {
          const phase = ((t / (s === 'listening' ? 2600 : 1500)) + i / rings) % 1
          g.beginPath()
          g.arc(cx, cy, r + phase * size * 0.2, 0, Math.PI * 2)
          g.strokeStyle = c1 + Math.round((1 - phase) * (s === 'listening' ? 40 : 90 * (0.3 + smooth))).toString(16).padStart(2, '0')
          g.lineWidth = 1.5
          g.stroke()
        }
      }

      // the orb (a wavy outline when speaking)
      const body = g.createRadialGradient(cx - r * 0.3, cy - r * 0.35, r * 0.1, cx, cy, r * 1.15)
      body.addColorStop(0, c1)
      body.addColorStop(1, c2)
      g.beginPath()
      if (s === 'speaking' && !reduced) {
        const amp = 0.05 + 0.3 * smooth
        for (let a = 0; a <= Math.PI * 2 + 0.05; a += 0.06) {
          const wob = (Math.sin(a * 3 + t / 190) + Math.sin(a * 5 - t / 310) + Math.sin(a * 2 + t / 520)) / 3
          const rr = r * (1 + amp * wob)
          const x = cx + Math.cos(a) * rr
          const y = cy + Math.sin(a) * rr
          if (a === 0) g.moveTo(x, y)
          else g.lineTo(x, y)
        }
        g.closePath()
      } else {
        g.arc(cx, cy, r, 0, Math.PI * 2)
      }
      g.fillStyle = body
      g.fill()

      if (s === 'off') {
        g.strokeStyle = '#3a424d'
        g.lineWidth = 2
        g.stroke()
      }

      // thinking: dots orbiting the orb
      if (s === 'thinking') {
        for (let i = 0; i < 3; i++) {
          const a = (reduced ? i * 2.1 : t / 520) + (i * Math.PI * 2) / 3
          g.beginPath()
          g.arc(cx + Math.cos(a) * (r + 16), cy + Math.sin(a) * (r + 16), 4, 0, Math.PI * 2)
          g.fillStyle = c1
          g.fill()
        }
      }

      if (!reduced || s !== stateRef.current) raf = requestAnimationFrame(draw)
    }
    raf = requestAnimationFrame(draw)
    return () => cancelAnimationFrame(raf)
  }, [level, size])

  return <canvas ref={canvas} className="avatar" style={{ width: size, height: size }} data-state={state} aria-hidden="true" />
}
