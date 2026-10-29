# Security policy

## Reporting a vulnerability

Report security problems privately through GitHub: on the repository's **Security** tab, choose **Report a
vulnerability**. Please do not open a public issue for them.

Say what an attacker can do and how, with the steps or a request that shows it, and the Cronlight version. The report
stays private until a fix is released.

## Supported versions

Fixes go into the latest release.

## Scope

Cronlight runs inside your app, behind your authentication and the policies you give it (`ViewPolicy`,
`ManagePolicy`, and the per-scheduler ones). In scope:

- How it applies those policies, to the API, the MCP server and AI mode
- Masking secrets and resolving secret references
- The cross-site request check and the security headers
- Reading and writing job files on export and import

Out of scope: the sample host's demo authentication, which lets anyone claim any role through the `X-Demo-Role`
header on purpose.
