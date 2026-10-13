using System.Security.Cryptography;
using System.Text;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// Previews each backup/replacement Wall placement and lists current wall-upgrade candidate sites. General
/// colony facts (owned construction, the Upkeep stone-structure census, map bounds) come from the shared
/// Rounder.Native read instead, the same split RoundsFieldPlanner uses between its own FieldNative and the
/// reviewer's Observation.IRoundsSource.
/// </summary>
public interface IRoundsStoneShellSource
{
    (WallUpgradeSites Sites, BridgeResult Result) ReadWallUpgradeSites(CancellationToken ct, C.Identity identity, string wall);
    (BuildingPreview Preview, BridgeResult Result) PreviewBuilding(CancellationToken ct, DomainAction action, GenerationSnapshot snapshot);
}

public sealed class RoundsStoneShellResult
{
    public Verdict Verdict { get; init; } = new();
    public PlanId Plan { get; init; }
}

public sealed class RoundsStoneShellPlanner
{
    /// <summary>At most this many undeduplicated flammable owned walls are considered per tick.</summary>
    private const int StoneShellCandidateBound = 8;

    private readonly Rounder _reviewer;
    private readonly IRoundsStoneShellSource _native;

    private RoundsStoneShellPlanner(Rounder reviewer, IRoundsStoneShellSource native)
    {
        _reviewer = reviewer;
        _native = native;
    }

    public static RoundsStoneShellPlanner Create(Rounder? reviewer, IRoundsStoneShellSource? native)
    {
        if (reviewer is null || native is null)
        {
            throw new ControlException("NewRoundsStoneShellPlanner: reviewer == nil || native == nil");
        }
        if (reviewer.Native is null)
        {
            throw new ControlException("NewRoundsStoneShellPlanner: reviewer.native == nil");
        }
        return new RoundsStoneShellPlanner(reviewer, native);
    }

    internal static MethodId StoneShellMethodId(string wall)
    {
        var sum = SHA256.HashData(Encoding.UTF8.GetBytes(wall));
        return new MethodId("wall-" + Convert.ToHexString(sum, 0, 16).ToLowerInvariant());
    }

    internal RoundsStoneShellResult Step(CancellationToken call, CancellationToken epoch, StepArbiter arbiter)
    {
        var p = _reviewer.Player;
        var state = p.Session.State();
        if (!state.Enabled)
        {
            return new RoundsStoneShellResult { Verdict = BuildingReasonDisabled };
        }
        if (!state.ObservationKnown)
        {
            throw new ControlException("step: !state.ObservationKnown");
        }
        var review = p.Journal.LoadRounds(call);
        if (!review.Enabled || review.Snapshot != state.Snapshot)
        {
            return new RoundsStoneShellResult { Verdict = BuildingReasonNoReview };
        }
        var (goal, workable) = p.Journal.Workable(call, review, MaintainStoneShell);
        if (!workable)
        {
            return new RoundsStoneShellResult { Verdict = BuildingReasonNoDeficit };
        }
        foreach (var method in goal.Methods)
        {
            var plan = p.Journal.LoadPlan(call, method.Plan);
            if (Sto.PlanState.PlanOpen(plan))
            {
                return new RoundsStoneShellResult { Verdict = BuildingReasonExistingWork };
            }
        }
        var expected = StepScope(call, _reviewer.Native);
        if (!RoundsBuildingBoundary(expected, state.Snapshot, review.Tick))
        {
            throw new ControlException("step: !roundsBuildingBoundary(expected, state.Snapshot, review.Tick)");
        }
        var claims = p.Journal.ConstructionClaims(call, state.Snapshot, expected.Tick);
        var read = _reviewer.ObserveOwned(call, _reviewer.Native, expected, claims);
        var projection = read.Projection;
        var owned = OwnedConstructions(claims, projection.Facts.CurrentConstruction);
        var targets = ReviewStoneShell(owned, projection.Facts.StoneStructures);
        if (!targets.TryGetValue(out var walls))
        {
            var field = owned.TryGetValue(out _) ? "stone_structures" : "current_construction";
            return new RoundsStoneShellResult { Verdict = FieldUnavailable(field) };
        }
        var seen = new HashSet<MethodId>();
        foreach (var method in goal.Methods)
        {
            seen.Add(method.MethodId);
        }
        var candidates = new List<string>();
        foreach (var wall in walls)
        {
            if (seen.Contains(StoneShellMethodId(wall)))
            {
                continue;
            }
            candidates.Add(wall);
            if (candidates.Count == StoneShellCandidateBound)
            {
                break;
            }
        }
        var unstocked = false;
        foreach (var wall in candidates)
        {
            var (result, ok) = Propose(call, epoch, goal, state, read, wall);
            if (ok)
            {
                return result;
            }
            unstocked = unstocked || result.Verdict == StoneShellUnstocked;
        }
        // A site with no replacement material while Stonecutting is unfinished waits on that research:
        // nothing cuts the blocks a stone wall needs.
        if (unstocked)
        {
            var gate = ResearchGate([StoneShellResearch], projection.Facts.Research);
            if (gate != "")
            {
                return new RoundsStoneShellResult { Verdict = ResearchWait(gate) };
            }
        }
        return new RoundsStoneShellResult { Verdict = NoSpace("stone_wall_upgrade_site") };
    }

    /// <summary>Whether budget covers walls walls of material.</summary>
    internal static bool StoneShellFunded(IReadOnlyDictionary<Pol.Resource, long> budget, Bridge.WallMaterial material, long walls)
    {
        foreach (var c in material.Costs)
        {
            if (budget.GetValueOrDefault(Pol.Resource.Of(c.Resource)) < c.Units * walls)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Builds and admits one candidate wall's bundle. ok is false only for a structural reason to move on to
    /// the next candidate (no site, no material, unsupported backup geometry); any other outcome, admitted or
    /// refused, is this tick's final result: the first candidate whose bundle fully builds wins.
    /// </summary>
    private (RoundsStoneShellResult Result, bool Ok) Propose(CancellationToken call, CancellationToken epoch, Sto.StandardState goal, ControlState state, Observation.RoundsReading read, string wall)
    {
        var none = (new RoundsStoneShellResult(), false);
        var p = _reviewer.Player;
        var projection = read.Projection;
        var (sites, _) = _native.ReadWallUpgradeSites(call, Boundary.Identity(state.Snapshot), wall);
        GenerationSnapshot current;
        try
        {
            current = Boundary.Context(sites.Context, state.Snapshot);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ControlException("propose: err != nil || current.Native != state.Snapshot.Native || domain.Tick(sites.Context.GetTick()) < projection");
        }
        if (current.Native != state.Snapshot.Native || new Tick(sites.Context.Tick) < projection.Identity.Tick)
        {
            throw new ControlException("propose: err != nil || current.Native != state.Snapshot.Native || domain.Tick(sites.Context.GetTick()) < projection");
        }
        if (sites.Sites.Count == 0)
        {
            return none;
        }
        var site = sites.Sites[0];
        if (!site.Eligible())
        {
            return none;
        }
        var catalog = PawnCatalog(call, _native, Boundary.Identity(state.Snapshot));
        if (catalog is null)
        {
            throw new ControlException("propose: the source serves no definition catalog");
        }
        // The candidate stuffs are the stony stuffs the wall can be made from, priced by the catalog rows.
        var materials = catalog.StuffMaterials("Wall", "Stony");
        if (materials.Count == 0)
        {
            return (new RoundsStoneShellResult { Verdict = StoneShellUnstocked }, false);
        }
        var backupCount = site.BackupCells.Count;
        if (backupCount != 0 && backupCount != 3)
        {
            return none;
        }
        // The backups and the permanent wall are funded from free stock, after construction and live bill jobs:
        // the first stone the budget covers. Without a stock census the first stone is proposed.
        var material = materials[0];
        var funded = true;
        if (MaterialBudget(projection.Facts.Resources, projection.Facts.ConstructionDeficit, projection.Facts.BillReservations, "").TryGetValue(out var budget))
        {
            funded = false;
            foreach (var option in materials)
            {
                if (StoneShellFunded(budget, option, backupCount + 1))
                {
                    material = option;
                    funded = true;
                    break;
                }
            }
        }
        if (!funded)
        {
            return (new RoundsStoneShellResult { Verdict = StoneShellUnstocked }, false);
        }
        var costs = material.Costs.Select(c => new Pol.Amount { Resource = Pol.Resource.Of(c.Resource), Count = c.Units }).ToList();
        var key = StoneShellMethodId(wall);
        var id = PlanIds.MintPlanId();
        var snapshot = state.Snapshot with { Plan = id, Revision = new PlanRevision(1) };
        var actions = new List<DomainAction>();
        var previews = new List<Pol.Preview>();
        var stock = new Pol.StockObservation { Snapshot = snapshot, Tick = projection.Identity.Tick };
        var first = true;
        var backupIds = new List<ActionId>(backupCount);
        for (var i = 0; i < site.BackupCells.Count; i++)
        {
            var cell = site.BackupCells[i];
            var building = new Building("Wall", cell, Rotation.North, material.Stuff);
            var actionId = new ActionId(string.Create(CultureInfo.InvariantCulture, $"{id}-backup-{i}"));
            var action = DomainAction.NewBuildingAction(actionId, building, BuildTier.Survive);
            var (preview, ok) = PreviewWall(call, action, snapshot, cell);
            if (!ok)
            {
                return none;
            }
            actions.Add(action);
            previews.Add(preview.Preview);
            stock = MergeRoundsStock(stock, preview.Stock, first);
            first = false;
            backupIds.Add(actionId);
        }
        var siteCell = new Cell(site.X, site.Z);
        var removal = new WallRemoval(site.TargetId, default, siteCell);
        var demolishId = new ActionId(string.Create(CultureInfo.InvariantCulture, $"{id}-demolish"));
        var demolish = DomainAction.NewWallRemovalAction(demolishId, removal);
        actions.Add(demolish);
        var permanentBuilding = new Building("Wall", siteCell, Rotation.North, material.Stuff);
        var permanentId = new ActionId(string.Create(CultureInfo.InvariantCulture, $"{id}-replace"));
        var permanent = DomainAction.NewBuildingAction(permanentId, permanentBuilding, BuildTier.Survive);
        var (permanentPreview, pok) = PreviewWall(call, permanent, snapshot, siteCell);
        if (!pok)
        {
            return none;
        }
        actions.Add(permanent);
        previews.Add(permanentPreview.Preview);
        stock = MergeRoundsStock(stock, permanentPreview.Stock, first);
        var dependencies = new List<ActionDependency> { new(permanentId, demolishId) };
        // Native lists a straight site as demolition-ready only once every backup stands; in between it is no site at all.
        foreach (var backupId in backupIds)
        {
            dependencies.Add(new ActionDependency(demolishId, backupId));
        }
        for (var i = 0; i < backupIds.Count; i++)
        {
            var backupRemoval = new WallRemoval("", backupIds[i], site.BackupCells[i]);
            var backupRemoveId = new ActionId(string.Create(CultureInfo.InvariantCulture, $"{id}-backup-remove-{i}"));
            actions.Add(DomainAction.NewWallRemovalAction(backupRemoveId, backupRemoval));
            dependencies.Add(new ActionDependency(backupRemoveId, permanentId));
            dependencies.Add(new ActionDependency(backupRemoveId, backupIds[i]));
        }
        var plan = new PlanSpec(id, new PlanRevision(1), actions, dependencies);
        // The chosen wall's per-unit cost applies to every Wall this bundle places (each backup and the
        // permanent replacement), so admission's stock check runs against the combined bundle, not a per-action share.
        for (var i = 0; i < previews.Count; i++)
        {
            previews[i] = previews[i] with { Costs = Known<IReadOnlyList<Pol.Amount>>(costs) };
        }
        p.Current(call, epoch);
        if (p.Session.State() != state)
        {
            throw new ControlException("propose: p.session.State() != state");
        }
        ScopeSnapshot actual;
        try
        {
            actual = StepScope(call, _reviewer.Native);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ControlException("propose: err != nil || !roundsBuildingBoundary(actual, state.Snapshot, projection.Identity.Tick)");
        }
        if (!RoundsBuildingBoundary(actual, state.Snapshot, projection.Identity.Tick))
        {
            throw new ControlException("propose: err != nil || !roundsBuildingBoundary(actual, state.Snapshot, projection.Identity.Tick)");
        }
        var now = _reviewer.Clock.Now();
        if (now < read.StartedAt || now - read.StartedAt > _reviewer.MaxAge)
        {
            throw new Observation.StaleException();
        }
        var decision = AdmitMethod(call, p.Journal, new Sto.BuildingMethodRequest
        {
            Owner = goal,
            Method = key,
            Plan = plan,
            Current = snapshot,
            Tick = projection.Identity.Tick,
            Bounds = Known<Pol.Rectangle>(projection.Bounds),
            Stock = stock,
            Previews = previews,
            Purpose = Pol.Purpose.Rounds,
        });
        var reason = AdmissionRefused(decision);
        if (decision.Admitted)
        {
            reason = BuildingReasonAdmitted;
        }
        return (new RoundsStoneShellResult { Verdict = reason, Plan = id }, true);
    }

    private (BuildingPreview Preview, bool Ok) PreviewWall(CancellationToken ct, DomainAction action, GenerationSnapshot snapshot, Cell cell)
    {
        var (preview, _) = _native.PreviewBuilding(ct, action, snapshot);
        var v = preview.Preview;
        var fk = v.Footprint.TryGetValue(out var footprint);
        var mk = v.MadeFromStuff.TryGetValue(out var made);
        var lk = v.CanPlace.TryGetValue(out var legal);
        var sk = v.SafeToPlace.TryGetValue(out var safe);
        if (!fk || !mk || !lk || !sk)
        {
            return (new BuildingPreview(), false);
        }
        // A Wall is a stuffed building: the preview of one that is not made from stuff is a contract
        // mismatch, not a placeable site.
        if (!made || footprint!.Count != 1 || footprint[0] != cell || !legal || !safe)
        {
            return (new BuildingPreview(), false);
        }
        return (preview, true);
    }
}
