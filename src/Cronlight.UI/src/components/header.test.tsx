import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { ThemeProvider } from 'next-themes'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { Header } from '@/components/header'
import { formatDateTime } from '@/lib/format'
import { scheduler } from '@/test/fixtures'
import type { ClusterNode, SchedulerInfo } from '@/types'

const renderHeader = (info: SchedulerInfo | null, onSchedulerChange?: (name: string) => void) =>
  render(
    <ThemeProvider attribute="class" storageKey="cronlight-theme">
      <Header scheduler={info} onSchedulerChange={onSchedulerChange} />
    </ThemeProvider>
  )

const node = (instanceId: string, overrides: Partial<ClusterNode> = {}): ClusterNode => ({
  instanceId,
  lastCheckinUtc: '2030-01-01T00:00:00Z',
  checkinIntervalMs: 7500,
  secondsSinceCheckin: 3,
  alive: true,
  thisNode: false,
  ...overrides,
})

describe('Header', () => {
  afterEach(() => {
    document.documentElement.className = ''
  })

  it('names no scheduler and shows no state until the scheduler answers, rather than a stand-in', () => {
    renderHeader(null)

    expect(screen.getByRole('heading', { name: 'Loading the scheduler' })).toBeInTheDocument()
    expect(screen.queryByText('Quartz')).toBeNull()
    expect(screen.queryByRole('button', { name: 'Scheduler details' })).toBeNull()
  })

  it.each([
    [{}, 'Running'],
    [{ inStandbyMode: true }, 'Standby'],
    [{ started: false, inStandbyMode: true }, 'Stopped'],
  ])('shows the scheduler state %o as %s', (overrides, state) => {
    renderHeader(scheduler(overrides))

    expect(screen.getByRole('heading', { name: 'SampleHost' })).toBeInTheDocument()
    expect(screen.getByText(state)).toBeInTheDocument()
  })

  it.each([
    [[node('a'), node('b')], 'Running · 2 nodes', 'outline', 'bg-emerald-500'],
    [[node('a'), node('b', { alive: false })], 'Running · 1 node', 'outline', 'bg-emerald-500'],
    [[node('a', { alive: false })], 'No nodes running', 'secondary', 'bg-amber-500'],
  ])(
    'describes a cluster by its running nodes, whatever this process does: %#',
    (clusterNodes, label, variant, dot) => {
      renderHeader(scheduler({ jobStoreClustered: true, started: false, clusterNodes }))

      const badge = screen.getByText(label)
      expect(badge).toHaveAttribute('data-variant', variant)
      expect(badge.querySelector('span')).toHaveClass(dot)
      expect(screen.queryByText('Stopped')).toBeNull()
    }
  )

  // Quartz keeps a node in standby checking in; only this node's standby can be seen
  it.each([
    [[node('node-1', { thisNode: true })], 'No nodes running'],
    [[node('node-1', { thisNode: true }), node('node-2')], 'Running · 1 node'],
  ])('does not count this node as running while it is in standby: %#', (clusterNodes, label) => {
    renderHeader(
      scheduler({ jobStoreClustered: true, started: true, inStandbyMode: true, clusterNodes })
    )

    expect(screen.getByText(label)).toBeInTheDocument()
  })

  it('says no nodes are running, and none checked in, for a cluster without nodes', async () => {
    renderHeader(scheduler({ jobStoreClustered: true, started: false, clusterNodes: [] }))

    expect(screen.getByText('No nodes running')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Scheduler details' }))
    expect(screen.getByText('None checked in')).toBeInTheDocument()
  })

  it('offers the other schedulers when the app serves more than one', async () => {
    const onSchedulerChange = vi.fn()
    renderHeader(scheduler({ schedulers: ['SampleHost', 'Reports'] }), onSchedulerChange)

    await userEvent.click(screen.getByRole('combobox', { name: 'Scheduler' }))
    await userEvent.click(screen.getByRole('option', { name: 'Reports' }))

    expect(onSchedulerChange).toHaveBeenCalledWith('Reports')
  })

  it('names the scheduler once, in the title, with the switch beside it', () => {
    renderHeader(scheduler({ schedulers: ['SampleHost', 'Reports'] }), vi.fn())

    expect(screen.getByRole('heading', { name: 'SampleHost' })).toBeInTheDocument()
    // The switch says which scheduler it is on to screen readers only
    const shown = screen.getAllByText('SampleHost').filter(e => !e.closest('.sr-only'))
    expect(shown).toHaveLength(1)
    expect(screen.getByRole('combobox', { name: 'Scheduler' })).toHaveTextContent('SampleHost')
    expect(screen.queryByText(/Cronlight ·/)).toBeNull()
  })

  it('shows the state as a status, apart from the blue of actions', () => {
    renderHeader(scheduler())

    expect(screen.getByText('Running')).toHaveAttribute('data-variant', 'outline')
  })

  it('shows no scheduler picker for a single scheduler', () => {
    renderHeader(scheduler(), vi.fn())

    expect(screen.queryByRole('combobox', { name: 'Scheduler' })).toBeNull()
  })

  it('marks a read-only viewer', () => {
    renderHeader(scheduler({ canManage: false }))

    expect(screen.getByText('Read-only')).toBeInTheDocument()
  })

  it('lists the scheduler details in a menu', async () => {
    renderHeader(
      scheduler({
        jobStore: 'JobStoreTX',
        jobStoreClustered: true,
        threadPoolSize: 4,
        numberOfJobsExecuted: 17,
        canManage: false,
      })
    )

    await userEvent.click(screen.getByRole('button', { name: 'Scheduler details' }))

    const value = (label: string) => screen.getByText(label).nextSibling?.textContent
    expect(value('Version')).toBe('3.13.1')
    expect(value('Job store')).toBe('JobStoreTX (clustered)')
    expect(value('Threads')).toBe('4')
    expect(value('Jobs executed')).toBe('17')
    expect(value('Your access')).toBe('Read-only')
  })

  it("lists a cluster's nodes in the details, and says this process does not run it", async () => {
    renderHeader(
      scheduler({
        jobStoreClustered: true,
        started: false,
        clusterNodes: [
          node('worker-a'),
          node('worker-b', { alive: false, secondsSinceCheckin: 300 }),
        ],
      })
    )

    await userEvent.click(screen.getByRole('button', { name: 'Scheduler details' }))

    expect(screen.getByText('Not started (manages only)')).toBeInTheDocument()
    expect(screen.getByText('worker-a')).toBeInTheDocument()
    expect(screen.getByText('checked in 3 sec. ago')).toBeInTheDocument()
    expect(screen.getByText('not responding · 5 min. ago')).toBeInTheDocument()
    expect(screen.getByText('worker-a').closest('li')).toHaveAttribute(
      'title',
      formatDateTime('2030-01-01T00:00:00Z')
    )
    for (const perProcess of ['Instance', 'Threads', 'Jobs executed', 'Running since'])
      expect(screen.queryByText(perProcess)).toBeNull()
  })

  it('says in hours how long ago a node stopped responding', async () => {
    renderHeader(
      scheduler({
        jobStoreClustered: true,
        started: false,
        clusterNodes: [node('worker-a', { alive: false, secondsSinceCheckin: 7200 })],
      })
    )

    await userEvent.click(screen.getByRole('button', { name: 'Scheduler details' }))

    expect(screen.getByText('not responding · 2 hr. ago')).toBeInTheDocument()
  })

  it('marks this node, with its own state, when this process is one of the nodes', async () => {
    renderHeader(
      scheduler({
        jobStoreClustered: true,
        inStandbyMode: true,
        clusterNodes: [
          node('node-1', { thisNode: true }),
          node('node-2', { secondsSinceCheckin: 90 }),
        ],
      })
    )

    await userEvent.click(screen.getByRole('button', { name: 'Scheduler details' }))

    expect(screen.getByText('this node')).toBeInTheDocument()
    expect(screen.getByText('Standby')).toBeInTheDocument()
    expect(screen.getByText('checked in 1 min. ago')).toBeInTheDocument()
    expect(screen.getByText('Jobs executed')).toBeInTheDocument()
  })

  it('switches to dark mode and remembers the choice', async () => {
    renderHeader(scheduler())

    await userEvent.click(screen.getByRole('button', { name: 'Change theme' }))
    expect(screen.getByRole('menuitemradio', { name: /System/ })).toBeChecked()
    await userEvent.click(screen.getByRole('menuitemradio', { name: /Dark/ }))

    expect(document.documentElement).toHaveClass('dark')
    expect(localStorage.getItem('cronlight-theme')).toBe('dark')
  })
})
