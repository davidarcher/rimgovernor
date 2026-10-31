namespace RimGovernor.Brain.Policy;

/// <summary>The medical beds a plan furnishes its hospital with: Count beds of Def. A count of 0 plans none (the patients are cared in reused or converted beds).</summary>
public sealed record HospitalBedChoice(string Def, int Count);

/// <summary>
/// The new medical capacity alternatives (task 4.2 of <c>redefine-habitat-requirements</c>): which bed def the planned hospital is
/// furnished with and how many, compared as layout alternatives by the shared ledger rather than fixed by a catalog ladder. The
/// baseline keeps the def the held plan's medical beds use (else the first candidate, <see cref="RoomFurniture.MedicalBedCandidates"/>)
/// at the patients' and goal floor's count; each deviation plans another buildable candidate def at that count, and one plans only
/// the goal's floor, the patients cared in reused or converted beds (CareCapacityRules) with no duplicate slot built for them. Each
/// is planned only through RegroupAsync, where its forecast prices the hospital's care (ForecastHospital, against the bed each
/// patient would be cared in now) against what it costs to build; the baseline keeps ties. The runtime binds the chosen pieces
/// (HospitalRules.UnbuiltMedicalBed) and never re-selects.
/// </summary>
public static class LayoutHospitalBedRules
{
    /// <summary>The regroup decision kind of a medical bed alternative (A: <see cref="MedicalBeds"/>, B: the def and count).</summary>
    public const string HospitalBedMove = "hospital_beds";

    /// <summary>The decision's A: the planned hospital's medical beds.</summary>
    public const string MedicalBeds = "medical_beds";

    /// <summary>The candidate defs compared, in preference order; the ones past it are named as skipped decisions.</summary>
    public const int MaxDefTrials = 3;

    /// <summary>The alternative's id, which also separates its trials' signatures from the baseline's.</summary>
    public static string IdOf(HospitalBedChoice c) => "medical:" + NameOf(c);

    /// <summary>The decision's B: the def and the count.</summary>
    public static string NameOf(HospitalBedChoice c) => c.Def + "x" + c.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The candidate defs a medical bed may be planned as: the catalog's (<see cref="RoomFurniture.MedicalBedCandidates"/>) that are
    /// stations the graph can place and the planning census describes as buildable now, a sleeping spot never while an operation
    /// waits on a better bed. Throws a RequiredFactException for a candidate station the census does not describe.
    /// </summary>
    public static IReadOnlyList<FurnitureBed> Candidates(StationFacts f) =>
        [.. f.Furniture.MedicalBedCandidates().Where(b => f.Catalog.Stations.ContainsKey(b.Def) && !(f.Surgical.Count > 0 && b.Def == SleepingRules.SleepingSpotDefinition))
            .Where(b => (f.BenchDefinitions.TryGetValue(b.Def, out var d) ? d : throw new RequiredFactException("bed_definition:" + b.Def)).Available)];

    /// <summary>The medical beds the patients and the goal's floor ask for: each patient who needs rest or an operation, at least the floor.</summary>
    public static int Demand(StationFacts f) => Math.Max(HospitalRules.IllPatients(f.Patients, f.Surgical).Count, f.GoalBuild.HospitalBeds);

    /// <summary>The def the held plan's medical bed pieces use; empty when it plans none.</summary>
    public static string HeldDef(LayoutPlan? plan) => PlannedPiece(plan)?.Def ?? "";

    /// <summary>
    /// The baseline: <see cref="Demand"/> beds of the held def while it is still a candidate, else of the first candidate; null
    /// when no bed is asked for. With no candidate the def is empty, which the station graph refuses.
    /// </summary>
    public static HospitalBedChoice? Baseline(StationFacts f)
    {
        var count = Demand(f);
        if (count <= 0) return null;
        var candidates = Candidates(f);
        var held = HeldDef(f.Plan);
        return new(candidates.Any(c => c.Def == held) ? held : (candidates.Count > 0 ? candidates[0].Def : ""), count);
    }

    /// <summary>The choice a facts' graph is built with: the one an alternative fixes, else the baseline; null when no bed is asked for.</summary>
    public static HospitalBedChoice? ChoiceOf(StationFacts f) => f.HospitalBeds ?? Baseline(f);

    /// <summary>
    /// The deviations compared against the baseline: each other candidate def among the first <see cref="MaxDefTrials"/> at the
    /// baseline count, then the goal's floor alone (fewer beds) when the patients ask for more; the candidates past the bound in
    /// Omitted. None when no bed is asked for.
    /// </summary>
    public static (IReadOnlyList<HospitalBedChoice> Deviations, IReadOnlyList<string> Omitted) DeviationsOf(StationFacts f)
    {
        if (Baseline(f) is not { Def.Length: > 0 } baseline) return ([], []);
        var candidates = Candidates(f).Select(c => c.Def).ToList();
        var compared = candidates.Take(MaxDefTrials).Append(baseline.Def).Distinct(StringComparer.Ordinal).ToList();
        var deviations = compared.Where(d => d != baseline.Def).Select(d => new HospitalBedChoice(d, baseline.Count)).ToList();
        if (baseline.Count > f.GoalBuild.HospitalBeds) deviations.Add(baseline with { Count = f.GoalBuild.HospitalBeds });
        return (deviations, [.. candidates.Except(compared, StringComparer.Ordinal)]);
    }

    /// <summary>The baseline input followed by one input per medical bed deviation (on the baseline hosts), each fixing its def and count.</summary>
    public static IReadOnlyList<HostedRegroupInput> AlternativesOf(StationFacts f, HostedRegroupInput baseline) =>
        [.. DeviationsOf(f).Deviations.Select(c => new HostedRegroupInput(baseline.Host with { Id = IdOf(c) },
            LayoutStationPlanRules.RegroupInputOf(f with { HospitalBeds = c }, baseline.Host)) { HospitalBeds = c })];

    /// <summary>
    /// The bed a plan's hospital is furnished with, with its def's stats: the def of its first medical bed piece (StationPurpose.SleepMed,
    /// rooms by id), no def when it plans none.
    /// Task 4.5: a vitals monitor the plan places touching that bed piece (<see cref="RoomFurniture.Monitor"/>, mustBePlacedAdjacent:
    /// eight-way) adds its statOffsets, as StatWorker adds a linked facility's CompAffectedByFacilities offsets before the room parts.
    /// Throws a RequiredFactException when the catalog's medical bed candidates
    /// (<see cref="RoomFurniture.MedicalBedCandidates"/>) do not describe that def.
    /// </summary>
    public static FurnitureBed PlannedBed(LayoutPlan? plan, IReadOnlyList<FurnitureBed> candidates, FacilityLink monitor)
    {
        if (Placed(plan) is not { } placed)
        {
            return new();
        }

        var bed = candidates.FirstOrDefault(b => b.Def == placed.Piece.Def) ?? throw new RequiredFactException("hospital_bed:" + placed.Piece.Def);
        var linked = monitor.Def.Length != 0 && placed.Room.Any(p => p.Def == monitor.Def && Touches(placed.Piece, p));
        return linked ? bed with
        {
            ImmunityGainSpeedFactor = bed.ImmunityGainSpeedFactor + monitor.ImmunityGainSpeedOffset,
            SurgerySuccessChanceFactor = bed.SurgerySuccessChanceFactor + monitor.SurgerySuccessChanceOffset,
        } : bed;
    }

    /// <summary>Whether two pieces' footprints touch or overlap eight-way (GenAdj.AdjacentTo8WayOrInside).</summary>
    private static bool Touches(InteriorPiece a, InteriorPiece b) =>
        a.Rect.X - 1 < b.Rect.X + b.Rect.Width && b.Rect.X < a.Rect.X + a.Rect.Width + 1
        && a.Rect.Z - 1 < b.Rect.Z + b.Rect.Height && b.Rect.Z < a.Rect.Z + a.Rect.Height + 1;

    private static InteriorPiece? PlannedPiece(LayoutPlan? plan) => Placed(plan)?.Piece;

    /// <summary>The plan's first medical bed piece (rooms by id) and the pieces of its room.</summary>
    private static (InteriorPiece Piece, IReadOnlyList<InteriorPiece> Room)? Placed(LayoutPlan? plan)
    {
        foreach (var (_, pieces) in (plan?.Pieces ?? new Dictionary<int, IReadOnlyList<InteriorPiece>>()).OrderBy(p => p.Key))
        {
            if (pieces.FirstOrDefault(p => PlannedRoomCredits.PurposeOf(p) == StationPurpose.SleepMed) is { } piece)
            {
                return (piece, pieces);
            }
        }

        return null;
    }
}
