import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { ThemeProvider } from 'next-themes'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { Header } from '@/components/header'
import { scheduler } from '@/test/fixtures'
import type { SchedulerInfo } from '@/types'

const renderHeader = (info: SchedulerInfo | null, onSchedulerChange?: (name: string) => void) =>
  render(
    <ThemeProvider attribute="class" storageKey="cronlight-theme">
      <Header scheduler={info} onSchedulerChange={onSchedulerChange} />
    </ThemeProvider>
  )

describe('Header', () => {
  afterEach(() => {
    document.documentElement.className = ''
  })

  it('shows a placeholder name and no state until the scheduler answers', () => {
    renderHeader(null)

    expect(screen.getByRole('heading', { name: 'Quartz' })).toBeInTheDocument()
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

  it('offers the other schedulers when the app serves more than one', async () => {
    const onSchedulerChange = vi.fn()
    renderHeader(scheduler({ schedulers: ['SampleHost', 'Reports'] }), onSchedulerChange)

    await userEvent.click(screen.getByRole('combobox', { name: 'Scheduler' }))
    await userEvent.click(screen.getByRole('option', { name: 'Reports' }))

    expect(onSchedulerChange).toHaveBeenCalledWith('Reports')
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

  it('switches to dark mode and remembers the choice', async () => {
    renderHeader(scheduler())

    await userEvent.click(screen.getByRole('button', { name: 'Change theme' }))
    expect(screen.getByRole('menuitemradio', { name: /System/ })).toBeChecked()
    await userEvent.click(screen.getByRole('menuitemradio', { name: /Dark/ }))

    expect(document.documentElement).toHaveClass('dark')
    expect(localStorage.getItem('cronlight-theme')).toBe('dark')
  })
})
