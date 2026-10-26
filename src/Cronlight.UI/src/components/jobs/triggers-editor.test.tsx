import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useState } from 'react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { api } from '@/api'
import { TriggersEditor } from '@/components/jobs/triggers-editor'
import { emptyTrigger, type TriggerDraft } from '@/lib/job-form'

// The cron preview asks the server; answer every expression as valid
vi.mock('@/api', () => ({ api: vi.fn() }))

function Editor({ initial }: { initial: TriggerDraft[] }) {
  const [triggers, setTriggers] = useState(initial)
  return <TriggersEditor triggers={triggers} onChange={setTriggers} />
}

describe('TriggersEditor', () => {
  beforeEach(() => {
    vi.mocked(api).mockImplementation(async path => {
      const expression = new URLSearchParams((path as string).split('?')[1]).get('expression')
      return {
        isValid: true,
        expression,
        description: null,
        nextFireTimes: ['2030-01-01T09:00:00Z'],
        errorMessage: null,
      }
    })
  })

  it("links to Quartz.NET's cron documentation, in a new tab", () => {
    render(<Editor initial={[emptyTrigger()]} />)

    const link = screen.getByRole('link', { name: /Quartz cron syntax/ })
    expect(link).toHaveAttribute(
      'href',
      'https://www.quartz-scheduler.net/documentation/quartz-3.x/tutorial/crontriggers.html'
    )
    expect(link).toHaveAttribute('target', '_blank')
    expect(link).toHaveAttribute('rel', 'noopener noreferrer')
  })

  it('adds and removes trigger rows', async () => {
    render(<Editor initial={[emptyTrigger()]} />)

    await userEvent.click(screen.getByRole('button', { name: /Add trigger/ }))
    expect(screen.getAllByLabelText('Cron expression')).toHaveLength(2)

    await userEvent.click(screen.getAllByRole('button', { name: 'Remove trigger' })[0])
    expect(screen.getAllByLabelText('Cron expression')).toHaveLength(1)
  })

  it('keeps each row’s own state when an earlier row is removed', async () => {
    render(<Editor initial={[emptyTrigger(), emptyTrigger()]} />)
    await userEvent.type(screen.getAllByLabelText('Cron expression')[0], '0 0 9 * * ?')
    await userEvent.type(screen.getAllByLabelText('Cron expression')[1], '0 0 5 * * ?')
    expect(await screen.findByText('At 05:00')).toBeInTheDocument()

    await userEvent.click(screen.getAllByRole('button', { name: 'Remove trigger' })[0])

    expect(screen.getByLabelText('Cron expression')).toHaveValue('0 0 5 * * ?')
    expect(screen.queryByText('At 09:00')).toBeNull()
    expect(screen.getByText('At 05:00')).toBeInTheDocument()
  })

  it('previews a cron expression once it is long enough, describing it in words', async () => {
    render(<Editor initial={[emptyTrigger()]} />)

    await userEvent.type(screen.getByLabelText('Cron expression'), '0 0 9 * * ?')

    expect(await screen.findByText('At 09:00')).toBeInTheDocument()
    expect(vi.mocked(api)).toHaveBeenCalledWith(
      expect.stringContaining('cron/preview?expression=0+0+9'),
      expect.anything()
    )
  })

  it('says an interval trigger needs two runs or more, pointing to one-time triggers', async () => {
    render(<Editor initial={[{ ...emptyTrigger('Interval'), every: '5', runs: '1' }]} />)

    expect(screen.getByText(/For a single run, choose One time/)).toBeInTheDocument()
    await userEvent.clear(screen.getByLabelText('Number of runs'))
    await userEvent.type(screen.getByLabelText('Number of runs'), '3')
    expect(screen.getByText(/Every 5m, starting now, 3 runs in all/)).toBeInTheDocument()
  })
})
