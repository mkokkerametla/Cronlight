import { expect, test } from '@playwright/test'
import { createJob, jobTypes, uniqueName } from './helpers'

// The browser sends the demo role with every request, like a logged-in user's cookie, but not the
// dashboard's own X-Cronlight-Request header, which only the dashboard page adds
test.use({ extraHTTPHeaders: { 'X-Demo-Role': 'admin' } })

test('a page on another site cannot change jobs with the viewer’s credentials', async ({ page, playwright, baseURL }) => {
  const name = uniqueName('target')
  const setup = await playwright.request.newContext({
    baseURL,
    extraHTTPHeaders: { 'X-Demo-Role': 'admin', 'X-Cronlight-Request': '1' },
  })
  await createJob(setup, { type: jobTypes.plain, name, triggers: [{ cronExpression: '0 0 6 * * ?' }] })
  const pauseUrl = new URL(`api/jobs/DEFAULT/${name}/pause`, baseURL).href
  const triggerState = async () =>
    ((await (await setup.get(`api/jobs/DEFAULT/${name}`)).json()) as { triggers: { state: string }[] }).triggers[0].state

  // The attacker's page: another site (localhost, not 127.0.0.1), on a page outside the dashboard, whose
  // Content-Security-Policy would otherwise stop the requests before the server's checks are reached.
  // Request interception (page.route) is not used: Playwright then answers CORS preflights itself.
  const attacker = new URL('/demo/status/200', baseURL!.replace('127.0.0.1', 'localhost')).href
  await page.goto(attacker)

  // A form posts without a preflight, so it reaches the server, which refuses it
  const [response] = await Promise.all([
    page.waitForResponse(pauseUrl),
    page.setContent(`<form method="post" action="${pauseUrl}"></form><script>document.forms[0].submit()</script>`),
  ])
  expect(response.status()).toBe(403)

  // A script cannot add the dashboard header: the browser's CORS preflight stops the request first
  await page.goto(attacker)
  const fetched = await page.evaluate(
    url =>
      fetch(url, { method: 'POST', credentials: 'include', headers: { 'X-Cronlight-Request': '1' } }).then(
        r => `status ${r.status}`,
        () => 'blocked'
      ),
    pauseUrl
  )
  expect(fetched).toBe('blocked')

  expect(await triggerState()).toBe('Normal')
  await setup.dispose()
})
