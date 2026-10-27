namespace Cronlight;

/// <summary>Optional metadata shown in the dashboard's job type picker.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class CronlightJobAttribute : Attribute
{
    public CronlightJobAttribute(string displayName) => DisplayName = displayName;

    public string DisplayName { get; }
    public string? Description { get; set; }

    /// <summary>Leaves this job type out of New job; the API refuses to create it too. Existing jobs still show.</summary>
    public bool Hidden { get; set; }
}

public enum JobParameterType
{
    String,
    Int,
    Bool,
    Url,
    Text,
    Secret
}

/// <summary>Describes a JobDataMap entry so the dashboard can render a form for it.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class JobParameterAttribute : Attribute
{
    public JobParameterAttribute(string key) => Key = key;

    public string Key { get; }
    public JobParameterType Type { get; set; } = JobParameterType.String;
    public bool Required { get; set; }
    public string? Description { get; set; }
    public string? DefaultValue { get; set; }

    /// <summary>Allowed values. The dashboard shows a dropdown and rejects anything else.</summary>
    public string[]? Choices { get; set; }
}
