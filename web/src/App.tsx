import { useEffect, useMemo, useState } from 'react'
import { createApi, type Api } from './api'
import { createAuth, readDevIdentity, writeDevIdentity, type Auth, type Session } from './auth'
import { loadConfig, type ClientConfig } from './config'
import RunsPage from './pages/RunsPage'
import RunPage from './pages/RunPage'
import ApprovalsPage from './pages/ApprovalsPage'
import AuditPage from './pages/AuditPage'

type Route = { name: 'runs' } | { name: 'run'; id: string } | { name: 'approvals' } | { name: 'audit' }

const NAV: { route: Route['name']; label: string }[] = [
  { route: 'runs', label: 'Runs' },
  { route: 'approvals', label: 'Approvals' },
  { route: 'audit', label: 'Audit' },
]

function useHashRoute(): Route {
  const read = (): Route => {
    const r = window.location.hash.replace(/^#\/?/, '')
    if (r === 'approvals' || r === 'audit') return { name: r }
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
        <Brand />
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
          {NAV.map((n) => (
            <a key={n.route} href={`#/${n.route}`} className={route.name === n.route || (n.route === 'runs' && route.name === 'run') ? 'active' : ''}>
              {n.label}
            </a>
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
          {route.name === 'runs' && <RunsPage api={api} profiles={config.profiles} />}
          {route.name === 'run' && <RunPage api={api} id={route.id} />}
          {route.name === 'approvals' && <ApprovalsPage api={api} />}
          {route.name === 'audit' && <AuditPage api={api} />}
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

function Brand() {
  return (
    <div className="brand">
      <svg viewBox="0 0 32 32" width="20" height="20" aria-hidden="true">
        <path d="M9 7v14a3 3 0 0 0 3 3h11" fill="none" stroke="currentColor" strokeWidth="3" strokeLinecap="round" strokeLinejoin="round" />
      </svg>
      Lots
    </div>
  )
}

function Centered({ children }: { children: React.ReactNode }) {
  return <div className="centered">{children}</div>
}
