import { useState } from 'react'
import type { Api, Capabilities } from '../api'
import { readDevIdentity, readPersonas, writeDevIdentity, writePersonas, writePreview, type Persona } from '../auth'
import { t } from '../i18n'

const splitRoles = (s: string) => s.split(',').map((r) => r.trim()).filter(Boolean)

/** Pick several roles from a list, plus any typed by hand. */
function RolePicker({ known, value, onChange }: { known: string[]; value: string[]; onChange: (roles: string[]) => void }) {
  const all = [...new Set([...known, ...value])]
  const toggle = (r: string) => onChange(value.includes(r) ? value.filter((x) => x !== r) : [...value, r])
  return (
    <fieldset className="rolepick">
      <legend className="sr">{t('Roles')}</legend>
      {all.map((r) => (
        <label key={r}>
          <input type="checkbox" checked={value.includes(r)} onChange={() => toggle(r)} /> {r}
        </label>
      ))}
    </fieldset>
  )
}

/**
 * "View as" (#156): an admin sees Lots with other roles. Rights only narrow (both role sets must allow a call), write tools are refused
 * unless asked for and nothing can be approved. Ends by itself.
 */
export function ViewAs({ api, caps }: { api: Api; caps: Capabilities }) {
  const [roles, setRoles] = useState<string[]>(['operator'])
  const [minutes, setMinutes] = useState(30)
  const [writes, setWrites] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const start = async () => {
    try {
      const p = await api.startPreview(roles, minutes, writes)
      writePreview({ token: p.token, header: p.header, roles: p.roles, expires: p.expires })
      window.location.reload()
    } catch (e) {
      setError(String(e))
    }
  }
  return (
    <details className="viewas">
      <summary className="btn">{t('View as')}</summary>
      <div className="viewas-panel card">
        <p className="small muted">{t('See Lots as these roles. You never get more than your own roles allow, write tools are blocked unless you allow them, and approving is off.')}</p>
        <RolePicker known={caps.knownRoles ?? []} value={roles} onChange={setRoles} />
        <label className="small">
          <input type="checkbox" checked={writes} onChange={(e) => setWrites(e.target.checked)} /> {t('Allow write tools')}
        </label>
        <label className="small">
          {t('For')}{' '}
          <select value={minutes} onChange={(e) => setMinutes(Number(e.target.value))}>
            {[15, 30, 60].map((m) => (
              <option key={m} value={m}>
                {t('{n} minutes', { n: m })}
              </option>
            ))}
          </select>
        </label>
        {error && <p className="error small">{error}</p>}
        <button type="button" className="btn primary" disabled={roles.length === 0} onClick={() => void start()}>
          {t('Start preview')}
        </button>
      </div>
    </details>
  )
}

/** Shown on every page while a preview is active, with the way out. */
export function PreviewBanner({ preview }: { preview: NonNullable<Capabilities['preview']> }) {
  const leave = () => {
    writePreview(null)
    window.location.reload()
  }
  const until = new Date(preview.expires).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
  return (
    <div className="preview-banner" role="status">
      <span>
        {t('Viewing as {roles} until {until}.', { roles: preview.roles.join(', '), until })}{' '}
        <span className="muted">{preview.allowWrites ? t('Write tools allowed; approving is off.') : t('Write tools blocked; approving is off.')}</span>
      </span>
      <button type="button" className="btn" onClick={leave}>
        {t('Leave preview')}
      </button>
    </div>
  )
}

/** Dev mode only: switch the identity sent to the API (the server decides whether it is honoured), with roles picked from the deployment. */
export function DevIdentity({ knownRoles }: { knownRoles: string[] }) {
  const [id, setId] = useState(readDevIdentity)
  const [personas, setPersonas] = useState<Persona[]>(readPersonas)
  const apply = (next: typeof id) => {
    setId(next)
    writeDevIdentity(next)
  }
  const save = () => {
    const name = window.prompt(t('Name this persona'), `${id.user} (${id.roles})`)
    if (!name) return
    const next = [...personas.filter((p) => p.name !== name), { name, user: id.user, roles: id.roles }]
    setPersonas(next)
    writePersonas(next)
  }
  const choose = (name: string) => {
    const p = personas.find((x) => x.name === name)
    if (!p) return
    writeDevIdentity({ user: p.user, roles: p.roles })
    window.location.reload()
  }
  return (
    <form
      className="dev"
      onSubmit={(e) => {
        e.preventDefault()
        window.location.reload()
      }}
    >
      {personas.length > 0 && (
        <select aria-label={t('Persona')} value="" onChange={(e) => choose(e.target.value)}>
          <option value="">{t('Persona…')}</option>
          {personas.map((p) => (
            <option key={p.name} value={p.name}>
              {p.name}
            </option>
          ))}
        </select>
      )}
      <label>
        <span className="sr">{t('Dev user')}</span>
        <input value={id.user} onChange={(e) => apply({ ...id, user: e.target.value })} aria-label={t('Dev user')} />
      </label>
      <details className="viewas">
        <summary className="btn">{splitRoles(id.roles).join(', ') || t('No roles')}</summary>
        <div className="viewas-panel card">
          <RolePicker known={knownRoles} value={splitRoles(id.roles)} onChange={(r) => apply({ ...id, roles: r.join(',') })} />
          <button type="button" className="btn" onClick={save}>
            {t('Save as persona')}
          </button>
        </div>
      </details>
      <button className="btn" type="submit">
        {t('Apply')}
      </button>
    </form>
  )
}
