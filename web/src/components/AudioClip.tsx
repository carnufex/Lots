import { useEffect, useRef, useState } from 'react'
import type { Api } from '../api'

/**
 * One stored audio clip (ADR 0014): waveform, play/pause, seek by clicking the waveform, speed. The audio is fetched with the
 * caller's credentials (playback is audited server-side) and kept only in this page's memory.
 */
export default function AudioClip({ api, id, label }: { api: Api; id: string; label: string }) {
  const [url, setUrl] = useState<string | null>(null)
  const [peaks, setPeaks] = useState<number[]>([])
  const [error, setError] = useState(false)
  const [playing, setPlaying] = useState(false)
  const [progress, setProgress] = useState(0)
  const [rate, setRate] = useState(1)
  const audio = useRef<HTMLAudioElement | null>(null)

  const load = async () => {
    try {
      const res = await fetch(`/conversations/audio/${id}`, { headers: await api.authHeaders() })
      if (!res.ok) throw new Error(String(res.status))
      const blob = await res.blob()
      setUrl(URL.createObjectURL(blob))
      try {
        const ctx = new AudioContext()
        const buffer = await ctx.decodeAudioData(await blob.arrayBuffer())
        const data = buffer.getChannelData(0)
        const bins = 120
        const step = Math.max(1, Math.floor(data.length / bins))
        setPeaks(Array.from({ length: bins }, (_, i) => {
          let max = 0
          for (let j = i * step; j < Math.min(data.length, (i + 1) * step); j++) max = Math.max(max, Math.abs(data[j]))
          return max
        }))
        void ctx.close()
      } catch {
        setPeaks([]) // a format the browser cannot decode for the waveform still plays
      }
      return true
    } catch {
      setError(true)
      return false
    }
  }

  useEffect(() => () => {
    if (url) URL.revokeObjectURL(url)
  }, [url])

  const toggle = async () => {
    if (!url && !(await load())) return
    const a = audio.current
    if (!a) return
    if (a.paused) void a.play()
    else a.pause()
  }

  // Start playback once the first load created the element.
  useEffect(() => {
    if (url && audio.current && !playing && progress === 0) void audio.current.play()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [url])

  const max = Math.max(...peaks, 0.01)
  return (
    <span className="clip">
      <button type="button" className="btn small" onClick={() => void toggle()} aria-label={`${playing ? 'Pause' : 'Play'} ${label}`} disabled={error}>
        {error ? 'unavailable' : playing ? '❚❚' : '▶'} {label}
      </button>
      {url && (
        <>
          <svg
            className="wave"
            viewBox={`0 0 ${peaks.length || 1} 20`}
            preserveAspectRatio="none"
            onClick={(e) => {
              const a = audio.current
              if (!a || !a.duration) return
              const r = e.currentTarget.getBoundingClientRect()
              a.currentTime = ((e.clientX - r.left) / r.width) * a.duration
            }}
            role="slider"
            aria-label="Seek"
            aria-valuenow={Math.round(progress * 100)}
          >
            {peaks.map((p, i) => (
              <rect key={i} x={i} y={10 - (p / max) * 9} width={0.7} height={Math.max(0.5, (p / max) * 18)} className={i / peaks.length <= progress ? 'played' : undefined} />
            ))}
          </svg>
          <select
            value={rate}
            aria-label="Speed"
            onChange={(e) => {
              setRate(Number(e.target.value))
              if (audio.current) audio.current.playbackRate = Number(e.target.value)
            }}
          >
            {[0.75, 1, 1.25, 1.5, 2].map((r) => (
              <option key={r} value={r}>
                {r}×
              </option>
            ))}
          </select>
          <audio
            ref={audio}
            src={url}
            onPlay={() => setPlaying(true)}
            onPause={() => setPlaying(false)}
            onEnded={() => (setPlaying(false), setProgress(0))}
            onTimeUpdate={(e) => setProgress(e.currentTarget.duration ? e.currentTarget.currentTime / e.currentTarget.duration : 0)}
          />
        </>
      )}
    </span>
  )
}
