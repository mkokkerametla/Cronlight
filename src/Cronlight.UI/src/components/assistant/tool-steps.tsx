import { CircleAlert, CircleCheck, Loader2, Wrench } from 'lucide-react'
import { stepDetail, stepLabel, type ToolStep } from '@/lib/assistant-tools'
import { cn } from '@/lib/utils'

function Step({ step }: { step: ToolStep }) {
  const detail = stepDetail(step.arguments)
  return (
    <li className="flex items-start gap-2">
      {!step.done ? (
        <Loader2 className="mt-0.5 size-3.5 shrink-0 animate-spin text-link" />
      ) : step.error ? (
        <CircleAlert className="mt-0.5 size-3.5 shrink-0 text-amber-600 dark:text-amber-400" />
      ) : (
        <CircleCheck className="mt-0.5 size-3.5 shrink-0 text-emerald-600 dark:text-emerald-400" />
      )}
      <span className="min-w-0">
        <span className={cn(!step.done && 'text-foreground')}>{stepLabel(step)}</span>
        {detail && <span> · {detail}</span>}
        {step.error && (
          <span className="block text-amber-700 dark:text-amber-400">{step.error}</span>
        )}
      </span>
    </li>
  )
}

/**
 * The tools an answer used: listed as they run while it is written, then folded away under a summary, so the user
 * can still check where an answer came from.
 */
export function ToolSteps({ steps, open }: { steps: ToolStep[]; open: boolean }) {
  if (steps.length === 0) return null
  const list = (
    <ul className="space-y-1 text-xs text-muted-foreground" aria-label="Tools used">
      {steps.map(step => (
        <Step key={step.id} step={step} />
      ))}
    </ul>
  )
  if (open) return list
  const failed = steps.filter(s => s.error).length
  return (
    <details className="group text-xs text-muted-foreground">
      <summary className="inline-flex cursor-pointer items-center gap-1.5 rounded-md px-1.5 py-0.5 -ml-1.5 select-none hover:bg-muted hover:text-foreground">
        <Wrench className="size-3" />
        Used {steps.length} {steps.length === 1 ? 'tool' : 'tools'}
        {failed > 0 && (
          <span className="text-amber-700 dark:text-amber-400">· {failed} failed</span>
        )}
      </summary>
      <div className="mt-1.5 border-l pl-3">{list}</div>
    </details>
  )
}
