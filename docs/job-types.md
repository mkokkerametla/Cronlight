# Job types and secrets

Giving your job types a form in the dashboard, and keeping secrets out of the database.

Any `IJob` in your app can be created from the UI with free-form job data. Attributes give it a proper form:

```csharp
[CronlightJob("Send invoices", Description = "Emails pending invoices for a customer")]
[JobParameter("customerId", Type = JobParameterType.Int, Required = true)]
[JobParameter("smtpPassword", Type = JobParameterType.Secret)]
[JobParameter("batchSize", Type = JobParameterType.Int, DefaultValue = "50")]
public class InvoiceJob : IJob { ... }
```

Parameter types are `String`, `Int`, `Bool`, `Url`, `Text` (multi-line) and `Secret` (masked).
`Choices = new[] { "csv", "json" }` turns a parameter into a dropdown and the API rejects other values.

Only types discovered in your app can be created; the API never instantiates a type by an arbitrary name. Discovery
finds public, non-abstract `IJob` classes, so a job type you keep `internal` is never offered.
`[CronlightJob(..., Hidden = true)]` leaves a type out of New job and the API refuses to create it, while its
existing jobs still show and can be edited.

In the API a job carries its type as `jobType`, the attribute's name as `displayName` and its description as
`typeDescription` (`description` is the job's own). Creating a job takes the type as `type` or `jobType`;
without one, or without a `name`, the request is refused with 400.

## Secret references

Secret values are masked in the UI but stored as plain text in the Quartz tables. To keep them out of the
database, store a reference instead: set a secret parameter to `secret:Smtp:Password`, and Cronlight reads
the value from configuration just before each run.

```csharp
builder.Services.AddCronlight(o => o.SecretsSection = "JobSecrets");
```

```json
{ "JobSecrets": { "Smtp": { "Password": "…" } } }
```

Any configuration source works: user secrets, environment variables (`JobSecrets__Smtp__Password`) or
Azure Key Vault through its configuration provider (a vault secret named `JobSecrets--Smtp--Password`).

- Only keys inside `SecretsSection` can be referenced, so people who can edit jobs cannot read
  connection strings or other configuration.
- References are resolved only in secret parameters and keys matching `SensitiveKeyPatterns`. In other
  parameters `secret:…` is plain text, so a secret cannot be copied into a URL or request body.
- The API checks references when a job is saved and refuses unknown names. A run whose secret is missing
  (removed from configuration later, or a job saved by code) is vetoed and history shows why.
- References are not secret, so the UI and audit log show them as written.
- The value replaces the reference in the run's `context.MergedJobDataMap`, which is where jobs should
  read it. The stored job keeps the reference. `context.JobDetail.JobDataMap` holds the reference too.
- Every app that runs these jobs (each cluster node) needs `AddCronlight` with the same `SecretsSection`
  and the secrets in its configuration.
