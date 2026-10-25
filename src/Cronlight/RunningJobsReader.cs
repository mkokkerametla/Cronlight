using System.Data.Common;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Cronlight;

public record RunningJobDto(
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

public record RunningJobsDto(string Source, IReadOnlyList<RunningJobDto> Jobs);

/// <summary>
/// IScheduler.GetCurrentlyExecutingJobs only sees this node. For ADO stores the dashboard reads
/// QRTZ_FIRED_TRIGGERS instead, which covers every node in a cluster. Only the host's own scheduler's tables are
/// known, so another scheduler the host added shows this node's running jobs.
/// </summary>
internal sealed class RunningJobsReader(QuartzDataSource dataSource, CronlightSchedulers schedulers, ILogger<RunningJobsReader> logger)
{
    public async Task<RunningJobsDto> Read(IScheduler scheduler, CancellationToken cancellationToken)
    {
        var meta = await scheduler.GetMetaData(cancellationToken);

        if (meta.JobStoreSupportsPersistence && dataSource.Provider != null
            && meta.SchedulerName == (await schedulers.Default(cancellationToken)).SchedulerName)
        {
            try
            {
                return new RunningJobsDto("cluster", await ReadFiredTriggers(meta.SchedulerName, cancellationToken));
            }
            catch (Exception ex) when (ex is DbException or SchedulerException or InvalidOperationException)
            {
                logger.LogWarning(ex, "Could not read {Prefix}FIRED_TRIGGERS, falling back to this node's running jobs", dataSource.TablePrefix);
            }
        }

        var local = await scheduler.GetCurrentlyExecutingJobs(cancellationToken);
        return new RunningJobsDto("local", local.Select(c => new RunningJobDto(
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

    private async Task<IReadOnlyList<RunningJobDto>> ReadFiredTriggers(string schedulerName, CancellationToken cancellationToken)
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
