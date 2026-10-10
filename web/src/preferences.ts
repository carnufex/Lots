import type { Api } from './api'
import type { ProfileInfo } from './config'
import { language, setLanguage, type UiLanguage } from './i18n'
import { setTheme, theme, type Theme } from './theme'

/** Account preferences (#155), kept on the server so they follow the user; localStorage mirrors them for a fast start. */
export interface Preferences {
  theme: Theme
  language: UiLanguage | null
  defaultContext: string | null
  autoSpeak: boolean
}

const CONTEXT_KEY = 'lots.default-context'
const AUTO_KEY = 'lots.autospeak'

function local(key: string): string | null {
  try {
    return localStorage.getItem(key)
  } catch {
    return null
  }
}

function store(key: string, value: string | null) {
  try {
    if (value === null) localStorage.removeItem(key)
    else localStorage.setItem(key, value)
  } catch {
    /* ignore */
  }
}

export function current(): Preferences {
  return { theme: theme(), language: language(), defaultContext: local(CONTEXT_KEY), autoSpeak: local(AUTO_KEY) === '1' }
}

/** The context a new chat or run starts in: the user's default if it is still offered, else the first. */
export function defaultContext(profiles: ProfileInfo[]): string {
  const d = local(CONTEXT_KEY)
  return profiles.some((p) => p.name === d) ? d! : (profiles[0]?.name ?? '')
}

function mirror(p: Preferences) {
  setTheme(p.theme)
  store(CONTEXT_KEY, p.defaultContext)
  store(AUTO_KEY, p.autoSpeak ? '1' : null)
}

/**
 * Loads the server copy after sign-in. Returns true when the UI language changed (the caller reloads, since every string is
 * rendered once). Theme and the rest apply at once.
 */
export async function sync(api: Api): Promise<boolean> {
  const p = await api.raw<Preferences>('/me/preferences')
  mirror(p)
  if (p.language && p.language !== language()) {
    setLanguage(p.language)
    return true
  }
  return false
}

export async function save(api: Api, p: Preferences): Promise<Preferences> {
  mirror(p)
  return api.raw<Preferences>('/me/preferences', { method: 'PUT', body: JSON.stringify(p) })
}
