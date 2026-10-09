import type { Auth } from './auth'

export interface RunSummary {
  id: string
  prompt: string
  profile: string
  user: string
  status: RunStatus
  createdAt: string
}

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
      headers: { 'Content-Type': 'application/json', ...(await auth.headers()), ...init.headers },
    })
    if (!res.ok) throw new ApiError(res.status, `${res.status} ${res.statusText}`)
    return res.status === 204 ? (undefined as T) : res.json()
  }

  return {
    listRuns: () => request<RunSummary[]>('/runs'),
  }
}

export type Api = ReturnType<typeof createApi>
