import { useState } from 'react'
import type { Api, Capabilities } from '../api'
import type { ProfileInfo } from '../config'
import MyData from '../components/MyData'
import ApiTokens from '../components/ApiTokens'
import MemoryCard from '../components/MemoryCard'
import AwayPanel from '../components/Away'
import CredentialsTab from './CredentialsTab'
import { current, save, type Preferences } from '../preferences'
import { t, type UiLanguage } from '../i18n'
import type { Theme } from '../theme'

export const ACCOUNT_TABS = [
  { slug: 'overview', label: t('Overview') },
  { slug: 'settings', label: t('Settings') },
  { slug: 'data', label: t('Your data') },
  { slug: 'connections', label: t('Connections') },
  { slug: 'tokens', label: t('API tokens') },
  { slug: 'memory', label: t('Memory') },
] as const

export type AccountTab = (typeof ACCOUNT_TABS)[number]['slug']

/** The user's own things in one place (#155): who they are, preferences, their data, connected accounts, tokens and memory. */
export default function AccountPage({ api, tab, user, caps, profiles }: { api: Api; tab: AccountTab; user: string; caps: Capabilities | null; profiles: ProfileInfo[] }) {
  const active = ACCOUNT_TABS.find((it) => it.slug === tab) ?? ACCOUNT_TABS[0]
  return (
    <section>
      <h1>{t('Account')}</h1>
      <div className="tabs" role="tablist" aria-label={t('Account')}>
        {ACCOUNT_TABS.map((it) => (
          <a key={it.slug} href={`#/account/${it.slug}`} role="tab" aria-selected={it.slug === active.slug} className={it.slug === active.slug ? 'tab active' : 'tab'}>
            {it.label}
          </a>
        ))}
      </div>
      {active.slug === 'overview' && <Overview user={user} caps={caps} />}
      {active.slug === 'settings' && <Settings api={api} caps={caps} profiles={profiles} />}
      {active.slug === 'data' && <MyData api={api} profiles={profiles} />}
      {active.slug === 'connections' && <CredentialsTab api={api} mineOnly />}
      {active.slug === 'tokens' && <ApiTokens api={api} />}
      {active.slug === 'memory' && <MemoryCard api={api} />}
    </section>
  )
}

function Overview({ user, caps }: { user: string; caps: Capabilities | null }) {
  return (
    <div className="card">
      <dl className="facts">
        <dt>{t('Signed in as')}</dt>
        <dd className="mono">{user}</dd>
        <dt>{t('Roles')}</dt>
        <dd>
          {(caps?.roles ?? []).map((r) => (
            <span key={r} className="chip">
              {r}
            </span>
          ))}
        </dd>
        <dt>{t('Contexts you can use')}</dt>
        <dd>{caps?.contexts.join(', ') || '–'}</dd>
        <dt>{t('Pages')}</dt>
        <dd className="small muted">{caps?.pages.join(', ')}</dd>
      </dl>
      <p className="small muted">{t('Roles come from your identity provider. Ask an administrator if you need another one.')}</p>
    </div>
  )
}

function Settings({ api, caps, profiles }: { api: Api; caps: Capabilities | null; profiles: ProfileInfo[] }) {
  const [p, setP] = useState<Preferences>(current)
  const [state, setState] = useState<'idle' | 'saved' | string>('idle')
  const usable = profiles.filter((x) => caps?.contexts.includes(x.name) ?? true)
  const change = async (next: Preferences) => {
    const languageChanged = next.language !== p.language
    setP(next)
    try {
      await save(api, next)
      setState('saved')
      if (languageChanged) window.location.reload() // every string is rendered once in the chosen language
    } catch (e) {
      setState(String(e))
    }
  }
  return (
    <>
      <div className="card settings">
        <label>
          {t('Theme')}
          <select value={p.theme} onChange={(e) => void change({ ...p, theme: e.target.value as Theme })}>
            <option value="system">{t('System')}</option>
            <option value="light">{t('Light')}</option>
            <option value="dark">{t('Dark')}</option>
          </select>
        </label>
        <label>
          {t('Language')}
          <select value={p.language ?? 'en'} onChange={(e) => void change({ ...p, language: e.target.value as UiLanguage })}>
            <option value="en">English</option>
            <option value="sv">Svenska</option>
          </select>
        </label>
        <label>
          {t('Default context')}
          <select value={p.defaultContext ?? ''} onChange={(e) => void change({ ...p, defaultContext: e.target.value || null })}>
            <option value="">{t('First available')}</option>
            {usable.map((x) => (
              <option key={x.name} value={x.name}>
                {x.name}
              </option>
            ))}
          </select>
        </label>
        <label className="check">
          <input type="checkbox" checked={p.autoSpeak} onChange={(e) => void change({ ...p, autoSpeak: e.target.checked })} /> {t('Read answers aloud automatically')}
        </label>
        <p className="small muted" role="status">
          {state === 'saved' ? t('Saved. These settings follow you to other devices.') : state === 'idle' ? t('These settings follow you to other devices.') : state}
        </p>
      </div>
      {caps?.approve && <AwayPanel api={api} />}
    </>
  )
}
