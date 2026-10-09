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
        ...(init.body ? { 'Content-Type': 'application/json' } : {}),
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
    startRun: (prompt: string, profile: string) =>
      request<{ id: string; status: RunStatus }>('/runs', { method: 'POST', body: JSON.stringify({ prompt, profile }) }),
  }
}

export type Api = ReturnType<typeof createApi>
