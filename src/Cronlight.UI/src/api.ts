import type { Job } from '@/types'

// Relative to the page, so it resolves to {prefix}/api/... wherever the dashboard is mapped.
// Same-origin cookies flow automatically, so host cookie auth works without extra setup.
export async function api<T = void>(
  path: string,
  { method = 'GET', body, signal }: { method?: string; body?: unknown; signal?: AbortSignal } = {}
): Promise<T> {
  const res = await fetch(`api/${path}`, {
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
    const { title, detail } = problem as { title?: string; detail?: string }
    return [title, detail].filter(Boolean).join(' ') || `${res.status}`
  } catch {
    return text || `${res.status} ${res.statusText}`
  }
}

export const jobPath = (job: Pick<Job, 'group' | 'name'>) =>
  `jobs/${encodeURIComponent(job.group)}/${encodeURIComponent(job.name)}`
