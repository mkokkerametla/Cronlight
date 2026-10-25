using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Quartz;

namespace Cronlight.Tests;

/// <summary>
/// A host app with a database-backed Quartz scheduler (SQLite unless another database is given)
/// and Cronlight mapped at /cronlight. Quartz keeps schedulers and data sources in static
/// registries, so each host gets unique names.
/// </summary>
public sealed class TestHost : IAsyncDisposable
{
    private readonly WebApplication app;

    public string SchedulerName { get; }
    public TestDatabase Database { get; }
    public IServiceProvider Services => app.Services;

    private TestHost(WebApplication app, string schedulerName, TestDatabase database)
    {
        this.app = app;
        SchedulerName = schedulerName;
        Database = database;
    }

    public static async Task<TestHost> Start(
        string environment = "Development",
        Action<CronlightOptions>? configure = null,
        TestDatabase? database = null,
        Action<JobHistoryOptions>? history = null,
        bool useProperties = true,
        Action<IServiceCollection>? services = null,
        bool ramJobStore = false,
        Dictionary<string, string?>? settings = null,
        bool clustered = false,
        TimeSpan? misfireThreshold = null)
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var schedulerName = $"test-{id}";
        database ??= TestDatabase.Sqlite();
        await database.EnsureQuartzSchema();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.WebHost.UseTestServer();
        if (settings != null)
            builder.Configuration.AddInMemoryCollection(settings);
        // Quartz's LogProvider is process-static and captures the first host's ILoggerFactory.
        // A shared instance (which DI never disposes) keeps later hosts from hitting a disposed one.
        builder.Logging.ClearProviders();
        builder.Services.Replace(ServiceDescriptor.Singleton<ILoggerFactory>(ErrorFileLoggerFactory.Instance));

        builder.Services.AddQuartz(q =>
        {
            q.SchedulerName = schedulerName;
            if (misfireThreshold is { } threshold)
                q.MisfireThreshold = threshold;
            // Quartz's default in-memory store, as many hosts use: nothing Cronlight can read directly
            if (ramJobStore)
                return;
            q.UsePersistentStore(store =>
            {
                // Off lets job data hold non-string values, as many host apps do
                store.UseProperties = useProperties;
                database.UseFor(store, $"ds-{id}");
                store.UseNewtonsoftJsonSerializer();
                if (clustered)
                    store.UseClustering();
            });
        });
        builder.Services.AddQuartzHostedService();
        services?.Invoke(builder.Services);
        if (history != null)
            builder.Services.AddCronlightHistory(history);

        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, HeaderRoleAuthHandler>("Test", null);
        builder.Services.AddAuthorization(o =>
        {
            o.AddPolicy("Viewers", p => p.RequireRole("viewer", "admin"));
            o.AddPolicy("Admins", p => p.RequireRole("admin"));
        });

        builder.Services.AddCronlight(o =>
        {
            o.JobAssemblies.Add(typeof(TestHost).Assembly);
            configure?.Invoke(o);
        });

        WebApplication? app = null;
        try
        {
            app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapCronlight("/cronlight");
            await app.StartAsync();
            return new TestHost(app, schedulerName, database);
        }
        catch
        {
            // Tests that expect startup to fail get no host to dispose, so clean up here
            if (app != null)
                await app.DisposeAsync();
            database.Cleanup();
            throw;
        }
    }

    /// <summary>A client that calls the API as the dashboard does, unless <paramref name="asDashboard"/> is false.</summary>
    public HttpClient Client(string? role = null, bool asDashboard = true)
    {
        var client = app.GetTestClient();
        client.BaseAddress = new Uri(client.BaseAddress!, "/cronlight/");
        if (asDashboard)
            client.DefaultRequestHeaders.Add("X-Cronlight-Request", "1");
        if (role != null)
            client.DefaultRequestHeaders.Add(HeaderRoleAuthHandler.Header, role);
        return client;
    }

    public async Task<IScheduler> Scheduler() =>
        await app.Services.GetRequiredService<ISchedulerFactory>().GetScheduler();

    /// <summary>Simulates a job written by another application sharing this scheduler (SQLite tests only).</summary>
    public void InsertForeignJob(string group, string name) =>
        Database.Execute(
            $"INSERT INTO QRTZ_JOB_DETAILS VALUES ('{SchedulerName}', '{name}', '{group}', NULL, 'OtherApp.Jobs.ReportJob, OtherApp', 1, 0, 0, 0, NULL)")
            .GetAwaiter().GetResult();

    public async Task<int> CountJobRows(string group, string name) =>
        Convert.ToInt32(await Database.Scalar(
            $"SELECT count(*) FROM QRTZ_JOB_DETAILS WHERE SCHED_NAME = '{SchedulerName}' AND JOB_GROUP = '{group}' AND JOB_NAME = '{name}'"));

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
        Database.Cleanup();
    }
}

/// <summary>
/// Writes errors logged by every test host to the file named by CRONLIGHT_TEST_ERROR_LOG, as CI sets, so a 500 or a hang
/// there shows what the server ran into. Without the variable nothing is logged. Many tests cause errors on purpose.
/// </summary>
public sealed class ErrorFileLoggerFactory : ILoggerFactory
{
    public static readonly ErrorFileLoggerFactory Instance = new(Environment.GetEnvironmentVariable("CRONLIGHT_TEST_ERROR_LOG"));

    private readonly string? path;
    private readonly object gate = new();

    private ErrorFileLoggerFactory(string? path) => this.path = string.IsNullOrEmpty(path) ? null : path;

    public ILogger CreateLogger(string categoryName) => path == null ? NullLogger.Instance : new Category(this, categoryName);

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }

    private void Write(string category, LogLevel level, string message, Exception? exception)
    {
        var entry = $"{DateTimeOffset.UtcNow:O} {level} {category}: {message}{(exception == null ? "" : Environment.NewLine + exception)}{Environment.NewLine}{Environment.NewLine}";
        lock (gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path!))!);
                File.AppendAllText(path!, entry);
            }
            catch (IOException)
            {
                // Logging must never fail a test
            }
        }
    }

    private sealed class Category(ErrorFileLoggerFactory factory, string name) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
                factory.Write(name, logLevel, formatter(state, exception), exception);
        }
    }
}

public static class HttpExtensions
{
    public static Task<HttpResponseMessage> PostJson(this HttpClient client, string url, object body) =>
        client.PostAsJsonAsync(url, body);

    public static Task<HttpResponseMessage> PutJson(this HttpClient client, string url, object body) =>
        client.PutAsJsonAsync(url, body);
}

/// <summary>The "X-Test-Role" header becomes a role claim.</summary>
public class HeaderRoleAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string Header = "X-Test-Role";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var role = Request.Headers[Header].ToString();
        if (string.IsNullOrEmpty(role))
            return Task.FromResult(AuthenticateResult.NoResult());

        var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "test"), new Claim(ClaimTypes.Role, role) }, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}
