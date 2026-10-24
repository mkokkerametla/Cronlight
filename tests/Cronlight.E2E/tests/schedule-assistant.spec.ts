import { expect, test } from '@playwright/test'
import { openDashboard } from './helpers'

test('turns a plain-language schedule into a ChatGPT or Google question in a new tab', async ({ page, context }) => {
  // Never reach the real sites
  await context.route(/^https:\/\/(chatgpt\.com|www\.google\.com)\//, route =>
    route.fulfill({ status: 200, contentType: 'text/html', body: '<title>stub</title>' })
  )
  await openDashboard(page)
  await page.getByRole('button', { name: 'New job' }).click()
  const sheet = page.getByRole('dialog', { name: 'New job' })
  await sheet.getByLabel('Time zone').fill('America/Chicago')

  const ask = sheet.getByRole('button', { name: /^Ask / })
  await expect(ask).toBeDisabled()
  await sheet.getByLabel('Describe the schedule in plain language').fill('every Tuesday and Friday at 4pm')

  const [chatgpt] = await Promise.all([page.waitForEvent('popup'), ask.click()])
  const question = new URL(chatgpt.url()).searchParams.get('q')
  expect(new URL(chatgpt.url()).host).toBe('chatgpt.com')
  expect(question).toContain('Quartz.NET cron expression for: every Tuesday and Friday at 4pm')
  expect(question).toContain('America/Chicago')
  await chatgpt.close()

  await sheet.getByRole('button', { name: 'Choose where to ask' }).click()
  const [google] = await Promise.all([page.waitForEvent('popup'), page.getByRole('menuitem', { name: 'Google' }).click()])
  expect(new URL(google.url()).searchParams.get('q')).toBe('Quartz cron expression every Tuesday and Friday at 4pm')

  // The last choice becomes the main button
  await expect(sheet.getByRole('button', { name: /^Ask Google/ })).toBeVisible()
})
