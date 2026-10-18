using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// The runtime half of the biotech family (#3261): its read (the room census with the colony's construction claims,
/// the gestation read of the mechanitors and their mechs, and the pollution Need's refused hauls) and the work steps
/// of its ready Needs, each committing under the Need owner and admitted by claims: the mech charger and the gene
/// bank are one building Method each. The pollution Need's batches are the family's pure Methods, and the mechs
/// Need's gestation bill is the work ledger's (<see cref="MechBillDeclarer"/>).
/// </summary>
public sealed partial class BiotechRounds
{
    private readonly Rounder _reviewer;
    private readonly BiotechNeeds _needs;
    private readonly RoundsBuildingPlanner? _building;

    public BiotechRounds(Rounder reviewer, BiotechNeeds needs)
    {
        _reviewer = reviewer ?? throw new ControlException("BiotechRounds: reviewer is missing");
        _needs = needs ?? throw new ControlException("BiotechRounds: needs are missing");
        if (!needs.Any)
        {
            throw new ControlException("BiotechRounds: no biotech Need is composed");
        }
        if (needs.MechCharger || needs.GeneBank)
        {
            if (reviewer.Native is not IRoundsBuildingSource source)
            {
                throw new ControlException("BiotechRounds: the native source cannot preview buildings");
            }
            _building = new RoundsBuildingPlanner { Reviewer = reviewer, Native = source };
        }
    }

    /// <summary>An optional entry woken by its charger and gene bank buildings, clear-area edits and wastepack hauls.</summary>
    public static FamilySchedule Schedule { get; } = new()
    {
        Kinds = [ActionKind.Building, ActionKind.Area, ActionKind.WastepackHaul],
    };

    /// <summary>The family's scheduler entry; each toggle derives its Need.</summary>
    public FamilyRunner Runner() => new FamilyRunner<BiotechFacts>(new BiotechFamily(_needs), ReadAsync, Schedule, WorkAsync);

    /// <summary>The mechs Need's work: none of the family's own. Its one remedy is the gestation bill the work ledger places.</summary>
    internal static readonly Verdict MechBillVerdict = Verdict.WaitFor(Cause.Facility, "mech_gestation_bill");

    /// <summary>
    /// Reads the family's facts in <paramref name="scope"/>'s world: the room census with the construction claims,
    /// then the gestation read for the mechs Need and the refused hauls for the pollution Need. Throws when the read
    /// is from another world, older than the review, or slower than the review's age bound. The immediate pass reads
    /// nothing.
    /// </summary>
    internal async Task<BiotechFacts> ReadAsync(FamilyReadScope scope, CancellationToken ct)
    {
        if (scope.Immediate)
        {
            return new BiotechFacts();
        }
        var started = _reviewer.Clock.Now();
        var journal = _reviewer.Player.Journal;
        var review = await journal.LoadRoundsAsync(ct).ConfigureAwait(false);
        var expected = await StepScope.StepScopeAsync(_reviewer.Native, ct).ConfigureAwait(false);
        if (!RoundsBuildingPlanner.RoundsBuildingBoundary(expected, scope.World, review.Tick))
        {
            throw new ControlException("biotech read: the scope is another world or older than the review");
        }
        var claims = journal.ConstructionClaims(scope.World, expected.Tick, ct);
        var reading = await _reviewer.ObserveRoomsAsync(_reviewer.Native, expected, claims, ct).ConfigureAwait(false);
        var projection = reading.Projection;
        var facts = new BiotechFacts
        {
            MechChargerOwed = projection.Facts.MechChargerOwed,
            GeneBankOwed = projection.Facts.GeneBankOwed,
            Pollution = projection.Facts.Pollution,
            PollutedCells = [.. PollutionRules.PollutedWindowCells(projection.Cells)],
        };
        if (_needs.Mechs && await Rounder.MechGestationAsync(_reviewer.Native, Boundary.Identity(scope.World), projection, ct).ConfigureAwait(false) is { } gestation)
        {
            facts = facts with { MechsOwed = MechRules.MechGestationOwed(gestation) };
        }
        if (_needs.Pollution)
        {
            facts = facts with { PollutionRefused = PollutionRefused(journal, scope.World, ct) };
        }
        var elapsed = _reviewer.Clock.Now() - started;
        if (elapsed < TimeSpan.Zero || elapsed > _reviewer.MaxAge)
        {
            throw new ControlException("biotech read: the read took too long");
        }
        return facts;
    }

    /// <summary>The wastepacks a haul of the pollution Need's unretired Methods could not store, so the next batch leaves them out.</summary>
    internal static EquatableArray<string> PollutionRefused(JournalStore journal, GenerationSnapshot world, CancellationToken ct)
    {
        var refused = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var method in journal.LoadNeed(BiotechFamily.Pollution, world, ct).Methods)
        {
            foreach (var progress in journal.LoadPlan(method.Plan, ct).Progress)
            {
                if (progress.Action.WastepackHaul is { } haul && progress.View.Stage == Stage.Unsuccessful)
                {
                    refused.Add(haul.Thing);
                }
            }
        }
        return EquatableArray.From(refused);
    }

    /// <summary>The work of a ready Need: a charger or a gene bank, the ledger's gestation bill, or none for the pollution Need's pure Methods.</summary>
    internal async Task<Verdict> WorkAsync(NeedWork work, BiotechFacts facts, CancellationToken call)
    {
        if (work.Need.Id == BiotechFamily.MechCharger && _building is not null)
        {
            return await ChargerStepAsync(work, call).ConfigureAwait(false);
        }
        if (work.Need.Id == BiotechFamily.GeneBank && _building is not null)
        {
            return await GeneBankStepAsync(work, call).ConfigureAwait(false);
        }
        if (work.Need.Id == BiotechFamily.Mechs)
        {
            return MechBillVerdict;
        }
        return Verdict.AwaitingPlan(work.Need.Id.Value, "no_method");
    }

    /// <summary>The room census of a work step in the work's world, checked against the review.</summary>
    internal static async Task<RoundsReading> StepReadingAsync(Rounder reviewer, NeedWork work, CancellationToken call)
    {
        var journal = reviewer.Player.Journal;
        var review = await journal.LoadRoundsAsync(call).ConfigureAwait(false);
        var expected = await StepScope.StepScopeAsync(reviewer.Native, call).ConfigureAwait(false);
        if (!RoundsBuildingPlanner.RoundsBuildingBoundary(expected, work.State.Snapshot, review.Tick))
        {
            throw new ControlException("biotech step: boundary moved");
        }
        var claims = journal.ConstructionClaims(work.State.Snapshot, expected.Tick, call);
        return await reviewer.ObserveRoomsAsync(reviewer.Native, expected, claims, call).ConfigureAwait(false);
    }

    /// <summary>
    /// One mech charger on free ground away from fields and living rooms and near the atomizers (MechChargerSites):
    /// one Method per charger count, <c>mech-charger-&lt;def&gt;-&lt;n&gt;</c>, so the same charger continues and is
    /// re-issued once released with its product gone. A charger blueprint or frame of a claimed plan standing is the
    /// step in flight.
    /// </summary>
    private async Task<Verdict> ChargerStepAsync(NeedWork work, CancellationToken call)
    {
        var started = _reviewer.Clock.Now();
        var read = await StepReadingAsync(_reviewer, work, call).ConfigureAwait(false);
        var f = read.Projection;
        if (!f.Facts.MechChargerOwed.TryGetValue(out var owed))
        {
            return FieldUnavailable("mech_chargers");
        }
        if (!owed)
        {
            return BuildingReasonNoDeficit;
        }
        if (!f.Biotech.TryGetValue(out var biotech))
        {
            return FieldUnavailable("biotech");
        }
        var defs = MechChargerDefinitions(ref read);
        f = read.Projection;
        if (defs.Count == 0)
        {
            return FieldUnavailable("mech_charger_definition");
        }
        if (DefinitionIntentStanding(defs, f.Facts.ConstructionClaims, f.Facts.CurrentConstruction))
        {
            return BuildingReasonExistingWork;
        }
        var definition = defs[0].Name;
        var method = new MethodId($"mech-charger-{definition}-{biotech.Chargers.Count}");
        if (!_building!.MethodOnce(work.Owner, method, call, f.Facts.CurrentConstruction))
        {
            return WaitFor(Cause.MethodUsed, "mech_charger_method");
        }
        if (!defs[0].Size.TryGetValue(out var size) || size.Width <= 0 || size.Height <= 0)
        {
            return FieldUnavailable("mech_charger_size");
        }
        var fieldZones = new Dictionary<string, bool>();
        if (f.FoodFields.TryGetValue(out var fields))
        {
            foreach (var field in fields)
            {
                fieldZones[field.Id] = true;
            }
        }
        var siteFacts = new MechChargerSiteFacts
        {
            Bounds = f.Bounds,
            Cells = f.Cells,
            FieldZones = fieldZones,
            Rooms = f.Rooms,
            Disposal = [.. biotech.Atomizers.Select(a => a.Position)],
        };
        var sites = MechRules.MechChargerSites(siteFacts, size.Width, size.Height).Select(s => s.Site);
        var verdict = await PlaceAsync(_reviewer, _building!, work, f, definition, method, size, sites, int.MaxValue, "mech_charger_preview", started, call).ConfigureAwait(false);
        return verdict ?? NoSpace("charger_cell");
    }

    /// <summary>
    /// One gene bank on free sites around the gene assemblers (GeneBankAnchor), at most GeneBankSiteTries previews:
    /// one Method per bank count, <c>gene-bank-&lt;def&gt;-&lt;n&gt;</c>, continued and re-issued as the charger's is.
    /// </summary>
    private async Task<Verdict> GeneBankStepAsync(NeedWork work, CancellationToken call)
    {
        var started = _reviewer.Clock.Now();
        var read = await StepReadingAsync(_reviewer, work, call).ConfigureAwait(false);
        var f = read.Projection;
        if (!f.Facts.GeneBankOwed.TryGetValue(out var owed))
        {
            return FieldUnavailable("gene_banks");
        }
        if (!owed)
        {
            return BuildingReasonNoDeficit;
        }
        if (!f.Biotech.TryGetValue(out var biotech))
        {
            return FieldUnavailable("biotech");
        }
        var (defs, withDefs) = GeneBankDefinitions(read);
        f = withDefs.Projection;
        if (defs.Count == 0)
        {
            return FieldUnavailable("gene_bank_definition");
        }
        if (DefinitionIntentStanding(defs, f.Facts.ConstructionClaims, f.Facts.CurrentConstruction))
        {
            return BuildingReasonExistingWork;
        }
        var definition = defs[0].Name;
        var method = new MethodId($"gene-bank-{definition}-{biotech.GeneBanks.Count}");
        if (!_building!.MethodOnce(work.Owner, method, call, f.Facts.CurrentConstruction))
        {
            return WaitFor(Cause.MethodUsed, "gene_bank_method");
        }
        if (!defs[0].Size.TryGetValue(out var size) || size.Width <= 0 || size.Height <= 0)
        {
            return FieldUnavailable("gene_bank_size");
        }
        var (anchor, planned) = GeneBankAnchor(f, biotech);
        if (!planned)
        {
            return BuildingReasonNoLayoutPlan;
        }
        var sites = FreeSitesRules.FreeSites(new FreeSiteRequest { Bounds = f.Bounds, Anchor = anchor, Cells = f.Cells, Light = f.LightTerrains }, size.Width, size.Height);
        var verdict = await PlaceAsync(_reviewer, _building!, work, f, definition, method, size, sites, GeneBankSiteTries, "gene_bank_preview", started, call).ConfigureAwait(false);
        return verdict ?? NoSpace("gene_bank_cell");
    }

    /// <summary>
    /// Previews <paramref name="definition"/> on each of the first <paramref name="tries"/> sites and admits the first
    /// whole, legal, safe and reachable one as the Need's building Method. Null when no site fits; a field-unavailable
    /// verdict when a preview was unread and none fit.
    /// </summary>
    internal static async Task<Verdict?> PlaceAsync(Rounder reviewer, RoundsBuildingPlanner planner, NeedWork work, ColonyProjection f, string definition, MethodId method, Bounds size, IEnumerable<Rectangle> sites, int tries,
        string preview, DateTimeOffset started, CancellationToken call)
    {
        var p = reviewer.Player;
        var state = work.State;
        var planId = PlanIds.MintPlanId();
        var snapshot = state.Snapshot with { Plan = planId, Revision = new PlanRevision(1) };
        var unknown = false;
        foreach (var site in sites.Take(tries))
        {
            var building = new Building(definition, InteriorRules.AnchorForRect(site, new Cell(size.Width, size.Height), Rotation.North), Rotation.North, "");
            var action = DomainAction.NewBuildingAction(new ActionId(planId.Value + "-0"), building, ConstructionTier.Expand);
            var got = await planner.Native.PreviewBuildingAsync(action, snapshot, call).ConfigureAwait(false);
            var pv = got.Preview;
            if (!pv.Footprint.TryGetValue(out var footprint) || !pv.MadeFromStuff.TryGetValue(out var made) || !pv.CanPlace.TryGetValue(out var legal)
                || !pv.SafeToPlace.TryGetValue(out var safe) || !pv.WatchCellsAccessible.TryGetValue(out var reachable))
            {
                unknown = true;
                continue;
            }
            if (made || !legal || !safe || !reachable || !FootprintIsRect(footprint!, site))
            {
                continue;
            }
            p.Current(call, work.Epoch);
            var elapsed = reviewer.Clock.Now() - started;
            if (p.Session.State() != state || elapsed < TimeSpan.Zero || elapsed > reviewer.MaxAge)
            {
                throw new ControlException("biotech step: session state changed or the read took too long");
            }
            var decision = await AdmissionLog.AdmitMethodAsync(p.Journal, new BuildingMethodRequest
            {
                Owner = work.Owner,
                Method = method,
                Plan = new PlanSpec(planId, new PlanRevision(1), [action]),
                Current = snapshot,
                Tick = f.Identity.Tick,
                Bounds = Known(f.Bounds),
                Stock = got.Stock,
                Previews = [pv],
                Purpose = Purpose.Rounds,
            }, call).ConfigureAwait(false);
            return decision.Admitted ? BuildingReasonAdmitted : AdmissionLog.AdmissionRefused(decision);
        }
        return unknown ? FieldUnavailable(preview) : null;
    }
}

/// <summary>
/// Declares the biotech/mechs Need's gestation bill to the work ledger, filed under the Need: while the Need is ready
/// (unmet, its charger precondition met), the single-count gestation bill SelectMechGestationBill picks; nothing
/// otherwise; an abstain while the review or the mechs are unread.
/// </summary>
public sealed class MechBillDeclarer : IOrderDeclarer
{
    private readonly Rounder _reviewer;
    private readonly object _native;
    private readonly BiotechFamily _family;

    /// <summary>The declarer for a composition whose biotech family derives <paramref name="needs"/>; the charger precondition holds only when it is composed.</summary>
    public MechBillDeclarer(Rounder reviewer, object native, BiotechNeeds needs)
    {
        _reviewer = reviewer ?? throw new ControlException("MechBillDeclarer: reviewer is missing");
        _native = native ?? throw new ControlException("MechBillDeclarer: native is missing");
        _family = new BiotechFamily(needs ?? throw new ControlException("MechBillDeclarer: needs are missing"));
    }

    public async Task<Declared> DeclareOrdersAsync(GenerationSnapshot snapshot, ColonyProjection projection, IReadOnlyList<GearBench> benches, CancellationToken ct) =>
        (await DeclareAsync(snapshot, projection, benches, ct).ConfigureAwait(false)).For(BiotechFamily.Mechs.FilingKey.Value);

    private async Task<Declared> DeclareAsync(GenerationSnapshot snapshot, ColonyProjection projection, IReadOnlyList<GearBench> benches, CancellationToken ct)
    {
        var review = _reviewer.Player.Journal.LoadRounds(ct);
        if (!review.Enabled || review.Snapshot != snapshot)
        {
            return WorkRules.Abstaining(Cause.UnreadReview);
        }
        MechGestation? gestation;
        try
        {
            gestation = await Rounder.MechGestationAsync(_native, Boundary.Identity(snapshot), projection, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return RoundsGearPlanner.AbstainOnRead(Cause.UnreadMechs, ex, ct);
        }
        if (gestation is null)
        {
            return WorkRules.Abstaining(Cause.UnreadMechs);
        }
        return Declare(_family, projection, gestation, benches);
    }

    /// <summary>The gestation bill while the mechs Need is ready over <paramref name="gestation"/> and the projection's charger fact, else nothing.</summary>
    internal static Declared Declare(BiotechFamily family, ColonyProjection projection, MechGestation gestation, IReadOnlyList<GearBench> benches)
    {
        var facts = new BiotechFacts { MechsOwed = MechRules.MechGestationOwed(gestation), MechChargerOwed = projection.Facts.MechChargerOwed };
        if (!family.MechsReady(facts))
        {
            return new Declared();
        }
        var (selected, gap) = MechRules.SelectMechGestationBill(Rounder.MechBenches(benches), gestation);
        return CareOrdersRules.DeclareSelection(selected, gap == default, benches);
    }
}

partial class BiotechRounds
{
    internal const int GeneBankSiteTries = 8;

    internal static IReadOnlyList<PlanningDefinition> MechChargerDefinitions(ref RoundsReading read)
    {
        if (read.Catalog is not { } catalog)
        {
            return [];
        }

        var names = catalog.MechChargers();
        read = read with { Reading = read.Reading with { Projection = read.Projection.AddDefinitions(read.Frame, catalog, names) } };
        var output = new List<PlanningDefinition>();
        foreach (var d in BiotechColonyReads.MechChargerDefs(read.Projection.Definitions))
        {
            if (d.Available.TryGetValue(out var available) && available)
            {
                output.Add(d);
            }
        }

        return output;
    }

    internal static (IReadOnlyList<PlanningDefinition> Defs, RoundsReading Read) GeneBankDefinitions(RoundsReading read)
    {
        if (read.Catalog is null)
        {
            return ([], read);
        }

        var names = read.Catalog.GeneBanks();
        read = read.WithProjection(read.Projection.AddDefinitions(read.Frame, read.Catalog, names));
        var output = new List<PlanningDefinition>();
        foreach (var d in read.Projection.Definitions)
        {
            if (d.Available.TryGetValue(out var available) && available && names.Contains(d.Name))
            {
                output.Add(d);
            }
        }

        return (output, read);
    }

    internal static (Cell Anchor, bool Found) GeneBankAnchor(ColonyProjection facts, BiotechColony biotech)
    {
        if (biotech.GeneAssemblers.Count > 0)
        {
            return (biotech.GeneAssemblers[0].Position, true);
        }

        if (biotech.GeneBanks.Count > 0)
        {
            return (biotech.GeneBanks[0].Position, true);
        }

        if (!facts.Center().TryGetValue(out var center))
        {
            return (default, false);
        }

        return LayoutProtected.RoomAnchor(facts, PlannedRole.Workshop, center) is { } anchor ? (anchor, true) : (default, false);
    }

    internal static bool FootprintIsRect(IReadOnlyList<Cell> footprint, Rectangle r)
    {
        if (footprint.Count != r.Width * r.Height)
        {
            return false;
        }

        foreach (var c in footprint)
        {
            if (c.X < r.X || c.X >= r.X + r.Width || c.Z < r.Z || c.Z >= r.Z + r.Height)
            {
                return false;
            }
        }

        return true;
    }

    internal static bool DefinitionIntentStanding(IReadOnlyList<PlanningDefinition> defs, Fact<EquatableArray<ConstructionClaim>> claims, Fact<CurrentConstruction> observed)
    {
        if (!observed.TryGetValue(out var census) || !census.Colony)
        {
            return false;
        }

        var names = defs.Select(d => d.Name).ToHashSet();
        if (!claims.TryGetValue(out var history))
        {
            return false;
        }

        foreach (var claim in history)
        {
            if (claim.Building is { } building && names.Contains(building.Definition) && ConstructionOwnershipRules.WorkOpen(building, observed) == BuildingWork.BuildingOpen)
            {
                return true;
            }
        }

        return false;
    }
}
