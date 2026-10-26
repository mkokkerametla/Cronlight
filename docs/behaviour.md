# Behaviour worth knowing

How the dashboard and its API behave: secrets, editing, triggers, pausing, cancelling, large schedulers, security
headers and cross-site requests.

- **Secrets** are shown as `********`. Saving a masked value back keeps the stored value. To keep them
  out of the database too, use [secret references](job-types.md#secret-references).
- **Editing job data** changes only what is edited. `PUT api/jobs/{group}/{name}` sets the data keys it sends and
  keeps the others: `{"data": {"region": "us"}}` changes one value, and a key sent as `null` is removed. Required
  parameters are checked on the data the job ends up with. The dashboard's edit form sends only the fields, values
  and triggers changed in it, so a value the job saved meanwhile (with `[PersistJobDataAfterExecution]`) or a change
  someone else made is not undone; **Run with different data** compares with the values as the form opened too.
- **Jobs in Error.** When Quartz cannot create a job to run it (its constructor or the job factory throws), it sets
  every trigger of the job to Error and does not run it again by itself. The job list shows such jobs as **Error**,
  with a card counting them and a status filter (`status=Error` in `api/jobs/search`, `counts.error` in its answer),
  and the job's page explains it. History and the app's log say what was thrown. Fix the cause, then **Resume** the
  job, its group or every job: resuming sets triggers in Error back to normal.
- **Cancelling a run** from the Running tab asks the job to stop by cancelling
  `context.CancellationToken`, using Quartz's interrupt. Jobs that pass the token on (to `Task.Delay`,
  HttpClient, database calls) stop promptly; a job that never checks it keeps running. History records the
  run as Cancelled, with who cancelled it. Quartz can only interrupt runs on its own node, so in a cluster
  a run is cancelled from the dashboard of the node running it.
- **Large schedulers.** The job list is searched, filtered, counted and paged on the server
  (`GET api/jobs/search?search=&type=&status=&page=&pageSize=`). For database stores it reads every job's
  type and trigger states in two queries, and loads full details only for the page shown (50 jobs), so
  thousands of jobs cost about as much as fifty. `GET api/jobs` still returns every job with full
  details, for scripts and small schedulers.
- **Polling** happens every 10 seconds (`RefreshSeconds`) only while the dashboard is visible: a tab in the background asks
  nothing, and refreshes as soon as it is shown again. The 24-hour failure count is refreshed once a minute.
- **History holds still under the pointer.** While the pointer is over History, a refresh moves no row: it updates
  the runs listed where they are, dims those it no longer lists (a run that finished, under the Running filter),
  and keeps new runs aside behind **Show 3 new runs**, so a click lands on the run aimed at. They appear, and the
  dimmed runs go, when that is clicked or the pointer moves away; runs added with **Load older runs** stay. A run
  started with **Run again** is shown as soon as history has it, and choosing another filter drops what was kept
  aside.
- **The address keeps the view.** The tab, the job list's search, filters and page, the History filter and the open
  job are in the page's URL (`?tab=history&outcome=Failed`, `?group=billing&job=nightly`), so a link opens the same
  view and Back returns to the one before. Choosing another scheduler starts its dashboard afresh.
- **Keyboard:** `/` moves to the search box, `j` and `k` move through the jobs on the page, Enter opens the one
  marked, and Esc closes a panel. Keys typed into a field are text, not shortcuts.
- **Schedules in words.** Cron schedules read as text ("At 09:00, Monday through Friday") in the job list, the job
  panel and the editor's preview. Quartz numbers the days of the week from Sunday as 1.
- **Undo** follows pausing or resuming a job, in the toast that confirms it. A job Quartz stopped (in Error) has no
  undo for Resume, since pausing it would not stop it again.
- **Layout:** the Layout menu in the header uses the window's full width and makes table rows compact. Both are
  remembered in the browser, as the theme is.
- **Jobs from other apps** sharing the scheduler (types not loadable here) are listed but read-only.
- **Running jobs** are read from `QRTZ_FIRED_TRIGGERS` for ADO stores, so every cluster node is visible.
  For the in-memory store, or a custom connection provider, only this node is shown.
- **Errors** are returned as ProblemDetails with a trace id, never stack traces.
- **Security headers** are sent on every dashboard response: a Content-Security-Policy that allows only
  the dashboard's own files and API and no embedding in other sites (`frame-ancestors 'none'`, plus
  `X-Frame-Options: DENY`), `X-Content-Type-Options: nosniff` and `Referrer-Policy: no-referrer`. To
  embed the dashboard in your portal:
  `o.ContentSecurityPolicy = CronlightOptions.DefaultContentSecurityPolicy.Replace("frame-ancestors 'none'", "frame-ancestors https://portal.contoso.com");`
- **Cross-site requests** cannot change jobs. Requests that create, edit, pause, run or delete must
  send an `X-Cronlight-Request` header (any value), which the dashboard does and another site cannot,
  and browser requests marked `Sec-Fetch-Site: cross-site` are refused. Scripts calling the API add the
  header, e.g. `curl -X POST -H "X-Cronlight-Request: 1" …/api/jobs/DEFAULT/nightly/trigger`. Reads
  need no header.
- **Triggers** made in the dashboard are cron (with a time zone), one-time (runs once at a given time) or
  interval (every so many seconds, minutes, hours or days, from a start time, forever or a set number of
  runs). Editing a job in the dashboard replaces those three kinds. Waiting retries, manual runs waiting to
  start (shown as "Run now"), other trigger types (calendar and daily time interval triggers) and interval
  triggers that are not a whole number of seconds are shown but left alone. A trigger sent back unchanged is
  not rescheduled, and a paused trigger stays paused when its schedule changes. An interval trigger whose
  start has passed moves to its next run after now when it is changed, rather than running at once, and the
  runs it has had count towards its number of runs.
  - In the API a trigger is `{ "kind": "Once", "startAtUtc": "…" }` or
    `{ "kind": "Interval", "intervalSeconds": 300, "startAtUtc": "…", "repeatCount": 9 }` (runs after the
    first, at least 1; left out repeats forever), and cron is the default kind.
  - `PUT api/jobs/{group}/{name}` replaces the kinds named in `triggerKinds` (`["Cron", "Once", "Interval"]`,
    as the dashboard sends). Without it, it replaces cron triggers and the kinds it sends, so a script that
    only sends cron triggers keeps the job's one-time and interval triggers.
- **Pausing a group or everything.** A job's menu in the job list pauses or resumes its whole group, and
  "All jobs" pauses or resumes every job in the scheduler (`POST api/groups/{group}/pause|resume`,
  `POST api/scheduler/pause|resume`), after asking. Quartz does this in the job store, so it reaches every
  node, and a job added while everything is paused starts paused (with the in-memory store, also one added to
  a paused group). Resuming a group or everything resumes jobs that were paused one by one too. A group or
  scheduler that holds jobs of another app (types
  not loadable here) is refused unless `AllowManagingUnloadableJobs` is on, rather than half done. Audited as
  `pause-group`, `resume-group`, `pause-all` and `resume-all`.
