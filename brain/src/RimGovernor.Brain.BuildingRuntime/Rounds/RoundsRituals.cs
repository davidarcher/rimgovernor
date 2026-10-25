using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class Rounder
{
    /// <summary>Whether the colony is calm enough for a ritual or a gathering: no hostile and no patient; unknown when the emergency census is unread.</summary>
    internal static bool RoundsCalm(RoundsReading reading, GenerationSnapshot snapshot)
    {
        var tick = reading.Reading.Identity.Tick;
        var emergency = EmergencySnapshot.New(snapshot, tick, reading.Emergency);
        var (hostiles, patients) = EmergencyNeedsRules.EmergencyNeeds(emergency, snapshot, tick);
        return RitualRules.RitualCalm(hostiles, patients);
    }
}
