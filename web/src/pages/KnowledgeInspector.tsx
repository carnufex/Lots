import { useEffect, useMemo, useState } from 'react'
import type { Api } from '../api'
import { t } from '../i18n'

/** The knowledge inspector (#158): retrieval stage by stage, chunks, index health and an embedding map. Inspector roles only. */

interface Ranked { chunkId: string; sourceId: string; title: string; heading: string; rank: number; raw: number }
interface Hit { chunkId: string; sourceId: string; sourceName: string; title: string; heading: string; text: string; score: number; vectorRank: number | null; textRank: number | null }
interface Trace {
  model: string | null
  dims: number
  searchedSources: string[]
  byVector: Ranked[]
  byText: Ranked[]
  final: Hit[]
  terms: string[]
  otherModelChunks: number
  hidden: { sourceId: string; name: string; matches: number }[]
}
interface Probe { chunkId: string; readable: boolean; similarity: number | null; vectorRank: number | null; textRank: number | null; termMatches: number; finalRank: number | null; rankedAbove: string[]; verdict: string }
interface SearchResult { trace: Trace; explanation: string | null; readers: string[]; probe: Probe | null }
export interface ChunkInfo { id: string; seq: number; heading: string; text: string; chars: number; tokens: number; model: string; dims: number; norm: number; flags: string[] }
interface Health {
  currentModel: string | null
  currentDims: number | null
  chunks: number
  documents: number
  sources: { id: string; name: string; status: string; error: string | null; documents: number; chunks: number; models: Record<string, number>; onCurrentModel: number; notIndexed: number }[]
  issues: { kind: string; sourceId: string; documentId: string | null; chunkId: string | null; detail: string }[]
}
interface MapData { model: string; sampled: number; points: { chunkId: string; sourceId: string; title: string; heading: string; x: number; y: number }[]; query: { x: number; y: number } | null; neighbours: string[] }

export function Playground({ api }: { api: Api }) {
  const [query, setQuery] = useState('')
  const [k, setK] = useState(4)
  const [asUser, setAsUser] = useState('')
  const [asRoles, setAsRoles] = useState('')
  const [probe, setProbe] = useState('')
  const [result, setResult] = useState<SearchResult | null>(null)
  const [error, setError] = useState<string | null>(null)
  const run = async (probeId = probe) => {
    setError(null)
    try {
      setResult(await api.raw<SearchResult>('/knowledge/inspect/search', {
        method: 'POST',
        body: JSON.stringify({ query, k, probe: probeId || undefined, asUser: asUser || undefined, asRoles: asRoles ? asRoles.split(',').map((r) => r.trim()).filter(Boolean) : undefined }),
      }))
    } catch (e) {
      setError(String(e))
    }
  }
  return (
    <>
      <form className="filters" onSubmit={(e) => { e.preventDefault(); void run() }}>
        <label>
          {t('Query')}
          <input className="wide" value={query} onChange={(e) => setQuery(e.target.value)} required />
        </label>
        <label>
          k
          <input type="number" min={1} max={20} value={k} onChange={(e) => setK(Number(e.target.value))} />
        </label>
        <label>
          {t('As user (optional)')}
          <input value={asUser} onChange={(e) => setAsUser(e.target.value)} placeholder="alice" />
        </label>
        <label>
          {t('with roles')}
          <input value={asRoles} onChange={(e) => setAsRoles(e.target.value)} placeholder="operator" />
        </label>
        <label>
          {t('Why not this chunk? (id)')}
          <input value={probe} onChange={(e) => setProbe(e.target.value)} />
        </label>
        <button className="btn primary" type="submit">{t('Search')}</button>
      </form>
      <p className="small muted">{t('The exact retrieval the agent does (same code as search_knowledge), with every stage.')}</p>
      {error && <p className="error">{error}</p>}
      {result && (
        <>
          <p className="small">
            {t('Model')} <span className="mono">{result.trace.model ?? '–'}</span> ({result.trace.dims}) · {t('readers')} <span className="mono">{result.readers.join(' ')}</span> ·{' '}
            {t('searched')} {result.trace.searchedSources.join(', ') || '–'} · {t('terms')} <span className="mono">{result.trace.terms.join(' ')}</span>
          </p>
          {result.explanation && <p className="warn small">{t('Why:')} {result.explanation}</p>}
          {result.probe && (
            <div className="card small">
              <strong>{result.probe.chunkId}</strong>: {result.probe.verdict}
              <span className="muted"> · {t('similarity')} {result.probe.similarity ?? '–'} · {t('vector rank')} {result.probe.vectorRank ?? '–'} · {t('word rank')} {result.probe.textRank ?? '–'} · {t('words shared')} {result.probe.termMatches}</span>
              {result.probe.rankedAbove.length > 0 && <div className="muted mono">{t('ranked above:')} {result.probe.rankedAbove.join(', ')}</div>}
            </div>
          )}
          <div className="inspect-grid">
            <Ranking title={t('By vector')} rows={result.trace.byVector} onProbe={(id) => { setProbe(id); void run(id) }} />
            <Ranking title={t('By words')} rows={result.trace.byText} onProbe={(id) => { setProbe(id); void run(id) }} />
            <div className="card">
              <h3>{t('Final (sent to the model)')}</h3>
              <ol className="small">
                {result.trace.final.map((h) => (
                  <li key={h.chunkId}>
                    <strong>{h.title}</strong>{h.heading && h.heading !== h.title ? ` › ${h.heading}` : ''} <span className="muted mono">{h.chunkId} · rrf {h.score} · v{h.vectorRank ?? '–'}/t{h.textRank ?? '–'}</span>
                    <div className="muted">{h.text.slice(0, 240)}{h.text.length > 240 ? '…' : ''}</div>
                  </li>
                ))}
              </ol>
              {result.trace.hidden.length > 0 && (
                <p className="small muted">{t('Hidden by access rights:')} {result.trace.hidden.map((h) => `${h.name} (${h.matches})`).join(', ')}</p>
              )}
            </div>
          </div>
        </>
      )}
    </>
  )
}

function Ranking({ title, rows, onProbe }: { title: string; rows: Ranked[]; onProbe: (id: string) => void }) {
  return (
    <div className="card">
      <h3>{title}</h3>
      {rows.length === 0 ? <p className="muted small">{t('Nothing.')}</p> : (
        <ol className="small">
          {rows.slice(0, 12).map((r) => (
            <li key={r.chunkId}>
              {r.title}{r.heading && r.heading !== r.title ? ` › ${r.heading}` : ''} <span className="muted mono">{r.raw}</span>{' '}
              <button type="button" className="btn ghost small" onClick={() => onProbe(r.chunkId)}>{t('probe')}</button>
            </li>
          ))}
        </ol>
      )}
    </div>
  )
}

export function ChunkList({ api, documentId }: { api: Api; documentId: string }) {
  const [chunks, setChunks] = useState<ChunkInfo[] | null>(null)
  useEffect(() => void api.raw<ChunkInfo[]>(`/knowledge/inspect/documents/${documentId}/chunks`).then(setChunks, () => setChunks([])), [api, documentId])
  if (chunks === null) return <p className="muted small">{t('Loading…')}</p>
  return (
    <ol className="chunks small">
      {chunks.map((c) => (
        <li key={c.id} className={c.flags.length > 0 ? 'flagged' : undefined}>
          <div className="muted mono">
            #{c.seq} {c.heading} · {c.chars} {t('chars')} · ~{c.tokens} {t('tokens')} · {c.model} · {c.dims}d · ‖v‖ {c.norm} · {c.id}
            {c.flags.map((f) => <span key={f} className="decision Denied"> {f}</span>)}
          </div>
          <div>{c.text}</div>
        </li>
      ))}
    </ol>
  )
}

export function HealthTab({ api }: { api: Api }) {
  const [health, setHealth] = useState<Health | null>(null)
  const [error, setError] = useState<string | null>(null)
  const load = () => void api.raw<Health>('/knowledge/inspect/health').then(setHealth, (e: unknown) => setError(String(e)))
  useEffect(load, [api]) // eslint-disable-line react-hooks/exhaustive-deps
  if (error) return <p className="error">{error}</p>
  if (!health) return <p className="muted">{t('Loading…')}</p>
  const kinds = Object.entries(health.issues.reduce<Record<string, number>>((acc, i) => ({ ...acc, [i.kind]: (acc[i.kind] ?? 0) + 1 }), {}))
  return (
    <>
      <p className="small">
        {t('{docs} documents, {chunks} chunks; query model', { docs: health.documents, chunks: health.chunks })} <span className="mono">{health.currentModel ?? '–'}</span> ({health.currentDims ?? '?'}d)
      </p>
      <table>
        <thead>
          <tr><th>{t('Source')}</th><th>{t('Status')}</th><th>{t('Documents')}</th><th>{t('Chunks')}</th><th>{t('On the query model')}</th><th>{t('Models')}</th><th /></tr>
        </thead>
        <tbody>
          {health.sources.map((s) => (
            <tr key={s.id}>
              <td>{s.name}</td>
              <td>{s.status}{s.error && <span className="error small"> {s.error}</span>}</td>
              <td>{s.documents}{s.notIndexed > 0 && <span className="warn small"> ({s.notIndexed} {t('not indexed')})</span>}</td>
              <td>{s.chunks}</td>
              <td>{s.chunks === 0 ? '–' : `${Math.round((s.onCurrentModel / s.chunks) * 100)} %`}</td>
              <td className="mono small">{Object.entries(s.models).map(([m, n]) => `${m}: ${n}`).join(', ')}</td>
              <td>
                {s.onCurrentModel < s.chunks && (
                  <button type="button" className="btn small" onClick={() => void api.raw(`/knowledge/sources/${s.id}/reindex`, { method: 'POST', body: '{}' }).then(load)}>
                    {t('Re-embed')}
                  </button>
                )}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      <h2 className="section-title">{t('Issues')}</h2>
      {health.issues.length === 0 ? <p className="muted">{t('No issues found.')}</p> : (
        <>
          <p className="small">{kinds.map(([k, n]) => `${k}: ${n}`).join(' · ')}</p>
          <ul className="plain small">
            {health.issues.slice(0, 200).map((i, n) => (
              <li key={n}><span className="decision Denied">{i.kind}</span> <span className="muted">{i.sourceId}</span> {i.detail}</li>
            ))}
          </ul>
        </>
      )}
    </>
  )
}

const PALETTE = ['#2dd4bf', '#60a5fa', '#fbbf24', '#c084fc', '#f87171', '#34d399', '#f472b6', '#a3e635']

export function MapTab({ api }: { api: Api }) {
  const [query, setQuery] = useState('')
  const [data, setData] = useState<MapData | null>(null)
  const [picked, setPicked] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const load = (q = '') => void api.raw<MapData>(`/knowledge/inspect/map${q ? `?query=${encodeURIComponent(q)}` : ''}`).then(setData, (e: unknown) => setError(String(e)))
  useEffect(() => load(), [api]) // eslint-disable-line react-hooks/exhaustive-deps
  const sources = useMemo(() => [...new Set(data?.points.map((p) => p.sourceId) ?? [])], [data])
  if (error) return <p className="error">{error}</p>
  if (!data) return <p className="muted">{t('Loading…')}</p>
  const all = [...data.points, ...(data.query ? [{ ...data.query, chunkId: 'query', sourceId: '', title: query, heading: '' }] : [])]
  const xs = all.map((p) => p.x), ys = all.map((p) => p.y)
  const [minX, maxX, minY, maxY] = [Math.min(...xs), Math.max(...xs), Math.min(...ys), Math.max(...ys)]
  const sx = (x: number) => 20 + ((x - minX) / (maxX - minX || 1)) * 560
  const sy = (y: number) => 20 + ((y - minY) / (maxY - minY || 1)) * 360
  const pick = data.points.find((p) => p.chunkId === picked)
  return (
    <>
      <form className="filters" onSubmit={(e) => { e.preventDefault(); load(query) }}>
        <label>{t('Place a query')}<input className="wide" value={query} onChange={(e) => setQuery(e.target.value)} /></label>
        <button className="btn" type="submit">{t('Show')}</button>
      </form>
      <p className="small muted">{t('{n} chunks of {model}, projected to 2D (PCA). Click a point to see the chunk.', { n: data.sampled, model: data.model })}</p>
      <svg className="embedmap" viewBox="0 0 600 400" role="img" aria-label={t('Embedding map')}>
        {data.points.map((p) => (
          <circle key={p.chunkId} cx={sx(p.x)} cy={sy(p.y)} r={data.neighbours.includes(p.chunkId) ? 6 : 4} fill={PALETTE[sources.indexOf(p.sourceId) % PALETTE.length]}
            stroke={data.neighbours.includes(p.chunkId) ? 'var(--text)' : 'none'} onClick={() => setPicked(p.chunkId)}>
            <title>{p.title}{p.heading && p.heading !== p.title ? ` › ${p.heading}` : ''}</title>
          </circle>
        ))}
        {data.query && <path d={`M${sx(data.query.x) - 7},${sy(data.query.y)}h14M${sx(data.query.x)},${sy(data.query.y) - 7}v14`} stroke="var(--danger)" strokeWidth={3} />}
      </svg>
      <p className="small">
        {sources.map((s, i) => <span key={s}><span className="dot" style={{ background: PALETTE[i % PALETTE.length] }} /> {s} </span>)}
      </p>
      {pick && <p className="small"><strong>{pick.title}</strong> › {pick.heading} <span className="muted mono">{pick.chunkId}</span></p>}
    </>
  )
}
