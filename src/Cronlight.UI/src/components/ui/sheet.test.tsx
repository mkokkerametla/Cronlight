import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { Sheet, SheetContent, SheetDescription, SheetTitle } from '@/components/ui/sheet'

function Page() {
  return (
    <>
      <Sheet defaultOpen>
        <SheetContent>
          <SheetTitle>Job</SheetTitle>
          <SheetDescription>A job's panel</SheetDescription>
        </SheetContent>
      </Sheet>
      {/* Where Sonner draws toasts: outside the panel, over it. The open panel hides the rest from screen readers, so tests find these by text */}
      <ol data-sonner-toaster="">
        <li>
          <button type="button">Undo</button>
        </li>
      </ol>
      <button type="button">Elsewhere</button>
    </>
  )
}

// While the panel is open the page behind it takes no pointer events; the browser lets a toast's own
// pointer-events through, which jsdom does not model, so the check is left out here
const user = () => userEvent.setup({ pointerEventsCheck: 0 })

describe('Sheet', () => {
  it('stays open when a toast drawn over it is used', async () => {
    render(<Page />)

    await user().click(screen.getByText('Undo'))

    expect(screen.getByRole('dialog', { name: 'Job' })).toBeInTheDocument()
  })

  it('closes on a click anywhere else outside it', async () => {
    render(<Page />)

    await user().click(screen.getByText('Elsewhere'))

    expect(screen.queryByRole('dialog', { name: 'Job' })).toBeNull()
  })
})
