using Quartz;

namespace Cronlight;

/// <summary>
/// Masks a run's sensitive values in text about the run (what it logged, why it failed), as the API masks them in job
/// data. The merged data is used, since it holds the values the job reads, resolved secret references included.
/// </summary>
internal static class RunSecrets
{
    public static Func<string, string> Masker(IJobExecutionContext context, CronlightOptions options)
    {
        var secretKeys = DashboardApi.SecretKeys(context.JobDetail.JobType);
        var secrets = context.MergedJobDataMap
            .Where(kv => DashboardApi.IsSensitive(kv.Key, secretKeys, options))
            .Select(kv => kv.Value?.ToString())
            // Very short values would mask unrelated text, and are no secret worth the name
            .Where(v => v is { Length: >= 4 } && v != DashboardApi.Mask)
            .Distinct()
            .OrderByDescending(v => v!.Length)
            .ToList();
        return secrets.Count == 0
            ? text => text
            : text => secrets.Aggregate(text, (masked, secret) => masked.Replace(secret!, DashboardApi.Mask, StringComparison.Ordinal));
    }
}
