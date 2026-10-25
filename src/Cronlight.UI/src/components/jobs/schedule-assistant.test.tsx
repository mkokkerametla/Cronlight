import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ScheduleAssistant } from '@/components/jobs/schedule-assistant'

const describeBox = () => screen.getByLabelText('Describe the schedule in plain language')
const openedUrl = (open: ReturnType<typeof vi.spyOn>, call = 0) =>
  new URL(open.mock.calls[call][0] as string)

describe('ScheduleAssistant', () => {
  let open: ReturnType<typeof vi.spyOn>
  beforeEach(() => {
    open = vi.spyOn(window, 'open').mockReturnValue(null)
  })

  it('stays disabled until a schedule is described', async () => {
    render(<ScheduleAssistant timeZone="" />)
    expect(screen.getByRole('button', { name: /^Ask ChatGPT/ })).toBeDisabled()

    await userEvent.type(describeBox(), '   ')
    expect(screen.getByRole('button', { name: /^Ask ChatGPT/ })).toBeDisabled()
  })

  it('asks ChatGPT for a Quartz expression, including the time zone, in a new tab', async () => {
    render(<ScheduleAssistant timeZone=" America/Chicago " />)
    await userEvent.type(describeBox(), 'every Tuesday and Friday at 4pm')

    await userEvent.click(screen.getByRole('button', { name: /^Ask ChatGPT/ }))

    expect(open).toHaveBeenCalledOnce()
    const url = openedUrl(open)
    expect(url.origin).toBe('https://chatgpt.com')
    const question = url.searchParams.get('q')!
    expect(question).toContain(
      'Quartz.NET cron expression for: every Tuesday and Friday at 4pm (time zone America/Chicago)'
    )
    expect(question).toContain('6 or 7 fields')
    expect(open.mock.calls[0].slice(1)).toEqual(['_blank', 'noopener,noreferrer'])
  })

  it('asks with Enter too', async () => {
    render(<ScheduleAssistant timeZone="" />)
    await userEvent.type(describeBox(), 'hourly{Enter}')

    expect(openedUrl(open).searchParams.get('q')).toContain('for: hourly.')
  })

  it('searches Google from the menu and remembers that choice', async () => {
    const { unmount } = render(<ScheduleAssistant timeZone="UTC" />)
    await userEvent.type(describeBox(), 'weekdays at 9')

    await userEvent.click(screen.getByRole('button', { name: 'Choose where to ask' }))
    await userEvent.click(screen.getByRole('menuitem', { name: 'Google' }))

    const url = openedUrl(open)
    expect(url.origin + url.pathname).toBe('https://www.google.com/search')
    expect(url.searchParams.get('q')).toBe('Quartz cron expression weekdays at 9')
    expect(screen.getByRole('button', { name: /^Ask Google/ })).toBeInTheDocument()

    unmount()
    render(<ScheduleAssistant timeZone="UTC" />)
    expect(screen.getByRole('button', { name: /^Ask Google/ })).toBeInTheDocument()
  })
})
