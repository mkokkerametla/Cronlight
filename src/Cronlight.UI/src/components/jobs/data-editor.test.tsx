import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useState } from 'react'
import { describe, expect, it } from 'vitest'
import { DataEditor } from '@/components/jobs/data-editor'
import { MASK, rowsToData, type DataRow } from '@/lib/job-form'
import type { JobParameter } from '@/types'

const param = (key: string, overrides: Partial<JobParameter> = {}): JobParameter => ({
  key,
  type: 'String',
  required: false,
  description: null,
  defaultValue: null,
  choices: null,
  ...overrides,
})

/** Renders the editor with real state and shows what the form would send, as the job forms do. */
function Editor({ initial, secretReferences }: { initial: DataRow[]; secretReferences?: boolean }) {
  const [rows, setRows] = useState(initial)
  return (
    <>
      <DataEditor rows={rows} onChange={setRows} secretReferences={secretReferences} />
      <output data-testid="data">{JSON.stringify(rowsToData(rows))}</output>
    </>
  )
}
const sent = () => JSON.parse(screen.getByTestId('data').textContent!) as Record<string, string>

describe('DataEditor', () => {
  it('renders declared parameters by type, with required markers and descriptions', () => {
    render(
      <Editor
        initial={[
          {
            key: 'customerId',
            value: '',
            param: param('customerId', { type: 'Int', required: true }),
          },
          { key: 'endpoint', value: '', param: param('endpoint', { type: 'Url' }) },
          {
            key: 'notes',
            value: '',
            param: param('notes', { type: 'Text', description: 'Shown on the invoice' }),
          },
          { key: 'smtpPassword', value: '', param: param('smtpPassword', { type: 'Secret' }) },
        ]}
      />
    )

    expect(screen.getByLabelText(/customerId/)).toHaveAttribute('type', 'number')
    expect(screen.getByLabelText(/customerId/)).toBeRequired()
    expect(screen.getByLabelText('endpoint')).toHaveAttribute('type', 'url')
    expect(screen.getByLabelText('notes').tagName).toBe('TEXTAREA')
    expect(screen.getByText('Shown on the invoice')).toBeInTheDocument()
    expect(screen.getByLabelText('smtpPassword')).toHaveAttribute('type', 'password')
  })

  it('shows secret references in secret fields, with a hint, when the server resolves them', async () => {
    const secret = () => [
      { key: 'smtpPassword', value: '', param: param('smtpPassword', { type: 'Secret' }) },
    ]
    const { unmount } = render(<Editor initial={secret()} secretReferences />)

    expect(screen.getByText(/to use a secret stored in the server/)).toBeInTheDocument()
    await userEvent.type(screen.getByLabelText('smtpPassword'), 'secret:Smtp:Password')
    expect(screen.getByLabelText('smtpPassword')).toHaveAttribute('type', 'text')
    expect(sent()).toEqual({ smtpPassword: 'secret:Smtp:Password' })
    unmount()

    // Off: no hint, and a value that happens to start with secret: stays hidden
    render(<Editor initial={secret()} />)
    expect(screen.queryByText(/to use a secret stored in the server/)).not.toBeInTheDocument()
    await userEvent.type(screen.getByLabelText('smtpPassword'), 'secret:Smtp:Password')
    expect(screen.getByLabelText('smtpPassword')).toHaveAttribute('type', 'password')
  })

  it('offers a parameter’s choices in a dropdown', async () => {
    render(
      <Editor
        initial={[
          { key: 'format', value: '', param: param('format', { choices: ['csv', 'json'] }) },
        ]}
      />
    )

    await userEvent.click(screen.getByRole('combobox'))
    expect(screen.getAllByRole('option').map(o => o.textContent)).toEqual(['csv', 'json'])
    await userEvent.click(screen.getByRole('option', { name: 'json' }))

    expect(sent()).toEqual({ format: 'json' })
  })

  it('offers true or false for a Bool parameter, and takes several lines for a Text one', async () => {
    render(
      <Editor
        initial={[
          { key: 'dryRun', value: '', param: param('dryRun', { type: 'Bool' }) },
          { key: 'notes', value: '', param: param('notes', { type: 'Text' }) },
        ]}
      />
    )

    expect(screen.getByRole('combobox')).toHaveTextContent('Not set')
    await userEvent.click(screen.getByRole('combobox'))
    expect(screen.getAllByRole('option').map(o => o.textContent)).toEqual(['true', 'false'])
    await userEvent.click(screen.getByRole('option', { name: 'false' }))
    await userEvent.type(screen.getByLabelText('notes'), 'first{Enter}second')

    expect(sent()).toEqual({ dryRun: 'false', notes: 'first\nsecond' })
  })

  it('adds, edits and removes free-form entries', async () => {
    render(<Editor initial={[]} />)

    await userEvent.click(screen.getByRole('button', { name: /Add entry/ }))
    await userEvent.type(screen.getByLabelText('Data key'), 'region')
    await userEvent.type(screen.getByLabelText('Value for region'), 'eu-west')
    expect(sent()).toEqual({ region: 'eu-west' })

    await userEvent.click(screen.getByRole('button', { name: 'Remove region' }))
    expect(sent()).toEqual({})
  })

  it('explains that masked secrets are kept unless replaced', () => {
    render(<Editor initial={[{ key: 'apiKey', value: MASK }]} />)

    expect(screen.getByLabelText('Value for apiKey')).toHaveAttribute('type', 'password')
    expect(screen.getByText(/Masked values stay as they are/)).toBeInTheDocument()
    expect(sent()).toEqual({ apiKey: MASK })
  })
})
