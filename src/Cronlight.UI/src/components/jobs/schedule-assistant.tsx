import { useState } from 'react'
import { ChevronDown, ExternalLink, Sparkles } from 'lucide-react'
import { Button } from '@/components/ui/button'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'
import { Input } from '@/components/ui/input'

interface Provider {
  id: string
  label: string
  url: (description: string, timeZone: string) => string
}

// Quartz cron differs from Unix cron (leading seconds field, `?` in day-of-month or day-of-week),
// so the question says so explicitly; otherwise answers are usually 5-field Unix expressions.
const question = (description: string, timeZone: string) =>
  `Give me a Quartz.NET cron expression for: ${description}${timeZone ? ` (time zone ${timeZone})` : ''}. ` +
  'Use the Quartz format with 6 or 7 fields (seconds minutes hours day-of-month month day-of-week [year]) ' +
  'and ? in either day-of-month or day-of-week. Reply with the expression and a one-line explanation.'

const providers: Provider[] = [
  {
    id: 'chatgpt',
    label: 'ChatGPT',
    url: (d, tz) => `https://chatgpt.com/?q=${encodeURIComponent(question(d, tz))}`,
  },
  {
    id: 'google',
    label: 'Google',
    url: d =>
      `https://www.google.com/search?q=${encodeURIComponent(`Quartz cron expression ${d}`)}`,
  },
]

const STORAGE_KEY = 'cronlight.scheduleAssistant'

function savedProvider(): Provider {
  try {
    return providers.find(p => p.id === localStorage.getItem(STORAGE_KEY)) ?? providers[0]
  } catch {
    return providers[0]
  }
}

/**
 * Turns a plain-language schedule into a question for ChatGPT or Google, opened in a new tab.
 * Nothing is sent anywhere until the user clicks; the answer is pasted back into the cron field.
 */
export function ScheduleAssistant({ timeZone }: { timeZone: string }) {
  const [description, setDescription] = useState('')
  const [provider, setProvider] = useState(savedProvider)
  const text = description.trim()

  const ask = (target: Provider) => {
    if (!text) return
    setProvider(target)
    try {
      localStorage.setItem(STORAGE_KEY, target.id)
    } catch {
      // Remembering the choice is a convenience only
    }
    window.open(target.url(text, timeZone.trim()), '_blank', 'noopener,noreferrer')
  }

  return (
    <div className="flex gap-2">
      <div className="relative min-w-0 flex-1">
        <Sparkles className="pointer-events-none absolute top-1/2 left-2.5 size-3.5 -translate-y-1/2 text-muted-foreground" />
        <Input
          className="pl-8"
          value={description}
          onChange={e => setDescription(e.target.value)}
          onKeyDown={e => {
            if (e.key === 'Enter') {
              e.preventDefault()
              ask(provider)
            }
          }}
          placeholder="Not sure? Describe it: every Tuesday and Friday at 4pm"
          aria-label="Describe the schedule in plain language"
        />
      </div>
      <div className="flex shrink-0">
        <Button
          type="button"
          variant="outline"
          className="rounded-r-none"
          disabled={!text}
          onClick={() => ask(provider)}
          title={`Ask ${provider.label} in a new tab`}
        >
          Ask {provider.label} <ExternalLink />
        </Button>
        <DropdownMenu>
          <DropdownMenuTrigger asChild>
            <Button
              type="button"
              variant="outline"
              size="icon"
              className="rounded-l-none border-l-0"
              disabled={!text}
              aria-label="Choose where to ask"
            >
              <ChevronDown />
            </Button>
          </DropdownMenuTrigger>
          <DropdownMenuContent align="end">
            <DropdownMenuLabel>Ask in a new tab</DropdownMenuLabel>
            {providers.map(p => (
              <DropdownMenuItem key={p.id} onSelect={() => ask(p)}>
                {p.label}
              </DropdownMenuItem>
            ))}
          </DropdownMenuContent>
        </DropdownMenu>
      </div>
    </div>
  )
}
