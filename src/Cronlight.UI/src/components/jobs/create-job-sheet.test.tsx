import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useState } from 'react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { api } from '@/api'
import { CreateJobSheet } from '@/components/jobs/create-job-sheet'
import type { CreateJobRequest, JobParameter, JobType } from '@/types'

// The cron preview asks the server
vi.mock('@/api', () => ({ api: vi.fn() }))

const param = (key: string, overrides: Partial<JobParameter> = {}): JobParameter => ({
  key,
  type: 'String',
  required: false,
  description: null,
  defaultValue: null,
  choices: null,
  ...overrides,
})

const jobTypes: JobType[] = [
  {
    typeName: 'Acme.Jobs.InvoiceJob',
    displayName: 'Send invoices',
    description: 'Emails open invoices',
    hidden: false,
    parameters: [
      param('customerId', { type: 'Int', required: true }),
      param('currency', { defaultValue: 'EUR' }),
    ],
  },
  {
    typeName: 'Acme.Jobs.ReportJob',
    displayName: 'Build report',
    description: null,
    hidden: false,
    parameters: [param('format', { defaultValue: 'pdf' })],
  },
]

/** The sheet with real open state, as App uses it. */
function Harness({ onCreate }: { onCreate: (request: CreateJobRequest) => Promise<boolean> }) {
  const [open, setOpen] = useState(true)
  return (
    <>
      <button onClick={() => setOpen(true)}>Open</button>
      <CreateJobSheet
        open={open}
        onOpenChange={setOpen}
        jobTypes={jobTypes}
        showScheduleAssistant={false}
        onCreate={onCreate}
      />
    </>
  )
}

const sheet = () => screen.getByRole('dialog', { name: 'New job' })

describe('CreateJobSheet', () => {
  beforeEach(() => {
    vi.mocked(api).mockImplementation(async path => ({
      isValid: true,
      expression: new URLSearchParams((path as string).split('?')[1]).get('expression'),
      description: null,
      nextFireTimes: [],
      errorMessage: null,
    }))
  })

  it('starts with the first job type and its parameters, defaults filled in', () => {
    render(<Harness onCreate={vi.fn()} />)

    expect(within(sheet()).getByRole('combobox')).toHaveTextContent('Send invoices')
    expect(within(sheet()).getByText('Emails open invoices')).toBeInTheDocument()
    expect(within(sheet()).getByLabelText(/customerId/)).toHaveValue(null)
    expect(within(sheet()).getByLabelText('currency')).toHaveValue('EUR')
  })

  it('sends a trimmed request, leaving out empty optional fields, and closes', async () => {
    const onCreate = vi.fn(async () => true)
    render(<Harness onCreate={onCreate} />)
    const form = within(sheet())

    await userEvent.type(form.getByLabelText('Name'), '  invoices  ')
    await userEvent.type(form.getByLabelText(/customerId/), '42')
    await userEvent.type(form.getByLabelText('Cron expression'), '0 0 9 ? * MON-FRI')
    await userEvent.click(form.getByRole('button', { name: 'Create job' }))

    expect(onCreate).toHaveBeenCalledWith({
      type: 'Acme.Jobs.InvoiceJob',
      name: 'invoices',
      group: null,
      description: null,
      data: { customerId: '42', currency: 'EUR' },
      triggers: [{ name: null, cronExpression: '0 0 9 ? * MON-FRI', timeZone: null }],
    })
    expect(screen.queryByRole('dialog')).toBeNull()
  })

  it('swaps the parameters when another job type is chosen', async () => {
    const onCreate = vi.fn(async () => true)
    render(<Harness onCreate={onCreate} />)
    const form = within(sheet())

    await userEvent.click(form.getByRole('combobox'))
    await userEvent.click(screen.getByRole('option', { name: /Build report/ }))

    expect(form.queryByLabelText(/customerId/)).toBeNull()
    expect(form.getByLabelText('format')).toHaveValue('pdf')
    await userEvent.type(form.getByLabelText('Name'), 'report')
    await userEvent.click(form.getByRole('button', { name: 'Create job' }))
    expect(onCreate).toHaveBeenCalledWith(
      expect.objectContaining({
        type: 'Acme.Jobs.ReportJob',
        data: { format: 'pdf' },
        triggers: [],
      })
    )
  })

  it('stays open with the entered values when the server refuses the job', async () => {
    render(<Harness onCreate={vi.fn(async () => false)} />)
    const form = within(sheet())

    await userEvent.type(form.getByLabelText('Name'), 'invoices')
    await userEvent.type(form.getByLabelText(/customerId/), '42')
    await userEvent.click(form.getByRole('button', { name: 'Create job' }))

    expect(form.getByLabelText('Name')).toHaveValue('invoices')
    expect(form.getByRole('button', { name: 'Create job' })).toBeEnabled()
  })

  it('starts empty again after being closed and reopened', async () => {
    render(<Harness onCreate={vi.fn()} />)
    await userEvent.type(within(sheet()).getByLabelText('Name'), 'half-typed')

    await userEvent.click(within(sheet()).getByRole('button', { name: 'Cancel' }))
    await userEvent.click(screen.getByRole('button', { name: 'Open' }))

    expect(within(sheet()).getByLabelText('Name')).toHaveValue('')
  })
})
