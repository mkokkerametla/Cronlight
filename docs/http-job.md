# Scheduling API calls (HTTP job)

The sample's job type for calling HTTP APIs on a schedule, and its network rules.

The sample includes [`HttpJob`](../samples/Cronlight.SampleHost/HttpJob.cs), a job type for calling HTTP APIs
on a schedule: URL, method, headers, body, an `authorization` header kept as a masked secret, timeout and
expected status codes (`2xx`, `200,202`, ...). A response with any other status fails the run, so it shows
up in execution history.

Copy `HttpJob.cs` and `HttpJobNetwork.cs` into your app and register the job with the hosts it may call:

```csharp
builder.Services.AddHttpJob(o => o.AllowedHosts.AddRange(["api.partner.com", "*.hooks.contoso.com"]));
```

Anyone who can create jobs chooses the URL, so the job only calls what you allow:

- **Hosts** must be in `AllowedHosts`: exact names, or `*.example.com` for its subdomains. An empty list
  allows none.
- **Internal addresses are refused** even for allowed names: loopback, private networks, link-local
  (including the cloud metadata endpoint `169.254.169.254`) and other reserved ranges, in IPv4 and IPv6.
  The check runs on the address actually connected to, after DNS, so a name pointing (or re-pointed)
  at an internal address is refused too. Set `AllowPrivateNetworks = true` only if job authors may
  call internal services.
- **Redirects are not followed**, and no proxy is used: a 3xx response is a status like any other.
