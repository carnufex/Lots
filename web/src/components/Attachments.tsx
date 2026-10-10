import { useRef, useState } from 'react'
import type { Api } from '../api'

export interface UploadedAttachment {
  id: string
  name: string
  kind: 'image' | 'text' | 'document'
  size: number
}

const ACCEPT = 'image/png,image/jpeg,image/gif,image/webp,.pdf,.docx,.txt,.log,.md,.json,.csv,.tsv,.yaml,.yml,.xml,.ini,.conf,.toml'

/** Attach files to a question (#105): uploads on pick, shows chips, lets the user remove them before sending. */
export function AttachPicker({ api, files, onChange, disabled }: { api: Api; files: UploadedAttachment[]; onChange: (f: UploadedAttachment[]) => void; disabled?: boolean }) {
  const input = useRef<HTMLInputElement | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const pick = async (list: FileList | null) => {
    if (!list?.length) return
    setBusy(true)
    setError(null)
    const added: UploadedAttachment[] = []
    for (const file of Array.from(list)) {
      try {
        added.push(await api.uploadAttachment(file))
      } catch (e) {
        setError(`${file.name}: ${e instanceof Error ? e.message : String(e)}`)
      }
    }
    onChange([...files, ...added])
    setBusy(false)
    if (input.current) input.current.value = ''
  }

  return (
    <span className="attach">
      <input ref={input} type="file" multiple accept={ACCEPT} hidden onChange={(e) => void pick(e.target.files)} />
      <button type="button" className="btn ghost small" disabled={disabled || busy} onClick={() => input.current?.click()} title="Attach images, PDFs, Word documents or text files">
        {busy ? 'Uploading…' : 'Attach'}
      </button>
      {files.map((f) => (
        <span key={f.id} className="file-chip small">
          {f.kind === 'image' ? '🖼' : '📄'} {f.name}
          <button type="button" className="file-chip-x" aria-label={`Remove ${f.name}`} onClick={() => onChange(files.filter((x) => x.id !== f.id))}>
            ×
          </button>
        </span>
      ))}
      {error && <span className="error small">{error}</span>}
    </span>
  )
}

/** Names of a turn's attachments, each downloadable (with the auth header, so via fetch). */
export function AttachmentList({ api, items }: { api: Api; items: { id: string; name: string; kind: string }[] }) {
  if (items.length === 0) return null
  const download = async (id: string, name: string) => {
    const res = await fetch(`/attachments/${id}`, { headers: await api.authHeaders() })
    if (!res.ok) return
    const url = URL.createObjectURL(await res.blob())
    const a = document.createElement('a')
    a.href = url
    a.download = name
    a.click()
    URL.revokeObjectURL(url)
  }
  return (
    <div className="attachments small">
      {items.map((a) => (
        <button key={a.id} type="button" className="file-chip" onClick={() => void download(a.id, a.name)} title="Download">
          {a.kind === 'image' ? '🖼' : '📄'} {a.name}
        </button>
      ))}
    </div>
  )
}
