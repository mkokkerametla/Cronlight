import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { RecentRuns } from '@/components/jobs/recent-runs'
import { TooltipProvider } from '@/components/ui/tooltip'
import type { RecentRun } from '@/types'

const run = (id: number, status: RecentRun['status'], attempt = 1): RecentRun => ({
  id,
  status,
  firedTimeUtc: `2026-10-28T0${id}:00:00Z`,
  durationMs: 1500,
  attempt,
})

const renderRuns = (runs: RecentRun[]) =>
  render(
    <TooltipProvider>
      <RecentRuns runs={runs} />
    </TooltipProvider>
  )

describe('RecentRuns', () => {
  it('shows the runs oldest first, the server sending them newest first', () => {
    renderRuns([
      run(5, 'Running'),
      run(4, 'Failed'),
      run(3, 'Succeeded'),
      run(2, 'Cancelled'),
      run(1, 'Misfired'),
    ])

    expect(screen.getByRole('list')).toHaveAccessibleName(
      'Last 5 runs, oldest first: missed, cancelled, succeeded, failed, running'
    )
    expect(screen.getAllByRole('listitem')).toHaveLength(5)
  })

  it('leaves empty slots for runs a job has not had yet', () => {
    const { container } = renderRuns([run(1, 'Succeeded')])

    expect(screen.getAllByRole('listitem')).toHaveLength(1)
    expect(container.querySelectorAll('.border-dashed')).toHaveLength(4)
  })

  it('says when a job has no runs', () => {
    renderRuns([])

    expect(screen.getByRole('list')).toHaveAccessibleName('No runs yet')
  })

  it('shows a run’s outcome, start, duration and attempt on hover', async () => {
    renderRuns([run(2, 'Succeeded', 3), run(1, 'Vetoed')])

    await userEvent.hover(screen.getByLabelText('Succeeded'))

    const tip = await screen.findByRole('tooltip')
    expect(tip).toHaveTextContent('Succeeded ·')
    expect(tip).toHaveTextContent('1.5s')
    expect(tip).toHaveTextContent('attempt 3')
  })
})
