# Audit log

What is recorded for every change made through the dashboard or its API, and how to keep it.

Every change made through the dashboard or its API (create, edit, pause, resume, run now, cancel, delete,
pausing or resuming a group or the whole scheduler, and [importing jobs](export-import.md#audit)) is
logged under the `Cronlight.Audit` category: who made it, the job, whether it succeeded, for creates
and edits what changed, and for runs with different data what was changed for that run. Refused attempts are logged as warnings with the reason, such as a cross-site
request or read-only mode. Requests turned away by the host's authorization policies before reaching
Cronlight are logged by ASP.NET Core instead. Secret values are never written: a changed secret shows as
`********`.

```text
Audit: alice@contoso.com update billing.invoices in Billing: succeeded (204) {"description":{"Before":"Weekly","After":"Daily"},"triggers":{"Before":"0 0 9 ? * MON (UTC)","After":"0 0 9 * * ? (UTC)"}}
```

The fields are structured (`User`, `AuditAction`, `JobGroup`, `JobName`, `Scheduler`, `AuditOutcome`, `StatusCode`),
so log tools such as Seq, Application Insights or Elasticsearch can filter on them. To keep events in your
own store as well, register one or more sinks:

```csharp
// Resolved per request, so scoped services such as a DbContext can be used
builder.Services.AddScoped<ICronlightAuditSink, MyAuditSink>();

class MyAuditSink(AuditDbContext db) : ICronlightAuditSink
{
    public async Task WriteAsync(CronlightAuditEvent e, CancellationToken ct) { /* e.User, e.Action, e.Changes, ... */ }
}
```

A sink that throws is logged as an error; the change itself has already been made and is not undone.
