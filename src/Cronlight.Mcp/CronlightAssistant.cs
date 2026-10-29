using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Cronlight.Mcp;

public class CronlightAssistantOptions
{
    /// <summary>
    /// The key of the <see cref="IChatClient"/> to use, when the app registers it as a keyed service; null for the app's
    /// unkeyed IChatClient.
    /// </summary>
    public object? ChatClientKey { get; set; }

    /// <summary>
    /// A policy users must also meet to ask, since each question costs model calls; null lets everyone who may see the
    /// dashboard ask.
    /// </summary>
    public string? Policy { get; set; }

    /// <summary>
    /// A rate limiting policy for questions, as each costs model calls; null for none. The app registers the policy with
    /// <c>AddRateLimiter</c> and calls <c>UseRateLimiter</c>.
    /// </summary>
    public string? RateLimitPolicy { get; set; }

    /// <summary>How many tool calls one answer may make before the assistant has to answer with what it has.</summary>
    public int MaxToolCalls { get; set; } = 12;

    /// <summary>
    /// The most tokens the model may write in one answer; null leaves it to the provider, some of which stop at a few
    /// hundred (Cloudflare Workers AI's default is 256), cutting a table off midway.
    /// </summary>
    public int? MaxOutputTokens { get; set; } = 4096;
}

/// <summary>A message of the conversation the dashboard sends: the user's questions and the assistant's answers so far.</summary>
/// <param name="Role">user or assistant.</param>
internal record AssistantMessage(string Role, string Text);

internal record AskRequest(IReadOnlyList<AssistantMessage> Messages);

/// <summary>
/// What the answer stream says, one JSON object a line: <c>tool</c> when the model calls a tool, <c>tool-done</c> when it
/// has answered, <c>text</c> as the answer is written, then <c>done</c>, or <c>error</c> if the model call failed.
/// </summary>
/// <param name="Id">The tool call, numbered from 1, that a tool-done event finishes.</param>
/// <param name="Error">For tool-done, why the call was refused or failed; for error, what went wrong.</param>
/// <param name="CutOff">For done, true when the answer stopped at the model's output limit rather than its end.</param>
internal record AssistantEvent(
    string Type,
    string? Text = null,
    int? Id = null,
    string? Name = null,
    IReadOnlyDictionary<string, object?>? Arguments = null,
    string? Error = null,
    string? TraceId = null,
    bool? CutOff = null);

public static class CronlightAssistantExtensions
{
    /// <summary>
    /// Adds the dashboard's Ask area: users ask about their jobs in plain language, and the app's
    /// <see cref="IChatClient"/> answers with Cronlight's read-only MCP tools, under the asking user's permissions. Needs
    /// an IChatClient registered (any provider Microsoft.Extensions.AI supports) and <c>AddCronlight</c>; the dashboard
    /// shows the area by itself. The assistant cannot pause, resume, run or change jobs.
    /// </summary>
    public static IServiceCollection AddCronlightAssistant(this IServiceCollection services, Action<CronlightAssistantOptions>? configure = null)
    {
        services.AddOptions<CronlightAssistantOptions>();
        if (configure != null)
            services.Configure(configure);
        services.AddHttpContextAccessor();
        services.TryAddScoped<McpAccess>();
        // Once, however often it is called: a second registration would map the route twice
        services.TryAddEnumerable(ServiceDescriptor.Singleton<DashboardExtension, AssistantExtension>());
        return services;
    }
}

/// <summary>The assistant's endpoint in the dashboard's API.</summary>
internal sealed class AssistantExtension(IOptions<CronlightAssistantOptions> assistantOptions, ILoggerFactory loggers) : DashboardExtension
{
    public const string Name = "assistant";
    private const string StreamContentType = "application/x-ndjson";
    private const int MaxMessages = 40;
    private const int MaxMessageCharacters = 8_000;

    // The read-only tools, the same as MCP clients get; the action tools are left out, whatever AllowActions says
    private static readonly (Type Type, MethodInfo Method, string Name)[] Tools =
        new[] { typeof(CronlightMcpTools), typeof(CronlightMcpAnalytics) }
            .SelectMany(t => t.GetMethods().Select(m => (Type: t, Method: m, Tool: m.GetCustomAttribute<McpServerToolAttribute>())))
            .Where(x => x.Tool is { ReadOnly: true, Name: not null })
            .Select(x => (x.Type, x.Method, x.Tool!.Name!))
            .ToArray();

    // Built once: their schemas come from reflection. Each call makes its tool class from the request's services.
    private static readonly AIFunction[] Functions = Tools
        .Select(t => AIFunctionFactory.Create(t.Method,
            args => ActivatorUtilities.CreateInstance(args.Services ?? throw new InvalidOperationException("No services for the tool call."), t.Type),
            new AIFunctionFactoryOptions { Name = t.Name }))
        .ToArray();

    private readonly ILogger logger = loggers.CreateLogger("Cronlight.Assistant");

    public override string Feature => Name;

    public override void Map(RouteGroupBuilder api, CronlightOptions options)
    {
        var ask = api.MapPost("/assistant/ask", Ask)
            .WithTags("Assistant")
            .WithSummary("Answers a question about the jobs with the app's AI model and Cronlight's read-only tools, as a stream of events")
            .Produces<AssistantEvent>(contentType: StreamContentType)
            .ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        // A POST that spends the app's model calls: only from the dashboard's own pages, as its changes are
        ask.RequireDashboardRequests(("Questions cannot be asked", "Questions"));
        if (assistantOptions.Value.Policy is { } policy)
            ask.RequireAuthorization(policy);
        if (assistantOptions.Value.RateLimitPolicy is { } limit)
            ask.RequireRateLimiting(limit);
    }

    internal async Task<IResult> Ask(AskRequest request, HttpContext http, CancellationToken cancellationToken)
    {
        var o = assistantOptions.Value;
        var chat = o.ChatClientKey == null
            ? http.RequestServices.GetService<IChatClient>()
            : http.RequestServices.GetKeyedService<IChatClient>(o.ChatClientKey);
        if (chat == null)
            return Results.Problem(title: "The assistant is not set up.",
                detail: "This app has no IChatClient registered, so there is no AI model to answer with.",
                statusCode: StatusCodes.Status503ServiceUnavailable);

        var messages = request.Messages ?? [];
        if (messages.Count == 0 || messages[^1].Role != "user" || string.IsNullOrWhiteSpace(messages[^1].Text))
            return Results.Problem("Ask a question.", statusCode: StatusCodes.Status400BadRequest);
        if (messages.Count > MaxMessages || messages.Any(m => (m.Text?.Length ?? 0) > MaxMessageCharacters))
            return Results.Problem($"The conversation is too long: at most {MaxMessages} messages of {MaxMessageCharacters:N0} characters. Start a new one.",
                statusCode: StatusCodes.Status400BadRequest);
        if (messages.Any(m => m.Role is not ("user" or "assistant")))
            return Results.Problem("Messages are from the user or the assistant.", statusCode: StatusCodes.Status400BadRequest);

        var scheduler = await DashboardApi.Scheduler(http, cancellationToken);
        // Asked on the read group, whose per-scheduler check covers GETs only
        if (!await DashboardApi.CanView(http, http.RequestServices.GetRequiredService<IOptions<CronlightOptions>>().Value, scheduler.SchedulerName))
            return Results.Problem(title: "Access denied.", detail: $"You do not have access to scheduler '{scheduler.SchedulerName}'.",
                statusCode: StatusCodes.Status403Forbidden);
        var conversation = new List<ChatMessage> { new(ChatRole.System, SystemPrompt(scheduler.SchedulerName)) };
        conversation.AddRange(messages.Select(m => new ChatMessage(m.Role == "user" ? ChatRole.User : ChatRole.Assistant, m.Text)));

        // Each event goes out as it happens, past any buffering, so the page shows the tools and the answer as they come
        http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        return Results.Stream(async body =>
        {
            var events = new EventWriter(body);
            var calls = 0;
            var tools = Functions.Select(f => (AITool)new ReportedTool(f, scheduler.SchedulerName,
                events, () => Interlocked.Increment(ref calls), o.MaxToolCalls, logger)).ToList();

            // Not disposed: that would dispose the app's own client too, which serves every request. The function calling
            // around it holds nothing to release.
            var client = new ChatClientBuilder(chat)
                .UseFunctionInvocation(configure: f => f.MaximumIterationsPerRequest = o.MaxToolCalls + 1)
                .Build(http.RequestServices);
            var options = new ChatOptions { Tools = tools, MaxOutputTokens = o.MaxOutputTokens };
            ChatFinishReason? finish = null;
            try
            {
                await foreach (var update in client.GetStreamingResponseAsync(conversation, options, cancellationToken))
                {
                    if (update.Text is { Length: > 0 } text)
                        await events.Write(new AssistantEvent("text", Text: text), cancellationToken);
                    finish = update.FinishReason ?? finish;
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // The provider's message may quote its keys or endpoints: it stays in the log
                logger.LogError(ex, "The assistant's model call failed");
                await events.Write(new AssistantEvent("error", Error: "The AI model did not answer. The app's log says why, under this trace id.",
                    TraceId: http.TraceIdentifier), cancellationToken);
                return;
            }
            await events.Write(new AssistantEvent("done", CutOff: finish == ChatFinishReason.Length ? true : null), cancellationToken);
        }, StreamContentType);
    }

    /// <summary>Writes events to the answer stream one line each, one at a time, and sends each at once.</summary>
    private sealed class EventWriter(Stream body)
    {
        private readonly SemaphoreSlim one = new(1, 1);

        public async Task Write(AssistantEvent e, CancellationToken cancellationToken)
        {
            var line = JsonSerializer.SerializeToUtf8Bytes(e, Json);
            await one.WaitAsync(cancellationToken);
            try
            {
                await body.WriteAsync(line, cancellationToken);
                await body.WriteAsync("\n"u8.ToArray(), cancellationToken);
                await body.FlushAsync(cancellationToken);
            }
            finally
            {
                one.Release();
            }
        }

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };
    }

    private static string SystemPrompt(string scheduler) =>
        "You are the assistant in Cronlight, the dashboard of this app's Quartz.NET scheduled jobs. Answer the user's questions " +
        "about their jobs, schedules and runs with the tools, which see what the user may see in the dashboard. You can read but " +
        "not change anything: to pause, resume, run or edit a job, tell the user to open it from the job's link, whose panel has " +
        "those actions. The dashboard has the tabs Jobs, Running and History (each run, with its log); name no other views. " +
        $"The dashboard shows scheduler '{scheduler}', which the tools use when none is named. It is {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC now.\n\n" +
        "Which tool answers what:\n" +
        "- What failed, and why: failure_summary, then get_run_log for the newest failed run of a job when the error alone does not explain it.\n" +
        "- Health right now: status_summary and running_now.\n" +
        "- What runs next: upcoming_runs.\n" +
        "- Slower, faster, more or less reliable: run_statistics with compare, sorted as the question asks; run_timeline for trends over time.\n" +
        "- Common errors across jobs: error_patterns.\n" +
        "- A job's schedule, data or retry policy: list_jobs to find it, then get_job.\n" +
        "Call the tools you need before answering; do not ask the user for what a tool can tell you.\n\n" +
        "How to answer: lead with the answer in a sentence, then the details. Use a short list, or a table for several jobs with " +
        "figures, keeping cells short: an error once, by its first line, not again as an example. Keep it brief unless asked for more. Write Markdown without HTML or images. Link each job you name as " +
        "[name](job:GROUP/NAME), with GROUP and NAME exactly as the tools give them (plain hyphens) and URL-encoded, which " +
        "opens it in the dashboard. Name runs by their id. Do not name the tools, their arguments or their fields: say what you found. Say " +
        "which window counts cover, and that times are UTC. Do not make up jobs, runs or figures: if the tools do not say, say so.\n\n" +
        CronlightMcpExtensions.Instructions;

    /// <summary>
    /// A tool that reports each call on the answer stream as it starts and ends, and gives the model a refusal's or
    /// failure's reason instead of stopping the answer. Only Cronlight's own refusals are passed on as they are: other
    /// errors may quote the database.
    /// </summary>
    private sealed class ReportedTool(AIFunction inner, string scheduler, EventWriter events, Func<int> nextCall, int maxCalls,
        ILogger logger) : DelegatingAIFunction(inner)
    {
        // Left out, the tools would read the host's own scheduler, not the one the dashboard shows
        private readonly bool takesScheduler = inner.UnderlyingMethod?.GetParameters().Any(p => p.Name == "scheduler") ?? false;

        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            var id = nextCall();
            if (id > maxCalls)
                return "Too many tool calls for one answer: answer with what you have, and say what is missing.";
            var shown = arguments.Where(a => a.Value is not CancellationToken)
                .ToDictionary(a => a.Key, a => a.Value is JsonElement e ? (object?)e.Clone() : a.Value);
            await events.Write(new AssistantEvent("tool", Id: id, Name: Name, Arguments: shown), cancellationToken);
            if (takesScheduler && string.IsNullOrEmpty(Text(arguments.GetValueOrDefault("scheduler"))))
                arguments["scheduler"] = scheduler;
            string? error = null;
            try
            {
                return await base.InvokeCoreAsync(arguments, cancellationToken);
            }
            catch (McpException ex)
            {
                error = ex.Message;
                return $"Error: {ex.Message}";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "The assistant's tool {Tool} failed", Name);
                error = "The tool failed; the app's log says why.";
                return "Error: the tool failed; the app's log says why.";
            }
            finally
            {
                if (!cancellationToken.IsCancellationRequested)
                    await events.Write(new AssistantEvent("tool-done", Id: id, Name: Name, Error: error), cancellationToken);
            }
        }

        private static string? Text(object? value) => value switch
        {
            JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
            JsonElement => null,
            _ => value?.ToString(),
        };
    }
}
