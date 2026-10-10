import { useMemo, useState } from 'react'

/**
 * Readable views of tool output (#65). Tool output is untrusted data: everything here renders as React text nodes, never as HTML,
 * markdown or links with active content. The raw text is always one click away.
 */
type View =
  | { kind: 'table'; columns: string[]; rows: string[][] }
  | { kind: 'json'; value: unknown }
  | { kind: 'log'; lines: string[] }
  | { kind: 'series'; values: number[]; labels: string[] }
  | { kind: 'text' }

const LOG_LINE = /^(\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}|\[?\d{2}:\d{2}:\d{2}|\w{3} \d{1,2} \d{2}:\d{2}|(INFO|WARN|WARNING|ERROR|DEBUG|TRACE)\b)/

function cell(v: unknown): string {
  if (v === null || v === undefined) return ''
  return typeof v === 'object' ? JSON.stringify(v) : String(v)
}

/** Picks the most useful view for a tool result. Exported for reuse (tool call log). */
export function detect(text: string): View {
  const trimmed = text.trim()
  if ((trimmed.startsWith('{') || trimmed.startsWith('[')) && trimmed.length < 500_000) {
    try {
      const value: unknown = JSON.parse(trimmed)
      if (Array.isArray(value) && value.length > 0) {
        if (value.every((x) => typeof x === 'number')) return { kind: 'series', values: value as number[], labels: value.map((_, i) => String(i)) }
        if (value.every((x) => x && typeof x === 'object' && !Array.isArray(x))) {
          const rows = value as Record<string, unknown>[]
          const numericPairs = rows.every((r) => Object.keys(r).length === 2 && Object.values(r).some((v) => typeof v === 'number'))
          if (numericPairs && rows.length >= 3) {
            const [labelKey, valueKey] = Object.keys(rows[0]).sort((a) => (typeof rows[0][a] === 'number' ? 1 : -1))
            return { kind: 'series', values: rows.map((r) => Number(r[valueKey])), labels: rows.map((r) => cell(r[labelKey])) }
          }
          const columns = [...new Set(rows.flatMap((r) => Object.keys(r)))].slice(0, 12)
          return { kind: 'table', columns, rows: rows.map((r) => columns.map((c) => cell(r[c]))) }
        }
      }
      return { kind: 'json', value }
    } catch {
      // not JSON
    }
  }

  const lines = trimmed.split('\n').filter((l) => l.trim().length > 0)
  // "name | key=value | key=value" lines (the homelab server's style) become a table.
  if (lines.length >= 2 && lines.every((l) => l.includes(' | ') && l.includes('='))) {
    const parsed = lines.map((l) => {
      const parts = l.split(' | ')
      const row: Record<string, string> = { name: parts[0].trim() }
      for (const p of parts.slice(1)) {
        const i = p.indexOf('=')
        if (i > 0) row[p.slice(0, i).trim()] = p.slice(i + 1).trim()
      }
      return row
    })
    const columns = [...new Set(parsed.flatMap((r) => Object.keys(r)))]
    return { kind: 'table', columns, rows: parsed.map((r) => columns.map((c) => r[c] ?? '')) }
  }
  if (lines.length >= 5 && lines.filter((l) => LOG_LINE.test(l)).length >= lines.length * 0.6) return { kind: 'log', lines }
  return { kind: 'text' }
}

export default function ToolResult({ text }: { text: string }) {
  const view = useMemo(() => detect(text), [text])
  const [raw, setRaw] = useState(view.kind === 'text')
  return (
    <div className="tool-result">
      {view.kind !== 'text' && (
        <button type="button" className="btn ghost small" onClick={() => setRaw(!raw)}>
          {raw ? `show as ${view.kind}` : 'show raw'}
        </button>
      )}
      {raw || view.kind === 'text' ? <pre>{text}</pre> : <Rendered view={view} />}
    </div>
  )
}

function Rendered({ view }: { view: Exclude<View, { kind: 'text' }> }) {
  switch (view.kind) {
    case 'table':
      return <Table columns={view.columns} rows={view.rows} />
    case 'json':
      return (
        <div className="json-tree">
          <JsonNode value={view.value} depth={0} />
        </div>
      )
    case 'log':
      return <LogView lines={view.lines} />
    case 'series':
      return <Sparkline values={view.values} labels={view.labels} />
  }
}

function Table({ columns, rows }: { columns: string[]; rows: string[][] }) {
  const [sort, setSort] = useState<{ col: number; desc: boolean } | null>(null)
  const sorted = useMemo(() => {
    if (!sort) return rows
    const n = (s: string) => (s.trim() !== '' && !isNaN(Number(s)) ? Number(s) : null)
    return [...rows].sort((a, b) => {
      const x = a[sort.col], y = b[sort.col]
      const nx = n(x), ny = n(y)
      const c = nx !== null && ny !== null ? nx - ny : x.localeCompare(y)
      return sort.desc ? -c : c
    })
  }, [rows, sort])
  return (
    <div className="table-scroll">
      <table>
        <thead>
          <tr>
            {columns.map((c, i) => (
              <th key={c}>
                <button type="button" className="btn ghost small" onClick={() => setSort({ col: i, desc: sort?.col === i ? !sort.desc : false })}>
                  {c}
                  {sort?.col === i ? (sort.desc ? ' ▼' : ' ▲') : ''}
                </button>
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {sorted.map((r, i) => (
            <tr key={i}>
              {r.map((v, j) => (
                <td key={j} className="mono small">
                  {v}
                </td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

function JsonNode({ value, depth, name }: { value: unknown; depth: number; name?: string }) {
  const label = name !== undefined ? <span className="json-key">{name}: </span> : null
  if (value === null || typeof value !== 'object')
    return (
      <div>
        {label}
        <span className={`json-${value === null ? 'null' : typeof value}`}>{JSON.stringify(value)}</span>
      </div>
    )
  const entries = Array.isArray(value) ? value.map((v, i) => [String(i), v] as const) : Object.entries(value as Record<string, unknown>)
  return (
    <details open={depth < 2}>
      <summary>
        {label}
        <span className="muted">{Array.isArray(value) ? `[${entries.length}]` : `{${entries.length}}`}</span>
      </summary>
      <div className="json-children">
        {entries.slice(0, 500).map(([k, v]) => (
          <JsonNode key={k} name={k} value={v} depth={depth + 1} />
        ))}
        {entries.length > 500 && <div className="muted">… {entries.length - 500} more</div>}
      </div>
    </details>
  )
}

function LogView({ lines }: { lines: string[] }) {
  const [q, setQ] = useState('')
  const [onlyErrors, setOnlyErrors] = useState(false)
  const shown = lines.filter((l) => (!q || l.toLowerCase().includes(q.toLowerCase())) && (!onlyErrors || /\b(ERROR|ERR|FATAL|WARN|WARNING|exception|failed)\b/i.test(l)))
  return (
    <div>
      <div className="filters">
        <label>
          Search
          <input value={q} onChange={(e) => setQ(e.target.value)} />
        </label>
        <label>
          <input type="checkbox" checked={onlyErrors} onChange={(e) => setOnlyErrors(e.target.checked)} /> warnings and errors only
        </label>
        <span className="muted small">
          {shown.length} of {lines.length} lines
        </span>
      </div>
      <pre className="log">
        {shown.map((l, i) => (
          <span key={i} className={/\b(ERROR|FATAL|exception)\b/i.test(l) ? 'del' : /\bWARN(ING)?\b/i.test(l) ? 'warn' : undefined}>
            {l}
            {'\n'}
          </span>
        ))}
      </pre>
    </div>
  )
}

function Sparkline({ values, labels }: { values: number[]; labels: string[] }) {
  const w = 480, h = 80
  const min = Math.min(...values), max = Math.max(...values)
  const x = (i: number) => (values.length === 1 ? w / 2 : (i / (values.length - 1)) * w)
  const y = (v: number) => (max === min ? h / 2 : h - ((v - min) / (max - min)) * (h - 8) - 4)
  const d = values.map((v, i) => `${i === 0 ? 'M' : 'L'}${x(i).toFixed(1)},${y(v).toFixed(1)}`).join(' ')
  return (
    <figure className="sparkline">
      <svg viewBox={`0 0 ${w} ${h}`} width="100%" height={h} role="img" aria-label={`${values.length} values from ${min} to ${max}`}>
        <path d={d} fill="none" stroke="currentColor" strokeWidth="2" />
      </svg>
      <figcaption className="muted small">
        {labels[0]} … {labels[labels.length - 1]} · min {min} · max {max} · last {values[values.length - 1]}
      </figcaption>
    </figure>
  )
}
