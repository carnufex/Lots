import { UserManager, WebStorageStateStore, type User } from 'oidc-client-ts'
import type { ClientConfig } from './config'

export interface Session {
  user: string
  roles: string[]
}

export interface Auth {
  mode: 'dev' | 'oidc'
  /** Resolves the current session, completing a redirect login if one is in progress. Null = signed out. */
  init(): Promise<Session | null>
  login(): Promise<void>
  logout(): Promise<void>
  /** Headers that identify the caller to the API. */
  headers(): Promise<Record<string, string>>
}

const DEV_KEY = 'lots.dev-identity'

export interface DevIdentity {
  user: string
  roles: string
}

export function readDevIdentity(): DevIdentity {
  try {
    const raw = localStorage.getItem(DEV_KEY)
    if (raw) return JSON.parse(raw) as DevIdentity
  } catch {
    /* storage unavailable or corrupt: fall through */
  }
  return { user: 'dev', roles: 'operator' }
}

export function writeDevIdentity(id: DevIdentity) {
  try {
    localStorage.setItem(DEV_KEY, JSON.stringify(id))
  } catch {
    /* ignore */
  }
}

const splitRoles = (s: string) => s.split(',').map((r) => r.trim()).filter(Boolean)

/** Development only: no identity provider. The server decides whether the identity headers are honoured. */
function devAuth(): Auth {
  return {
    mode: 'dev',
    init: async () => {
      const id = readDevIdentity()
      return { user: id.user, roles: splitRoles(id.roles) }
    },
    login: async () => {},
    logout: async () => {},
    headers: async () => {
      const id = readDevIdentity()
      return { 'X-Dev-User': id.user, 'X-Dev-Roles': id.roles }
    },
  }
}

/** Authorization-code flow with PKCE against the configured OIDC provider. */
function oidcAuth(cfg: NonNullable<ClientConfig['oidc']>): Auth {
  const manager = new UserManager({
    authority: cfg.authority,
    client_id: cfg.clientId,
    redirect_uri: window.location.origin + '/',
    post_logout_redirect_uri: window.location.origin + '/',
    response_type: 'code',
    scope: cfg.scope,
    userStore: new WebStorageStateStore({ store: window.sessionStorage }),
  })

  const toSession = (u: User): Session => {
    const p = u.profile as Record<string, unknown>
    const roles = (p.roles ?? p.groups ?? []) as string[] | string
    return {
      user: String(p.preferred_username ?? p.email ?? p.sub),
      roles: Array.isArray(roles) ? roles : splitRoles(String(roles)),
    }
  }

  return {
    mode: 'oidc',
    init: async () => {
      const params = new URLSearchParams(window.location.search)
      if (params.has('code') && params.has('state')) {
        const user = await manager.signinCallback()
        window.history.replaceState({}, '', window.location.pathname + window.location.hash)
        return user ? toSession(user) : null
      }
      const user = await manager.getUser()
      return user && !user.expired ? toSession(user) : null
    },
    login: () => manager.signinRedirect(),
    logout: () => manager.signoutRedirect(),
    headers: async () => {
      const user = await manager.getUser()
      const none: Record<string, string> = {}
      return user && !user.expired ? { Authorization: `Bearer ${user.access_token}` } : none
    },
  }
}

export function createAuth(config: ClientConfig): Auth {
  return config.authMode === 'oidc' && config.oidc ? oidcAuth(config.oidc) : devAuth()
}
