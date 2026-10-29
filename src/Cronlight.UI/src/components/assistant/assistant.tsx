import { useEffect, useRef, useState, type Dispatch, type SetStateAction } from 'react'
import {
  ArrowDown,
  ArrowUp,
  ChevronRight,
  Copy,
  RotateCcw,
  RotateCw,
  Square,
  TriangleAlert,
} from 'lucide-react'
import { toast } from 'sonner'
import { Button } from '@/components/ui/button'
import { ToolSteps } from '@/components/assistant/tool-steps'
import { ApiError, askAssistant } from '@/api'
import { asMessages, type Turn } from '@/hooks/use-conversation'
import { Markdown } from '@/lib/markdown'
import { cn } from '@/lib/utils'
import type { AssistantEvent } from '@/types'

/** What the dashboard knows about the scheduler now, for questions worth asking first */
export interface SchedulerStatus {
  /** Failed runs in the last 24 hours; null without history, undefined while unknown */
  failed?: number | null
  running?: number
  paused?: number
}

interface Suggestion {
  text: string
  /** The figure that makes it worth asking, e.g. "791 failed" */
  note?: string
  alert?: boolean
}

/** Up to four questions, led by what needs attention now; those that need history are left out without it */
function suggestionsFor({ failed, running = 0, paused = 0 }: SchedulerStatus): Suggestion[] {
  const history = failed !== null
  const list: (Suggestion | false)[] = [
    history &&
      failed !== 0 && {
        text: 'Which jobs failed in the last 24 hours, and why?',
        note: failed ? `${failed.toLocaleString()} failed` : undefined,
        alert: Boolean(failed),
      },
    running > 0 && { text: 'What is running now, and for how long?', note: `${running} running` },
    { text: 'What runs in the next 6 hours?' },
    history && { text: 'Which jobs got slower this week?' },
    history && failed !== 0 && { text: 'What are the most common errors?' },
    paused > 0 && { text: 'Which jobs are paused?', note: `${paused} paused` },
  ]
  return list.filter((s): s is Suggestion => Boolean(s)).slice(0, 4)
}

const setupDocs = 'https://github.com/mkokkerametla/Cronlight/blob/main/docs/ai-mode.md'

/** An answer as its stream's events change it */
function apply(turn: Turn, event: AssistantEvent): Turn {
  switch (event.type) {
    case 'tool':
      return {
        ...turn,
        tools: [
          ...(turn.tools ?? []),
          { id: event.id, name: event.name, arguments: event.arguments ?? {}, done: false },
        ],
      }
    case 'tool-done':
      return {
        ...turn,
        tools: turn.tools?.map(s =>
          s.id === event.id ? { ...s, done: true, error: event.error } : s
        ),
      }
    case 'text':
      return { ...turn, text: turn.text + event.text }
    case 'error':
      return {
        ...turn,
        status: 'error',
        error: event.traceId ? `${event.error} (${event.traceId})` : event.error,
      }
    case 'done':
      return { ...turn, status: 'done', ...(event.cutOff && { cutOff: true }) }
  }
}

/**
 * AI mode: ask the app's AI model about the jobs in plain language. It answers with Cronlight's read-only tools, as the
 * signed-in user, so it sees what the dashboard shows them and changes nothing. The answer streams in, with each tool
 * shown as it runs.
 */
export function Assistant({
  scheduler,
  status = {},
  turns,
  onTurnsChange,
  onOpenJob,
}: {
  scheduler: string
  status?: SchedulerStatus
  turns: Turn[]
  onTurnsChange: Dispatch<SetStateAction<Turn[]>>
  onOpenJob: (group: string, name: string) => void
}) {
  const [question, setQuestion] = useState('')
  const [pending, setPending] = useState<AbortController | null>(null)
  const box = useRef<HTMLTextAreaElement>(null)
  const end = useRef<HTMLDivElement>(null)

  // Follows the answer as it grows, unless the user has scrolled up to read
  const following = useRef(true)
  useEffect(() => {
    const onScroll = () => {
      following.current =
        window.innerHeight + window.scrollY >= document.documentElement.scrollHeight - 80
    }
    window.addEventListener('scroll', onScroll, { passive: true })
    return () => window.removeEventListener('scroll', onScroll)
  }, [])
  // A block, not an expression: newer browsers' scrollIntoView returns a promise, which React would call as cleanup
  useEffect(() => {
    if (following.current) end.current?.scrollIntoView?.({ block: 'end' })
  }, [turns])

  // A question still being answered is dropped when the page is left
  const asking = useRef<AbortController | null>(null)
  useEffect(() => {
    asking.current = pending
  }, [pending])
  useEffect(() => () => asking.current?.abort(), [])

  // The answer being written is the last turn; after New conversation there is none, and late events change nothing
  const updateAnswer = (change: (answer: Turn) => Turn) =>
    onTurnsChange(t => {
      const answer = t[t.length - 1]
      return answer?.role === 'assistant' ? [...t.slice(0, -1), change(answer)] : t
    })

  /** Asks the conversation so far, with a new question or, to retry, without one */
  const ask = async (conversation: Turn[]) => {
    if (pending) return
    const controller = new AbortController()
    following.current = true
    setPending(controller)
    onTurnsChange([
      ...conversation,
      { role: 'assistant', text: '', tools: [], status: 'streaming' },
    ])
    try {
      await askAssistant(
        asMessages(conversation),
        event => updateAnswer(answer => apply(answer, event)),
        controller.signal
      )
      // A stream that ended without saying it was done was cut off, e.g. by a proxy's timeout: what was written
      // stays on screen, and is not sent back as an answer
      updateAnswer(answer =>
        answer.status === 'streaming'
          ? {
              ...answer,
              status: 'error',
              error: answer.text
                ? 'The answer was cut off: the connection closed before it finished.'
                : 'The answer stopped before it began.',
            }
          : answer
      )
    } catch (e) {
      updateAnswer(answer =>
        controller.signal.aborted
          ? { ...answer, status: 'stopped' }
          : {
              ...answer,
              status: 'error',
              error: e instanceof Error ? e.message : String(e),
              notSetUp: e instanceof ApiError && e.status === 503,
            }
      )
    } finally {
      setPending(null)
      box.current?.focus()
    }
  }

  const askQuestion = (text: string) => {
    const asked = text.trim()
    if (!asked || pending) return
    setQuestion('')
    void ask([...turns, { role: 'user', text: asked }])
  }

  const retry = () => void ask(turns.slice(0, -1))
  // Asking the same again would stop at the same limit: the model is asked to go on instead, in the open
  const continueAnswer = () => askQuestion(continueQuestion)

  const startOver = () => {
    pending?.abort()
    onTurnsChange([])
    setQuestion('')
    box.current?.focus()
  }

  const composer = (
    <Composer
      boxRef={box}
      value={question}
      onChange={setQuestion}
      onAsk={() => askQuestion(question)}
      onStop={pending ? () => pending.abort() : undefined}
    />
  )

  if (turns.length === 0)
    return (
      <div className="mx-auto w-full max-w-3xl space-y-5 pt-2">
        <div className="space-y-1">
          <h2 className="text-lg font-semibold tracking-tight">Ask about {scheduler}'s jobs</h2>
          <p className="text-sm text-muted-foreground">
            Answers in plain language from this app's AI model, which reads what you can see here
            and cannot change anything.
          </p>
        </div>
        {composer}
        <section aria-labelledby="assistant-suggestions">
          <h3 id="assistant-suggestions" className="mb-2 text-xs font-medium text-muted-foreground">
            Worth asking now
          </h3>
          <ul className="divide-y overflow-hidden rounded-lg border bg-card">
            {suggestionsFor(status).map(({ text, note, alert }) => (
              <li key={text}>
                <button
                  type="button"
                  onClick={() => askQuestion(text)}
                  className="group flex w-full items-center gap-3 px-3.5 py-2.5 text-left text-sm transition-colors hover:bg-muted/50 focus-visible:bg-muted/50 focus-visible:outline-none"
                >
                  <span className="min-w-0 flex-1">{text}</span>
                  {note && (
                    <span
                      className={cn(
                        'shrink-0 text-xs tabular-nums',
                        alert ? 'font-medium text-destructive' : 'text-muted-foreground'
                      )}
                    >
                      {note}
                    </span>
                  )}
                  <ChevronRight className="size-4 shrink-0 text-muted-foreground transition-transform group-hover:translate-x-0.5" />
                </button>
              </li>
            ))}
          </ul>
        </section>
      </div>
    )

  return (
    <div className="mx-auto flex w-full max-w-3xl flex-col">
      <div className="flex justify-end pb-2">
        <Button variant="ghost" size="sm" onClick={startOver}>
          <RotateCcw /> New conversation
        </Button>
      </div>
      {/* Says what the answer is doing, not each word of it as it streams in */}
      <p className="sr-only" role="status">
        {announcement(turns[turns.length - 1])}
      </p>

      <div className="space-y-6 pb-4">
        {turns.map((turn, n) =>
          turn.role === 'user' ? (
            <div key={n} className="flex justify-end">
              <p className="max-w-[85%] rounded-2xl rounded-br-md bg-muted px-4 py-2.5 text-sm whitespace-pre-wrap">
                {turn.text}
              </p>
            </div>
          ) : (
            <Answer
              key={n}
              turn={turn}
              onOpenJob={onOpenJob}
              onRetry={n === turns.length - 1 && !pending ? retry : undefined}
              onContinue={n === turns.length - 1 && !pending ? continueAnswer : undefined}
            />
          )
        )}
        <div ref={end} />
      </div>

      <div className="sticky bottom-0 z-10 bg-gradient-to-t from-background from-75% to-transparent pt-4 pb-1">
        {composer}
        <p className="mt-2 text-center text-xs text-muted-foreground">
          AI answers can be wrong. Check what matters in the dashboard.
        </p>
      </div>
    </div>
  )
}

/** What a screen reader hears about the latest answer */
function announcement(turn: Turn | undefined) {
  if (turn?.role !== 'assistant') return ''
  switch (turn.status) {
    case 'streaming':
      return turn.text ? 'Writing the answer' : 'Thinking'
    case 'done':
      return turn.cutOff ? 'Answer ready, cut off at the length limit' : 'Answer ready'
    case 'stopped':
      return 'Stopped'
    case 'error':
      // The reason is on screen, next to Try again
      return 'The answer failed'
    default:
      return ''
  }
}

/** What Continue asks after an answer was cut off */
const continueQuestion = 'Continue from where you stopped.'

/** An answer: the tools it used, its text as it is written, and what can be done with it */
function Answer({
  turn,
  onOpenJob,
  onRetry,
  onContinue,
}: {
  turn: Turn
  onOpenJob: (group: string, name: string) => void
  /** Asks the question again; offered for the latest answer when it failed or was stopped */
  onRetry?: () => void
  /** Asks the model to go on; offered for the latest answer when it was cut off */
  onContinue?: () => void
}) {
  const streaming = turn.status === 'streaming'
  const tools = turn.tools ?? []
  const thinking = streaming && !turn.text && tools.every(s => s.done)
  return (
    <div className="flex">
      <div className="min-w-0 flex-1 space-y-3">
        <ToolSteps steps={tools} open={streaming} />
        {thinking && (
          <p className="animate-pulse text-sm text-muted-foreground" aria-hidden>
            Thinking…
          </p>
        )}
        {turn.text && <Markdown text={turn.text} links={{ onOpenJob }} />}
        {turn.status === 'stopped' && (
          <div className="-ml-2 flex items-center gap-1">
            <p className="px-2 text-xs text-muted-foreground italic">Stopped.</p>
            {onRetry && (
              <Button
                variant="ghost"
                size="sm"
                className="h-7 text-xs text-muted-foreground"
                onClick={onRetry}
              >
                <RotateCw /> Try again
              </Button>
            )}
          </div>
        )}
        {turn.cutOff && (
          <p className="text-xs text-muted-foreground italic">
            Cut off: the answer reached the model's length limit.{' '}
            {onContinue
              ? 'Continue asks for the rest.'
              : 'Ask for the rest, or for a shorter answer.'}
          </p>
        )}
        {turn.status === 'error' &&
          (turn.notSetUp ? (
            <div className="rounded-xl border bg-muted/40 px-4 py-3 text-sm">
              <p className="font-medium">AI mode has no model to answer with yet</p>
              <p className="mt-1 text-muted-foreground">
                This app turned AI mode on but has no AI model (an IChatClient) registered. Once one
                is set up, ask again.{' '}
                <a
                  href={setupDocs}
                  target="_blank"
                  rel="noopener noreferrer"
                  className="font-medium text-primary underline-offset-2 hover:underline"
                >
                  How to set it up
                </a>
              </p>
            </div>
          ) : (
            <div className="flex flex-wrap items-center gap-x-3 gap-y-2 rounded-xl border border-destructive/30 bg-destructive/5 px-4 py-3 text-sm text-destructive">
              <TriangleAlert className="size-4 shrink-0" />
              <span className="min-w-0 flex-1">{turn.error}</span>
              {onRetry && (
                <Button variant="outline" size="sm" onClick={onRetry}>
                  <RotateCw /> Try again
                </Button>
              )}
            </div>
          ))}
        {turn.status === 'done' && turn.text && (
          <div className="-ml-2 flex items-center gap-1">
            <Button
              variant="ghost"
              size="sm"
              className="h-7 text-xs text-muted-foreground"
              onClick={() =>
                void navigator.clipboard
                  ?.writeText(turn.text)
                  .then(() => toast.success('Copied the answer'))
              }
            >
              <Copy /> Copy
            </Button>
            {turn.cutOff && onContinue && (
              <Button
                variant="ghost"
                size="sm"
                className="h-7 text-xs text-muted-foreground"
                onClick={onContinue}
              >
                <ArrowDown /> Continue
              </Button>
            )}
          </div>
        )}
      </div>
    </div>
  )
}

/** The question box: Enter asks, Shift+Enter starts a new line, and while an answer is written, Stop */
function Composer({
  boxRef,
  value,
  onChange,
  onAsk,
  onStop,
}: {
  boxRef: React.RefObject<HTMLTextAreaElement | null>
  value: string
  onChange: (value: string) => void
  onAsk: () => void
  onStop?: () => void
}) {
  return (
    <form
      className="relative rounded-2xl border bg-card shadow-sm transition-shadow focus-within:border-ring focus-within:ring-3 focus-within:ring-ring/30"
      onSubmit={e => {
        e.preventDefault()
        onAsk()
      }}
    >
      <textarea
        ref={boxRef}
        aria-label="Your question"
        placeholder="Ask about jobs, schedules, runs or errors"
        value={value}
        maxLength={8000}
        rows={1}
        onChange={e => onChange(e.target.value)}
        onKeyDown={e => {
          if (e.key === 'Enter' && !e.shiftKey && !e.nativeEvent.isComposing) {
            e.preventDefault()
            onAsk()
          }
        }}
        className="field-sizing-content block max-h-52 min-h-[3.25rem] w-full resize-none bg-transparent px-4 pt-3.5 pb-11 text-sm outline-none placeholder:text-muted-foreground"
      />
      <div className="pointer-events-none absolute inset-x-0 bottom-0 flex items-center justify-between gap-2 px-3 pb-2.5">
        <span className="hidden text-xs text-muted-foreground sm:block">
          Enter to ask · Shift+Enter for a new line
        </span>
        {onStop ? (
          <Button
            type="button"
            size="icon"
            variant="outline"
            aria-label="Stop"
            className="pointer-events-auto ml-auto size-8 rounded-full"
            onClick={onStop}
          >
            <Square className="size-3 fill-current" />
          </Button>
        ) : (
          <Button
            type="submit"
            size="icon"
            aria-label="Ask"
            className="pointer-events-auto ml-auto size-8 rounded-full"
            disabled={!value.trim()}
          >
            <ArrowUp />
          </Button>
        )}
      </div>
    </form>
  )
}
