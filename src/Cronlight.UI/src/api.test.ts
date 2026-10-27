import { afterEach, describe, expect, it, vi } from 'vitest'
import { api, currentScheduler, jobPath, selectScheduler } from '@/api'

const fetchMock = vi.fn<typeof fetch>()
const respond = (body: BodyInit | null, init: ResponseInit = {}) =>
  fetchMock.mockResolvedValueOnce(new Response(body, init))

describe('api', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
    selectScheduler(null)
  })

  const stub = () => vi.stubGlobal('fetch', fetchMock)

  it('requests paths relative to the page, with same-origin cookies and the dashboard header', async () => {
    stub()
    respond(JSON.stringify([{ name: 'nightly' }]), {
      headers: { 'Content-Type': 'application/json' },
    })

    const jobs = await api<{ name: string }[]>('jobs')

    expect(jobs).toEqual([{ name: 'nightly' }])
    expect(fetchMock).toHaveBeenCalledWith('api/jobs', {
      method: 'GET',
      signal: undefined,
      credentials: 'same-origin',
      headers: { 'X-Cronlight-Request': '1' },
      body: undefined,
    })
  })

  it('sends every request to the chosen scheduler, and keeps the choice in the page URL', async () => {
    stub()
    respond(null, { status: 204 })
    respond(null, { status: 204 })

    selectScheduler('Billing & Co')
    await api('jobs/search?page=2')
    await api('running')

    expect(fetchMock.mock.calls.map(([url]) => url)).toEqual([
      'api/jobs/search?page=2&scheduler=Billing%20%26%20Co',
      'api/running?scheduler=Billing%20%26%20Co',
    ])
    expect(new URLSearchParams(window.location.search).get('scheduler')).toBe('Billing & Co')

    // The host's own scheduler needs no parameter
    selectScheduler(null)
    expect(currentScheduler()).toBeNull()
    expect(window.location.search).toBe('')
  })

  it('sends a body as JSON', async () => {
    stub()
    respond(null, { status: 204 })

    await api('jobs', { method: 'POST', body: { name: 'nightly' } })

    expect(fetchMock).toHaveBeenCalledWith(
      'api/jobs',
      expect.objectContaining({
        method: 'POST',
        headers: { 'X-Cronlight-Request': '1', 'Content-Type': 'application/json' },
        body: '{"name":"nightly"}',
      })
    )
  })

  it('resolves to nothing for an empty response', async () => {
    stub()
    respond(null, { status: 204 })
    await expect(api('jobs/DEFAULT/nightly/pause', { method: 'POST' })).resolves.toBeUndefined()

    respond('', { status: 200, headers: { 'Content-Length': '0' } })
    await expect(api('jobs/DEFAULT/nightly', { method: 'DELETE' })).resolves.toBeUndefined()
  })

  it('turns problem details, plain JSON strings and text into error messages', async () => {
    stub()
    respond(JSON.stringify({ title: 'Invalid request.', detail: "Unknown job type 'Nope'" }), {
      status: 400,
    })
    await expect(api('jobs')).rejects.toThrow(/^Unknown job type 'Nope'$/)

    respond(JSON.stringify({ title: 'The scheduler operation failed.' }), { status: 500 })
    await expect(api('jobs')).rejects.toThrow('The scheduler operation failed.')

    respond(JSON.stringify('Job not found'), { status: 404 })
    await expect(api('jobs')).rejects.toThrow('Job not found')

    respond(JSON.stringify({}), { status: 409 })
    await expect(api('jobs')).rejects.toThrow('409')

    respond('upstream timeout', { status: 502 })
    await expect(api('jobs')).rejects.toThrow('upstream timeout')

    respond('', { status: 503, statusText: 'Service Unavailable' })
    await expect(api('jobs')).rejects.toThrow('503 Service Unavailable')
  })
})

describe('jobPath', () => {
  it('escapes the group and name', () => {
    expect(jobPath({ group: 'a/b', name: 'x y?' })).toBe('jobs/a%2Fb/x%20y%3F')
  })
})
