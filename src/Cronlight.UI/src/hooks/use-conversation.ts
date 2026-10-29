import { useEffect, useState, type Dispatch, type SetStateAction } from 'react'
import type { ToolStep } from '@/lib/assistant-tools'

/** A message on screen: the user's question, or the assistant's answer with the tools it used */
export interface Turn {
  role: 'user' | 'assistant'
  text: string
  tools?: ToolStep[]
  /** An answer being written, finished, stopped by the user, or that failed (error says why) */
  status?: 'streaming' | 'done' | 'stopped' | 'error'
  error?: string
  /** The app has the assistant but no AI model to answer with */
  notSetUp?: boolean
  /** The answer stopped at the model's output limit */
  cutOff?: boolean
}

const key = (scheduler: string) => `cronlight-assistant:${scheduler}`

function stored(scheduler: string): Turn[] {
  try {
    const turns = JSON.parse(sessionStorage.getItem(key(scheduler)) ?? '[]') as Turn[]
    if (!Array.isArray(turns)) return []
    // An answer still being written when the page went away was cut short
    return turns.map(t => (t.status === 'streaming' ? { ...t, status: 'stopped' } : t))
  } catch {
    return []
  }
}

/**
 * The conversation with the assistant about one scheduler, kept for this tab's session: a reload, or a look at the
 * jobs, keeps it, and closing the tab forgets it. Nothing is kept on the server.
 */
export function useConversation(scheduler: string): [Turn[], Dispatch<SetStateAction<Turn[]>>] {
  const [turns, setTurns] = useState(() => stored(scheduler))
  useEffect(() => {
    try {
      if (turns.length) sessionStorage.setItem(key(scheduler), JSON.stringify(turns))
      else sessionStorage.removeItem(key(scheduler))
    } catch {
      // Kept until the page is left
    }
  }, [scheduler, turns])
  return [turns, setTurns]
}

/** The conversation as the server takes it: questions, and the answers that were written */
export const asMessages = (turns: Turn[]) =>
  turns
    .filter(t => t.role === 'user' || (t.text.trim() && t.status !== 'error'))
    .map(({ role, text }) => ({ role, text }))
