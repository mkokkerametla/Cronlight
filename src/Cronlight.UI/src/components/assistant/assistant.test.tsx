import { useState } from 'react'
import { act, render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { ApiError, askAssistant } from '@/api'
import { Assistant, type SchedulerStatus } from '@/components/assistant/assistant'
import type { Turn } from '@/hooks/use-conversation'
import type { AssistantEvent } from '@/types'

vi.mock('@/api', async importOriginal => ({
  ...(await importOriginal<typeof import('@/api')>()),
  askAssistant: vi.fn(),
}))

function Harness({
  onOpenJob = () => {},
  status,
}: {
  onOpenJob?: (group: string, name: string) => void
  status?: SchedulerStatus
}) {
  const [turns, setTurns] = useState<Turn[]>([])
  return (
    <Assistant
      scheduler="SampleHost"
      status={status}
      turns={turns}
      onTurnsChange={setTurns}
      onOpenJob={onOpenJob}
    />
  )
}

const box = () => screen.getByRole('textbox', { name: 'Your question' })

/** An answer stream that sends these events, in order */
const answers = (...events: AssistantEvent[]) =>
  vi.mocked(askAssistant).mockImplementation(async (_messages, onEvent) => {
    for (const event of events) onEvent(event)
  })

describe('Assistant', () => {
  it('shows each tool as it runs, streams the answer in, then folds the tools away', async () => {
    let send!: (event: AssistantEvent) => void
    let finish!: () => void
    vi.mocked(askAssistant).mockImplementation(
      (_messages, onEvent) =>
        new Promise<void>(resolve => {
          send = onEvent
          finish = resolve
        })
    )
    const onOpenJob = vi.fn()
    render(<Harness onOpenJob={onOpenJob} />)

    await userEvent.type(box(), 'What failed?{Enter}')
    expect(screen.getByText('What failed?')).toBeInTheDocument()
    expect(screen.getByRole('status')).toHaveTextContent('Thinking')

    act(() => send({ type: 'tool', id: 1, name: 'failure_summary', arguments: { hours: 24 } }))
    expect(screen.getByText('Reading the failure summary')).toBeInTheDocument()
    expect(screen.getByText('· last 24 h')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Stop' })).toBeInTheDocument()

    act(() => {
      send({ type: 'tool-done', id: 1, name: 'failure_summary' })
      send({ type: 'text', text: '[invoices](job:billing/invoices) ' })
      send({ type: 'text', text: 'failed twice.' })
    })
    expect(screen.getByText('Read the failure summary')).toBeInTheDocument()
    expect(screen.getByText('failed twice.', { exact: false })).toBeInTheDocument()

    await act(async () => {
      send({ type: 'done' })
      finish()
    })
    expect(screen.getByText('Used 1 tool')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Copy' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Ask' })).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'invoices' }))
    expect(onOpenJob).toHaveBeenCalledWith('billing', 'invoices')
  })

  it('suggests questions from what needs attention now, and none that need history without it', () => {
    const { unmount } = render(<Harness status={{ failed: 791, running: 2, paused: 0 }} />)
    const suggestions = () =>
      within(screen.getByRole('region', { name: 'Worth asking now' }))
        .getAllByRole('button')
        .map(b => b.textContent)

    expect(suggestions()[0]).toBe('Which jobs failed in the last 24 hours, and why?791 failed')
    expect(suggestions()[1]).toBe('What is running now, and for how long?2 running')
    expect(suggestions()).toHaveLength(4)
    unmount()

    render(<Harness status={{ failed: null, running: 0, paused: 3 }} />)
    expect(suggestions()).toEqual([
      'What runs in the next 6 hours?',
      'Which jobs are paused?3 paused',
    ])
  })

  it('asks with the conversation so far', async () => {
    answers({ type: 'text', text: 'Two failed.' }, { type: 'done' })
    render(<Harness />)

    await userEvent.type(box(), 'What failed?{Enter}')
    await screen.findByText('Two failed.')
    await userEvent.type(box(), 'Why?{Enter}')

    expect(vi.mocked(askAssistant).mock.calls[1][0]).toEqual([
      { role: 'user', text: 'What failed?' },
      { role: 'assistant', text: 'Two failed.' },
      { role: 'user', text: 'Why?' },
    ])
  })

  it('asks a suggested question, and starts a new conversation', async () => {
    answers({ type: 'text', text: 'Nothing failed.' }, { type: 'done' })
    render(<Harness />)
    expect(screen.getByText(/SampleHost's jobs/)).toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: /Which jobs failed/ }))
    await screen.findByText('Nothing failed.')
    await userEvent.click(screen.getByRole('button', { name: 'New conversation' }))

    expect(screen.queryByText('Nothing failed.')).toBeNull()
    expect(screen.getByRole('button', { name: /Which jobs failed/ })).toBeInTheDocument()
  })

  it('says when the model failed, and tries again without asking twice', async () => {
    answers({ type: 'error', error: 'The AI model did not answer.', traceId: 'trace-1' })
    render(<Harness />)

    await userEvent.type(box(), 'Hello?{Enter}')

    expect(await screen.findByText('The AI model did not answer. (trace-1)')).toBeInTheDocument()
    answers({ type: 'text', text: 'Hello.' }, { type: 'done' })
    await userEvent.click(screen.getByRole('button', { name: 'Try again' }))

    expect(await screen.findByText('Hello.')).toBeInTheDocument()
    expect(screen.queryByText(/did not answer/)).toBeNull()
    expect(screen.getAllByText('Hello?')).toHaveLength(1)
    expect(vi.mocked(askAssistant).mock.calls[1][0]).toEqual([{ role: 'user', text: 'Hello?' }])
  })

  it('explains an app with AI mode but no model', async () => {
    vi.mocked(askAssistant).mockImplementation(() =>
      Promise.reject(new ApiError('This app has no IChatClient registered.', 503))
    )
    render(<Harness />)

    await userEvent.type(box(), 'Hello?{Enter}')

    expect(await screen.findByText('AI mode has no model to answer with yet')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'How to set it up' })).toHaveAttribute(
      'href',
      expect.stringContaining('docs/ai-mode.md')
    )
  })

  it('stops an answer, keeping what was written', async () => {
    vi.mocked(askAssistant).mockImplementation(
      (_messages, onEvent, signal) =>
        new Promise<void>((_resolve, reject) => {
          onEvent({ type: 'text', text: 'Half an answer' })
          signal?.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')))
        })
    )
    render(<Harness />)

    await userEvent.type(box(), 'Hello?{Enter}')
    await screen.findByText('Half an answer')
    await userEvent.click(screen.getByRole('button', { name: 'Stop' }))

    expect(await screen.findByText('Stopped.')).toBeInTheDocument()
    expect(screen.getByText('Half an answer')).toBeInTheDocument()
  })

  it("says when the answer was cut off at the model's length limit", async () => {
    answers({ type: 'text', text: 'Half a table' }, { type: 'done', cutOff: true })
    render(<Harness />)

    await userEvent.type(box(), 'What failed?{Enter}')

    expect(await screen.findByText(/Cut off: the answer reached/)).toBeInTheDocument()
    expect(screen.getByText('Half a table')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Copy' })).toBeInTheDocument()
  })

  it('says when the connection closed before the answer finished, and does not send it back', async () => {
    answers({ type: 'text', text: 'Half a table' })
    render(<Harness />)

    await userEvent.type(box(), 'What failed?{Enter}')

    expect(await screen.findByText(/connection closed before it finished/)).toBeInTheDocument()
    expect(screen.getByText('Half a table')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Copy' })).toBeNull()
    answers({ type: 'text', text: 'All of it.' }, { type: 'done' })
    await userEvent.click(screen.getByRole('button', { name: 'Try again' }))
    expect(await screen.findByText('All of it.')).toBeInTheDocument()
    expect(vi.mocked(askAssistant).mock.calls[1][0]).toEqual([
      { role: 'user', text: 'What failed?' },
    ])
  })

  it('folds away a refused tool, saying so', async () => {
    answers(
      { type: 'tool', id: 1, name: 'get_job', arguments: { group: 'billing', name: 'x' } },
      { type: 'tool-done', id: 1, name: 'get_job', error: 'There is no job billing.x.' },
      { type: 'text', text: 'No such job.' },
      { type: 'done' }
    )
    render(<Harness />)

    await userEvent.type(box(), 'Tell me about x{Enter}')
    await screen.findByText('No such job.')
    await userEvent.click(screen.getByText('Used 1 tool'))

    const tools = screen.getByRole('list', { name: 'Tools used' })
    expect(within(tools).getByText('Read the job')).toBeInTheDocument()
    expect(within(tools).getByText('There is no job billing.x.')).toBeInTheDocument()
    expect(screen.getByText('· 1 failed')).toBeInTheDocument()
  })

  it('starts a new line with Shift+Enter, and does not send a blank question', async () => {
    render(<Harness />)

    await userEvent.type(box(), '   {Enter}')
    await userEvent.type(box(), 'a{Shift>}{Enter}{/Shift}b')

    expect(askAssistant).not.toHaveBeenCalled()
    expect(box()).toHaveValue('   a\nb')
  })

  it('keeps working where scrollIntoView returns a promise, as in newer browsers', async () => {
    vi.spyOn(Element.prototype, 'scrollIntoView').mockImplementation(
      () => Promise.resolve() as unknown as void
    )
    answers({ type: 'text', text: 'Nothing failed.' }, { type: 'done' })
    render(<Harness />)

    await userEvent.type(box(), 'What failed?{Enter}')
    expect(await screen.findByText('Nothing failed.')).toBeInTheDocument()
    await userEvent.type(box(), 'And yesterday?{Enter}')
    expect(await screen.findAllByText('Nothing failed.')).toHaveLength(2)
  })
})
