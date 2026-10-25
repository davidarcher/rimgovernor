namespace RimGovernor.Brain.Policy;

/// <summary>When an event occurs in a model run. NotReached also covers a run stopped at its event budget.</summary>
public abstract record ModelEventTime
{
    private protected ModelEventTime() { }
    public sealed record AtDay(double Day) : ModelEventTime;
    public sealed record NotReached : ModelEventTime;
}
