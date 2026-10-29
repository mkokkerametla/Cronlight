# Contributing to Cronlight

Issues and pull requests are welcome.

## Before you start

- **Bugs:** open an issue with the Cronlight, Quartz.NET and .NET versions, the job store, what you did and what you
  saw.
- **Features:** open an issue first to agree on the shape. The [roadmap](docs/ROADMAP.md) lists what is planned and
  what is not.
- **Security problems:** report them privately, as [SECURITY.md](SECURITY.md) describes, not in an issue.

## Building and testing

[Development](docs/development.md) covers the setup: the .NET 10 SDK, Node.js, the sample host, the UI dev server and
every test suite. Before you open a pull request, run:

```bash
build/test-all.sh                                # UI lint, types and tests, and the .NET tests
cd tests/Cronlight.E2E && npx playwright test    # end-to-end tests, for changes to the dashboard
```

CI runs both, and the .NET tests against PostgreSQL, MySQL and SQL Server too.

## Pull requests

- One change per pull request, with tests for what it changes.
- Format the UI with Prettier (`npm run format` in `src/Cronlight.UI`); CI checks it.
- Update the docs page a change affects, and the README when it changes setup, features or limitations.
- Write UI text and docs in plain words and sentence case, as the dashboard does: say what a control does and what
  went wrong, and how to fix it.
