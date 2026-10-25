namespace RimGovernor.Brain.Runtime;

/// <summary>
/// The armory's work-ledger declarer: the declaration prices the weapon and shell demand (#3259). With no bench
/// standing its orders name a catalog recipe and no bench, and the plan hosts the station that works it (#3659).
/// </summary>
public sealed partial class ArmoryPlanner
{
    private readonly ColonyReview _reviewer;
    internal ColonyReview Reviewer => _reviewer;
    private readonly GameClient _native;

    public ArmoryPlanner(ColonyReview reviewer, GameClient native)
    {
        if (reviewer is null || native is null)
        {
            throw new ControlException("NewReviewArmoryPlanner: reviewer == nil || native == nil");
        }
        _reviewer = reviewer;
        _native = native;
    }
}

public sealed partial class ColonyReview
{
    private readonly object _equipmentMu = new();
    private (GenerationSnapshot World, bool Owed) _equipment;

    /// <summary>
    /// Notes whether the review in <paramref name="world"/> found the composed military/equipment Need unmet: the
    /// workshop ladder's research rung for equipment is owed only then. Unknown is not owed.
    /// </summary>
    internal void NoteEquipment(GenerationSnapshot world, ReviewFacts facts)
    {
        var owed = Needs.Contains(MilitaryFamily.Equipment)
            && !MilitaryFamily.EquipmentMet(new MilitaryFacts { Gear = facts.Gear, ShellsShort = facts.ShellsShort });
        lock (_equipmentMu)
        {
            _equipment = (world, owed);
        }
    }

    /// <summary>Whether the last review in <paramref name="world"/> found the equipment Need unmet.</summary>
    internal bool EquipmentOwed(GenerationSnapshot world)
    {
        lock (_equipmentMu)
        {
            return _equipment.World == world && _equipment.Owed;
        }
    }
}
