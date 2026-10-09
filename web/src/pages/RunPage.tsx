import { useEffect, useState } from 'react'
import { isTerminal, type Api, type RunDetail, type Step } from '../api'

const POLL_MS = 1500

export default function RunPage({ api, id }: { api: Api; id: string }) {
  const [run, setRun] = useState<RunDetail | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    let cancelled = false
    let timer: number | undefined

    const tick = async () => {
      try {
        const r = await api.getRun(id)
        if (cancelled) return
        setRun(r)
        setError(null)
        if (!isTerminal(r.status)) timer = window.setTimeout(() => void tick(), POLL_MS)
      } catch (e) {
        if (cancelled) return
        setError(e instanceof Error ? e.message : String(e))
        timer = window.setTimeout(() => void tick(), POLL_MS * 3) // keep trying: transient errors happen
      }
    }
    void tick()

    return () => {
      cancelled = true
      if (timer) window.clearTimeout(timer)
    }
  }, [api, id])

  return (
    <section>
      <p>
        <a href="#/runs">← Runs</a>
      </p>
      {error && !run && (
        <p role="alert" className="error">
          Could not load this run: {error}
        </p>
      )}
      {!run && !error && <p className="muted">Loading…</p>}
      {run && <Detail run={run} reconnecting={error !== null} />}
    </section>
  )
}

function Detail({ run, reconnecting }: { run: RunDetail; reconnecting: boolean }) {
  const live = !isTerminal(run.status)
  const tokens = run.steps.reduce((n, s) => n + (s.promptTokens ?? 0) + (s.completionTokens ?? 0), 0)

  return (
    <>
      <h1 className="runtitle">{run.prompt}</h1>
      <div className="meta">
        <span className={`status ${run.status}`}>{run.status}</span>
        {live && <span className="muted">{run.status === 'WaitingForApproval' ? 'waiting for an approval…' : 'working…'}</span>}
        {reconnecting && <span className="warn">connection problem, retrying…</span>}
        <span className="muted mono">{run.steps.length} steps · {tokens} tokens</span>
      </div>

      {run.status === 'Completed' && (
        <div className="answer" aria-label="Answer">
          {run.finalAnswer}
        </div>
      )}
      {run.status === 'Failed' && (
        <div className="answer failed" role="alert">
          {run.error ?? 'The run failed.'}
        </div>
      )}

      <h2>Trace</h2>
      {run.steps.length === 0 ? (
        <p className="muted">No steps yet.</p>
      ) : (
        <ol className="trace">
          {run.steps.map((s) => (
            <StepRow key={s.seq} step={s} />
          ))}
        </ol>
      )}
    </>
  )
}

function StepRow({ step }: { step: Step }) {
  const isTool = step.kind === 'ToolCall'
  return (
    <li className={`step ${isTool ? 'tool' : 'model'}`}>
      <details>
        <summary>
          <span className="kind">{isTool ? 'tool' : 'model'}</span>
          <span className="name">{step.name}</span>
          <span className="muted mono">
            {step.latencyMs} ms
            {step.promptTokens != null && ` · ${step.promptTokens}+${step.completionTokens ?? 0} tok`}
          </span>
        </summary>
        {step.arguments && (
          <>
            <div className="label">{isTool ? 'Arguments' : 'Tool calls requested'}</div>
            <pre>{pretty(step.arguments)}</pre>
          </>
        )}
        {step.result && (
          <>
            <div className="label">{isTool ? 'Result' : 'Reply'}</div>
            <pre>{step.result}</pre>
          </>
        )}
        {!step.arguments && !step.result && <p className="muted">No content.</p>}
      </details>
    </li>
  )
}

function pretty(json: string): string {
  try {
    return JSON.stringify(JSON.parse(json), null, 2)
  } catch {
    return json
  }
}
