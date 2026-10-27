import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { importJobs } from '@/api'
import { ImportJobsSheet } from '@/components/jobs/import-jobs-sheet'
import type { ImportedJob } from '@/types'

vi.mock('@/api', () => ({ importJobs: vi.fn() }))

const imported = (overrides: Partial<ImportedJob>): ImportedJob => ({
  group: 'DEFAULT',
  name: 'nightly',
  action: 'unchanged',
  error: null,
  changes: {},
  ...overrides,
})

const yaml = 'version: 1\njobs:\n- name: nightly\n'

function renderSheet() {
  const onImported = vi.fn()
  const onOpenChange = vi.fn()
  render(<ImportJobsSheet open onOpenChange={onOpenChange} onImported={onImported} />)
  return { onImported, onOpenChange, sheet: screen.getByRole('dialog', { name: 'Import jobs' }) }
}

describe('ImportJobsSheet', () => {
  beforeEach(() => vi.mocked(importJobs).mockReset())

  it('shows what the file changes, and imports it only once checked', async () => {
    const jobs = [
      imported({ name: 'fresh', action: 'create' }),
      imported({
        name: 'nightly',
        action: 'update',
        changes: { triggers: { before: '0 0 2 * * ? (UTC)', after: '0 30 3 * * ? (UTC)' } },
      }),
      imported({ name: 'cleanup' }),
    ]
    vi.mocked(importJobs).mockImplementation(async (_text, _yaml, dryRun) => ({
      ok: true,
      result: { dryRun, created: 1, updated: 1, unchanged: 1, jobs },
    }))
    const { onImported, onOpenChange, sheet } = renderSheet()
    expect(within(sheet).getByRole('button', { name: 'Check file' })).toBeDisabled()

    await userEvent.type(within(sheet).getByLabelText('Exported JSON or YAML'), yaml)
    await userEvent.click(within(sheet).getByRole('button', { name: 'Check file' }))

    expect(importJobs).toHaveBeenCalledWith(yaml, true, true)
    expect(within(sheet).getByRole('status')).toHaveTextContent(
      '1 to create, 1 to update, 1 unchanged.'
    )
    const list = within(sheet).getByRole('list', { name: 'Jobs in the file' })
    const items = within(list).getAllByRole('listitem')
    expect(items.map(item => item.textContent)).toEqual([
      expect.stringMatching(/^NewDEFAULT\.fresh/),
      expect.stringMatching(
        /^ChangedDEFAULT\.nightly.*0 0 2 \* \* \? \(UTC\) → 0 30 3 \* \* \? \(UTC\)/
      ),
      expect.stringMatching(/^UnchangedDEFAULT\.cleanup/),
    ])

    await userEvent.click(within(sheet).getByRole('button', { name: 'Import 2 jobs' }))
    expect(importJobs).toHaveBeenLastCalledWith(yaml, true, false)
    expect(onImported).toHaveBeenCalled()
    expect(onOpenChange).toHaveBeenCalledWith(false)
  })

  it("lists each job's mistake and offers no import while the file has one", async () => {
    vi.mocked(importJobs).mockResolvedValue({
      ok: false,
      message: '1 of 2 jobs cannot be imported, so none was.',
      jobs: [
        imported({ name: 'fresh', action: 'create' }),
        imported({ name: 'bad', action: 'error', error: "Unknown job type 'Nope.Job'" }),
      ],
    })
    const { sheet } = renderSheet()

    await userEvent.type(within(sheet).getByLabelText('Exported JSON or YAML'), '{{"version": 1}')
    await userEvent.click(within(sheet).getByRole('button', { name: 'Check file' }))

    // JSON, since it starts with a brace
    expect(vi.mocked(importJobs).mock.calls[0][1]).toBe(false)
    expect(within(sheet).getByText('Nothing can be imported')).toBeInTheDocument()
    expect(within(sheet).getByText("Unknown job type 'Nope.Job'")).toBeInTheDocument()
    expect(within(sheet).queryByRole('button', { name: /^Import/ })).toBeNull()
  })

  it('asks for the file to be checked again once it is changed', async () => {
    vi.mocked(importJobs).mockResolvedValue({
      ok: true,
      result: {
        dryRun: true,
        created: 1,
        updated: 0,
        unchanged: 0,
        jobs: [imported({ action: 'create' })],
      },
    })
    const { sheet } = renderSheet()
    const text = within(sheet).getByLabelText('Exported JSON or YAML')

    await userEvent.type(text, yaml)
    await userEvent.click(within(sheet).getByRole('button', { name: 'Check file' }))
    expect(within(sheet).getByRole('button', { name: 'Import 1 job' })).toBeInTheDocument()

    await userEvent.type(text, '- name: another\n')
    expect(within(sheet).queryByRole('button', { name: /^Import/ })).toBeNull()
    expect(within(sheet).getByRole('button', { name: 'Check file' })).toBeEnabled()
    expect(within(sheet).queryByRole('list', { name: 'Jobs in the file' })).toBeNull()
  })

  it('says when every job is already as the file says', async () => {
    vi.mocked(importJobs).mockResolvedValue({
      ok: true,
      result: { dryRun: true, created: 0, updated: 0, unchanged: 1, jobs: [imported({})] },
    })
    const { sheet } = renderSheet()

    await userEvent.type(within(sheet).getByLabelText('Exported JSON or YAML'), yaml)
    await userEvent.click(within(sheet).getByRole('button', { name: 'Check file' }))

    expect(within(sheet).getByRole('status')).toHaveTextContent('nothing to import')
    expect(within(sheet).queryByRole('button', { name: /^Import/ })).toBeNull()
  })
})
