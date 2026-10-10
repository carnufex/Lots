import type { Api } from './api'

export interface Resource {
  kind: string
  name: string
  version: number | null
  managedBy: 'file' | 'config' | 'api' | 'gitops'
  appliedAt: string | null
  appliedBy: string | null
  editable: boolean
}

export interface ApplyResult {
  kind: string
  name: string
  action: 'create' | 'update' | 'unchanged' | 'delete' | 'invalid'
  version: number
  diff: string | null
  errors: string[]
}

export interface ApplyOutcome {
  applied: boolean
  dryRun: boolean
  results: ApplyResult[]
  hasErrors: boolean
}

export interface ResourceVersion {
  id: string
  version: number
  action: string
  managedBy: string
  appliedBy: string
  appliedAt: string
  diff: string
}

export interface GitOpsInfo {
  state: { enabled: boolean; path: string | null; revision: string | null; lastSyncAt: string | null; lastResult: string | null; error: string | null }
  drift: ApplyResult[]
}

export interface ProfilePolicy {
  profile: string
  version: number
  managedBy: string
  roles: { role: string; allow: string[]; requireApproval: string[]; approve: string[] }[]
  tools: { name: string; risk: string }[]
  tests: { test: { roles: string[]; tool: string; expect: string }; passed: boolean; actual: string; description: string }[]
}

export interface Simulation {
  decision: 'Allow' | 'RequireApproval' | 'Deny'
  reason: string
  rule: string | null
  canApprove: boolean
  approverRoles: string[]
  visibleTools: string[]
}

export interface ModelsInfo {
  endpoints: { name: string; baseUrl: string; location: string; clearance?: string; up: boolean; latencyMs: number; error: string | null; models: string[] }[]
  aliases: { name: string; targets: { endpoint: string; model: string }[]; fastReasoningEffort: string | null; usedBy: string[] }[]
}

export interface IdentityInfo {
  mode: string
  authority: string | null
  clientId: string | null
  audience: string | null
  userClaim: string
  roleClaim: string
  rolePrefix: string | null
  adminRoles: string
  auditRoles: string
  knownRoles: string[]
  servers: { server: string; profiles: string; auth: string; credentialType: string | null; tokenUrl: string | null }[]
  devHeaders: boolean
}

export interface MappingTest {
  user: string | null
  roles: string[]
  unknownRoles: string[]
  isAdmin: boolean
  isAuditor: boolean
  expiresAt: string | null
  issuer: string | null
  note: string
}

export interface UserInfo {
  user: string
  roles: string[]
  firstSeen: string
  lastActive: string
  runs: number
  runs30Days: number
  tokens30Days: number
  approvals30Days: number
  ownVoice: boolean
  personalSources: number
}

/** Roles × tools for one profile (#156): each cell is the policy engine's decision for a user with only that role. */
export interface PolicyMatrix {
  profile: string
  roles: string[]
  tools: { name: string; risk: string }[]
  cells: Record<string, Record<string, { decision: 'Allow' | 'Deny' | 'RequireApproval'; reason: string }>>
}

/** Admin endpoints (#66, #68-#73); uses the app's authenticated request helper. */
export function adminApi(api: Api) {
  const r = api.raw
  return {
    resources: () => r<Resource[]>('/admin/v1/resources'),
    resource: (kind: string, name: string) => r<{ kind: string; name: string; managedBy: string; version: number | null; spec: string }>(`/admin/v1/resources/${kind}/${encodeURIComponent(name)}`),
    versions: (kind: string, name: string) => r<ResourceVersion[]>(`/admin/v1/resources/${kind}/${encodeURIComponent(name)}/versions`),
    apply: (yaml: string, dryRun: boolean) => r<ApplyOutcome>('/admin/v1/apply', { method: 'POST', body: JSON.stringify({ yaml, dryRun }) }, [422]),
    remove: (kind: string, name: string) => r<ApplyResult>(`/admin/v1/resources/${kind}/${encodeURIComponent(name)}`, { method: 'DELETE' }),
    gitops: () => r<GitOpsInfo>('/admin/v1/gitops'),
    syncGitops: () => r<GitOpsInfo['state']>('/admin/v1/gitops/sync', { method: 'POST', body: '{}' }),
    policy: () => r<ProfilePolicy[]>('/admin/v1/policy'),
    simulate: (profile: string, roles: string[], tool: string) =>
      r<Simulation>('/admin/v1/policy/simulate', { method: 'POST', body: JSON.stringify({ profile, roles, tool }) }),
    matrix: (profile: string) => r<PolicyMatrix>(`/admin/v1/policy/matrix?profile=${encodeURIComponent(profile)}`),
    models: () => r<ModelsInfo>('/models'),
    identity: () => r<IdentityInfo>('/admin/v1/identity'),
    testMapping: (token: string) => r<MappingTest>('/admin/v1/identity/test', { method: 'POST', body: JSON.stringify({ token }) }),
    users: () => r<UserInfo[]>('/admin/v1/users'),
  }
}

export type AdminApi = ReturnType<typeof adminApi>
