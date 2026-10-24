namespace RimGovernor.Brain.Observation;

/// <summary>The native clearance census read.</summary>
public interface IClearanceSource
{
    /// <summary>
    /// The clearance census for <paramref name="expected"/>; non-empty <paramref name="ground"/> widens it to the
    /// player's buildings (Player rows) and constructed floors on those rectangles. Throws when the native cannot
    /// read it, and <see cref="ObservationChangedException"/> when the reply answers another colony context.
    /// </summary>
    Task<O.ClearanceTargetsSnapshot> ReadClearanceAsync(Identity expected, bool includeSalvage, IReadOnlyList<Rectangle> ground, CancellationToken cancellationToken);
}

/// <summary>The planner's clearance work items from the native census rows.</summary>
public static class ClearanceReads
{
    /// <summary>The units one hauler carries per trip when pricing a salvage yield; policy, not a native fact.</summary>
    internal const long SalvageUnitsPerTrip = 75;

    /// <summary>The planner's work items for the census rows, in census order.</summary>
    public static IReadOnlyList<ClearanceTarget> Targets(O.ClearanceTargetsSnapshot census) =>
        [.. census.Targets.Select(Target)];

    /// <summary>The planner's work item for one census row: the class by its wire name, salvage priced as a source candidate.</summary>
    public static ClearanceTarget Target(O.ClearanceTarget row)
    {
        var target = new ClearanceTarget
        {
            EntityId = row.EntityId,
            DefName = row.DefName,
            Class = Wire.Name(row.Class),
            Minimum = row.Minimum,
            Maximum = row.Maximum,
            Deconstructible = true,
            AncientDanger = row.AncientDanger,
            RoofBlocker = row.RoofBlocker ?? "",
            Designated = row.Designated,
            Player = row.Player,
            EnclosesRoom = row.EnclosesRoom,
        };
        if (row.Salvage is O.ClearanceSalvage.Computed computed)
        {
            return target with { Salvage = Salvage(row.EntityId, computed.Evidence) };
        }
        // A skipped row was not computed, which is not the same as no salvage; NotRequested carries neither.
        return target with { SalvageSkipped = row.Salvage is O.ClearanceSalvage.Skipped };
    }

    private static SalvageEvidence Salvage(string id, O.SalvageEvidence s)
    {
        var yields = new List<CandidateYield>(s.Yields.Count);
        foreach (var y in s.Yields)
        {
            yields.Add(CandidateRules.SourceYield(new ResourceKey { Def = new Resource(y.DefName) }, y.Count, y.UnitValue, new YieldStorage.Accepts(y.StorageHeadroom)));
        }
        var candidate = CandidateRules.SourceCandidate(CandidateKind.Salvage, id, s.Labor, new CandidatePath.Walk(s.PathLength), true, SalvageUnitsPerTrip, [.. yields]);
        return new SalvageEvidence { Safe = s.Safe, Candidate = candidate };
    }
}
