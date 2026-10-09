import { useState } from 'react'
import type { Api } from '../api'
import type { ProfileInfo } from '../config'

export default function NewRun({ api, profiles }: { api: Api; profiles: ProfileInfo[] }) {
  const [prompt, setPrompt] = useState('')
  const [profile, setProfile] = useState(profiles[0]?.name ?? '')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const submit = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!prompt.trim() || busy) return
    setBusy(true)
    setError(null)
    try {
      const run = await api.startRun(prompt.trim(), profile)
      window.location.hash = `#/runs/${run.id}`
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
      setBusy(false)
    }
  }

  return (
    <form className="newrun" onSubmit={(e) => void submit(e)}>
      <label className="sr" htmlFor="prompt">
        Question
      </label>
      <textarea
        id="prompt"
        rows={2}
        placeholder="Ask something, e.g. which containers are unhealthy?"
        value={prompt}
        onChange={(e) => setPrompt(e.target.value)}
        onKeyDown={(e) => {
          if (e.key === 'Enter' && (e.metaKey || e.ctrlKey)) void submit(e)
        }}
      />
      <div className="row">
        {profiles.length > 1 ? (
          <select aria-label="Profile" value={profile} onChange={(e) => setProfile(e.target.value)}>
            {profiles.map((p) => (
              <option key={p.name} value={p.name}>
                {p.name}
              </option>
            ))}
          </select>
        ) : (
          <span className="muted">{profile}</span>
        )}
        <span className="grow" />
        {error && (
          <span role="alert" className="error">
            {error}
          </span>
        )}
        <button className="btn primary" type="submit" disabled={busy || !prompt.trim()}>
          {busy ? 'Starting…' : 'Start run'}
        </button>
      </div>
    </form>
  )
}
