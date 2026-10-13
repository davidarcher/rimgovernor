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
    Task<(WallUpgradeSites Sites, BridgeResult Result)> ReadWallUpgradeSitesAsync(C.Identity identity, string wall, CancellationToken ct);
    Task<(BuildingPreview Preview, BridgeResult Result)> PreviewBuildingAsync(DomainAction action, GenerationSnapshot snapshot, CancellationToken ct);
}

public sealed class RoundsStoneShellResult : IPlannerMethod
{
    public Verdict Verdict { get; init; }
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

    internal async Task<RoundsStoneShellResult> StepAsync(StepArbiter arbiter, CancellationToken call, CancellationToken epoch)
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
        var (goal, workable) = p.Journal.Workable(review, MaintainStoneShell, call);
        if (!workable || goal is null)
        {
            return new RoundsStoneShellResult { Verdict = BuildingReasonNoDeficit };
        }
        foreach (var method in goal.Methods)
        {
            var plan = p.Journal.LoadPlan(method.Plan, call);
            if (StorePackage.PlanOpen(plan))
            {
                return new RoundsStoneShellResult { Verdict = BuildingReasonExistingWork };
            }
        }
        var expected = await StepScopeAsync(_reviewer.Native, call).ConfigureAwait(false);
        if (!RoundsBuildingBoundary(expected, state.Snapshot, review.Tick))
        {
            throw new ControlException("step: !roundsBuildingBoundary(expected, state.Snapshot, review.Tick)");
        }
        var claims = p.Journal.ConstructionClaims(state.Snapshot, expected.Tick, call);
        var read = await _reviewer.ObserveOwnedAsync(_reviewer.Native, expected, claims, call).ConfigureAwait(false);
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
            var (result, ok) = await ProposeAsync(goal, state, read, wall, call, epoch).ConfigureAwait(false);
            if (ok)
            {
                return result;
            }
            unstocked = unstocked || result.Verdict == BuildingReasonStoneShellUnstocked;
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
            if (budget.GetValueOrDefault(new Pol.Resource(c.Resource)) < c.Units * walls)
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
    private async Task<(RoundsStoneShellResult Result, bool Ok)> ProposeAsync(Sto.StandardState goal, ControlState state, Observation.RoundsReading read, string wall, CancellationToken call, CancellationToken epoch)
    {
        var none = (new RoundsStoneShellResult(), false);
        var p = _reviewer.Player;
        var projection = read.Projection;
        var (sites, _) = await _native.ReadWallUpgradeSitesAsync(Boundary.Identity(state.Snapshot), wall, call).ConfigureAwait(false);
        GenerationSnapshot current;
        try
        {
            current = Boundary.Context(sites.Context, state.Snapshot);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ControlException("propose: err != nil || current.Native != state.Snapshot.Native || domain.Tick(sites.Context.GetTick()) < projection");
        }
        if (current.Native != state.Snapshot.Native || new Tick(sites.Context?.Tick ?? 0) < projection.Identity.Tick)
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
        var catalog = await PawnCatalogAsync(_native, Boundary.Identity(state.Snapshot), call).ConfigureAwait(false);
        if (catalog is null)
        {
            throw new ControlException("propose: the source serves no definition catalog");
        }
        // The candidate stuffs are the stony stuffs the wall can be made from, priced by the catalog rows.
        var materials = catalog.StuffMaterials("Wall", "Stony");
        if (materials.Count == 0)
        {
            return (new RoundsStoneShellResult { Verdict = BuildingReasonStoneShellUnstocked }, false);
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
        if (MaterialBudget(projection.Facts.Resources, projection.Facts.ConstructionDeficit, projection.Facts.BillReservations, default(PawnId)).TryGetValue(out var budget))
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
            return (new RoundsStoneShellResult { Verdict = BuildingReasonStoneShellUnstocked }, false);
        }
        var costs = material.Costs.Select(c => new Pol.Amount { Resource = new Pol.Resource(c.Resource), Count = c.Units }).ToList();
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
            var action = DomainAction.NewBuildingAction(actionId, building, ConstructionTier.Survive);
            if (await PreviewWallAsync(action, snapshot, cell, call).ConfigureAwait(false) is not { } preview)
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
        var permanent = DomainAction.NewBuildingAction(permanentId, permanentBuilding, ConstructionTier.Survive);
        if (await PreviewWallAsync(permanent, snapshot, siteCell, call).ConfigureAwait(false) is not { } permanentPreview)
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
            previews[i] = previews[i] with { Costs = Known<EquatableArray<Pol.Amount>>([.. costs]) };
        }
        p.Current(call, epoch);
        if (p.Session.State() != state)
        {
            throw new ControlException("propose: p.session.State() != state");
        }
        Observation.Identity actual;
        try
        {
            actual = await StepScopeAsync(_reviewer.Native, call).ConfigureAwait(false);
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
            throw new Observation.ObservationStaleException();
        }
        var decision = await AdmissionLog.AdmitMethodAsync(p.Journal, new Sto.BuildingMethodRequest
        {
            Owner = goal,
            Method = key,
            Plan = plan,
            Current = snapshot,
            Tick = projection.Identity.Tick,
            Bounds = Known(projection.Bounds),
            Stock = stock,
            Previews = previews,
            Purpose = Pol.Purpose.Rounds,
        }, call).ConfigureAwait(false);
        var reason = AdmissionLog.AdmissionRefused(decision);
        if (decision.Admitted)
        {
            reason = BuildingReasonAdmitted;
        }
        return (new RoundsStoneShellResult { Verdict = reason, Plan = id }, true);
    }

    private async Task<BuildingPreview?> PreviewWallAsync(DomainAction action, GenerationSnapshot snapshot, Cell cell, CancellationToken ct)
    {
        var (preview, _) = await _native.PreviewBuildingAsync(action, snapshot, ct).ConfigureAwait(false);
        var v = preview.Preview;
        var fk = v.Footprint.TryGetValue(out var footprint);
        var mk = v.MadeFromStuff.TryGetValue(out var made);
        var lk = v.CanPlace.TryGetValue(out var legal);
        var sk = v.SafeToPlace.TryGetValue(out var safe);
        if (!fk || !mk || !lk || !sk)
        {
            return null;
        }
        // A Wall is a stuffed building: the preview of one that is not made from stuff is a contract
        // mismatch, not a placeable site.
        if (!made || footprint!.Count != 1 || footprint[0] != cell || !legal || !safe)
        {
            return null;
        }
        return preview;
    }
}
