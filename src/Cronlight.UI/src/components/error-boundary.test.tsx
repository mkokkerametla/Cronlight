import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { ErrorBoundary } from '@/components/error-boundary'

function Broken(): never {
  throw new Error('Cannot read properties of undefined')
}

describe('ErrorBoundary', () => {
  it('renders its children while they work', () => {
    render(
      <ErrorBoundary>
        <p>dashboard</p>
      </ErrorBoundary>
    )

    expect(screen.getByText('dashboard')).toBeInTheDocument()
  })

  it('replaces a crashed page with the error and a reload button', async () => {
    const consoleError = vi.spyOn(console, 'error').mockImplementation(() => {})
    const reload = vi.fn()
    vi.spyOn(window, 'location', 'get').mockReturnValue({ ...window.location, reload })

    render(
      <ErrorBoundary>
        <Broken />
      </ErrorBoundary>
    )

    expect(screen.getByText('Something went wrong')).toBeInTheDocument()
    expect(screen.getByText('Cannot read properties of undefined')).toBeInTheDocument()
    expect(consoleError).toHaveBeenCalledWith(
      'Cronlight crashed',
      expect.any(Error),
      expect.anything()
    )
    await userEvent.click(screen.getByRole('button', { name: /Reload/ }))
    expect(reload).toHaveBeenCalled()
  })
})
