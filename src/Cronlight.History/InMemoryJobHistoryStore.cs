namespace Cronlight.History;

/// <summary>
/// Keeps the most recent executions of this node in memory. Used with the RAM job store, or when the
/// host's database is not one the ADO store supports.
/// </summary>
internal sealed class InMemoryJobHistoryStore(int capacity) : IJobHistoryStore, IUnfinishedRuns
{
    private readonly LinkedList<JobExecutionRecord> records = new(); // newest first
    private readonly object gate = new();
    private long nextId;

    public Task Initialize(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task RecordStarted(JobExecutionRecord record, CancellationToken cancellationToken)
    {
        lock (gate)
            Add(record);
        return Task.CompletedTask;
    }

    public Task RecordFinished(JobExecutionRecord record, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var node = records.First;
            while (node != null && !(node.Value.FireInstanceId == record.FireInstanceId
                                     && node.Value.RefireCount == record.RefireCount
                                     && node.Value.SchedulerName == record.SchedulerName))
                node = node.Next;

            if (node == null)
                Add(record);
            else
                node.Value = record with { Id = node.Value.Id };
        }
        return Task.CompletedTask;
    }

    public Task<bool> FinishIfRunning(JobExecutionRecord record, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            for (var node = records.First; node != null; node = node.Next)
            {
                if (node.Value.FireInstanceId != record.FireInstanceId || node.Value.RefireCount != record.RefireCount
                    || node.Value.SchedulerName != record.SchedulerName)
                    continue;
                if (node.Value.Status != JobExecutionStatus.Running)
                    return Task.FromResult(false);
                node.Value = record with { Id = node.Value.Id };
                return Task.FromResult(true);
            }
        }
        return Task.FromResult(false);
    }

    public Task<IReadOnlyList<JobExecutionRecord>> Query(string schedulerName, JobHistoryQuery query, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            IReadOnlyList<JobExecutionRecord> result = records
                .Where(r => Matches(r, schedulerName, query) && (query.BeforeId == null || r.Id < query.BeforeId))
                .Take(Math.Clamp(query.Limit, 1, 500))
                // Logs are read one run at a time, as the database store does
                .Select(r => r with { Log = null, HasLog = r.Log != null })
                .ToList();
            return Task.FromResult(result);
        }
    }

    public Task<JobHistorySummary> Summarize(string schedulerName, DateTimeOffset sinceUtc, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var recent = records.Where(r => r.SchedulerName == schedulerName && r.FiredTimeUtc >= sinceUtc).ToList();
            int Count(JobExecutionStatus status) => recent.Count(r => r.Status == status);
            return Task.FromResult(new JobHistorySummary(sinceUtc, Count(JobExecutionStatus.Succeeded),
                Count(JobExecutionStatus.Failed), Count(JobExecutionStatus.Vetoed), Count(JobExecutionStatus.Running)));
        }
    }

    public Task<JobRunStatistics> Statistics(string schedulerName, JobHistoryQuery query, CancellationToken cancellationToken)
    {
        lock (gate)
            return Task.FromResult(HistoryStatistics.Compute(records.Where(r => Matches(r, schedulerName, query)).ToList()));
    }

    public Task<IReadOnlyList<JobRunLogEntry>?> GetLog(string schedulerName, long id, CancellationToken cancellationToken)
    {
        lock (gate)
            return Task.FromResult(records.FirstOrDefault(r => r.Id == id && r.SchedulerName == schedulerName)?.Log);
    }

    public Task<int> DeleteFiredBefore(string schedulerName, DateTimeOffset cutoffUtc, CancellationToken cancellationToken)
    {
        var removed = 0;
        lock (gate)
        {
            for (var node = records.First; node != null;)
            {
                var next = node.Next;
                if (node.Value.SchedulerName == schedulerName && node.Value.FiredTimeUtc < cutoffUtc)
                {
                    records.Remove(node);
                    removed++;
                }
                node = next;
            }
        }
        return Task.FromResult(removed);
    }

    private static bool Matches(JobExecutionRecord r, string schedulerName, JobHistoryQuery query) =>
        r.SchedulerName == schedulerName
        && (query.JobGroup == null || r.JobGroup == query.JobGroup)
        && (query.JobName == null || r.JobName == query.JobName)
        && (query.Status == null || r.Status == query.Status)
        && (query.FiredFromUtc == null || r.FiredTimeUtc >= query.FiredFromUtc)
        && (query.FiredBeforeUtc == null || r.FiredTimeUtc < query.FiredBeforeUtc);

    private void Add(JobExecutionRecord record)
    {
        records.AddFirst(record with { Id = ++nextId });
        while (records.Count > capacity)
            records.RemoveLast();
    }
}
