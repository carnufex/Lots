export interface OidcConfig {
  authority: string
  clientId: string
  scope: string
  roleClaim: string
  rolePrefix: string | null
}

export interface ProfileInfo {
  name: string
  description: string
  /** What telemetry keeps of this profile's runs (#145): off, metadata, redacted or full. */
  telemetryContent?: 'off' | 'metadata' | 'redacted' | 'full'
}

export interface VoiceConfig {
  enabled: boolean
  languages: string[]
  defaultLanguage: 'auto' | 'sv' | 'en'
}

export interface ClientConfig {
  authMode: 'dev' | 'oidc'
  oidc: OidcConfig | null
  profiles: ProfileInfo[]
  voice: VoiceConfig
  /** Link template to a trace in the tracing UI, with {traceId}; null = no link (#76). */
  traceUrl: string | null
  /** Dev mode only: every role the deployment checks, for the identity picker (#156). */
  devRoles?: string[] | null
}

/** Public runtime configuration served by the shell (no secrets). */
export async function loadConfig(): Promise<ClientConfig> {
  const res = await fetch('/config')
  if (!res.ok) throw new Error(`Could not load configuration (${res.status})`)
  return res.json()
}
