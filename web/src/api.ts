import type { Auth } from './auth'

export interface RunSummary {
  id: string
  prompt: string
  profile: string
  user: string
  status: RunStatus
  createdAt: string
}

export interface Step {
  seq: number
  kind: 'ModelCall' | 'ToolCall'
  name: string
  toolCallId: string | null
  arguments: string | null
  result: string | null
  latencyMs: number
  promptTokens: number | null
  completionTokens: number | null
  at: string
  endpoint: string | null
  /** Tool steps: the policy decision and its reason (why it ran or was stopped). */
  decision: string | null
  reason: string | null
  /** Tool steps: the output looked like an injected instruction and was flagged for the model (#85). */
  flagged?: boolean
}

export interface RunDetail {
  id: string
  prompt: string
  status: RunStatus
  finalAnswer: string | null
  error: string | null
  createdAt: string
  updatedAt: string
  steps: Step[]
  /** What an unfinished run waits for; null when it is finished. */
  waiting: 'queued' | 'model' | 'tool' | 'approval' | 'cancelling' | null
  retryOf: string | null
  traceId: string | null
  /** The highest data class the run has read (#89): public, internal, confidential or restricted. */
  sensitivity?: string
}

export const isTerminal = (s: RunStatus) => s === 'Completed' || s === 'Failed' || s === 'Cancelled'

export interface Approval {
  id: string
  runId: string
  tool: string
  arguments: string | null
  requestedBy: string
  requestedAt: string
  status: 'Pending' | 'Approved' | 'Denied' | 'Expired'
  decidedBy: string | null
  decidedAt: string | null
  comment: string | null
  risk: string | null
  requiredApprovals: number
  approvedBy: string[]
  expiresAt: string | null
  commentRequired: boolean
}

export interface AuditEntry {
  id: string
  at: string
  user: string
  roles: string
  profile: string
  profileVersion: number
  runId: string
  tool: string
  arguments: string | null
  decision: string
  reason: string
  approver: string | null
  result: string | null
  backendAuth: string | null
}

export interface AuditFilter {
  user?: string
  runId?: string
  from?: string
  to?: string
  tool?: string
  decision?: string
  profile?: string
}

export function auditQuery(f: AuditFilter): URLSearchParams {
  const q = new URLSearchParams()
  for (const k of ['user', 'runId', 'tool', 'decision', 'profile'] as const) if (f[k]) q.set(k, f[k]!)
  if (f.from) q.set('from', new Date(f.from).toISOString())
  if (f.to) q.set('to', new Date(f.to).toISOString())
  return q
}

export interface ToolCall {
  runId: string
  seq: number
  at: string
  user: string
  profile: string
  tool: string
  arguments: string | null
  result: string | null
  latencyMs: number
  status: 'ok' | 'error' | 'denied'
  decision: string | null
  reason: string | null
  approver: string | null
  backendAuth: string | null
}

export interface ToolStats {
  tool: string
  calls: number
  errors: number
  denied: number
  errorRate: number
  p50Ms: number
  p95Ms: number
}

export interface ToolCallList {
  calls: ToolCall[]
  tools: ToolStats[]
  total: number
  truncated: boolean
}

export interface ToolCallFilter {
  user?: string
  profile?: string
  tool?: string
  status?: string
  from?: string
  to?: string
}

export interface ServerInfo {
  name: string
  url: string
  auth: string
  credentialType: string | null
  profiles: string[]
  health: 'ok' | 'unavailable' | 'per-user' | 'unknown'
  error: string | null
  checkedAt: string | null
  tools: { name: string; description: string; profiles: string[] }[]
}

export interface CatalogEntry {
  tool: string
  description: string
  server: string | null
  profile: string | null
  risk: string | null
  status: 'exposed' | 'missing' | 'per-user' | 'unclassified'
  allowedRoles: string[]
  approvalRoles: string[]
  approverRoles: string[]
  calls: number
  lastUsed: string | null
}

export interface KnowledgeSource {
  id: string
  name: string
  kind: 'upload' | 'directory' | 'url'
  location: string | null
  readers: string[]
  /** Data class of the source's passages (#89). */
  sensitivity?: string
  owner: string
  status: 'queued' | 'indexing' | 'ready' | 'failed'
  error: string | null
  indexedAt: string | null
  documents: number
  chunks: number
  embedModel: string | null
  managedBy: string
  canManage: boolean
  personal: boolean
}

export interface KnowledgeOverview {
  backend: string
  vectorExtension: boolean
  note: string | null
  embeddingsConfigured: boolean
  embedModel: string
  sources: KnowledgeSource[]
}

export interface KnowledgeDoc {
  id: string
  externalId: string
  title: string
  url: string | null
  chars: number
  updatedAt: string
}

export interface KnowledgeHit {
  chunkId: string
  sourceId: string
  sourceName: string
  title: string
  url: string | null
  updatedAt: string
  heading: string
  text: string
  score: number
  vectorRank: number | null
  textRank: number | null
}

export interface Conflict {
  id: string
  question: string
  summary: string
  status: 'open' | 'resolved'
  detectedAt: string
  options: { chunkId: string; title: string; sourceName: string; heading: string; text: string; updatedAt: string; changed: boolean }[]
  myVote: string | null
  tally: { options: { option: string; weight: number; votes: number }[]; votes: number; staleVotes: number; suggestion: string | null; swing: boolean }
  resolvedOption: string | null
  resolvedBy: string | null
}

export interface Transcription {
  text: string
  language: string | null
  durationSeconds: number | null
}

export interface Vocabulary {
  words: string[]
  shared: string[]
}

export interface StageTotals {
  sttMs: number
  llmMs: number
  toolMs: number
  ttsMs: number
  otherMs: number
}

export interface TimelineEvent {
  kind: 'stt' | 'llm' | 'tool' | 'tts'
  name: string
  startMs: number
  durationMs: number
  promptTokens: number | null
  completionTokens: number | null
}

export interface ConversationSummary {
  id: string
  userId: string
  profile: string
  title: string
  summary: string | null
  voice: boolean
  startedAt: string
  endedAt: string
  durationMs: number
  turns: number
  messages: number
  status: string
  promptTokens: number
  completionTokens: number
  stages: StageTotals
}

export interface ConversationTurn {
  runId: string
  prompt: string
  answer: string | null
  status: string
  error: string | null
  startedAt: string
  durationMs: number
  events: TimelineEvent[]
}

export interface ConversationDetail {
  conversation: ConversationSummary
  turns: ConversationTurn[]
}

export interface ConversationList {
  conversations: ConversationSummary[]
  stages: StageTotals
  count: number
}

export interface UserSettings {
  talkativeness: number
  warmth: number
  formality: number
  expressiveness: number
  pace: number
  ownVoice: { seconds: number; consentAt: string } | null
  voiceEnabled: boolean
}

export type VoiceLanguage = 'auto' | 'sv' | 'en'

export type RunStatus = 'Pending' | 'Running' | 'WaitingForApproval' | 'Completed' | 'Failed' | 'Cancelled'

export class ApiError extends Error {
  status: number

  constructor(status: number, message: string) {
    super(message)
    this.status = status
  }
}

/** The server's validation messages when it sent some (FastEndpoints: {errors: {field: [..]}}), else the status. */
async function describe(res: Response): Promise<string> {
  try {
    const body = (await res.json()) as { errors?: Record<string, string[]> }
    const messages = Object.values(body.errors ?? {}).flat()
    if (messages.length) return messages.join(' ')
  } catch {
    // not JSON
  }
  return `${res.status} ${res.statusText}`
}

export function createApi(auth: Auth) {
  /** okStatuses: error statuses whose JSON body is still the answer (e.g. 422 from apply with per-resource errors). */
  async function request<T>(path: string, init: RequestInit = {}, okStatuses: number[] = []): Promise<T> {
    const res = await fetch(path, {
      ...init,
      headers: {
        // Only a request with a body declares one; FastEndpoints rejects an empty JSON body with 400.
        ...(typeof init.body === 'string' ? { 'Content-Type': 'application/json' } : {}),
        ...(await auth.headers()),
        ...init.headers,
      },
    })
    if (!res.ok && !okStatuses.includes(res.status)) throw new ApiError(res.status, await describe(res))
    return res.status === 204 ? (undefined as T) : res.json()
  }

  return {
    /** The authenticated request helper itself, for feature modules with their own endpoints (admin pages). */
    raw: request,
    listRuns: () => request<RunSummary[]>('/runs'),
    getRun: (id: string) => request<RunDetail>(`/runs/${id}`),
    cancelRun: (id: string) => request<{ id: string; status: RunStatus }>(`/runs/${id}/cancel`, { method: 'POST', body: '{}' }),
    retryRun: (id: string) => request<{ id: string; status: RunStatus }>(`/runs/${id}/retry`, { method: 'POST', body: '{}' }),
    /** Dictation: audio in, text out. The text is only a draft for the user to review. */
    transcribe: (audio: Blob, language: VoiceLanguage, conversationId?: string) => {
      const form = new FormData()
      form.append('Audio', audio, audio.type.includes('wav') ? 'speech.wav' : 'dictation.webm')
      if (language !== 'auto') form.append('Language', language)
      if (conversationId) form.append('ConversationId', conversationId)
      return request<Transcription>('/voice/transcribe', { method: 'POST', body: form })
    },
    listConversations: (f: { q?: string; status?: string; profile?: string; from?: string } = {}) => {
      const q = new URLSearchParams()
      if (f.q) q.set('q', f.q)
      if (f.status) q.set('status', f.status)
      if (f.profile) q.set('profile', f.profile)
      if (f.from) q.set('from', new Date(f.from).toISOString())
      return request<ConversationList>(`/conversations?${q}`)
    },
    getConversation: (id: string) => request<ConversationDetail>(`/conversations/${id}`),
    conversationAudio: (id: string) => request<{ id: string; kind: 'user' | 'agent'; runId: string | null; at: string; bytes: number }[]>(`/conversations/${id}/audio`),
    deleteConversationAudio: (id: string) => request<void>(`/conversations/${id}/audio`, { method: 'DELETE' }),
    deleteConversation: (id: string) => request<void>(`/conversations/${id}`, { method: 'DELETE' }),
    summarizeConversation: (id: string) => request<{ title: string; summary: string }>(`/conversations/${id}/summarize`, { method: 'POST', body: '{}' }),
    conversationStats: (days: number) =>
      request<{ day: string; conversations: number; turns: number; avgTurnMs: number; sttMs: number; llmMs: number; toolMs: number; ttsMs: number; tokens: number }[]>(
        `/conversations/stats?days=${days}`,
      ),
    /** Auth headers for requests that need the raw response (audio blobs). */
    authHeaders: () => auth.headers(),
    getSettings: () => request<UserSettings>('/me/settings'),
    putSettings: (s: Pick<UserSettings, 'talkativeness' | 'warmth' | 'formality' | 'expressiveness' | 'pace'>) =>
      request<UserSettings>('/me/settings', {
        method: 'PUT',
        body: JSON.stringify({ talkativeness: s.talkativeness, warmth: s.warmth, formality: s.formality, expressiveness: s.expressiveness, pace: s.pace }),
      }),
    /** Registers the caller's own voice. `consent` must be true: the user confirmed it is their own voice. */
    recordVoice: (clip: Blob, consent: boolean) => {
      const form = new FormData()
      form.append('Audio', clip, 'voice.webm')
      form.append('Consent', String(consent))
      return request<UserSettings>('/me/voice', { method: 'PUT', body: form })
    },
    deleteVoice: () => request<UserSettings>('/me/voice', { method: 'DELETE' }),
    getVocabulary: () => request<Vocabulary>('/voice/vocabulary'),
    putVocabulary: (words: string[]) => request<Vocabulary>('/voice/vocabulary', { method: 'PUT', body: JSON.stringify({ words }) }),
    /** The spoken final answer of a run, as an audio blob. */
    speak: async (runId: string, language: VoiceLanguage): Promise<Blob> => {
      const res = await fetch(`/runs/${runId}/speak`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', ...(await auth.headers()) },
        body: JSON.stringify(language === 'auto' ? {} : { language }),
      })
      if (!res.ok) throw new ApiError(res.status, `${res.status} ${res.statusText}`)
      return res.blob()
    },
    listApprovals: () => request<Approval[]>('/approvals'),
    decide: (id: string, outcome: 'approve' | 'deny', comment: string) =>
      request<Approval>(`/approvals/${id}/${outcome}`, { method: 'POST', body: JSON.stringify({ comment: comment || null }) }),
    listAudit: (f: AuditFilter) => {
      const q = auditQuery(f)
      q.set('limit', '200')
      return request<AuditEntry[]>(`/audit?${q}`)
    },
    listToolCalls: (f: ToolCallFilter) => {
      const q = new URLSearchParams()
      for (const k of ['user', 'profile', 'tool', 'status'] as const) if (f[k]) q.set(k, f[k]!)
      if (f.from) q.set('from', new Date(f.from).toISOString())
      if (f.to) q.set('to', new Date(f.to).toISOString())
      q.set('limit', '200')
      return request<ToolCallList>(`/tool-calls?${q}`)
    },
    listServers: () => request<ServerInfo[]>('/integrations/servers'),
    catalog: () => request<CatalogEntry[]>('/integrations/catalog'),
    knowledge: () => request<KnowledgeOverview>('/knowledge'),
    createSource: (body: object) => request<KnowledgeSource>('/knowledge/sources', { method: 'POST', body: JSON.stringify(body) }),
    deleteSource: (id: string) => request<void>(`/knowledge/sources/${encodeURIComponent(id)}`, { method: 'DELETE' }),
    reindexSource: (id: string) => request<unknown>(`/knowledge/sources/${encodeURIComponent(id)}/reindex`, { method: 'POST', body: '{}' }),
    listDocuments: (id: string) => request<KnowledgeDoc[]>(`/knowledge/sources/${encodeURIComponent(id)}/documents`),
    addText: (id: string, title: string, text: string) =>
      request<KnowledgeDoc>(`/knowledge/sources/${encodeURIComponent(id)}/documents`, { method: 'POST', body: JSON.stringify({ title, text }) }),
    uploadFile: (id: string, file: File) => {
      const form = new FormData()
      form.append('file', file)
      return request<KnowledgeDoc>(`/knowledge/sources/${encodeURIComponent(id)}/files`, { method: 'POST', body: form })
    },
    deleteDocument: (id: string, doc: string) =>
      request<void>(`/knowledge/sources/${encodeURIComponent(id)}/documents/${doc}`, { method: 'DELETE' }),
    searchKnowledge: (q: string, source?: string) =>
      request<KnowledgeHit[]>(`/knowledge/search?${new URLSearchParams({ q, ...(source ? { source } : {}) })}`),
    conflict: (id: string) => request<Conflict>(`/knowledge/conflicts/${id}`),
    conflicts: () => request<Conflict[]>('/knowledge/conflicts'),
    voteConflict: (id: string, option: string) => request<Conflict>(`/knowledge/conflicts/${id}/vote`, { method: 'POST', body: JSON.stringify({ option }) }),
    resolveConflict: (id: string, option: string) => request<void>(`/knowledge/conflicts/${id}/resolve`, { method: 'POST', body: JSON.stringify({ option }) }),
    chunk: (id: string) => request<KnowledgeHit>(`/knowledge/chunks/${encodeURIComponent(id)}`),
    startRun: (prompt: string, profile: string, options: { voice?: boolean; conversationId?: string } = {}) =>
      request<{ id: string; status: RunStatus }>('/runs', { method: 'POST', body: JSON.stringify({ prompt, profile, ...options }) }),
    /** A short fixed acknowledgement ("Jag kollar.") to play while the agent works; null when unavailable. */
    ack: async (language: VoiceLanguage): Promise<Blob | null> => {
      const res = await fetch(`/voice/ack?language=${language === 'auto' ? 'sv' : language}`, { headers: await auth.headers() })
      return res.ok ? res.blob() : null
    },
  }
}

export type Api = ReturnType<typeof createApi>
