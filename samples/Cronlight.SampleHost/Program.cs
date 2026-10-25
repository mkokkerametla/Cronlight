using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Quartz;
using Cronlight;
using Cronlight.SampleHost;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Quartz") ?? "Data Source=quartz.db";
EnsureSchema(connectionString);

// The host's normal Quartz setup; the dashboard does not touch any of this
builder.Services.AddQuartz(q =>
{
    q.SchedulerName = "SampleHost";
    q.UsePersistentStore(store =>
    {
        store.UseProperties = true;
        store.UseMicrosoftSQLite(connectionString);
        store.UseNewtonsoftJsonSerializer();
        store.PerformSchemaValidation = true;
    });
});
builder.Services.AddQuartzHostedService(o => o.WaitForJobsToComplete = true);

// Execution history, stored next to the Quartz tables (QRTZ_CRONLIGHT_HISTORY)
builder.Services.AddCronlightHistory(o => o.Retention = TimeSpan.FromDays(7));

// HTTP request job: lets people schedule API calls from the UI, to the hosts listed here only
builder.Services.AddHttpJob(o =>
{
    // The demo calls its own /demo endpoints on this machine. Real apps list their APIs' hosts
    // instead and leave AllowPrivateNetworks off.
    o.AllowedHosts.AddRange(["127.0.0.1", "localhost"]);
    o.AllowPrivateNetworks = true;
    builder.Configuration.GetSection("HttpJob").Bind(o);
});

// Demo auth: the "X-Demo-Role" header becomes a role claim. Real hosts use their own scheme.
builder.Services.AddAuthentication("Demo").AddScheme<AuthenticationSchemeOptions, DemoAuthHandler>("Demo", null);
builder.Services.AddAuthorization(o =>
{
    o.AddPolicy("QuartzViewers", p => p.RequireRole("viewer", "admin"));
    o.AddPolicy("QuartzAdmins", p => p.RequireRole("admin"));
});

// Demo secret; real apps keep these in user secrets, environment variables or a key vault
builder.Configuration["JobSecrets:Demo:OrdersToken"] ??= "Bearer demo-token";

builder.Services.AddCronlight(o =>
{
    // Off by default, since what users type goes to ChatGPT or Google; the demo shows it
    o.ShowScheduleAssistant = true;
    // Secret parameters may name a secret from configuration instead of holding it, e.g.
    // authorization = secret:Demo:OrdersToken for the HTTP job calling /demo/orders
    o.SecretsSection = "JobSecrets";
    // Bound from config so the spike can flip modes without recompiling
    builder.Configuration.GetSection("Cronlight").Bind(o);
});

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", () => Results.Redirect("/ops/jobs/"));

// Local endpoints for the HTTP job to call in the demo, so it works without an external API
app.MapGet("/demo/status/{code:int}", (int code) => Results.StatusCode(code));
app.MapPost("/demo/orders", async (HttpRequest request) =>
{
    using var reader = new StreamReader(request.Body);
    var body = await reader.ReadToEndAsync();
    return request.Headers.Authorization.ToString() == "Bearer demo-token"
        ? Results.Accepted(value: new { received = body.Length })
        : Results.Unauthorized();
});
app.MapGet("/demo/slow", async () =>
{
    await Task.Delay(TimeSpan.FromSeconds(10));
    return Results.Ok();
});
app.MapCronlight("/ops/jobs");

app.Run();

static void EnsureSchema(string connectionString)
{
    using var connection = new SqliteConnection(connectionString);
    connection.Open();
    using var check = connection.CreateCommand();
    check.CommandText = "SELECT count(*) FROM sqlite_master WHERE name = 'QRTZ_JOB_DETAILS'";
    if ((long)check.ExecuteScalar()! > 0)
        return;

    using var create = connection.CreateCommand();
    create.CommandText = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "tables_sqlite.sql"));
    create.ExecuteNonQuery();
}

class DemoAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var role = Request.Headers["X-Demo-Role"].ToString();
        if (string.IsNullOrEmpty(role))
            return Task.FromResult(AuthenticateResult.NoResult());

        var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "demo"), new Claim(ClaimTypes.Role, role) }, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}
