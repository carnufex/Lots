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
}

export const isTerminal = (s: RunStatus) => s === 'Completed' || s === 'Failed'

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

export interface Transcription {
  text: string
  language: string | null
  durationSeconds: number | null
}

export interface Vocabulary {
  words: string[]
  shared: string[]
}

export type VoiceLanguage = 'auto' | 'sv' | 'en'

export type RunStatus = 'Pending' | 'Running' | 'WaitingForApproval' | 'Completed' | 'Failed'

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
    /** Dictation: audio in, text out. The text is only a draft for the user to review. */
    transcribe: (audio: Blob, language: VoiceLanguage) => {
      const form = new FormData()
      form.append('Audio', audio, audio.type.includes('wav') ? 'speech.wav' : 'dictation.webm')
      if (language !== 'auto') form.append('Language', language)
      return request<Transcription>('/voice/transcribe', { method: 'POST', body: form })
    },
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
