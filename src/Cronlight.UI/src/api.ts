import type { Job } from '@/types'

const SCHEDULER_PARAM = 'scheduler'

// The scheduler every request is for, from the page's ?scheduler=; null for the host's own
let scheduler: string | null =
  new URLSearchParams(window.location.search).get(SCHEDULER_PARAM) || null

export const currentScheduler = () => scheduler

/** Sends later requests to this scheduler (null for the host's own), and keeps it in the page's URL for reloads and links. */
export function selectScheduler(name: string | null) {
  scheduler = name
  const url = new URL(window.location.href)
  if (name) url.searchParams.set(SCHEDULER_PARAM, name)
  else url.searchParams.delete(SCHEDULER_PARAM)
  window.history.replaceState(window.history.state, '', url)
}

const forScheduler = (path: string) =>
  scheduler
    ? `${path}${path.includes('?') ? '&' : '?'}${SCHEDULER_PARAM}=${encodeURIComponent(scheduler)}`
    : path

// Relative to the page, so it resolves to {prefix}/api/... wherever the dashboard is mapped.
// Same-origin cookies flow automatically, so host cookie auth works without extra setup.
export async function api<T = void>(
  path: string,
  { method = 'GET', body, signal }: { method?: string; body?: unknown; signal?: AbortSignal } = {}
): Promise<T> {
  const res = await fetch(`api/${forScheduler(path)}`, {
    method,
    signal,
    credentials: 'same-origin',
    // The server refuses changes without this header, which another site cannot add (CSRF protection)
    headers: {
      'X-Cronlight-Request': '1',
      ...(body ? { 'Content-Type': 'application/json' } : {}),
    },
    body: body ? JSON.stringify(body) : undefined,
  })
  if (!res.ok) throw new Error(await errorMessage(res))
  if (res.status === 204 || res.headers.get('content-length') === '0') return undefined as T
  return (await res.json()) as T
}

async function errorMessage(res: Response): Promise<string> {
  const text = await res.text()
  try {
    const problem: unknown = JSON.parse(text)
    if (typeof problem === 'string') return problem
    // The detail says what is wrong; the title only names the kind of error
    const { title, detail } = problem as { title?: string; detail?: string }
    return detail || title || `${res.status}`
  } catch {
    return text || `${res.status} ${res.statusText}`
  }
}

export const jobPath = (job: Pick<Job, 'group' | 'name'>) =>
  `jobs/${encodeURIComponent(job.group)}/${encodeURIComponent(job.name)}`
