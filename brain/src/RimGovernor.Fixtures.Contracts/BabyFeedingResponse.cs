namespace RimGovernor.Fixtures.Contracts;

/// <summary>Read-only evidence for one staged native feeding bout under governor control.</summary>
public sealed record BabyFeedingResponseRead
{
    public bool Success { get; init; }
    public string Reason { get; init; } = "";
    public string Baby { get; init; } = "";
    public string Carer { get; init; } = "";
    public string CompetingMethod { get; init; } = "";
    public bool HungerStaged { get; init; }
    public bool Released { get; init; }
    public bool FeedingSeen { get; init; }
    public bool Intake { get; init; }
    public bool ReturnSeen { get; init; }
    public bool FeedingProtected { get; init; }
    public bool ReturnProtected { get; init; }
    public bool Finished { get; init; }
    public bool ReassignedDuringCare { get; init; }
    public double Food { get; init; }
    public double Malnutrition { get; init; }
    public bool Dead { get; init; }
}
