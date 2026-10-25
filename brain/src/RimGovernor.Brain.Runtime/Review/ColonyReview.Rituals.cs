using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.Runtime;

public sealed partial class ColonyReview
{
    /// <summary>Whether the colony is calm enough for a ritual or a gathering: no hostile and no patient; unknown when the emergency census is unread.</summary>
    internal static bool Calm(ReviewReading reading, GenerationSnapshot snapshot)
    {
        var tick = reading.Reading.Identity.Tick;
        var emergency = EmergencySnapshot.New(snapshot, tick, reading.Emergency);
        var (hostiles, patients) = EmergencyNeedsRules.EmergencyNeeds(emergency, snapshot, tick);
        return RitualRules.RitualCalm(hostiles, patients);
    }
}
