import { describe, expect, it } from 'vitest'
import { defaultView, readView, viewSearch, type View } from '@/lib/view-state'

const view: View = {
  tab: 'history',
  jobQuery: { search: 'invoice', type: 'Send invoices', status: 'Paused', page: 3 },
  historyFilter: { status: 'Failed', job: { group: 'billing', name: 'nightly run' } },
  selection: { group: 'billing', name: 'nightly run' },
}

describe('view state', () => {
  it('writes only what differs from the default view, and reads it back', () => {
    expect(viewSearch(defaultView, '')).toBe('')

    const search = viewSearch(view, '')
    expect(readView(search)).toEqual(view)
  })

  it("keeps the address's other parameters, such as the scheduler", () => {
    const search = viewSearch(
      { ...defaultView, tab: 'running' },
      '?scheduler=Reports&tab=history&job=x'
    )
    expect(new URLSearchParams(search).get('scheduler')).toBe('Reports')
    expect(new URLSearchParams(search).get('tab')).toBe('running')
    expect(new URLSearchParams(search).has('job')).toBe(false)
  })

  it('falls back to the default for anything missing or unknown', () => {
    expect(readView('?tab=settings&status=Bogus&outcome=Exploded&page=-2&group=billing')).toEqual(
      defaultView
    )
    expect(readView('?page=abc').jobQuery.page).toBe(1)
  })
})
