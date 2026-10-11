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
  /** Sub-agents (#103): the run that delegated to this one, and the runs this one delegated to. */
  parentRunId?: string | null
  subRuns?: string[] | null
  /** The chat this run is a turn of (#152); null for scheduled, API and webhook runs. */
  conversationId?: string | null
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
  /** Chat groups (#153). */
  groupId?: string | null
  isolated: boolean
  pinned: boolean
  archived: boolean
}

/** A folder of the user's chats (#153). */
export interface ChatGroup {
  id: string
  name: string
  color: string | null
  icon: string | null
  sortOrder: number
  pinned: boolean
  archived: boolean
  instructions: string | null
  defaultContext: string | null
  shareContext: boolean
  chats: number
}

/** The router could not choose with confidence (#150): the user picks one of these. */
export interface RouteChoice {
  reason: string
  candidates: { profile: string; score: number; readOnly: boolean }[]
}

export interface StartedRun {
  id: string
  status: RunStatus
  /** The context that answers and how it was chosen (manual, only, auto, sticky, chosen, corrected, default). */
  profile?: string
  routing?: string
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
  voice?: boolean
  retryOf?: string | null
  /** Regenerated or edited: replaced by a later turn (#95). */
  superseded?: boolean
  attachments?: { id: string; name: string; kind: string }[] | null
  /** The user's own rating of this answer (#121): 1 good, -1 bad. */
  rating?: number | null
  feedbackComment?: string | null
  /** The context that answered this turn and how it was chosen (#150). */
  profile?: string | null
  routing?: string | null
  /** Contexts that answered together (#151). */
  contexts?: string[] | null
}

/** A user's rating of an answer (#121). */
export interface Feedback {
  id: string
  runId: string
  rating: number
  comment: string | null
  updatedAt: string
  state: 'Open' | 'Resolved' | 'Converted'
}

export interface FeedbackCase {
  id: string
  question: string
  profile?: string
  expectedTools?: string[]
  expectedFacts?: string[]
  forbiddenTools?: string[]
  expectRefusal?: boolean
  judge?: string
}

export interface FeedbackItem {
  id: string
  runId: string
  user: string
  profile: string
  rating: number
  comment: string | null
  prompt: string
  answer: string | null
  toolsCalled: string[]
  createdAt: string
  state: Feedback['state']
  reviewedBy: string | null
  reviewedAt: string | null
  reviewNote: string | null
  case: FeedbackCase | null
}

/** What the caller may use (#157): pages of the UI and the contexts (profiles) they can use. */
export interface Capabilities {
  pages: string[]
  contexts: string[]
  admin: boolean
  audit: boolean
  reviewer: boolean
  insights: boolean
  approve: boolean
  /** Set while viewing as other roles (#156). */
  preview: { roles: string[]; realRoles: string[]; expires: string; allowWrites: boolean } | null
  canPreview: boolean
  /** The knowledge inspector (#158). */
  knowledgeInspect: boolean
  roles: string[]
  knownRoles: string[] | null
}

export interface StartedPreview {
  token: string
  roles: string[]
  expires: string
  allowWrites: boolean
  header: string
}

/** Insights (#141, #143, #144, #146). */
export interface OutcomeGroup {
  key: string
  runs: number
  successRate: number
  p50WallMs: number
  p95WallMs: number
  cost: number
  toolErrors: number
  denials: number
  thumbsDown: number
  problems: Record<string, number>
}

export interface OutcomeReport {
  from: string
  to: string
  total: OutcomeGroup
  byProfileVersion: OutcomeGroup[]
  byModel: OutcomeGroup[]
  byChannel: OutcomeGroup[]
  byTool: { tool: string; calls: number; errors: number; errorRate: number }[]
  items: { runId: string; endedAt: string; profile: string; profileVersion: number; model: string | null; channel: string; status: string; problem: string; wallMs: number; feedbackRating: number | null; traceId: string | null }[]
  byDay?: OutcomeGroup[]
}

export interface MinedCase {
  id: string
  question: string
  profile?: string
  expectedTools?: string[]
  forbiddenTools?: string[]
  expectRefusal?: boolean
  judge?: string
}

export interface Candidate {
  id: string
  signature: string
  profile: string
  profileVersion: number
  problem: string
  runs: number
  impact: number
  runIds: string[]
  draft: MinedCase
  case: MinedCase | null
  state: 'Open' | 'Accepted' | 'Rejected'
  reviewedBy: string | null
  reviewNote: string | null
  lastSeen: string
}

export interface Proposal {
  id: string
  profile: string
  baseVersion: number
  title: string
  rationale: string
  evidence: { runIds?: string[]; notes?: string } | null
  diff: string
  state: 'Draft' | 'Evaluated' | 'PrOpened' | 'Merged' | 'Rejected'
  evaluation: { current: { passRate: number; passed: number; cases: number }; proposed: { passRate: number; passed: number; cases: number }; regressions: string[]; improvements: string[] } | null
  regresses: boolean | null
  prUrl: string | null
  createdBy: string
  createdAt: string
  note: string | null
}

export interface FollowUp {
  verdict: 'not-applied' | 'too-early' | 'improved' | 'no-effect' | 'regressed'
  before: { version: number; runs: number; successRate: number; p95WallMs: number }
  after: { version: number; runs: number; successRate: number; p95WallMs: number } | null
  explanation: string
  revertDiff: string | null
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
    listConversations: (f: { q?: string; status?: string; profile?: string; from?: string; group?: string; archived?: boolean; mine?: boolean } = {}) => {
      const q = new URLSearchParams()
      if (f.mine) q.set('mine', 'true')
      if (f.group) q.set('group', f.group)
      if (f.archived) q.set('archived', 'true')
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
    myFeedback: (runId: string) =>
      request<Feedback>(`/runs/${runId}/feedback`).catch((e) => {
        if (e instanceof ApiError && e.status === 404) return null
        throw e
      }),
    rate: (runId: string, rating: 1 | -1, comment?: string) =>
      request<Feedback>(`/runs/${runId}/feedback`, { method: 'PUT', body: JSON.stringify({ rating, comment: comment?.trim() || null }) }),
    unrate: (runId: string) => request<void>(`/runs/${runId}/feedback`, { method: 'DELETE' }),
    feedbackQueue: (f: { state?: string; rating?: string; profile?: string } = {}) => {
      const q = new URLSearchParams(Object.entries(f).filter(([, v]) => v) as [string, string][])
      return request<{ items: FeedbackItem[]; open: number; down: number; up: number }>(`/feedback?${q}`)
    },
    resolveFeedback: (id: string, note?: string) =>
      request<Feedback>(`/feedback/${id}/resolve`, { method: 'POST', body: JSON.stringify({ note: note?.trim() || null }) }),
    feedbackToCase: (id: string, body: Omit<FeedbackCase, 'id' | 'profile'>) =>
      request<FeedbackCase>(`/feedback/${id}/eval-case`, { method: 'POST', body: JSON.stringify(body) }),
    feedbackExport: (kind: 'eval-cases' | 'labels', profile?: string) =>
      request<unknown>(`/feedback/${kind}${profile ? `?profile=${encodeURIComponent(profile)}` : ''}`),
    outcomes: (days: number) => request<OutcomeReport>(`/insights/outcomes?from=${encodeURIComponent(new Date(Date.now() - days * 86400000).toISOString())}&limit=50`),
    candidates: (state: string) => request<Candidate[]>(`/insights/candidates?state=${state}`),
    mine: () => request<{ runsWithSignals: number; clusters: number; newCandidates: number; updatedCandidates: number }>('/insights/mine', { method: 'POST', body: '{}' }),
    acceptCandidate: (id: string, c: MinedCase, note?: string) =>
      request<Candidate>(`/insights/candidates/${id}/accept`, { method: 'POST', body: JSON.stringify({ case: c, note: note || null }) }),
    rejectCandidate: (id: string, note?: string) => request<Candidate>(`/insights/candidates/${id}/reject`, { method: 'POST', body: JSON.stringify({ note: note || null }) }),
    minedDataset: () => request<unknown>('/insights/eval-cases'),
    proposals: () => request<Proposal[]>('/insights/proposals'),
    openPullRequest: (id: string) => request<Proposal>(`/insights/proposals/${id}/pr`, { method: 'POST', body: '{}' }),
    rejectProposal: (id: string, note?: string) => request<Proposal>(`/insights/proposals/${id}/reject`, { method: 'POST', body: JSON.stringify({ note: note || null }) }),
    followUp: (id: string) => request<FollowUp>(`/insights/proposals/${id}/follow-up`),
    capabilities: () => request<Capabilities>('/me/capabilities'),
    groups: () => request<ChatGroup[]>('/groups'),
    createGroup: (g: Partial<ChatGroup>) => request<ChatGroup>('/groups', { method: 'POST', body: JSON.stringify(g) }),
    updateGroup: (id: string, g: Partial<ChatGroup>) => request<ChatGroup>(`/groups/${id}`, { method: 'PUT', body: JSON.stringify(g) }),
    deleteGroup: (id: string) => request<void>(`/groups/${id}`, { method: 'DELETE' }),
    organise: (id: string, o: { groupId?: string; ungroup?: boolean; isolated?: boolean; pinned?: boolean; archived?: boolean }) =>
      request<unknown>(`/conversations/${id}/organise`, { method: 'PUT', body: JSON.stringify(o) }),
    startPreview: (roles: string[], minutes: number, allowWrites: boolean) =>
      request<StartedPreview>('/me/preview', { method: 'POST', body: JSON.stringify({ roles, minutes, allowWrites }) }),
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
    /** Answer the latest turn again: the same or an edited prompt, or in another context ("use cmdb instead", #150). */
    regenerateRun: (id: string, prompt?: string, profile?: string) =>
      request<StartedRun>(`/runs/${id}/regenerate`, { method: 'POST', body: JSON.stringify({ ...(prompt ? { prompt } : {}), ...(profile ? { profile } : {}) }) }),
    /**
     * Live run events (#95) via fetch (EventSource cannot send the Authorization header). Calls onPartial with the answer written
     * so far, onChanged when status or steps change; resolves when the run is done or the stream ends.
     */
    streamRun: async (id: string, handlers: { onPartial?: (text: string) => void; onChanged?: () => void }, signal?: AbortSignal) => {
      const res = await fetch(`/runs/${id}/events`, { headers: await auth.headers(), signal })
      if (!res.ok || !res.body) throw new ApiError(res.status, `events ${res.status}`)
      const reader = res.body.pipeThrough(new TextDecoderStream()).getReader()
      let buffer = ''
      for (;;) {
        const { value, done } = await reader.read()
        if (done) return
        buffer += value
        let cut: number
        while ((cut = buffer.indexOf('\n\n')) >= 0) {
          const block = buffer.slice(0, cut)
          buffer = buffer.slice(cut + 2)
          const event = /^event: (.*)$/m.exec(block)?.[1]
          const data = /^data: (.*)$/m.exec(block)?.[1]
          if (event === 'partial' && data) handlers.onPartial?.((JSON.parse(data) as { text: string }).text)
          else if (event === 'changed') handlers.onChanged?.()
          else if (event === 'done') {
            handlers.onChanged?.()
            return
          }
        }
      }
    },
    uploadAttachment: async (file: File) => {
      const form = new FormData()
      form.append('File', file, file.name)
      const res = await fetch('/attachments', { method: 'POST', body: form, headers: await auth.headers() })
      if (!res.ok) throw new ApiError(res.status, await describe(res))
      return (await res.json()) as { id: string; name: string; kind: 'image' | 'text' | 'document'; size: number }
    },
    /**
     * Starts a run. profile null (or "auto") lets the shell choose the context (#150); when it is unsure it answers with a choice
     * instead of a run, which the caller shows and sends again with routing "chosen".
     */
    startRun: async (
      prompt: string,
      profile: string | null,
      options: { voice?: boolean; conversationId?: string; attachments?: string[]; routing?: 'chosen'; contexts?: string[]; groupId?: string } = {},
    ): Promise<StartedRun | { choose: RouteChoice }> => {
      const r = await request<StartedRun | RouteChoice>('/runs', { method: 'POST', body: JSON.stringify({ prompt, profile: profile || 'auto', ...options }) }, [409])
      return 'candidates' in r ? { choose: r } : r
    },
    /** A short fixed acknowledgement ("Jag kollar.") to play while the agent works; null when unavailable. */
    ack: async (language: VoiceLanguage): Promise<Blob | null> => {
      const res = await fetch(`/voice/ack?language=${language === 'auto' ? 'sv' : language}`, { headers: await auth.headers() })
      return res.ok ? res.blob() : null
    },
  }
}

export type Api = ReturnType<typeof createApi>
