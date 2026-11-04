import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useState } from 'react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { api } from '@/api'
import { CommandPalette } from '@/components/command-palette'
import type { Job } from '@/types'

vi.mock('@/api', () => ({ api: vi.fn() }))

const job = (name: string, overrides: Partial<Job> = {}): Job => ({
  name,
  group: 'DEFAULT',
  description: null,
  jobType: null,
  displayName: null,
  durable: true,
  loadable: true,
  loadError: null,
  data: {},
  triggers: [],
  retry: null,
  ...overrides,
})

const setup = (canManage = true) => {
  const onOpenJob = vi.fn()
  const onRun = vi.fn()
  const goToHistory = vi.fn()
  render(<Harness />)
  function Harness() {
    const [open, setOpen] = useState(false)
    return (
      <CommandPalette
        open={open}
        onOpenChange={setOpen}
        commands={[{ label: 'History', icon: null, run: goToHistory }]}
        canManage={canManage}
        onOpenJob={onOpenJob}
        onRun={onRun}
      />
    )
  }
  return { onOpenJob, onRun, goToHistory }
}

const palette = () => within(screen.getByRole('dialog', { name: 'Find a job or command' }))

describe('CommandPalette', () => {
  beforeEach(() => {
    vi.mocked(api).mockResolvedValue({ jobs: [job('send-invoices')] })
  })

  it('opens on Ctrl+K and runs a command with Enter', async () => {
    const { goToHistory } = setup()
    expect(screen.queryByRole('dialog')).toBeNull()

    await userEvent.keyboard('{Control>}k{/Control}')
    await userEvent.keyboard('hist{Enter}')

    expect(goToHistory).toHaveBeenCalled()
    expect(screen.queryByRole('dialog')).toBeNull()
  })

  it('finds jobs on the server, and runs one from the keyboard', async () => {
    const { onRun } = setup()
    await userEvent.keyboard('{Control>}k{/Control}')
    await userEvent.keyboard('invoices')

    expect(await palette().findByRole('option', { name: /Open job/ })).toHaveAttribute(
      'aria-selected',
      'true'
    )
    expect(vi.mocked(api).mock.calls[0][0]).toContain('search=invoices')
    await userEvent.keyboard('{ArrowDown}{Enter}')

    expect(onRun).toHaveBeenCalledWith(
      expect.objectContaining({ name: 'send-invoices' }),
      'trigger'
    )
  })

  it('only opens jobs for a user who cannot manage them', async () => {
    const { onOpenJob } = setup(false)
    await userEvent.keyboard('{Control>}k{/Control}')
    await userEvent.keyboard('invoices')

    await palette().findByRole('option', { name: /Open job/ })
    expect(palette().getAllByRole('option')).toHaveLength(1)
    await userEvent.keyboard('{Enter}')
    expect(onOpenJob).toHaveBeenCalledWith(expect.objectContaining({ name: 'send-invoices' }))
  })
})
