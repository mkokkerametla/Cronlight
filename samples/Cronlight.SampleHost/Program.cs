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

// HTTP request job: lets people schedule API calls from the UI. Restrict AllowedHosts in real apps.
builder.Services.AddHttpClient(HttpJob.ClientName);
builder.Services.Configure<HttpJobOptions>(builder.Configuration.GetSection("HttpJob"));

// Demo auth: the "X-Demo-Role" header becomes a role claim. Real hosts use their own scheme.
builder.Services.AddAuthentication("Demo").AddScheme<AuthenticationSchemeOptions, DemoAuthHandler>("Demo", null);
builder.Services.AddAuthorization(o =>
{
    o.AddPolicy("QuartzViewers", p => p.RequireRole("viewer", "admin"));
    o.AddPolicy("QuartzAdmins", p => p.RequireRole("admin"));
});

builder.Services.AddCronlight(o =>
{
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
