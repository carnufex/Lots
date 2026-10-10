import { useEffect, useState } from 'react'
import type { Api, KnowledgeHit, Step } from '../api'
import { fmt, t } from '../i18n'

/** k-number -> chunk id, from the "Sources: k1=..., k2=..." line of search_knowledge results (later searches win). */
export function citationMap(steps: Step[]): Map<string, string> {
  const map = new Map<string, string>()
  for (const s of steps) {
    if (s.kind !== 'ToolCall' || s.name !== 'search_knowledge' || !s.result) continue
    const line = /^Sources: (.*)$/m.exec(s.result)
    for (const m of line?.[1].matchAll(/k(\d+)=([0-9a-f]+)/g) ?? []) map.set(`k${m[1]}`, m[2])
  }
  return map
}

/** The knowledge passages an answer cites ([k1], [k2]), resolved with the viewer's own access. */
export default function Citations({ api, answer, steps }: { api: Api; answer: string; steps: Step[] }) {
  const [hits, setHits] = useState<{ key: string; hit: KnowledgeHit | null }[]>([])

  useEffect(() => {
    const map = citationMap(steps)
    const cited = [...new Set([...answer.matchAll(/\[(k\d+)\]/g)].map((m) => m[1]))].filter((k) => map.has(k)).sort((a, b) => Number(a.slice(1)) - Number(b.slice(1)))
    let cancelled = false
    void Promise.all(cited.map(async (key) => ({ key, hit: await api.chunk(map.get(key)!).catch(() => null) }))).then(
      (r) => !cancelled && setHits(r),
    )
    return () => {
      cancelled = true
    }
  }, [api, answer, steps])

  if (hits.length === 0) return null
  return (
    <div className="citations" aria-label={t('Sources')}>
      <div className="label">{t('Sources')}</div>
      <ol>
        {hits.map(({ key, hit }) => (
          <li key={key}>
            <span className="mono">[{key}]</span>{' '}
            {hit ? (
              <details>
                <summary>
                  {hit.url ? (
                    <a href={hit.url} target="_blank" rel="noreferrer noopener">
                      {hit.title}
                    </a>
                  ) : (
                    hit.title
                  )}
                  {hit.heading && hit.heading !== hit.title && <span className="muted"> › {hit.heading.replace(`${hit.title} > `, '')}</span>}
                  <span className="muted small"> · {hit.sourceName} · {fmt.date(hit.updatedAt)}</span>
                </summary>
                <pre>{hit.text}</pre>
              </details>
            ) : (
              <span className="muted">{t('not available to you')}</span>
            )}
          </li>
        ))}
      </ol>
    </div>
  )
}
