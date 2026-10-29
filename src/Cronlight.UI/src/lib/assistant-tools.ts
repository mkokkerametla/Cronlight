/** How the assistant's tool calls read on screen */

/** A tool the assistant called for an answer, as the answer stream reported it */
export interface ToolStep {
  id: number
  name: string
  arguments: Record<string, unknown>
  done: boolean
  /** Why it was refused or failed */
  error?: string
}

// What each tool does, while it runs and once it has: the model calls them, the user reads these
const labels: Record<string, [running: string, done: string]> = {
  list_schedulers: ['Listing the schedulers', 'Listed the schedulers'],
  get_scheduler: ['Checking the scheduler', 'Checked the scheduler'],
  list_jobs: ['Looking through the jobs', 'Looked through the jobs'],
  get_job: ['Reading the job', 'Read the job'],
  query_runs: ['Looking up runs', 'Looked up runs'],
  get_run_log: ['Reading the run log', 'Read the run log'],
  running_now: ['Checking what is running', 'Checked what is running'],
  upcoming_runs: ['Checking upcoming runs', 'Checked upcoming runs'],
  status_summary: ['Reading the status summary', 'Read the status summary'],
  failure_summary: ['Reading the failure summary', 'Read the failure summary'],
  preview_cron: ['Previewing the cron expression', 'Previewed the cron expression'],
  run_statistics: ['Working out run statistics', 'Worked out run statistics'],
  run_timeline: ['Building a run timeline', 'Built a run timeline'],
  error_patterns: ['Grouping the errors', 'Grouped the errors'],
}

/** The tool's label: what it does, or did */
export function stepLabel(step: Pick<ToolStep, 'name' | 'done'>) {
  const [running, done] = labels[step.name] ?? [`Calling ${step.name}`, `Called ${step.name}`]
  return step.done ? done : running
}

/** What the call was about, in a few words: the job, the run, the window */
export function stepDetail(args: Record<string, unknown>) {
  const text = (key: string) =>
    typeof args[key] === 'string' && args[key] ? String(args[key]) : null
  const parts: string[] = []
  const job = text('name') ?? text('jobName')
  const group = text('group') ?? text('jobGroup')
  if (job) parts.push(group ? `${group}.${job}` : job)
  if (text('search')) parts.push(`“${text('search')}”`)
  if (typeof args.runId === 'number') parts.push(`run ${args.runId}`)
  if (text('status')) parts.push(String(text('status')).toLowerCase())
  if (typeof args.hours === 'number')
    parts.push(
      args.hours >= 48 && args.hours % 24 === 0
        ? `last ${args.hours / 24} days`
        : `last ${args.hours} h`
    )
  if (text('fromUtc') || text('toUtc')) parts.push('a date range')
  if (text('expression')) parts.push(String(text('expression')))
  if (text('scheduler')) parts.push(`on ${text('scheduler')}`)
  return parts.join(' · ')
}
