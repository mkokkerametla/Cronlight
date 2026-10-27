# Exporting and importing jobs

Jobs can be written to a JSON or YAML file and read back from one: to move them from one environment to another, or
to keep them in source control and apply them from a deployment.

## From the dashboard

The **All jobs** menu above the job list has **Export as YAML** and **Export as JSON**, which download every job in
the scheduler. Viewers can export too: the file holds what the dashboard already shows them.

**Import jobs…** (for users who can manage jobs) opens a panel to choose a file or paste one. **Check file** says what
importing it would do to each job, with every field it would change, before anything is changed. **Import** then
carries it out.

## From a script

```bash
# Export: format=json (the default) or yaml, and group= for one group only
curl -o jobs.yaml "https://…/ops/jobs/api/jobs/export?format=yaml"

# Check what importing would change, then import. YAML needs its content type; anything else is read as JSON
curl -X POST -H "X-Cronlight-Request: 1" -H "Content-Type: application/yaml" --data-binary @jobs.yaml \
  "https://…/ops/jobs/api/jobs/import?dryRun=true"
curl -X POST -H "X-Cronlight-Request: 1" -H "Content-Type: application/yaml" --data-binary @jobs.yaml \
  "https://…/ops/jobs/api/jobs/import"
```

Add `scheduler=<name>` for a scheduler other than the app's own. Import needs the manage permission and the
`X-Cronlight-Request` header, as every change does. It answers with what it did to each job: `create`, `update` or
`unchanged`, and the fields it changed, by the names the [audit log](audit.md) uses.

## The file

```yaml
version: 1
scheduler: Billing            # where it was exported from; import ignores it
exportedAtUtc: 2026-10-27T09:00:00+00:00
jobs:
- group: billing              # DEFAULT when left out
  name: send-invoices
  type: Contoso.Jobs.InvoiceJob
  description: Emails pending invoices
  paused: true                # every trigger is paused
  retry:                      # left out, the job follows its type's [CronlightRetry]
    maxRetries: 3
    delaySeconds: 60
    exponential: true
  data:
    customerId: "42"
    smtpPassword: secret:Smtp:Password
  triggers:
  - name: nightly
    kind: Cron                # Cron, Once or Interval
    cronExpression: 0 0 2 * * ?
    timeZone: Europe/London   # UTC when left out
    skipMissed: true
  - name: every-ten-minutes
    kind: Interval
    startAtUtc: 2026-10-27T09:00:00+00:00
    intervalSeconds: 600
    repeatCount: 5            # left out, it repeats forever
```

The trigger fields are those of the API's triggers: `kind`, `cronExpression`, `timeZone`, `startAtUtc`,
`intervalSeconds`, `repeatCount` and `skipMissed`. A property Cronlight does not know is refused, so a misspelt one
does not go unnoticed.

In YAML, values without quotes are read as YAML 1.2 reads them: `42` is a number, `true` a boolean and `~` nothing.
Job data is text, so `customerId: 42` is saved as `"42"`, but `007` would be saved as `7`: quote values that must stay
as written, as the export does. A job data value of `~` (or `null`) is the same as leaving the key out. Aliases
(`*name`) are refused, and a file can be at most 10 MB.

## What importing does

- A job the scheduler does not have is **created**, as `POST api/jobs` would create it: its type must be one New job
  offers, and its data must satisfy the type's parameters. Created paused when the file says `paused: true`.
- A job it has is **updated to match the file**: its description, retry policy, job data and cron, one-time and
  interval triggers become the file's. Data keys the file does not have are removed, and triggers it does not list
  are unscheduled. Waiting retries, pending manual runs and triggers of other kinds are kept, as editing a job keeps
  them. A job that is paused, or not, stays so.
- A trigger in the file with the name of one the job has is that trigger: changing its schedule keeps its Quartz
  calendar, priority and data, as the editor does. A trigger without a name is the job's trigger with the same
  schedule, if it has one, so importing the same hand-written file again changes nothing.
- A job the file does not list is **left alone**. Importing never deletes a job.
- A job's **type cannot change**: the import refuses a job whose type differs from the one in the scheduler. Delete
  the job first, or import it under another name.

Every job is checked before any is changed: if one cannot be imported, nothing is, and the answer (400) lists what is
wrong with each job. If the scheduler itself fails partway, the jobs before it stay imported, and the audit event
lists them.

## Secrets

Secret values are never exported: they are written as `********`, as the dashboard shows them, except for
[secret references](job-types.md) (`secret:Name`), which name a secret without revealing it. Importing `********`
keeps the value the job already has. A job the import creates has no value to keep, so it is refused until the file
gives the value or, better, a secret reference that each environment's configuration resolves.

## What is not exported

- Jobs of other apps sharing the scheduler (types this app cannot load) are left out.
- Waiting retries, pending manual runs, triggers of other kinds, and the Quartz calendar, priority and data of
  triggers.
- Whether a job is durable: jobs the import creates are durable, as jobs created from the dashboard are.
- An interval trigger's runs so far: imported elsewhere with a start in the past, it moves on to its next run on the
  same rhythm, with its full repeat count.

## Audit

An import is audited as one event, `import` (`import-dry-run` for a check), with `<group>.<name>` set to `created` or
`updated` for each job it changed and `<group>.<name>: <field>` for each change, secrets masked.
