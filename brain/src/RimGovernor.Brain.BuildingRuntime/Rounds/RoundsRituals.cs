namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class Rounder
{
    /// <summary>Whether the colony is calm enough for a ritual or a gathering: no hostile and no patient; unknown when the emergency census is unread.</summary>
    internal static Fact<bool> RoundsCalm(RoundsReading reading, GenerationSnapshot snapshot)
    {
        var tick = reading.Projection.Identity.Tick;
        try
        {
            var emergency = EmergencySnapshot.New(snapshot, tick, reading.Emergency);
            var (hostiles, patients) = PolicyFunctions.EmergencyNeeds(emergency, snapshot, tick);
            return RitualRules.RitualCalm(hostiles, patients);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return Unknown<bool>();
        }
    }
}
