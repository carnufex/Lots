export interface OidcConfig {
  authority: string
  clientId: string
  scope: string
}

export interface ProfileInfo {
  name: string
  description: string
}

export interface ClientConfig {
  authMode: 'dev' | 'oidc'
  oidc: OidcConfig | null
  profiles: ProfileInfo[]
}

/** Public runtime configuration served by the shell (no secrets). */
export async function loadConfig(): Promise<ClientConfig> {
  const res = await fetch('/config')
  if (!res.ok) throw new Error(`Could not load configuration (${res.status})`)
  return res.json()
}
