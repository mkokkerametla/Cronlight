using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quartz;
using Quartz.Listener;

namespace Cronlight;

/// <summary>
/// Quartz could not create a job to run it: its job factory or constructor threw, e.g. because a dependency is not
/// registered. Quartz then puts every trigger of the job in its Error state, and the job does not run again until it
/// is resumed. Job and trigger listeners hear nothing of it, so history, alerts and metrics are told from here.
/// </summary>
/// <param name="Id">Identifies this failure as a fire instance id identifies a run: the run never got one.</param>
/// <param name="Error">Why it did not run, starting "Not run:" as for other runs that never started.</param>
/// <param name="Stopped">Quartz has put the job's triggers in Error, so it does not run again until it is resumed.</param>
internal sealed record JobNotCreated(string Id, string SchedulerName, string InstanceName, JobKey Job, string? JobType,
    DateTimeOffset TimeUtc, string Error, bool Stopped);

/// <summary>Told about each job Quartz could not create, on the node that tried to run it.</summary>
internal interface IJobNotCreatedHandler
{
    void JobNotCreated(JobNotCreated failure);
}

/// <summary>
/// Hears one scheduler's errors and picks out the jobs it could not create. Quartz reports those only as a scheduler
/// error naming the job in its message, then sets the job's triggers to Error.
/// </summary>
/// <param name="appStopping">Cancelled when the app starts to stop, after which failures to create jobs are expected.</param>
internal sealed class JobCreationListener(IScheduler scheduler, IReadOnlyList<IJobNotCreatedHandler> handlers,
    CancellationToken appStopping, ILogger logger) : SchedulerListenerSupport
{
    // Quartz 3 says "An error occurred instantiating job to be executed. job= '{key}'" before 3.17 and "job='{key}'" since,
    // either followed by the exception's message
    private const string Prefix = "An error occurred instantiating job";
    private static readonly TimeSpan ErrorStateWait = TimeSpan.FromSeconds(5);

    public override Task SchedulerError(string msg, SchedulerException cause, CancellationToken cancellationToken = default)
    {
        // Quartz's scheduler thread calls this, and sets the triggers to Error once it returns: work it out in the background
        if (msg.StartsWith(Prefix, StringComparison.Ordinal) && !ShuttingDown())
            _ = Task.Run(() => Report(msg, cause, DateTimeOffset.UtcNow));
        return Task.CompletedTask;
    }

    private async Task Report(string message, SchedulerException cause, DateTimeOffset time)
    {
        try
        {
            var candidates = await Candidates(message);
            if (candidates.Count == 0)
            {
                logger.LogWarning("Quartz could not create a job, and Cronlight could not tell which one from: {Message}", message);
                return;
            }
            // Should two jobs fit the message, the one Quartz has just stopped is it
            var stopped = await Stopped(candidates);
            var job = stopped ?? candidates[0];
            string? type = null;
            try
            {
                type = (await scheduler.GetJobDetail(job))?.JobType.FullName;
            }
            catch (JobPersistenceException)
            {
                // Its type cannot be loaded here
            }

            var reason = Describe(cause);
            var failure = new JobNotCreated(Id(job, time), scheduler.SchedulerName, scheduler.SchedulerInstanceId, job, type, time,
                $"Not run: the job could not be created. {reason}{(reason.EndsWith('.') ? "" : ".")}" +
                (stopped != null ? " Quartz has stopped its triggers until the job is resumed." : ""),
                stopped != null);
            foreach (var handler in handlers)
            {
                try
                {
                    handler.JobNotCreated(failure);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "{Handler} failed for job {Job}, which Quartz could not create", handler.GetType().Name, job);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not report that Quartz failed to create a job: {Message}", message);
        }
    }

    /// <summary>
    /// The jobs the message may name. The key follows "job=" in quotes, as group.name, and ends at a quote, or at a comma
    /// before the exception's message. Names can hold dots, commas and quotes themselves, so each reading is looked up.
    /// </summary>
    private async Task<List<JobKey>> Candidates(string message)
    {
        var result = new List<JobKey>();
        var at = message.IndexOf("job=", StringComparison.Ordinal);
        var start = at < 0 ? -1 : message.IndexOf('\'', at);
        if (start < 0)
            return result;
        var text = message[(start + 1)..];
        foreach (var end in Enumerable.Range(0, text.Length).Where(i => text[i] is '\'' or ',').Take(10))
        {
            var key = text[..end];
            for (var dot = key.IndexOf('.'); dot >= 0; dot = key.IndexOf('.', dot + 1))
            {
                var job = new JobKey(key[(dot + 1)..], key[..dot]);
                if (!result.Contains(job) && await scheduler.CheckExists(job))
                    result.Add(job);
            }
        }
        return result;
    }

    /// <summary>The job among these that Quartz has put in Error, waiting a little for it to do so; null when none is.</summary>
    private async Task<JobKey?> Stopped(List<JobKey> jobs)
    {
        for (var deadline = DateTime.UtcNow + ErrorStateWait; ; await Task.Delay(100))
        {
            foreach (var job in jobs)
                foreach (var trigger in await scheduler.GetTriggersOfJob(job))
                    if (await scheduler.GetTriggerState(trigger.Key) == TriggerState.Error)
                        return job;
            if (DateTime.UtcNow > deadline)
                return null;
        }
    }

    // While the app stops, a job factory whose container is being disposed fails to create jobs, and Quartz does not stop
    // them for it. Otherwise any failure counts, a timeout or a disposed service included.
    private bool ShuttingDown() => appStopping.IsCancellationRequested || scheduler.IsShutdown;

    // What the factory or constructor threw, inside Quartz's and reflection's wrapping
    private static string Describe(SchedulerException cause)
    {
        var exception = RunOutcome.Cause(cause)!;
        while (exception is TargetInvocationException { InnerException: { } inner })
            exception = RunOutcome.Cause(inner)!;
        return $"{exception.GetType().Name}: {exception.Message}";
    }

    // Short enough for the history table's entry id column with any job key
    private static string Id(JobKey job, DateTimeOffset time)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{job.Group}\n{job.Name}"));
        return $"notcreated-{time.UtcTicks}-{Convert.ToHexString(hash, 0, 8)}";
    }
}

/// <summary>Attaches <see cref="JobCreationListener"/>, when anything listens for jobs that could not be created.</summary>
internal sealed class JobCreationAttachment(IEnumerable<IJobNotCreatedHandler> handlers, IServiceProvider services,
    ILogger<JobCreationAttachment> logger) : ISchedulerAttachment
{
    public Task Attach(IScheduler scheduler, CancellationToken cancellationToken)
    {
        var all = handlers.ToList();
        var appStopping = services.GetService<IHostApplicationLifetime>()?.ApplicationStopping ?? CancellationToken.None;
        if (all.Count > 0 && scheduler.ListenerManager.GetSchedulerListeners().All(l => l is not JobCreationListener))
            scheduler.ListenerManager.AddSchedulerListener(new JobCreationListener(scheduler, all, appStopping, logger));
        return Task.CompletedTask;
    }
}

internal static class JobCreationExtensions
{
    /// <summary>Has <typeparamref name="T"/>, a singleton registered on its own, told about jobs Quartz could not create.</summary>
    public static IServiceCollection AddJobNotCreatedHandler<T>(this IServiceCollection services) where T : class, IJobNotCreatedHandler
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IJobNotCreatedHandler, T>(provider => provider.GetRequiredService<T>()));
        return services.AddSchedulerAttachment<JobCreationAttachment>();
    }
}
