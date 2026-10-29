import { act, renderHook } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'
import { asMessages, useConversation, type Turn } from '@/hooks/use-conversation'

describe('useConversation', () => {
  afterEach(() => sessionStorage.clear())

  it("keeps each scheduler's conversation for the tab's session", () => {
    const first = renderHook(() => useConversation('Billing'))
    act(() => first.result.current[1]([{ role: 'user', text: 'What failed?' }]))
    first.unmount()

    expect(renderHook(() => useConversation('Billing')).result.current[0]).toEqual([
      { role: 'user', text: 'What failed?' },
    ])
    expect(renderHook(() => useConversation('Reports')).result.current[0]).toEqual([])
  })

  it('counts an answer cut off by a reload as stopped, and forgets a cleared conversation', () => {
    sessionStorage.setItem(
      'cronlight-assistant:Billing',
      JSON.stringify([{ role: 'assistant', text: 'Half', status: 'streaming' }])
    )
    const { result } = renderHook(() => useConversation('Billing'))
    expect(result.current[0][0].status).toBe('stopped')

    act(() => result.current[1]([]))
    expect(sessionStorage.getItem('cronlight-assistant:Billing')).toBeNull()
  })

  it('starts afresh when what was kept cannot be read', () => {
    sessionStorage.setItem('cronlight-assistant:Billing', '{not json')
    expect(renderHook(() => useConversation('Billing')).result.current[0]).toEqual([])
  })

  it('sends the questions and the answers written, not failed ones', () => {
    const turns: Turn[] = [
      { role: 'user', text: 'Hello?' },
      { role: 'assistant', text: '', status: 'error', error: 'No model' },
      { role: 'user', text: 'Hello again?' },
      { role: 'assistant', text: 'Hi.', status: 'done', tools: [] },
    ]
    expect(asMessages(turns)).toEqual([
      { role: 'user', text: 'Hello?' },
      { role: 'user', text: 'Hello again?' },
      { role: 'assistant', text: 'Hi.' },
    ])
  })
})
