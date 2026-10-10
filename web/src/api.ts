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
}

export const isTerminal = (s: RunStatus) => s === 'Completed' || s === 'Failed' || s === 'Cancelled'

export interface Approval {
  id: string
  runId: string
  tool: string
  arguments: string | null
  requestedBy: string
  requestedAt: string
  status: 'Pending' | 'Approved' | 'Denied'
  decidedBy: string | null
  decidedAt: string | null
  comment: string | null
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

export function createApi(auth: Auth) {
  async function request<T>(path: string, init: RequestInit = {}): Promise<T> {
    const res = await fetch(path, {
      ...init,
      headers: {
        // Only a request with a body declares one; FastEndpoints rejects an empty JSON body with 400.
        ...(typeof init.body === 'string' ? { 'Content-Type': 'application/json' } : {}),
        ...(await auth.headers()),
        ...init.headers,
      },
    })
    if (!res.ok) throw new ApiError(res.status, `${res.status} ${res.statusText}`)
    return res.status === 204 ? (undefined as T) : res.json()
  }

  return {
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
    listConversations: (f: { q?: string; status?: string } = {}) => {
      const q = new URLSearchParams()
      if (f.q) q.set('q', f.q)
      if (f.status) q.set('status', f.status)
      return request<ConversationList>(`/conversations?${q}`)
    },
    getConversation: (id: string) => request<ConversationDetail>(`/conversations/${id}`),
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
      const q = new URLSearchParams()
      if (f.user) q.set('user', f.user)
      if (f.runId) q.set('runId', f.runId)
      if (f.from) q.set('from', new Date(f.from).toISOString())
      if (f.to) q.set('to', new Date(f.to).toISOString())
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
