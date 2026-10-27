using Microsoft.Extensions.Logging;
using Quartz;

namespace Cronlight;

/// <param name="SecondsSinceCheckin">By the server's clock, so a browser with a wrong clock does not misread it.</param>
/// <param name="Alive">Checked in within its interval plus Quartz's check-in misfire threshold, so Quartz does not take it for failed.</param>
/// <param name="ThisNode">This process's scheduler instance; a process that never starts its scheduler is never listed.</param>
public record ClusterNodeDto(
    string InstanceId,
    DateTimeOffset LastCheckinUtc,
    long CheckinIntervalMs,
    long SecondsSinceCheckin,
    bool Alive,
    bool ThisNode
);

/// <summary>
/// The nodes of a clustered scheduler, from SCHEDULER_STATE, where each node checks in. Quartz's API describes only
/// this process's instance, which says nothing about a cluster that other processes run.
/// </summary>
internal sealed class ClusterNodesReader(QuartzDataSource hostDataSource, CronlightSchedulers schedulers, ILogger<ClusterNodesReader> logger)
{
    /// <summary>Null when the scheduler is not clustered on a database store Cronlight can read, or when reading it fails.</summary>
    public async Task<IReadOnlyList<ClusterNodeDto>?> Read(IScheduler scheduler, CancellationToken cancellationToken)
    {
        var meta = await scheduler.GetMetaData(cancellationToken);
        if (!meta.JobStoreClustered || await schedulers.DataSource(scheduler, hostDataSource, cancellationToken) is not { } dataSource)
            return null;
        try
        {
            return await ReadNodes(dataSource, meta.SchedulerName, meta.SchedulerInstanceId, cancellationToken);
        }
        // Any failure, an odd value in a row included: the node table must never break the dashboard
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not read {Prefix}SCHEDULER_STATE, so this node's state is shown instead", dataSource.TablePrefix);
            return null;
        }
    }

    private static async Task<IReadOnlyList<ClusterNodeDto>> ReadNodes(QuartzDataSource dataSource, string schedulerName,
        string instanceId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnection(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT INSTANCE_NAME, LAST_CHECKIN_TIME, CHECKIN_INTERVAL
            FROM {dataSource.TablePrefix}SCHEDULER_STATE
            WHERE SCHED_NAME = {dataSource.Parameter("schedName")}
            ORDER BY INSTANCE_NAME
            """;
        dataSource.AddParameter(command, "schedName", schedulerName);

        var threshold = dataSource.ClusterCheckinMisfireThreshold;
        var now = DateTimeOffset.UtcNow;
        var nodes = new List<ClusterNodeDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var instance = reader.GetString(0);
            // Quartz stores the check-in as UTC ticks and the interval in milliseconds
            var checkin = new DateTimeOffset(Convert.ToInt64(reader.GetValue(1)), TimeSpan.Zero);
            var interval = Convert.ToInt64(reader.GetValue(2));
            nodes.Add(new ClusterNodeDto(
                instance,
                checkin,
                interval,
                (long)Math.Max(0, (now - checkin).TotalSeconds),
                checkin + TimeSpan.FromMilliseconds(interval) + threshold >= now,
                instance == instanceId
            ));
        }
        return nodes;
    }
}
