import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { RecentRuns } from '@/components/jobs/recent-runs'
import { TooltipProvider } from '@/components/ui/tooltip'
import type { RecentRun } from '@/types'

const run = (
  id: number,
  status: RecentRun['status'],
  durationMs: number | null = 1500,
  attempt = 1
): RecentRun => ({
  id,
  status,
  firedTimeUtc: `2026-10-28T${String(id).padStart(2, '0')}:00:00Z`,
  durationMs,
  attempt,
})

const renderRuns = (runs: RecentRun[]) =>
  render(
    <TooltipProvider>
      <RecentRuns runs={runs} />
    </TooltipProvider>
  )

/** Each run's bar, oldest first */
const bars = () =>
  screen.getAllByRole('listitem').map(item => item.querySelector('[data-shape]') as HTMLElement)

describe('RecentRuns', () => {
  it('shows the last 10 runs, oldest first, the server sending them newest first', () => {
    renderRuns(Array.from({ length: 12 }, (_, i) => run(12 - i, 'Succeeded')))

    expect(screen.getAllByRole('listitem')).toHaveLength(10)
    expect(screen.getByRole('list')).toHaveAccessibleName(/^Last 10 runs, oldest first: succeeded/)
  })

  it('names each outcome in order for screen readers', () => {
    renderRuns([
      run(5, 'Running', null),
      run(4, 'Failed'),
      run(3, 'Succeeded'),
      run(2, 'Cancelled'),
      run(1, 'Misfired', null),
    ])

    expect(screen.getByRole('list')).toHaveAccessibleName(
      'Last 5 runs, oldest first: missed, cancelled, succeeded, failed, running'
    )
  })

  it('draws a successful run as tall as it took, against the longest shown, up to three quarters', () => {
    renderRuns([run(3, 'Succeeded', 1000), run(2, 'Succeeded', 500), run(1, 'Succeeded', 100)])

    // A very short run stays visible
    expect(bars().map(bar => bar.style.height)).toEqual(['20%', '37.5%', '75%'])
  })

  it('draws failures and waiting retries full height whatever they took, so they stand out by shape too', () => {
    renderRuns([run(3, 'Failed', 0), run(2, 'Retried', 0), run(1, 'Succeeded', 5000)])

    expect(bars().map(bar => bar.style.height)).toEqual(['75%', '100%', '100%'])
  })

  it('draws missed and blocked runs hollow, as nothing ran', () => {
    renderRuns([run(3, 'Misfired', null), run(2, 'Vetoed', null), run(1, 'Succeeded')])

    expect(bars().map(bar => bar.dataset.shape)).toEqual(['bar', 'hollow', 'hollow'])
  })

  it('marks a run that needed retries, and says which attempt it was', () => {
    renderRuns([run(1, 'Succeeded', 1500, 3)])

    const item = screen.getByRole('listitem', { name: 'Succeeded, attempt 3' })
    expect(item.querySelector('[data-retried]')).not.toBeNull()
  })

  it('leaves room for runs a job has not had yet', () => {
    const { container } = renderRuns([run(1, 'Succeeded')])

    expect(screen.getAllByRole('listitem')).toHaveLength(1)
    expect(container.querySelectorAll('[data-shape="empty"]')).toHaveLength(9)
  })

  it('says when a job has no runs', () => {
    renderRuns([])

    expect(screen.getByRole('list')).toHaveAccessibleName('No runs yet')
  })

  it('shows a run’s outcome, start, duration and attempt on hover', async () => {
    renderRuns([run(2, 'Succeeded', 1500, 3), run(1, 'Vetoed')])

    await userEvent.hover(screen.getByRole('listitem', { name: 'Succeeded, attempt 3' }))

    const tip = await screen.findByRole('tooltip')
    expect(tip).toHaveTextContent('Succeeded ·')
    expect(tip).toHaveTextContent('1.5s')
    expect(tip).toHaveTextContent('attempt 3')
  })
})
