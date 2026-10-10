import { useEffect, useMemo, useState } from 'react'
import { createApi, type Api } from './api'
import { createAuth, readDevIdentity, writeDevIdentity, type Auth, type Session } from './auth'
import { loadConfig, type ClientConfig } from './config'
import RunsPage from './pages/RunsPage'
import RunPage from './pages/RunPage'
import ApprovalsPage from './pages/ApprovalsPage'
import AuditPage from './pages/AuditPage'
import VoicePage from './pages/VoicePage'
import HistoryPage from './pages/HistoryPage'
import IntegrationsPage, { INTEGRATION_TABS, type IntegrationTab } from './pages/IntegrationsPage'
import PlaceholderPage from './pages/Placeholder'
import KnowledgePage from './pages/KnowledgePage'
import { PLANNED } from './planned'
import { Icon, type IconName } from './components/Icon'

type Route = { name: 'runs' } | { name: 'run'; id: string } | { name: 'approvals' } | { name: 'audit' } | { name: 'voice' } | { name: 'knowledge' } | { name: 'history'; id?: string } | { name: 'integrations'; tab: IntegrationTab } | { name: 'planned'; slug: string }

type NavItem = { href: string; label: string; icon: IconName; active: (r: Route) => boolean }

const nav = (name: 'runs' | 'approvals' | 'audit', label: string, icon: IconName): NavItem => ({
  href: `#/${name}`,
  label,
  icon,
  active: (r) => r.name === name || (name === 'runs' && r.name === 'run'),
})
const planned = (slug: string): NavItem => {
  const p = PLANNED.find((x) => x.slug === slug)!
  return { href: `#/${slug}`, label: p.label, icon: p.icon, active: (r) => r.name === 'planned' && r.slug === slug }
}

const historyNav: NavItem = { href: '#/history', label: 'History', icon: 'transcribe', active: (r) => r.name === 'history' }
const integrationsNav: NavItem = { href: '#/integrations', label: 'Integrations', icon: 'tools', active: (r) => r.name === 'integrations' }
const voiceNav: NavItem = { href: '#/voice', label: 'Voice', icon: 'voice', active: (r) => r.name === 'voice' }
const knowledgeNav: NavItem = { href: '#/knowledge', label: 'Knowledge', icon: 'rag', active: (r) => r.name === 'knowledge' }

const NAV: { title: string; items: NavItem[] }[] = [
  { title: 'Work', items: [nav('runs', 'Runs', 'runs'), historyNav, nav('approvals', 'Approvals', 'approvals'), nav('audit', 'Audit', 'audit')] },
  { title: 'Capabilities', items: [knowledgeNav, integrationsNav, voiceNav, planned('transcription'), planned('models')] },
  { title: 'Administration', items: ['profiles', 'policy', 'identity'].map(planned) },
]

function useHashRoute(): Route {
  const read = (): Route => {
    const r = window.location.hash.replace(/^#\/?/, '')
    if (r === 'approvals' || r === 'audit' || r === 'voice' || r === 'knowledge') return { name: r }
    if (r === 'history') return { name: 'history' }
    const it = /^integrations(?:\/([a-z-]+))?$/.exec(r)
    if (it) return { name: 'integrations', tab: INTEGRATION_TABS.find((t) => t.slug === it[1])?.slug ?? 'tool-calls' }
    const h = /^history\/([0-9a-f-]{36})$/i.exec(r)
    if (h) return { name: 'history', id: h[1] }
    if (PLANNED.some((p) => p.slug === r)) return { name: 'planned', slug: r }
    const m = /^runs\/([0-9a-f-]{36})$/i.exec(r)
    return m ? { name: 'run', id: m[1] } : { name: 'runs' }
  }
  const [route, setRoute] = useState<Route>(read)
  useEffect(() => {
    const on = () => setRoute(read())
    window.addEventListener('hashchange', on)
    return () => window.removeEventListener('hashchange', on)
  }, [])
  return route
}

type State =
  | { phase: 'loading' }
  | { phase: 'error'; message: string }
  | { phase: 'signed-out'; auth: Auth }
  | { phase: 'ready'; config: ClientConfig; auth: Auth; session: Session }

export default function App() {
  const [state, setState] = useState<State>({ phase: 'loading' })

  useEffect(() => {
    let cancelled = false
    ;(async () => {
      try {
        const config = await loadConfig()
        const auth = createAuth(config)
        const session = await auth.init()
        if (cancelled) return
        setState(session ? { phase: 'ready', config, auth, session } : { phase: 'signed-out', auth })
      } catch (e) {
        if (!cancelled) setState({ phase: 'error', message: e instanceof Error ? e.message : String(e) })
      }
    })()
    return () => {
      cancelled = true
    }
  }, [])

  if (state.phase === 'loading') return <Centered>Loading…</Centered>
  if (state.phase === 'error')
    return (
      <Centered>
        <p className="muted">Could not start the app.</p>
        <p className="mono">{state.message}</p>
      </Centered>
    )
  if (state.phase === 'signed-out')
    return (
      <Centered>
        <Brand large />
        <p className="muted">Sign in to continue.</p>
        <button className="btn primary" onClick={() => void state.auth.login()}>
          Sign in
        </button>
      </Centered>
    )
  return <Shell config={state.config} auth={state.auth} session={state.session} />
}

function Shell({ config, auth, session }: { config: ClientConfig; auth: Auth; session: Session }) {
  const route = useHashRoute()
  const api: Api = useMemo(() => createApi(auth), [auth])

  return (
    <div className="app">
      <aside className="rail">
        <Brand />
        <nav aria-label="Main">
          {NAV.map((g) => (
            <div key={g.title} className="navgroup">
              <div className="navtitle">{g.title}</div>
              {g.items.map((n) => (
                <a key={n.href} href={n.href} className={n.active(route) ? 'active' : ''} aria-current={n.active(route) ? 'page' : undefined}>
                  <Icon name={n.icon} />
                  {n.label}
                </a>
              ))}
            </div>
          ))}
        </nav>
      </aside>
      <div className="main">
        <header className="topbar">
          <span className="muted">{config.profiles.map((p) => p.name).join(' · ')}</span>
          <div className="who">
            {auth.mode === 'dev' && <DevIdentity />}
            <span className="user">{session.user}</span>
            {session.roles.map((r) => (
              <span key={r} className="chip">
                {r}
              </span>
            ))}
            {auth.mode === 'oidc' && (
              <button className="btn" onClick={() => void auth.logout()}>
                Sign out
              </button>
            )}
          </div>
        </header>
        <main>
          {route.name === 'runs' && <RunsPage api={api} profiles={config.profiles} voice={config.voice} />}
          {route.name === 'run' && <RunPage api={api} id={route.id} voice={config.voice} />}
          {route.name === 'approvals' && <ApprovalsPage api={api} />}
          {route.name === 'audit' && <AuditPage api={api} />}
          {route.name === 'history' && <HistoryPage api={api} id={route.id} />}
          {route.name === 'integrations' && <IntegrationsPage api={api} tab={route.tab} />}
          {route.name === 'voice' && <VoicePage api={api} voice={config.voice} />}
          {route.name === 'knowledge' && <KnowledgePage api={api} />}
          {route.name === 'planned' && <PlaceholderPage item={PLANNED.find((p) => p.slug === route.slug)!} />}
        </main>
      </div>
    </div>
  )
}

/** Dev mode only: switch the identity sent to the API (the server decides whether it is honoured). */
function DevIdentity() {
  const [id, setId] = useState(readDevIdentity)
  const apply = (next: typeof id) => {
    setId(next)
    writeDevIdentity(next)
  }
  return (
    <form
      className="dev"
      onSubmit={(e) => {
        e.preventDefault()
        window.location.reload()
      }}
    >
      <label>
        <span className="sr">Dev user</span>
        <input value={id.user} onChange={(e) => apply({ ...id, user: e.target.value })} aria-label="Dev user" />
      </label>
      <label>
        <span className="sr">Dev roles</span>
        <input value={id.roles} onChange={(e) => apply({ ...id, roles: e.target.value })} aria-label="Dev roles" />
      </label>
      <button className="btn" type="submit">
        Apply
      </button>
    </form>
  )
}

function Brand({ large = false }: { large?: boolean }) {
  return (
    <div className={large ? 'brand large' : 'brand'}>
      <img src="/lots-logo.svg" alt="Lots" />
    </div>
  )
}

function Centered({ children }: { children: React.ReactNode }) {
  return <div className="centered">{children}</div>
}
