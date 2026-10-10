import { useCallback, useEffect, useState } from 'react'
import { ConflictList } from '../components/ConflictVote'
import { ApiError, type Api, type KnowledgeDoc, type KnowledgeHit, type KnowledgeOverview, type KnowledgeSource } from '../api'

const statusClass: Record<string, string> = { ready: 'Allowed', failed: 'Denied', queued: 'ApprovalRequested', indexing: 'ApprovalRequested' }

const message = (e: unknown) => (e instanceof Error ? e.message : String(e))

/** Knowledge sources the user may read, their indexing status, documents, and a search to try retrieval with the user's own access. */
export default function KnowledgePage({ api }: { api: Api }) {
  const [overview, setOverview] = useState<KnowledgeOverview | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [open, setOpen] = useState<string | null>(null)

  const reload = useCallback(() => {
    api
      .knowledge()
      .then((o) => {
        setOverview(o)
        setError(null)
      })
      .catch((e: unknown) => setError(message(e)))
  }, [api])

  useEffect(() => {
    reload()
    // Indexing runs in the background: refresh while something is queued or indexing.
    const t = window.setInterval(() => {
      if (overview?.sources.some((s) => s.status === 'queued' || s.status === 'indexing')) reload()
    }, 2000)
    return () => window.clearInterval(t)
  }, [reload, overview])

  return (
    <section>
      <h1>Knowledge</h1>
      {error && (
        <p role="alert" className="error">
          Could not load knowledge: {error}
        </p>
      )}
      {overview && (
        <>
          <p className="muted small">
            {overview.embeddingsConfigured ? (
              <>
                Embeddings: <span className="mono">{overview.embedModel}</span> · storage: {overview.backend}
                {overview.vectorExtension ? ' with pgvector' : ''}
              </>
            ) : (
              <span className="warn">No embedding model is configured (Models:Aliases:embed): search is off.</span>
            )}
            {overview.note && <span className="warn"> · {overview.note}</span>}
          </p>

          <TryIt api={api} sources={overview.sources} />

          <h2 className="section-title">Sources</h2>
          {overview.sources.length === 0 ? (
            <div className="empty">
              <p>No sources you can read yet.</p>
            </div>
          ) : (
            <table>
              <thead>
                <tr>
                  <th>Source</th>
                  <th>Kind</th>
                  <th>Status</th>
                  <th>Documents</th>
                  <th>Chunks</th>
                  <th>Readers</th>
                  <th>Indexed</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                {overview.sources.map((s) => (
                  <SourceRow key={s.id} api={api} source={s} open={open === s.id} onToggle={() => setOpen(open === s.id ? null : s.id)} onChange={reload} />
                ))}
              </tbody>
            </table>
          )}

          <NewSource api={api} onCreated={reload} />
          <ConflictList api={api} />
        </>
      )}
    </section>
  )
}

function SourceRow({ api, source: s, open, onToggle, onChange }: { api: Api; source: KnowledgeSource; open: boolean; onToggle: () => void; onChange: () => void }) {
  const [busy, setBusy] = useState(false)
  const act = async (f: () => Promise<unknown>) => {
    setBusy(true)
    try {
      await f()
    } finally {
      setBusy(false)
      onChange()
    }
  }
  return (
    <>
      <tr>
        <td>
          <button type="button" className="btn ghost" aria-expanded={open} onClick={onToggle}>
            {s.name}
          </button>
          {s.personal && <span className="muted small"> personal</span>}
          {s.managedBy === 'config' && <span className="muted small"> config</span>}
        </td>
        <td>{s.kind}</td>
        <td title={s.error ?? undefined}>
          <span className={`decision ${statusClass[s.status] ?? 'ApprovalRequested'}`}>{s.status}</span>
        </td>
        <td>{s.documents}</td>
        <td>{s.chunks}</td>
        <td className="muted small">{s.readers.join(', ')}</td>
        <td className="muted">{s.indexedAt ? new Date(s.indexedAt).toLocaleString() : ''}</td>
        <td>
          {(s.canManage || s.managedBy === 'config') && (
            <button type="button" className="btn small" disabled={busy} onClick={() => void act(() => api.reindexSource(s.id))}>
              Re-index
            </button>
          )}{' '}
          {s.canManage && (
            <button
              type="button"
              className="btn small"
              disabled={busy}
              onClick={() => {
                if (window.confirm(`Delete the source "${s.name}" and everything indexed from it?`)) void act(() => api.deleteSource(s.id))
              }}
            >
              Delete
            </button>
          )}
        </td>
      </tr>
      {s.error && (
        <tr>
          <td colSpan={8} className="error small">
            {s.error}
          </td>
        </tr>
      )}
      {open && (
        <tr>
          <td colSpan={8}>
            <Documents api={api} source={s} onChange={onChange} />
          </td>
        </tr>
      )}
    </>
  )
}

function Documents({ api, source, onChange }: { api: Api; source: KnowledgeSource; onChange: () => void }) {
  const [docs, setDocs] = useState<KnowledgeDoc[] | null>(null)
  const [title, setTitle] = useState('')
  const [text, setText] = useState('')
  const [error, setError] = useState<string | null>(null)
  const load = useCallback(() => {
    api.listDocuments(source.id).then(setDocs).catch((e: unknown) => setError(message(e)))
  }, [api, source.id])
  useEffect(load, [load, source.documents])

  const upload = async (files: FileList | null) => {
    setError(null)
    try {
      for (const f of Array.from(files ?? [])) await api.uploadFile(source.id, f)
      onChange()
      load()
    } catch (e) {
      setError(message(e))
    }
  }

  return (
    <div>
      {docs === null ? (
        <p className="muted">Loading…</p>
      ) : docs.length === 0 ? (
        <p className="muted">No documents.</p>
      ) : (
        <ul className="plain">
          {docs.map((d) => (
            <li key={d.id}>
              {d.url ? (
                <a href={d.url} target="_blank" rel="noreferrer noopener">
                  {d.title}
                </a>
              ) : (
                d.title
              )}{' '}
              <span className="muted small mono">
                {d.externalId} · {d.chars} chars
              </span>
              {source.canManage && source.kind === 'upload' && (
                <button
                  type="button"
                  className="btn ghost small"
                  onClick={() => void api.deleteDocument(source.id, d.id).then(() => (onChange(), load()))}
                >
                  remove
                </button>
              )}
            </li>
          ))}
        </ul>
      )}
      {source.canManage && source.kind === 'upload' && (
        <div className="filters">
          <label>
            Upload (md, txt, html, docx)
            <input type="file" multiple accept=".md,.markdown,.txt,.html,.htm,.docx" onChange={(e) => void upload(e.target.files)} />
          </label>
          <label>
            Title
            <input value={title} onChange={(e) => setTitle(e.target.value)} />
          </label>
          <label className="grow">
            Text
            <textarea rows={3} value={text} onChange={(e) => setText(e.target.value)} />
          </label>
          <button
            type="button"
            className="btn"
            disabled={!title.trim() || !text.trim()}
            onClick={() =>
              void api
                .addText(source.id, title.trim(), text)
                .then(() => {
                  setTitle('')
                  setText('')
                  onChange()
                  load()
                })
                .catch((e: unknown) => setError(message(e)))
            }
          >
            Add text
          </button>
        </div>
      )}
      {error && (
        <p role="alert" className="error small">
          {error}
        </p>
      )}
    </div>
  )
}

function NewSource({ api, onCreated }: { api: Api; onCreated: () => void }) {
  const [name, setName] = useState('')
  const [kind, setKind] = useState<'personal' | 'upload' | 'directory' | 'url'>('personal')
  const [location, setLocation] = useState('')
  const [readers, setReaders] = useState('role:operator')
  const [error, setError] = useState<string | null>(null)

  return (
    <>
      <h2 className="section-title">New source</h2>
      <form
        className="filters"
        onSubmit={(e) => {
          e.preventDefault()
          setError(null)
          const body =
            kind === 'personal'
              ? { name, personal: true }
              : { name, kind, location: location || undefined, readers: readers.split(',').map((r) => r.trim()).filter(Boolean) }
          api
            .createSource(body)
            .then(() => {
              setName('')
              setLocation('')
              onCreated()
            })
            .catch((e: unknown) => setError(e instanceof ApiError && e.status === 403 ? 'Only admins can create shared sources; you can create personal ones.' : message(e)))
        }}
      >
        <label>
          Name
          <input value={name} onChange={(e) => setName(e.target.value)} required />
        </label>
        <label>
          Kind
          <select value={kind} onChange={(e) => setKind(e.target.value as typeof kind)}>
            <option value="personal">Personal notes (only you)</option>
            <option value="upload">Shared: uploads</option>
            <option value="directory">Shared: directory</option>
            <option value="url">Shared: web pages</option>
          </select>
        </label>
        {(kind === 'directory' || kind === 'url') && (
          <label className="grow">
            {kind === 'directory' ? 'Directory (under /knowledge)' : 'URLs, one per line'}
            {kind === 'url' ? (
              <textarea rows={2} value={location} onChange={(e) => setLocation(e.target.value)} />
            ) : (
              <input value={location} onChange={(e) => setLocation(e.target.value)} />
            )}
          </label>
        )}
        {kind !== 'personal' && (
          <label>
            Readers
            <input value={readers} onChange={(e) => setReaders(e.target.value)} title="*, role:<name> or user:<id>, comma separated" />
          </label>
        )}
        <button className="btn" type="submit">
          Create
        </button>
      </form>
      {error && (
        <p role="alert" className="error small">
          {error}
        </p>
      )}
    </>
  )
}

function TryIt({ api, sources }: { api: Api; sources: KnowledgeSource[] }) {
  const [q, setQ] = useState('')
  const [source, setSource] = useState('')
  const [hits, setHits] = useState<KnowledgeHit[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  return (
    <>
      <form
        className="filters"
        onSubmit={(e) => {
          e.preventDefault()
          setError(null)
          api
            .searchKnowledge(q, source || undefined)
            .then(setHits)
            .catch((e: unknown) => setError(message(e)))
        }}
      >
        <label className="grow">
          Try a search (with your own access, exactly what the agent would see)
          <input value={q} onChange={(e) => setQ(e.target.value)} placeholder="how do I restart postgres?" />
        </label>
        <label>
          Source
          <select value={source} onChange={(e) => setSource(e.target.value)}>
            <option value="">All</option>
            {sources.map((s) => (
              <option key={s.id} value={s.id}>
                {s.name}
              </option>
            ))}
          </select>
        </label>
        <button className="btn" type="submit" disabled={!q.trim()}>
          Search
        </button>
      </form>
      {error && (
        <p role="alert" className="error small">
          {error}
        </p>
      )}
      {hits && hits.length === 0 && <p className="muted">Nothing found that you may read.</p>}
      {hits && hits.length > 0 && (
        <ol className="hits">
          {hits.map((h) => (
            <li key={h.chunkId}>
              <div>
                <strong>{h.title}</strong>
                {h.heading && h.heading !== h.title && <span className="muted"> › {h.heading.replace(`${h.title} > `, '')}</span>}
                <span className="muted small mono">
                  {' '}
                  {h.sourceName} · score {h.score.toFixed(4)} · vector #{h.vectorRank ?? '–'} · text #{h.textRank ?? '–'}
                </span>
              </div>
              <pre>{h.text}</pre>
            </li>
          ))}
        </ol>
      )}
    </>
  )
}
