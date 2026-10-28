namespace RimGovernor.Fixtures.Contracts;

/// <summary>Scoped evidence for finite forced work returning to native selection.</summary>
public sealed record OrderedWorkLifecycleRead
{
    public bool Admitted { get; init; }
    public int[] IntendedJobs { get; init; } = [];
    public int[] StartedJobs { get; init; } = [];
    public string[] Outcomes { get; init; } = [];
    public bool TargetsRemoved { get; init; }
    public bool ReturnedToNative { get; init; }
    public int? CurrentJob { get; init; }
    public int[] QueuedJobs { get; init; } = [];
    public int? ForeignJob { get; init; }
    public string JobDef { get; init; } = "";
}
