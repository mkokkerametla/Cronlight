import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { Markdown } from '@/lib/markdown'

describe('Markdown', () => {
  it('shows paragraphs, lists, tables, code and emphasis', () => {
    const text = [
      '## Failures',
      'Two jobs **failed**, one *twice*.',
      '',
      '- first',
      '- second,',
      '  wrapped',
      '',
      '| Job | Failures |',
      '|---|---:|',
      '| `billing.invoices` | 2 |',
      '',
      '```',
      'System.TimeoutException',
      '```',
    ].join('\n')
    const { container } = render(<Markdown text={text} />)

    expect(screen.getByText('Failures', { selector: 'p' })).toHaveClass('font-semibold')
    expect(screen.getByText('failed').tagName).toBe('STRONG')
    expect(screen.getByText('twice').tagName).toBe('EM')
    expect(screen.getAllByRole('listitem').map(li => li.textContent)).toEqual([
      'first',
      'second, wrapped',
    ])
    expect(screen.getByRole('columnheader', { name: 'Failures' })).toHaveClass('text-right')
    expect(screen.getByRole('cell', { name: '2' })).toHaveClass('text-right')
    expect(screen.getByRole('columnheader', { name: 'Job' })).toHaveClass('text-left')
    expect(
      screen.getByRole('cell', { name: 'billing.invoices' }).querySelector('code')
    ).not.toBeNull()
    expect(container.querySelector('pre')).toHaveTextContent('System.TimeoutException')
  })

  it('opens a job from its link', async () => {
    const onOpenJob = vi.fn()
    render(<Markdown text="See [nightly run](job:billing/nightly%20run)." links={{ onOpenJob }} />)

    await userEvent.click(screen.getByRole('button', { name: 'nightly run' }))

    expect(onOpenJob).toHaveBeenCalledWith('billing', 'nightly run')
  })

  it('opens a job whose name has parentheses, which URL-encoding leaves', async () => {
    const onOpenJob = vi.fn()
    render(
      <Markdown text="Run [cleanup](job:maint/cleanup%20(daily)) again." links={{ onOpenJob }} />
    )

    await userEvent.click(screen.getByRole('button', { name: 'cleanup' }))

    expect(onOpenJob).toHaveBeenCalledWith('maint', 'cleanup (daily)')
    expect(screen.getByText(/again\.$/).textContent).toBe('Run cleanup again.')
  })

  it('leaves the stars of a cron expression alone, and still emphasises words', () => {
    const { container } = render(
      <Markdown
        text={'| Job | Cron |\n|---|---|\n| sync | 0 */15 * * * ? |\n\n*Every* **15** minutes.'}
      />
    )

    expect(container.querySelector('td:last-child')).toHaveTextContent('0 */15 * * * ?')
    expect(container.querySelector('td em')).toBeNull()
    expect(container.querySelector('p em')).toHaveTextContent('Every')
    expect(container.querySelector('p strong')).toHaveTextContent('15')
  })

  it('reads links and code inside emphasis, as models write them', async () => {
    const onOpenJob = vi.fn()
    const { container } = render(
      <Markdown
        text={'**[partner-webhook](job:http/partner-webhook)** failed. _e.g. `HTTP 503`_'}
        links={{ onOpenJob }}
      />
    )

    await userEvent.click(screen.getByRole('button', { name: 'partner-webhook' }))

    expect(onOpenJob).toHaveBeenCalledWith('http', 'partner-webhook')
    expect(container.querySelector('strong button')).not.toBeNull()
    expect(container.querySelector('em code')).toHaveTextContent('HTTP 503')
    expect(container).not.toHaveTextContent('](job:')
  })

  it('keeps bold inside italics', () => {
    const { container } = render(<Markdown text="*In all, **933** errors.*" />)

    expect(container.querySelector('em strong')).toHaveTextContent('933')
    expect(container).toHaveTextContent(/^In all, 933 errors\.$/)
  })

  it('emphasises _words_ but leaves names with underscores alone', () => {
    const { container } = render(<Markdown text="Run _nightly_ for nightly_report_job." />)

    expect(container.querySelectorAll('em')).toHaveLength(1)
    expect(container.querySelector('em')).toHaveTextContent('nightly')
    expect(container).toHaveTextContent('Run nightly for nightly_report_job.')
  })

  it('opens a job whose link the model wrote with typographic hyphens', async () => {
    const onOpenJob = vi.fn()
    render(
      <Markdown
        text={'[partner\u2011webhook](job:http/partner\u2011webhook)'}
        links={{ onOpenJob }}
      />
    )

    await userEvent.click(screen.getByRole('button', { name: 'partner\u2011webhook' }))

    expect(onOpenJob).toHaveBeenCalledWith('http', 'partner-webhook')
  })

  it('spaces a date from its time with a full-width no-break space', () => {
    render(<Markdown text={'Failed at 2026-10-29\u202f01:56 UTC.'} />)

    expect(
      screen.getByText('Failed at 2026-10-29\u00a001:56 UTC.', { normalizer: s => s })
    ).toBeInTheDocument()
  })

  it('breaks a line at <br>, as models write in table cells', () => {
    const { container } = render(
      <Markdown text={'| Jobs |\n|---|\n| a/one <br> b/two <BR/> c |'} links={{}} />
    )

    const cell = container.querySelector('td')!
    expect(cell.querySelectorAll('br')).toHaveLength(2)
    expect(cell).toHaveTextContent('a/one b/two c')
  })

  it('shows other links and HTML as text, never as something to click or markup', () => {
    const { container } = render(
      <Markdown
        text={'[Reset your password](https://evil.example/login) <img src=x onerror=alert(1)>'}
      />
    )

    expect(screen.queryByRole('link')).toBeNull()
    expect(container.querySelector('img')).toBeNull()
    expect(container).toHaveTextContent(
      'Reset your password (https://evil.example/login) <img src=x onerror=alert(1)>'
    )
  })

  it('renders every part of an answer as it streams in, a table before its |---| line included', () => {
    const answer = [
      '**3 runs failed** in the last 24 hours.',
      '',
      '| Job | Failures |',
      '|---|---:|',
      '| [invoices](job:billing/invoices) | 2 |',
      '',
      '- first',
      '1. one',
      '## Next',
      '```',
      'code',
      '```',
    ].join('\n')
    for (let end = 1; end <= answer.length; end++) {
      const { unmount } = render(<Markdown text={answer.slice(0, end)} />)
      unmount()
    }
    render(<Markdown text={'| Job | Failures |'} />)
    expect(screen.getByText('| Job | Failures |')).toBeInTheDocument()
  })
})
