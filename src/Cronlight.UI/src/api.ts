import type { AssistantEvent, AssistantMessage, ImportedJob, ImportResult, Job } from '@/types'

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

/** A request the server answered with an error: its message, and the HTTP status */
export class ApiError extends Error {
  constructor(
    message: string,
    readonly status: number
  ) {
    super(message)
  }
}

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
  if (!res.ok) throw new ApiError(await errorMessage(res), res.status)
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

/**
 * Downloads the scheduler's jobs as a JSON or YAML file, named as the server names it. Fetched rather than linked, so
 * a failure shows as an error instead of downloading it. Answers the file's name, and how many jobs were left out
 * because their class cannot be loaded in this app.
 */
export async function exportJobs(format: 'json' | 'yaml') {
  const res = await fetch(`api/${forScheduler(`jobs/export?format=${format}`)}`, {
    credentials: 'same-origin',
  })
  if (!res.ok) throw new Error(await errorMessage(res))
  const name =
    /filename="([^"]+)"/.exec(res.headers.get('content-disposition') ?? '')?.[1] ??
    `cronlight-jobs.${format}`
  const url = URL.createObjectURL(await res.blob())
  const link = Object.assign(document.createElement('a'), { href: url, download: name })
  document.body.append(link)
  link.click()
  link.remove()
  URL.revokeObjectURL(url)
  return { name, notExported: Number(res.headers.get('x-cronlight-not-exported') ?? 0) || 0 }
}

/** What importing a file did or would do; a file with mistakes changes nothing, and says what is wrong with each job. */
export type ImportOutcome =
  { ok: true; result: ImportResult } | { ok: false; message: string; jobs: ImportedJob[] }

/** Imports a file of jobs, or with dryRun only checks it and says what importing it would change. */
export async function importJobs(
  text: string,
  yaml: boolean,
  dryRun: boolean
): Promise<ImportOutcome> {
  const res = await fetch(`api/${forScheduler(`jobs/import?dryRun=${dryRun}`)}`, {
    method: 'POST',
    credentials: 'same-origin',
    headers: {
      'X-Cronlight-Request': '1',
      'Content-Type': yaml ? 'application/yaml' : 'application/json',
    },
    body: text,
  })
  if (res.ok) return { ok: true, result: (await res.json()) as ImportResult }
  const body = await res.text()
  try {
    const problem = JSON.parse(body) as { title?: string; detail?: string; jobs?: ImportedJob[] }
    return {
      ok: false,
      message: problem.detail || problem.title || `${res.status}`,
      jobs: problem.jobs ?? [],
    }
  } catch {
    return { ok: false, message: body || `${res.status} ${res.statusText}`, jobs: [] }
  }
}

/**
 * Asks the assistant, with the conversation so far, and hands over each event of its answer as it arrives. A question
 * the server refuses (not set up, not allowed, too long) throws an ApiError before any event.
 */
export async function askAssistant(
  messages: AssistantMessage[],
  onEvent: (event: AssistantEvent) => void,
  signal?: AbortSignal
) {
  const res = await fetch(`api/${forScheduler('assistant/ask')}`, {
    method: 'POST',
    signal,
    credentials: 'same-origin',
    headers: { 'X-Cronlight-Request': '1', 'Content-Type': 'application/json' },
    body: JSON.stringify({ messages }),
  })
  if (!res.ok || !res.body) throw new ApiError(await errorMessage(res), res.status)
  const reader = res.body.pipeThrough(new TextDecoderStream()).getReader()
  // One JSON object a line; a read can end mid-line, so the rest waits for the next
  let rest = ''
  const emit = (line: string) => {
    if (line.trim()) onEvent(JSON.parse(line) as AssistantEvent)
  }
  for (;;) {
    const { value, done } = await reader.read()
    if (done) break
    const lines = (rest + value).split('\n')
    rest = lines.pop() ?? ''
    lines.forEach(emit)
  }
  emit(rest)
}
