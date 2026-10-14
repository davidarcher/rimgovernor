namespace RimGovernor.Brain.Domain;

/// <summary>The brain's source of wall-clock time; planners and the executor read the same instant source.</summary>
public interface IClock
{
    DateTimeOffset Now();
}
