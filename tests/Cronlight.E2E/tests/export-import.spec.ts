import { readFile } from 'node:fs/promises'
import { expect, test } from './fixtures'
import { createJob, jobRow, jobTypes, openDashboard, searchJobs, uniqueName } from './helpers'

test('exports the jobs as YAML from the All jobs menu', async ({ page, request }) => {
  const name = uniqueName('exported')
  await createJob(request, {
    type: jobTypes.invoice,
    name,
    group: 'export-tests',
    data: { customerId: '42', smtpPassword: 'hunter2' },
    triggers: [{ name: 'nightly', cronExpression: '0 0 2 * * ?', timeZone: 'Europe/London' }],
  })
  await openDashboard(page)

  await page.getByRole('button', { name: 'All jobs' }).click()
  const [download] = await Promise.all([
    page.waitForEvent('download'),
    page.getByRole('menuitem', { name: 'Export as YAML' }).click(),
  ])
  expect(download.suggestedFilename()).toMatch(/^cronlight-SampleHost-\d{8}-\d{6}\.yaml$/)
  const yaml = await readFile(await download.path(), 'utf8')
  expect(yaml).toContain(`- group: export-tests\n  name: ${name}\n  type: ${jobTypes.invoice}\n`)
  expect(yaml).toContain('cronExpression: 0 0 2 * * ?\n    timeZone: Europe/London')
  // Secrets are never exported
  expect(yaml).toContain("smtpPassword: '********'")
  expect(yaml).not.toContain('hunter2')
})

test('checks a file, shows what it changes, then imports it', async ({ page, request }) => {
  const existing = uniqueName('import-existing')
  const created = uniqueName('import-new')
  await createJob(request, {
    type: jobTypes.plain,
    name: existing,
    group: 'import-tests',
    triggers: [{ name: `${existing}-nightly`, cronExpression: '0 0 2 * * ?' }],
  })
  const file = [
    'version: 1',
    'jobs:',
    '- group: import-tests',
    `  name: ${existing}`,
    `  type: ${jobTypes.plain}`,
    '  triggers:',
    `  - name: ${existing}-nightly`,
    '    cronExpression: 0 30 3 * * ?',
    '- group: import-tests',
    `  name: ${created}`,
    `  type: ${jobTypes.invoice}`,
    '  data: { customerId: 7 }',
    '',
  ].join('\n')
  await openDashboard(page)

  await page.getByRole('button', { name: 'All jobs' }).click()
  await page.getByRole('menuitem', { name: /Import jobs/ }).click()
  const sheet = page.getByRole('dialog', { name: 'Import jobs' })
  await sheet.getByLabel('Choose a file to import').setInputFiles({
    name: 'jobs.yaml',
    mimeType: 'application/yaml',
    buffer: Buffer.from(file),
  })
  await expect(sheet.getByLabel('Exported JSON or YAML')).toHaveValue(file)
  await sheet.getByRole('button', { name: 'Check file' }).click()

  await expect(sheet.getByRole('status')).toHaveText('1 to create, 1 to update, 0 unchanged.')
  const jobs = sheet.getByRole('list', { name: 'Jobs in the file' }).getByRole('listitem')
  await expect(jobs.filter({ hasText: existing })).toContainText('Changed')
  await expect(jobs.filter({ hasText: existing })).toContainText('0 0 2 * * ? (UTC) → 0 30 3 * * ? (UTC)')
  await expect(jobs.filter({ hasText: created })).toContainText('New')
  // Checking changed nothing
  expect((await request.get(`api/jobs/import-tests/${created}`)).status()).toBe(404)

  await sheet.getByRole('button', { name: 'Import 2 jobs' }).click()
  await expect(page.getByText('Imported: 1 created, 1 updated')).toBeVisible()
  await expect(sheet).toBeHidden()
  await searchJobs(page, created)
  await expect(jobRow(page, created)).toBeVisible()
  const job = await (await request.get(`api/jobs/import-tests/${existing}`)).json()
  expect(job.triggers.map((t: { cronExpression: string }) => t.cronExpression)).toEqual(['0 30 3 * * ?'])
})

test('a file with a mistake imports nothing, and says what is wrong', async ({ page }) => {
  const name = uniqueName('import-bad')
  await openDashboard(page)

  await page.getByRole('button', { name: 'All jobs' }).click()
  await page.getByRole('menuitem', { name: /Import jobs/ }).click()
  const sheet = page.getByRole('dialog', { name: 'Import jobs' })
  await sheet
    .getByLabel('Exported JSON or YAML')
    .fill(`version: 1\njobs:\n- name: ${name}\n  type: ${jobTypes.plain}\n- name: ${name}-2\n  type: Nope.Job\n`)
  await sheet.getByRole('button', { name: 'Check file' }).click()

  await expect(sheet.getByText('Nothing can be imported')).toBeVisible()
  await expect(sheet.getByText("Unknown job type 'Nope.Job'")).toBeVisible()
  await expect(sheet.getByRole('button', { name: /^Import/ })).toHaveCount(0)
})

test.describe('viewer role', () => {
  test.use({ extraHTTPHeaders: { 'X-Demo-Role': 'viewer', 'X-Cronlight-Request': '1' } })

  test('can export the jobs, but not import them', async ({ page }) => {
    await openDashboard(page)

    await page.getByRole('button', { name: 'All jobs' }).click()
    await expect(page.getByRole('menuitem')).toHaveText(['Export as YAML', 'Export as JSON'])
  })
})
