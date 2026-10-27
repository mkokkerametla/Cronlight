# Setup and options

How to add Cronlight to an app, and every option it takes.

```csharp
// 1. Your existing Quartz setup stays exactly as it is
builder.Services.AddQuartz(q =>
{
    q.UsePersistentStore(s =>
    {
        s.UseProperties = true;
        s.UseSqlServer(builder.Configuration.GetConnectionString("Quartz")!); // from Key Vault, env vars, ...
        s.UseNewtonsoftJsonSerializer();
    });
});
builder.Services.AddQuartzHostedService();

// 2. Add Cronlight
builder.Services.AddCronlight(o =>
{
    o.ViewPolicy = "QuartzViewers";   // required outside Development
    o.ManagePolicy = "QuartzAdmins";  // create/edit/pause/delete
});

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();

// 3. Map it under any path
app.MapCronlight("/ops/jobs");
```

Open `/ops/jobs/`. The API is at `/ops/jobs/api/*`, and described at `/ops/jobs/api/openapi.json`.

## The API's description

`api/openapi.json` is an OpenAPI 3.1 document of every endpoint: what it takes, what it answers and its errors. Load
it into a client generator, Postman or a Swagger UI the host serves. It is for those who may see the dashboard
(`ViewPolicy`), as the rest of the API is.

It is a document of its own, named `cronlight`, and the API's endpoints are in that document group. So the host's own
OpenAPI documents leave them out: `AddOpenApi()`'s, and Swashbuckle's with its default rules, take endpoints with no
group or their own. A host that wants them in its own document can include the `cronlight` group there.

## Options

| Option | Default | |
|---|---|---|
| `ViewPolicy` | none | Authorization policy for the UI and read endpoints |
| `ManagePolicy` | `ViewPolicy` | Policy for mutations |
| `ReadOnly` | `false` | Disable every mutation |
| `AllowAnonymousOutsideDevelopment` | `false` | Without a `ViewPolicy` Cronlight returns 403 outside Development unless this is set |
| `AllowManagingUnloadableJobs` | `false` | Allow pause/resume/delete on jobs whose type isn't in this app |
| `JobAssemblies` | all loaded app assemblies | Where to look for `IJob` types offered in "New Job". Left empty, every loaded assembly but .NET's and Quartz.NET's (`System`, `Microsoft`, `Quartz`, `netstandard` and names under them, such as `Quartz.Jobs`). A scheduler can offer its own instead: `Schedulers[name].JobAssemblies` |
| `Schedulers[name]` | none | Per scheduler: `ViewPolicy`, `ManagePolicy` and `JobAssemblies` ([several schedulers](schedulers.md)) |
| `RefreshSeconds` | `10` | How often an open dashboard refreshes jobs, running jobs and history (1 to 300 seconds) |
| `SensitiveKeyPatterns` | password, secret, token, apikey, connectionstring | JobDataMap keys masked in responses |
| `SecretsSection` | none | Configuration section that [secret references](job-types.md#secret-references) are read from; none turns them off |
| `ContentSecurityPolicy` | own files only, no framing | Content-Security-Policy for the dashboard. Change `frame-ancestors` to show it inside your own portal; `null` sends none |
