namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// What judging a breach needs beyond the shrine census: the colonists' combat facts, the built traps around
/// the breach wall and the emergency census. A native that lacks any of them leaves every shrine's readiness
/// unknown.
/// </summary>
public interface IShrineReadinessNative
{
    (ObservationsPb.ListPawnsReply Reply, BridgeResult Result) ReadCombatPawns(CancellationToken ct, CommonPb.Identity identity, IReadOnlyList<string> pawns);
    (Bridge.DefenseSite Site, BridgeResult Result) ReadDefenseSite(CancellationToken ct, CommonPb.Identity identity, Bridge.CellRect region);
    (Bridge.EmergencyObservation Observed, BridgeResult Result) ReadEmergency(CancellationToken ct, CommonPb.Identity identity);
}

/// <summary>One shrine's breach judgement with the reading it was made from.</summary>
public sealed record ShrineReadinessReport
{
    public string Shrine { get; init; } = "";
    public Pol.ShrineReadiness Readiness { get; init; } = new();

    /// <summary>
    /// The unfogged, walkable, unbuilt cells of the trap window read around the chosen wall (the breach
    /// squad's candidate positions, #458); empty for a shrine the window was not read for.
    /// </summary>
    public IReadOnlyList<Cell> Standing { get; init; } = [];

    /// <summary>The built spike traps in that window.</summary>
    public IReadOnlyList<Cell> Traps { get; init; } = [];
}

internal static partial class BuildingRuntimePackage
{
    /// <summary>The half-width of the defense-site window read around a breach wall's outside cell: one policy trap radius plus a cell.</summary>
    internal const int ShrineTrapWindow = 13;

    /// <summary>The journal's reason under a native that cannot read combat pawns, the defense site or the emergency census.</summary>
    public const string ShrineHoldReadinessUnknown = "readiness_unknown";

    /// <summary>
    /// Whether the emergency census lists a threat the colony owes a fight, on the emergency decision's own
    /// rule (ThreatHolds): a live, standing, discovered hostile, hunting predator or hostile building that is
    /// not distant. The census also carries watch rows -- a wild boar fifteen cells out, a downed animal, an
    /// ignored hunt -- and a sealed shrine's own undiscovered guard, none of which a breach waits on.
    /// </summary>
    internal static bool EmergencyActive(Pol.EmergencyFacts facts)
    {
        foreach (var threat in facts.Threats)
        {
            if (ThreatHolds(threat))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Judges every sealed shrine that shows a breach wall. The costly reads happen once and only when such a
    /// shrine exists; shrines with nothing to breach are judged from the census alone. raidPoints is the
    /// colony census's reading; the storyteller is not observed, so the Peaceful softening stays off until it
    /// is. A null colonists list takes the emergency census's colonists.
    /// </summary>
    internal static List<ShrineReadinessReport> ShrineReadiness(CancellationToken ct, IShrineReadinessNative native, CommonPb.Identity identity, IReadOnlyList<Pol.AncientShrine> shrines, List<string>? colonists, Fact<double> raidPoints, Cell center, Pol.Bounds bounds)
    {
        var output = new List<ShrineReadinessReport>(shrines.Count);
        var needsReads = false;
        foreach (var shrine in shrines)
        {
            if (shrine.Sealed && shrine.BreachWalls.Count > 0)
            {
                needsReads = true;
            }
        }
        IReadOnlyList<Pol.ShrineDefenderFacts> squad = [];
        var emergency = false;
        if (needsReads)
        {
            var (observed, _) = native.ReadEmergency(ct, identity);
            emergency = EmergencyActive(observed.Facts);
            if (colonists is null)
            {
                colonists = [.. observed.Facts.Colonists.Select(p => p.Id.Value)];
            }
            squad = ShrineSquad(ct, native, identity, colonists, null);
        }
        foreach (var shrine in shrines)
        {
            var request = new Pol.ShrineReadinessRequest { Shrine = shrine, RaidPoints = raidPoints, Peaceful = Unknown<bool>(), Squad = squad, Center = center, Emergency = emergency };
            var standing = new List<Cell>();
            var traps = new List<Cell>();
            if (shrine.Sealed && shrine.BreachWalls.Count > 0)
            {
                // The wall the policy will choose is the one nearest the centre; judge it first so the trap window is read around it.
                var wall = ShrineBreachReadiness(new Pol.ShrineReadinessRequest { Shrine = shrine, Center = center }).Wall;
                var minX = Math.Max(0, wall.Outside.X - ShrineTrapWindow);
                var minZ = Math.Max(0, wall.Outside.Z - ShrineTrapWindow);
                var maxX = wall.Outside.X + ShrineTrapWindow;
                var maxZ = wall.Outside.Z + ShrineTrapWindow;
                if (bounds.Width > 0 && maxX > bounds.Width - 1)
                {
                    maxX = bounds.Width - 1;
                }
                if (bounds.Height > 0 && maxZ > bounds.Height - 1)
                {
                    maxZ = bounds.Height - 1;
                }
                var region = new Bridge.CellRect(new Cell(minX, minZ), new Cell(maxX, maxZ));
                var (site, _) = native.ReadDefenseSite(ct, identity, region);
                foreach (var cell in site.Cells)
                {
                    if (cell.Fogged)
                    {
                    }
                    else if (cell.PlayerOwned && cell.EdificeDefName == DefenseDefinitions.Trap)
                    {
                        traps.Add(cell.Cell);
                    }
                    else if (cell.Walkable && cell.EdificeDefName == "")
                    {
                        standing.Add(cell.Cell);
                    }
                }
                request = request with { Traps = traps };
            }
            SnapshotPackage.NoteShrineReadiness(ct, request);
            output.Add(new ShrineReadinessReport { Shrine = shrine.Id, Standing = standing, Traps = traps, Readiness = ShrineBreachReadiness(request) });
        }
        ct.ThrowIfCancellationRequested();
        return output;
    }

    /// <summary>
    /// Judges every shrine the review's census lists for the journal: the readiness reason, guards_alive after
    /// a breach, or ready with the chosen wall, then one row per casket naming its CasketDecisionUnder so a
    /// sealed filled casket is not silence, and one per released occupant naming its OccupantDecision. A native
    /// without the readiness reads leaves every shrine row readiness_unknown; an unknown census leaves no rows.
    /// </summary>
    internal static (List<Pol.ShrineHold> Holds, Pol.ShrinePolicy Policy) RoundsShrineHolds(CancellationToken ct, object native, GenerationSnapshot snapshot, Observation.ColonyProjection projection)
    {
        if (!projection.Facts.Upkeep.Shrines.TryGetValue(out var shrines) || shrines.Count == 0)
        {
            return ([], new Pol.ShrinePolicy());
        }
        var shrinePolicy = new Pol.ShrinePolicy();
        var custody = JoinerCapacity(projection.Facts.JoinerCapacity());
        var reads = native as IShrineReadinessNative;
        var planned = projection.Center().TryGetValue(out var center);
        if (reads is null || !planned)
        {
            var unknown = new List<Pol.ShrineHold>(shrines.Count);
            foreach (var shrine in shrines)
            {
                unknown.Add(new Pol.ShrineHold { Shrine = shrine.Id, Reason = ShrineHoldReadinessUnknown });
                unknown.AddRange(CasketHolds(shrine, shrinePolicy, custody));
            }
            return (unknown, shrinePolicy);
        }
        var reports = ShrineReadiness(ct, reads, BoundaryApi.Identity(snapshot), shrines, null, projection.Threat.RaidPoints, center, projection.Bounds);
        shrinePolicy = ShrineOpening(ct, reads, BoundaryApi.Identity(snapshot), shrines, projection);
        var output = new List<Pol.ShrineHold>(reports.Count);
        for (var i = 0; i < reports.Count; i++)
        {
            output.Add(new Pol.ShrineHold { Shrine = shrines[i].Id, Reason = ShrineHoldReason(shrines[i], reports[i].Readiness), Wall = reports[i].Readiness.Wall.EntityId });
            output.AddRange(CasketHolds(shrines[i], shrinePolicy, custody));
        }
        return (output, shrinePolicy);
    }

    /// <summary>
    /// Names each casket decision under the opening policy and, after an opening, each released occupant
    /// decision; custody is JoinerCapacity: an unknown reading captures nobody.
    /// </summary>
    internal static List<Pol.ShrineHold> CasketHolds(Pol.AncientShrine shrine, Pol.ShrinePolicy shrinePolicy, Fact<bool> custody)
    {
        var output = new List<Pol.ShrineHold>(shrine.Caskets.Count + shrine.Occupants.Count);
        foreach (var casket in shrine.Caskets)
        {
            output.Add(new Pol.ShrineHold { Shrine = shrine.Id, Reason = CasketDecisionUnder(casket, shrine, shrinePolicy), Casket = casket.EntityId });
        }
        custody.TryGetValue(out var room);
        foreach (var occupant in shrine.Occupants)
        {
            output.Add(new Pol.ShrineHold { Shrine = shrine.Id, Reason = OccupantDecision(occupant, room), Occupant = occupant.EntityId });
        }
        return output;
    }

    /// <summary>
    /// Reads the combat facts of the named colonists (every colonist of the emergency census when null) as
    /// shrine defenders: the breach squad and the melee lock staff from it.
    /// </summary>
    internal static List<Pol.ShrineDefenderFacts> ShrineSquad(CancellationToken ct, IShrineReadinessNative native, CommonPb.Identity identity, List<string>? colonists, IReadOnlySet<Pol.PawnId>? needed)
    {
        if (colonists is null)
        {
            var (observed, _) = native.ReadEmergency(ct, identity);
            colonists = [.. observed.Facts.Colonists.Select(p => p.Id.Value)];
        }
        var (pawns, _) = native.ReadCombatPawns(ct, identity, colonists);
        var arms = ReadArmament(ct, native, identity);
        var squad = new List<Pol.ShrineDefenderFacts>();
        foreach (var row in pawns.Observed?.Pawns ?? [])
        {
            if (row is null || row.Pawn is null)
            {
                continue;
            }
            var ranged = RangedWeaponEquipped(row.Equipment, arms);
            var facts = new Pol.ShrineDefenderFacts { SquadDefenderFacts = SquadDefenderFacts(row, needed ?? new HashSet<Pol.PawnId>(), ranged) };
            var reach = arms.PrimaryRange(row.Equipment);
            if (reach > 0)
            {
                facts = facts with { WeaponRange = Known<double>(reach) };
            }
            squad.Add(facts);
        }
        return squad;
    }

    /// <summary>
    /// Judges the opening gate for every shrine with filled caskets owed an opening: the squad, the emergency
    /// census and the review's custody, medicine, doctor and raid-point readings. A shrine it judges not ready
    /// keeps its caskets sealed under the hold reason.
    /// </summary>
    internal static Pol.ShrinePolicy ShrineOpening(CancellationToken ct, IShrineReadinessNative native, CommonPb.Identity identity, IReadOnlyList<Pol.AncientShrine> shrines, Observation.ColonyProjection projection)
    {
        var output = new Pol.ShrinePolicy { Opening = new Dictionary<string, string>(StringComparer.Ordinal) };
        var every = new Pol.ShrinePolicy { Opening = new Dictionary<string, string>(StringComparer.Ordinal) };
        foreach (var shrine in shrines)
        {
            every.Opening[shrine.Id] = CasketOpen;
        }
        var targets = ShrineOpenTargets(shrines, every);
        if (targets.Count == 0)
        {
            return output;
        }
        var (observed, _) = native.ReadEmergency(ct, identity);
        var colonists = new List<string>();
        var emergency = false;
        foreach (var pawn in observed.Facts.Colonists)
        {
            colonists.Add(pawn.Id.Value);
            pawn.Downed.TryGetValue(out var downed);
            pawn.Bleeding.TryGetValue(out var bleeding);
            emergency = emergency || downed || bleeding;
        }
        var squad = ShrineSquad(ct, native, identity, colonists, null);
        var facts = projection.Facts;
        var medicine = Unknown<long>();
        if (facts.MedicalReserve.Items.TryGetValue(out var items))
        {
            long n = 0;
            foreach (var item in items)
            {
                if (!item.Forbidden)
                {
                    n += item.Count;
                }
            }
            medicine = Known<long>(n);
        }
        var doctors = Unknown<int>();
        if (facts.WorkRoster.TryGetValue(out var roster))
        {
            var n = 0;
            foreach (var row in roster)
            {
                if (row.Work == Pol.WorkType.Doctor)
                {
                    n = row.Capable;
                }
            }
            doctors = Known<int>(n);
        }
        var custody = JoinerCapacity(facts.JoinerCapacity());
        foreach (var (id, caskets) in targets)
        {
            output.Opening[id] = ShrineOpenReadiness(new Pol.ShrineOpenRequest { Caskets = caskets, Squad = squad, CustodyRoom = custody, Medicine = medicine, Doctors = doctors, Emergency = emergency, Combat = EmergencyActive(observed.Facts), RaidPoints = projection.Threat.RaidPoints });
        }
        ct.ThrowIfCancellationRequested();
        return output;
    }

    /// <summary>Reads back the review's opening decisions: a shrine with a casket decided open is owed its opening.</summary>
    internal static Pol.ShrinePolicy ShrineOpeningFromHolds(IEnumerable<Pol.ShrineHold> holds)
    {
        var output = new Pol.ShrinePolicy { Opening = new Dictionary<string, string>(StringComparer.Ordinal) };
        foreach (var hold in holds)
        {
            if (hold.Casket != "" && hold.Reason == CasketOpen)
            {
                output.Opening[hold.Shrine] = CasketOpen;
            }
        }
        return output;
    }
}
