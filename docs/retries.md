# Retries and re-running

Retrying failed runs automatically, and running a job again by hand.

A failed run can be retried automatically. Give the job type a default:

```csharp
// Waits 30 s, 60 s, then 120 s; without Exponential, 30 s each time
[CronlightRetry(3, DelaySeconds = 30, Exponential = true)]
public class InvoiceJob : IJob { ... }
```

and change it for a single job in the dashboard's create and edit forms (Retries 0 turns them off, even
when the type has a default). A job that has not been given its own policy follows its type's, so changing
the attribute later applies to it.

- **Each retry is a one-off trigger** of the same job, in the trigger group `Cronlight.Retries`. It is
  stored like any trigger, so in a cluster any node may run it and it survives a restart. It carries the
  failed run's trigger data, so a run started with different data is retried with that data.
- **Only failures are retried**, not cancelled or vetoed runs. Waits are capped at a day.
- **Only some failures**, if you like: `[CronlightRetry(3, RetryOn = [typeof(HttpRequestException)])]`, or
  "Only retry these exceptions" in the editor, retries only those exception types (by full or short name)
  and types derived from them. Other failures fail at once.
- **Waiting retries** show on the job's page as a `Retry` trigger with its attempt number, and can be
  cancelled there (`POST …/jobs/{group}/{name}/retries/{trigger}/cancel-retry`, audited as `cancel-retry`).
  The attempt that failed stays `Retried` in history, and no alert is sent for it.
- **A retry that cannot be scheduled** (the job store is unavailable, or the job was deleted meanwhile)
  leaves the run `Failed`, with the reason after its error, and it counts for alerts.
- **Pausing** the job pauses a waiting retry too. A job paused while its run was failing keeps the
  retry paused; a manual run of a paused job is retried as usual.
- **History** shows an attempt that will be retried as `Retried`, with its attempt number, and only the last
  attempt as `Failed` or `Succeeded`. **Alerts** look at the last attempt only, so a run that succeeds on a
  retry raises none.
- The policy is kept in the job's data under `Cronlight.Retry`. Job data keys starting with `Cronlight.`
  are Cronlight's own: the dashboard hides them and the API refuses to set them.
- Every app that runs these jobs (each cluster node) needs `AddCronlight`, which attaches the retry
  listener.

To run a job once with different data, open it and choose **Run with different data**. The changes apply
to that run and its retries only; the saved job keeps its data, and the audit log records what was changed.
From the API, send the changed values with the run (validated like an edit; a masked secret keeps the
saved value):

```bash
curl -X POST -H "X-Cronlight-Request: 1" -H "Content-Type: application/json" \
  -d '{"data":{"region":"us"}}' …/api/jobs/DEFAULT/nightly/trigger
```

The History tab has **Run again** on runs that failed, were vetoed or were cancelled. It repeats the run
with that run's own data: history keeps each run's trigger data (such as the values of a run with different
data, shown as "own data"), with sensitive values masked, so a masked secret is run with the saved value. The
audit log records which run it repeats as `retryOf`.
