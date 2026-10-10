import { useEffect, useMemo, useState } from 'react'
import { createApi, type Api, type Capabilities } from './api'
import { createAuth, readDevIdentity, writeDevIdentity, type Auth, type Session } from './auth'
import { loadConfig, type ClientConfig } from './config'
import RunsPage from './pages/RunsPage'
import ChatPage from './pages/ChatPage'
import RunPage from './pages/RunPage'
import ApprovalsPage from './pages/ApprovalsPage'
import AuditPage from './pages/AuditPage'
import VoicePage from './pages/VoicePage'
import HistoryPage from './pages/HistoryPage'
import IntegrationsPage, { INTEGRATION_TABS, type IntegrationTab } from './pages/IntegrationsPage'
import PlaceholderPage from './pages/Placeholder'
import KnowledgePage from './pages/KnowledgePage'
import ProfilesPage from './pages/ProfilesPage'
import PolicyPage from './pages/PolicyPage'
import ModelsPage from './pages/ModelsPage'
import IdentityPage from './pages/IdentityPage'
import UsagePage from './pages/UsagePage'
import FeedbackPage from './pages/FeedbackPage'
import InsightsPage from './pages/InsightsPage'
import { PLANNED } from './planned'
import { Icon, type IconName } from './components/Icon'
import { language, setLanguage, t, type UiLanguage } from './i18n'
import { saveLanguage } from './voice/language'

type Route = { name: 'chat'; id?: string } | { name: 'runs' } | { name: 'run'; id: string } | { name: 'approvals' } | { name: 'audit' } | { name: 'voice' } | { name: 'knowledge' } | { name: 'profiles' } | { name: 'policy' } | { name: 'models' } | { name: 'identity' } | { name: 'usage' } | { name: 'feedback' } | { name: 'insights' } | { name: 'history'; id?: string } | { name: 'integrations'; tab: IntegrationTab } | { name: 'planned'; slug: string }

type NavItem = { href: string; label: string; icon: IconName; active: (r: Route) => boolean }

/** The capability page a route belongs to (#157): what /me/capabilities lists. */
function pageOf(r: Route): string {
  if (r.name === 'run') return 'runs'
  if (r.name === 'planned') return r.slug
  return r.name
}

const pageOfHref = (href: string) => href.replace(/^#\//, '').split('/')[0]

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

const chatNav: NavItem = { href: '#/chat', label: 'Chat', icon: 'chat', active: (r) => r.name === 'chat' }
const historyNav: NavItem = { href: '#/history', label: 'History', icon: 'transcribe', active: (r) => r.name === 'history' }
const integrationsNav: NavItem = { href: '#/integrations', label: 'Integrations', icon: 'tools', active: (r) => r.name === 'integrations' }
const voiceNav: NavItem = { href: '#/voice', label: 'Voice', icon: 'voice', active: (r) => r.name === 'voice' }
const knowledgeNav: NavItem = { href: '#/knowledge', label: 'Knowledge', icon: 'rag', active: (r) => r.name === 'knowledge' }
const page = (name: 'profiles' | 'policy' | 'models' | 'identity' | 'usage' | 'feedback' | 'insights', label: string, icon: IconName): NavItem => ({
  href: `#/${name}`,
  label,
  icon,
  active: (r) => r.name === name,
})

const NAV: { title: string; items: NavItem[] }[] = [
  { title: 'Work', items: [chatNav, nav('runs', 'Runs', 'runs'), historyNav, nav('approvals', 'Approvals', 'approvals'), nav('audit', 'Audit', 'audit'), page('usage', 'Usage', 'models')] },
  { title: 'Capabilities', items: [knowledgeNav, integrationsNav, voiceNav, planned('transcription'), page('models', 'Models', 'models')] },
  { title: 'Administration', items: [page('profiles', 'Profiles', 'profiles'), page('policy', 'Policy', 'policy'), page('identity', 'Identity', 'identity'), page('feedback', 'Feedback', 'approvals'), page('insights', 'Insights', 'insights')] },
]

function useHashRoute(): Route {
  const read = (): Route => {
    const r = window.location.hash.replace(/^#\/?/, '').split('?')[0] // a page may carry its own query (e.g. ?connected=)
    if (r === 'approvals' || r === 'audit' || r === 'voice' || r === 'knowledge' || r === 'profiles' || r === 'policy' || r === 'models' || r === 'identity' || r === 'usage' || r === 'feedback' || r === 'insights')
      return { name: r }
    if (r === 'history') return { name: 'history' }
    if (r === 'chat') return { name: 'chat' }
    const c = /^chat\/([0-9a-f-]{36})$/i.exec(r)
    if (c) return { name: 'chat', id: c[1] }
    const it = /^integrations(?:\/([a-z-]+))?$/.exec(r)
    if (it) return { name: 'integrations', tab: INTEGRATION_TABS.find((tab) => tab.slug === it[1])?.slug ?? 'tool-calls' }
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

  if (state.phase === 'loading') return <Centered>{t('Loading…')}</Centered>
  if (state.phase === 'error')
    return (
      <Centered>
        <p className="muted">{t('Could not start the app.')}</p>
        <p className="mono">{state.message}</p>
      </Centered>
    )
  if (state.phase === 'signed-out')
    return (
      <Centered>
        <Brand large />
        <p className="muted">{t('Sign in to continue.')}</p>
        <button className="btn primary" onClick={() => void state.auth.login()}>
          {t('Sign in')}
        </button>
      </Centered>
    )
  return <Shell config={state.config} auth={state.auth} session={state.session} />
}

function Shell({ config, auth, session }: { config: ClientConfig; auth: Auth; session: Session }) {
  const route = useHashRoute()
  const api: Api = useMemo(() => createApi(auth), [auth])
  // Permission-aware navigation (#157): only what the caller may use. Hiding is UX; every endpoint checks for itself.
  const [caps, setCaps] = useState<Capabilities | null>(null)
  useEffect(() => {
    api.capabilities().then(setCaps).catch(() => setCaps(null))
  }, [api, session.user, session.roles.join(',')])
  const may = (page: string) => caps === null ? Capabilities_EVERYONE.includes(page) : caps.pages.includes(page)
  const nav = NAV.map((g) => ({ ...g, items: g.items.filter((n) => may(pageOfHref(n.href))) })).filter((g) => g.items.length > 0)
  // Phones (#113): the navigation folds into a menu; it closes when a page is chosen.
  const [menu, setMenu] = useState(false)
  useEffect(() => {
    const close = () => setMenu(false)
    window.addEventListener('hashchange', close)
    return () => window.removeEventListener('hashchange', close)
  }, [])

  return (
    <div className="app">
      <a className="skip" href="#main" onClick={(e) => { e.preventDefault(); document.getElementById('main')?.focus() }}>
        {t('Skip to content')}
      </a>
      <aside className="rail">
        <div className="railhead">
          <Brand />
          <button type="button" className="btn menu-toggle" aria-expanded={menu} aria-controls="mainnav" onClick={() => setMenu((m) => !m)}>
            {menu ? t('Close menu') : t('Menu')}
          </button>
        </div>
        <nav id="mainnav" aria-label={t('Main')} className={menu ? 'open' : undefined}>
          {nav.map((g) => (
            <div key={g.title} className="navgroup">
              <div className="navtitle">{t(g.title)}</div>
              {g.items.map((n) => (
                <a key={n.href} href={n.href} className={n.active(route) ? 'active' : ''} aria-current={n.active(route) ? 'page' : undefined}>
                  <Icon name={n.icon} />
                  {t(n.label)}
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
            <LanguageSwitch />
            {auth.mode === 'dev' && <DevIdentity />}
            <span className="user">{session.user}</span>
            {session.roles.map((r) => (
              <span key={r} className="chip">
                {r}
              </span>
            ))}
            {auth.mode === 'oidc' && (
              <button className="btn" onClick={() => void auth.logout()}>
                {t('Sign out')}
              </button>
            )}
          </div>
        </header>
        <main id="main" tabIndex={-1}>
          {caps !== null && !may(pageOf(route)) ? <NoAccess /> : <>
          {route.name === 'chat' && <ChatPage key={route.id ?? 'new'} api={api} profiles={config.profiles} id={route.id} />}
          {route.name === 'runs' && <RunsPage api={api} profiles={config.profiles} voice={config.voice} />}
          {route.name === 'run' && <RunPage api={api} id={route.id} voice={config.voice} traceUrl={config.traceUrl} />}
          {route.name === 'approvals' && <ApprovalsPage api={api} />}
          {route.name === 'audit' && <AuditPage api={api} />}
          {route.name === 'history' && <HistoryPage api={api} id={route.id} />}
          {route.name === 'integrations' && <IntegrationsPage api={api} tab={route.tab} />}
          {route.name === 'voice' && <VoicePage api={api} voice={config.voice} />}
          {route.name === 'knowledge' && <KnowledgePage api={api} />}
          {route.name === 'profiles' && <ProfilesPage api={api} />}
          {route.name === 'policy' && <PolicyPage api={api} />}
          {route.name === 'models' && <ModelsPage api={api} />}
          {route.name === 'identity' && <IdentityPage api={api} />}
          {route.name === 'usage' && <UsagePage api={api} profiles={config.profiles} />}
          {route.name === 'feedback' && <FeedbackPage api={api} />}
          {route.name === 'insights' && <InsightsPage api={api} traceUrl={config.traceUrl} />}
          {route.name === 'planned' && <PlaceholderPage item={PLANNED.find((p) => p.slug === route.slug)!} />}
          </>}
        </main>
      </div>
    </div>
  )
}

/**
 * UI language (#111). The spoken-language default follows it, so dictation and the agent's voice match what the user reads; a
 * language picked explicitly for voice later is kept.
 */
function LanguageSwitch() {
  const change = (l: UiLanguage) => {
    setLanguage(l)
    saveLanguage(l)
    window.location.reload() // every string and date is rendered again in the new language
  }
  return (
    <select className="lang" aria-label={t('Language')} value={language()} onChange={(e) => change(e.target.value as UiLanguage)}>
      <option value="en">English</option>
      <option value="sv">Svenska</option>
    </select>
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
        <span className="sr">{t('Dev user')}</span>
        <input value={id.user} onChange={(e) => apply({ ...id, user: e.target.value })} aria-label={t('Dev user')} />
      </label>
      <label>
        <span className="sr">{t('Dev roles')}</span>
        <input value={id.roles} onChange={(e) => apply({ ...id, roles: e.target.value })} aria-label={t('Dev roles')} />
      </label>
      <button className="btn" type="submit">
        {t('Apply')}
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

/** Pages everyone has, shown while capabilities are loading so the menu does not flash admin entries. */
const Capabilities_EVERYONE = ['chat', 'history', 'runs', 'usage', 'voice', 'knowledge', 'integrations', 'transcription']

/** A page the caller may not use (#157): explained, not a broken page or a raw 403. */
function NoAccess() {
  return (
    <section className="placeholder">
      <h1>{t('You do not have access to this page')}</h1>
      <p className="muted">{t('Your roles do not include it. Ask an administrator if you need it.')}</p>
      <p><a href="#/chat">{t('Back to Chat')}</a></p>
    </section>
  )
}
