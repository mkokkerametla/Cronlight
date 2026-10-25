import { expect, test } from './fixtures'
import { choose, createJob, jobRow, jobTypes, openDashboard, uniqueName } from './helpers'

test('the dashboard works under its Content-Security-Policy', async ({ page, request }) => {
  // Collect every violation the browser reports: blocked scripts, styles, images, fonts or requests
  await page.addInitScript(() => {
    const seen: string[] = ((window as unknown as { cspViolations: string[] }).cspViolations = [])
    document.addEventListener('securitypolicyviolation', e => seen.push(`${e.violatedDirective} ${e.blockedURI}`))
  })
  const consoleErrors: string[] = []
  page.on('console', message => {
    if (message.type() === 'error' && message.text().includes('Content Security Policy')) consoleErrors.push(message.text())
  })
  const name = uniqueName('csp')
  await createJob(request, { type: jobTypes.invoice, name, data: { customerId: '1' }, triggers: [{ cronExpression: '0 0 6 * * ?' }] })

  const response = await page.goto('./')
  expect(response?.headers()['content-security-policy']).toContain("script-src 'self'")
  await expect(page.getByRole('tab', { name: /Jobs/ })).toBeVisible()

  // Fonts, icons and the logo
  await expect(page.getByRole('img', { name: 'Cronlight' })).toBeVisible()
  expect(await page.evaluate(() => document.fonts.check('16px Geist'))).toBe(true)
  // Menus, dark mode, a select, the cron preview, tooltips, toasts, sheets and the history tab
  await page.getByRole('button', { name: 'Change theme' }).click()
  await page.getByRole('menuitemradio', { name: /Dark/ }).click()
  await page.getByRole('button', { name: 'Scheduler details' }).click()
  await page.keyboard.press('Escape')
  await page.getByRole('button', { name: 'New job' }).click()
  const sheet = page.getByRole('dialog', { name: 'New job' })
  await choose(page, sheet.locator('#job-type'), /PlainJob/)
  await sheet.getByLabel('Cron expression').fill('0 0 9 * * ?')
  await expect(sheet.getByText('Next (your local time)')).toBeVisible()
  await page.keyboard.press('Escape')
  await jobRow(page, name).getByRole('button', { name: `Run ${name} now` }).hover()
  await expect(page.getByRole('tooltip')).toBeVisible()
  await jobRow(page, name).getByRole('button', { name: `Pause ${name}` }).click()
  await expect(page.getByText(`${name}: paused`)).toBeVisible()
  await jobRow(page, name).click()
  await expect(page.getByRole('dialog', { name })).toContainText('Recent runs')
  await page.keyboard.press('Escape')
  await page.getByRole('tab', { name: /History/ }).click()
  await expect(page.getByLabel('Filter by outcome')).toBeVisible()

  expect(await page.evaluate(() => (window as unknown as { cspViolations: string[] }).cspViolations)).toEqual([])
  expect(consoleErrors).toEqual([])
})

test('another site cannot show the dashboard in a frame', async ({ page, baseURL }) => {
  const refusals: string[] = []
  page.on('console', message => {
    if (message.text().includes('frame-ancestors')) refusals.push(message.text())
  })

  // Another site (localhost instead of 127.0.0.1), on a page outside the dashboard, so the dashboard's
  // own policy does not block the frame from the parent's side
  await page.goto(new URL('/demo/status/200', baseURL!.replace('127.0.0.1', 'localhost')).href)
  await page.setContent(`<iframe src="${baseURL}" width="800" height="600"></iframe>`)

  await expect.poll(() => refusals.length).toBeGreaterThan(0)
  const frame = page.frames().find(f => f !== page.mainFrame())!
  await expect(frame.getByRole('tab', { name: /Jobs/ })).toHaveCount(0)
})
