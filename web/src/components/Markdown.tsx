import { Fragment, type ReactNode } from 'react'

/**
 * Safe markdown for answers (#95). Model output is untrusted, so nothing is ever parsed as HTML: the text is split into React
 * elements (React escapes everything). Supported: paragraphs, headings, lists, fenced and inline code, bold, italic, links (http,
 * https and mailto only, opened without referrer). Images are shown as links, never loaded: a remote image could leak data.
 */
export default function Markdown({ text }: { text: string }) {
  return <div className="md">{blocks(text)}</div>
}

function blocks(text: string): ReactNode[] {
  const out: ReactNode[] = []
  const lines = text.replace(/\r\n/g, '\n').split('\n')
  let i = 0
  let key = 0
  while (i < lines.length) {
    const line = lines[i]
    const fence = /^\s*```\s*([\w+-]*)\s*$/.exec(line)
    if (fence) {
      const code: string[] = []
      i++
      while (i < lines.length && !/^\s*```\s*$/.test(lines[i])) code.push(lines[i++])
      i++ // closing fence (or end of text while streaming)
      out.push(
        <pre key={key++} className="code">
          <code data-lang={fence[1] || undefined}>{code.join('\n')}</code>
        </pre>,
      )
      continue
    }
    const heading = /^(#{1,4})\s+(.*)$/.exec(line)
    if (heading) {
      const level = Math.min(heading[1].length + 2, 6) // answers never outrank the page title
      const Tag = `h${level}` as 'h3'
      out.push(<Tag key={key++}>{inline(heading[2])}</Tag>)
      i++
      continue
    }
    if (/^\s*([-*+]|\d+[.)])\s+/.test(line)) {
      const ordered = /^\s*\d+[.)]\s+/.test(line)
      const items: string[] = []
      while (i < lines.length && /^\s*([-*+]|\d+[.)])\s+/.test(lines[i])) items.push(lines[i++].replace(/^\s*([-*+]|\d+[.)])\s+/, ''))
      const List = ordered ? 'ol' : 'ul'
      out.push(
        <List key={key++}>
          {items.map((it, n) => (
            <li key={n}>{inline(it)}</li>
          ))}
        </List>,
      )
      continue
    }
    if (!line.trim()) {
      i++
      continue
    }
    const para: string[] = []
    while (i < lines.length && lines[i].trim() && !/^\s*```/.test(lines[i]) && !/^#{1,4}\s/.test(lines[i]) && !/^\s*([-*+]|\d+[.)])\s+/.test(lines[i]))
      para.push(lines[i++])
    out.push(<p key={key++}>{inline(para.join('\n'))}</p>)
  }
  return out
}

const SAFE_URL = /^(https?:\/\/|mailto:)/i

/** Inline: `code`, **bold**, *italic*, [text](url), ![alt](url) (as a link), bare https links, line breaks. */
function inline(text: string): ReactNode[] {
  const out: ReactNode[] = []
  const pattern = /(`[^`]+`)|(\*\*[^*]+\*\*)|(\*[^*\s][^*]*\*)|(!?\[[^\]]*\]\([^)\s]+\))|(https?:\/\/[^\s<>()]+[^\s<>().,;:!?])|(\n)/g
  let last = 0
  let key = 0
  for (const m of text.matchAll(pattern)) {
    if (m.index > last) out.push(text.slice(last, m.index))
    const t = m[0]
    if (m[1]) out.push(<code key={key++}>{t.slice(1, -1)}</code>)
    else if (m[2]) out.push(<strong key={key++}>{inline(t.slice(2, -2))}</strong>)
    else if (m[3]) out.push(<em key={key++}>{inline(t.slice(1, -1))}</em>)
    else if (m[4]) {
      const image = t.startsWith('!')
      const [, label, url] = /^!?\[([^\]]*)\]\(([^)\s]+)\)$/.exec(t)!
      out.push(link(url, image ? `image: ${label || url}` : label || url, key++))
    } else if (m[5]) out.push(link(t, t, key++))
    else out.push(<br key={key++} />)
    last = m.index + t.length
  }
  if (last < text.length) out.push(text.slice(last))
  return out.map((n, i) => (typeof n === 'string' ? <Fragment key={`t${i}`}>{n}</Fragment> : n))
}

function link(url: string, label: string, key: number): ReactNode {
  if (!SAFE_URL.test(url)) return <Fragment key={key}>{label}</Fragment>
  return (
    <a key={key} href={url} target="_blank" rel="noopener noreferrer nofollow">
      {label}
    </a>
  )
}
