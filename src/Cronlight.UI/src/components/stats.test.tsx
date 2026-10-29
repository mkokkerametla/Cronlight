import { cleanup, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { Stats } from '@/components/stats'

const renderStats = (overrides: Partial<Parameters<typeof Stats>[0]> = {}) => {
  const onSelect = vi.fn()
  render(
    <Stats
      total={12}
      running={2}
      paused={3}
      unloadable={1}
      error={0}
      failed={4}
      active="all"
      onSelect={onSelect}
      {...overrides}
    />
  )
  return onSelect
}

const card = (label: string) => screen.getByRole('button', { name: new RegExp(label) })

describe('Stats', () => {
  it('shows each count on a card that selects its view', async () => {
    const onSelect = renderStats()

    expect(card('Total jobs')).toHaveTextContent('12')
    expect(card('Running now')).toHaveTextContent('2')
    expect(card('Paused')).toHaveTextContent('3')
    expect(card('Failed')).toHaveTextContent('4')
    expect(card('Unloadable')).toHaveTextContent('1')

    for (const label of ['Total jobs', 'Running now', 'Paused', 'Failed', 'Unloadable'])
      await userEvent.click(card(label))
    expect(onSelect.mock.calls.map(([key]) => key)).toEqual([
      'all',
      'running',
      'Paused',
      'failed',
      'Unloadable',
    ])
  })

  it('marks the card for what is on screen', () => {
    renderStats({ active: 'Paused' })

    expect(card('Paused')).toHaveAttribute('aria-pressed', 'true')
    expect(card('Total jobs')).toHaveAttribute('aria-pressed', 'false')
  })

  it('shows jobs Quartz stopped only when there are some', async () => {
    const onSelect = renderStats({ error: 2 })

    expect(card('Error')).toHaveTextContent('2')
    await userEvent.click(card('Error'))
    expect(onSelect).toHaveBeenCalledWith('Error')
  })

  it("shows other apps' jobs only when there are some, as it does jobs Quartz stopped", () => {
    renderStats({ unloadable: 0 })

    expect(screen.queryByRole('button', { name: /Unloadable/ })).toBeNull()
    expect(screen.getAllByRole('button')).toHaveLength(4)
  })

  it('leaves out failed runs when history is off', () => {
    renderStats({ failed: null })

    expect(screen.getAllByRole('button')).toHaveLength(4)
    expect(screen.queryByText('Failed (24h)')).toBeNull()
  })

  it('shows failed runs, paused and unloadable jobs in colour only when there are some', () => {
    const count = (label: string) => card(label).querySelector('.tabular-nums')
    renderStats({ failed: 4, paused: 3, unloadable: 1 })
    expect(count('Failed')).toHaveClass('text-destructive')
    expect(count('Paused')).toHaveClass('text-amber-600')
    expect(count('Unloadable')).toHaveClass('text-destructive')
    cleanup()

    renderStats({ failed: 0, paused: 0 })

    expect(count('Failed')).not.toHaveClass('text-destructive')
    expect(count('Paused')).not.toHaveClass('text-amber-600')
  })
})
