using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.Extensions.Logging;
using Quartz;
using Quartz.Impl.Matchers;

namespace Cronlight;

/// <summary>What the job list filters, counts and sorts on, for one job.</summary>
/// <param name="Error">
/// A trigger of the job is in Quartz's Error state: Quartz could not create the job to run it, and the job does not run
/// again until it is resumed.
/// </param>
internal sealed record JobIndexEntry(string Group, string Name, bool Loadable, string TypeLabel, bool Paused, bool Error = false)
{
    public const string UnloadableLabel = "Unloadable";

    /// <summary>The same rule as the dashboard's status badge.</summary>
    public string Status => !Loadable ? "Unloadable" : Error ? "Error" : Paused ? "Paused" : "Active";

    public JobKey Key => new(Name, Group);
}

/// <summary>
/// Reads every job's type and trigger states without loading each job. For ADO stores that is two queries on
/// the Quartz tables, however many jobs there are, so the dashboard can search, filter, count and page on
/// the server and load full details only for the page it shows.
/// Another scheduler the host added is read this way only when the host gave its properties to UseCronlight;
/// otherwise through its API.
/// </summary>
internal sealed class JobIndexReader(QuartzDataSource hostDataSource, CronlightSchedulers schedulers, JobTypeCatalog catalog,
    ILogger<JobIndexReader> logger)
{
    // Quartz stores job types as "Namespace.Type, Assembly"; resolving one is costly, and the answer does not change
    private readonly ConcurrentDictionary<string, Type?> types = new();

    public async Task<IReadOnlyList<JobIndexEntry>> Read(IScheduler scheduler, CancellationToken cancellationToken)
    {
        var meta = await scheduler.GetMetaData(cancellationToken);
        if (meta.JobStoreSupportsPersistence && await schedulers.DataSource(scheduler, hostDataSource, cancellationToken) is { } dataSource)
        {
            try
            {
                return await ReadTables(dataSource, meta.SchedulerName, cancellationToken);
            }
            catch (Exception ex) when (ex is DbException or InvalidOperationException)
            {
                logger.LogWarning(ex, "Could not read the {Prefix}JOB_DETAILS and {Prefix}TRIGGERS tables, loading each job instead",
                    dataSource.TablePrefix, dataSource.TablePrefix);
            }
        }
        return await ReadScheduler(scheduler, cancellationToken);
    }

    private async Task<IReadOnlyList<JobIndexEntry>> ReadTables(QuartzDataSource dataSource, string schedulerName, CancellationToken cancellationToken)
    {
        var prefix = dataSource.TablePrefix;
        await using var connection = await dataSource.OpenConnection(cancellationToken);

        // Paused per job: it has triggers and every one is paused (PAUSED_BLOCKED is paused while its job runs).
        // In error: any one is in Quartz's Error state.
        var paused = new Dictionary<(string, string), bool>();
        var errors = new HashSet<(string, string)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                $"SELECT JOB_GROUP, JOB_NAME, TRIGGER_STATE FROM {prefix}TRIGGERS WHERE SCHED_NAME = {dataSource.Parameter("schedName")}";
            dataSource.AddParameter(command, "schedName", schedulerName);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var key = (reader.GetString(0), reader.GetString(1));
                var state = reader.GetString(2);
                paused[key] = paused.GetValueOrDefault(key, true) && state is "PAUSED" or "PAUSED_BLOCKED";
                if (state == "ERROR")
                    errors.Add(key);
            }
        }

        var result = new List<JobIndexEntry>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                $"SELECT JOB_GROUP, JOB_NAME, JOB_CLASS_NAME FROM {prefix}JOB_DETAILS WHERE SCHED_NAME = {dataSource.Parameter("schedName")}";
            dataSource.AddParameter(command, "schedName", schedulerName);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var key = (reader.GetString(0), reader.GetString(1));
                var type = types.GetOrAdd(reader.GetString(2), name => Type.GetType(name, throwOnError: false));
                result.Add(Entry(key.Item1, key.Item2, type, paused.GetValueOrDefault(key), errors.Contains(key)));
            }
        }
        return result;
    }

    /// <summary>For the in-memory store, another scheduler, or when the tables cannot be read: the scheduler's own API.</summary>
    private async Task<IReadOnlyList<JobIndexEntry>> ReadScheduler(IScheduler scheduler, CancellationToken cancellationToken)
    {
        var result = new List<JobIndexEntry>();
        foreach (var key in await scheduler.GetJobKeys(GroupMatcher<JobKey>.AnyGroup(), cancellationToken))
        {
            Type? type;
            try
            {
                if (await scheduler.GetJobDetail(key, cancellationToken) is not { } detail)
                    continue; // deleted meanwhile
                type = detail.JobType;
            }
            catch (JobPersistenceException)
            {
                type = null;
            }

            var triggers = await scheduler.GetTriggersOfJob(key, cancellationToken);
            var paused = triggers.Count > 0;
            var error = false;
            foreach (var trigger in triggers)
            {
                var state = await scheduler.GetTriggerState(trigger.Key, cancellationToken);
                paused &= state == TriggerState.Paused;
                error |= state == TriggerState.Error;
            }
            result.Add(Entry(key.Group, key.Name, type, paused, error));
        }
        return result;
    }

    // Labels match the dashboard's type column: the [CronlightJob] name, else the class name
    private JobIndexEntry Entry(string group, string name, Type? type, bool paused, bool error) =>
        new(group, name, type != null,
            type == null ? JobIndexEntry.UnloadableLabel : catalog.Describe(type)?.DisplayName ?? type.FullName!.Split('.')[^1],
            paused, error);
}
