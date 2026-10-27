# Cronlight documentation

The [README](../README.md) covers what Cronlight is, what it needs and how to start. These pages cover the rest.
They are also on the [website](https://cronlight.pages.dev/docs/), next to a [live demo](https://cronlight.pages.dev/demo/).

## Using Cronlight

| Page | |
|---|---|
| [Setup and options](configuration.md) | Adding Cronlight to an app, and every option it takes |
| [Job types and secrets](job-types.md) | Forms for your job types, masked secrets, and secret references kept out of the database |
| [Retries and re-running](retries.md) | Retrying failed runs automatically, and running a job again by hand |
| [Execution history](history.md) | The optional package: every run, what it logged, misfires, late starts and runs that never finished |
| [Failure alerts](alerts.md) | Slack, Teams, webhook, email or your own sink when jobs fail, recover, stop or misfire |
| [Audit log](audit.md) | What is recorded for every change, and how to keep it in your own store |
| [Metrics and health checks](metrics-and-health.md) | OpenTelemetry metrics for every run, and a health check per scheduler |
| [Several schedulers in one app](schedulers.md) | Serving schedulers the app builds in code, with permissions per scheduler |
| [Scheduling API calls (HTTP job)](http-job.md) | The sample's job type for calling HTTP APIs, and its network rules |
| [Behaviour worth knowing](behaviour.md) | Editing, triggers, pausing, cancelling, large schedulers, jobs in Error, security headers and cross-site requests |

## Working on Cronlight

| Page | |
|---|---|
| [Development](development.md) | Repository layout, building, running the sample, the test suites, CI and releases |
| [Roadmap](ROADMAP.md) | What is planned, how Cronlight compares, known gaps and design notes |
