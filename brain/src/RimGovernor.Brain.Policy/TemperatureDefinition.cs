namespace RimGovernor.Brain.Policy;

/// <summary>A native thermal mechanism the ordinary room placement/control path models.</summary>
public sealed record TemperatureDefinition
{
    public required string Name { get; init; }
    public required double HeatPerSecond { get; init; }
    public required double PowerW { get; init; }
    public required double MinimumC { get; init; }
    public required double MaximumC { get; init; }
    public required bool Wall { get; init; }
    public required bool Controlled { get; init; }
    public required double BuildValue { get; init; }
    public required IReadOnlyList<Resource> Fuels { get; init; }

    public bool Heats => HeatPerSecond > 0;

    public void Validate()
    {
        if (string.IsNullOrEmpty(Name) || !double.IsFinite(HeatPerSecond) || HeatPerSecond == 0
            || !double.IsFinite(PowerW) || PowerW < 0 || !double.IsFinite(MinimumC)
            || !double.IsFinite(MaximumC) || MinimumC >= MaximumC || !double.IsFinite(BuildValue) || BuildValue < 0)
            throw new RequiredFactException("thermal mechanism:" + Name);
    }
}
