using System.Data.Common;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Cronlight;

internal record RunningJobDto(
    string Name,
    string Group,
    string? JobType,
    string TriggerName,
    string TriggerGroup,
    string FireInstanceId,
    string InstanceName,
    DateTimeOffset FiredTimeUtc,
    long RunningForMs
);

/// <param name="Source">
/// cluster when read from the Quartz tables, which list every node's runs; local when only this node's are known.
/// </param>
internal record RunningJobsDto(string Source, IReadOnlyList<RunningJobDto> Jobs)
{
    public const string Cluster = "cluster";
    public const string Local = "local";
}

/// <summary>
/// IScheduler.GetCurrentlyExecutingJobs only sees this node. For ADO stores the dashboard reads
/// QRTZ_FIRED_TRIGGERS instead, which covers every node in a cluster. Another scheduler the host added is read this
/// way only when the host gave its properties to UseCronlight; otherwise this node's running jobs are shown.
/// </summary>
internal sealed class RunningJobsReader(QuartzDataSource hostDataSource, CronlightSchedulers schedulers, ILogger<RunningJobsReader> logger)
{
    public async Task<RunningJobsDto> Read(IScheduler scheduler, CancellationToken cancellationToken)
    {
        var meta = await scheduler.GetMetaData(cancellationToken);

        if (meta.JobStoreSupportsPersistence && await schedulers.DataSource(scheduler, hostDataSource, cancellationToken) is { } dataSource)
        {
            try
            {
                return new RunningJobsDto(RunningJobsDto.Cluster, await ReadFiredTriggers(dataSource, meta.SchedulerName, cancellationToken));
            }
            catch (Exception ex) when (ex is DbException or SchedulerException or InvalidOperationException)
            {
                logger.LogWarning(ex, "Could not read {Prefix}FIRED_TRIGGERS, falling back to this node's running jobs", dataSource.TablePrefix);
            }
        }

        var local = await scheduler.GetCurrentlyExecutingJobs(cancellationToken);
        return new RunningJobsDto(RunningJobsDto.Local, local.Select(c => new RunningJobDto(
            c.JobDetail.Key.Name,
            c.JobDetail.Key.Group,
            c.JobDetail.JobType.FullName,
            c.Trigger.Key.Name,
            c.Trigger.Key.Group,
            c.FireInstanceId,
            meta.SchedulerInstanceId,
            c.FireTimeUtc,
            (long)c.JobRunTime.TotalMilliseconds
        )).ToList());
    }

    private static async Task<IReadOnlyList<RunningJobDto>> ReadFiredTriggers(QuartzDataSource dataSource, string schedulerName,
        CancellationToken cancellationToken)
    {
        var prefix = dataSource.TablePrefix;
        await using var connection = await dataSource.OpenConnection(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT f.JOB_NAME, f.JOB_GROUP, j.JOB_CLASS_NAME, f.TRIGGER_NAME, f.TRIGGER_GROUP,
                   f.ENTRY_ID, f.INSTANCE_NAME, f.FIRED_TIME
            FROM {prefix}FIRED_TRIGGERS f
            LEFT JOIN {prefix}JOB_DETAILS j
              ON j.SCHED_NAME = f.SCHED_NAME AND j.JOB_NAME = f.JOB_NAME AND j.JOB_GROUP = f.JOB_GROUP
            WHERE f.SCHED_NAME = {dataSource.Parameter("schedName")} AND f.STATE = 'EXECUTING'
            ORDER BY f.FIRED_TIME
            """;
        dataSource.AddParameter(command, "schedName", schedulerName);

        var now = DateTimeOffset.UtcNow;
        var result = new List<RunningJobDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            // Quartz stores fire times as UTC ticks
            var fired = new DateTimeOffset(Convert.ToInt64(reader.GetValue(7)), TimeSpan.Zero);
            var jobClass = reader.IsDBNull(2) ? null : reader.GetString(2);
            result.Add(new RunningJobDto(
                reader.GetString(0),
                reader.GetString(1),
                jobClass?.Split(',')[0],
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                fired,
                (long)(now - fired).TotalMilliseconds
            ));
        }
        return result;
    }
}

/// <summary>
/// What a run going on now has logged so far, kept by Cronlight.History's run log capture in its
/// <see cref="Quartz.IJobExecutionContext"/>, so the dashboard can show it before the run ends.
/// </summary>
internal abstract class RunningLog
{
    public const string ContextKey = "Cronlight.RunLog";

    /// <summary>The entries so far, sensitive values masked.</summary>
    public abstract IReadOnlyList<JobRunLogEntry> Read();
}

/// <summary>
/// A job wrapped by the run log capture: the run's <see cref="Quartz.IJobExecutionContext.JobInstance"/> from before it
/// starts, so a run listed as going on is known to keep a log even before its log is made.
/// </summary>
internal interface IKeepsRunLog
{
    /// <summary>The run's log; null until the run has started.</summary>
    RunningLog? Log { get; }
}
