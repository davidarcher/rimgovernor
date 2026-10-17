using System.Security.Cryptography;
using System.Text;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>The stone-shell step of the layout family (#3256): one wall upgrade bundle per flammable owned wall, under the Need owner.</summary>
public sealed partial class LayoutRounds
{
    /// <summary>At most this many flammable owned walls not yet worked are considered per step.</summary>
    private const int StoneShellCandidateBound = 8;

    internal static MethodId StoneShellMethodId(string wall)
    {
        var sum = SHA256.HashData(Encoding.UTF8.GetBytes(wall));
        return new MethodId("wall-" + Convert.ToHexString(sum, 0, 16).ToLowerInvariant());
    }

    /// <summary>
    /// The <c>layout/stone-shell</c> step: rereads the owned walls and the structure census, skips each wall whose
    /// Method (<c>wall-&lt;digest&gt;</c>) is still at work or built what stands, and admits the first candidate whose
    /// bundle fully builds. Walls already at work do not hold the others: each bundle is admitted by its claims.
    /// </summary>
    private async Task<Verdict> StoneShellStepAsync(NeedWork work, CancellationToken call)
    {
        var p = _reviewer.Player;
        var state = work.State;
        var review = await p.Journal.LoadRoundsAsync(call).ConfigureAwait(false);
        var expected = await StepScope.StepScopeAsync(_reviewer.Native, call).ConfigureAwait(false);
        if (!RoundsBuildingPlanner.RoundsBuildingBoundary(expected, state.Snapshot, review.Tick))
        {
            throw new ControlException("stone shell: the scope is another world or older than the review");
        }
        var claims = p.Journal.ConstructionClaims(state.Snapshot, expected.Tick, call);
        var read = await _reviewer.ObserveOwnedAsync(_reviewer.Native, expected, claims, call).ConfigureAwait(false);
        var projection = read.Projection;
        var owned = OwnedConstructions(claims, projection.Facts.CurrentConstruction);
        var targets = ReviewStoneShell(owned, projection.Facts.StoneStructures);
        if (!targets.TryGetValue(out var walls))
        {
            return FieldUnavailable(owned.TryGetValue(out _) ? "stone_structures" : "current_construction");
        }
        var candidates = new List<string>();
        var working = false;
        foreach (var wall in walls)
        {
            var method = StoneShellMethodId(wall);
            if (!RoundsBuildingPlanner.MethodOnce(p.Journal, work.Owner, method, call, projection.Facts.CurrentConstruction))
            {
                working = working || (p.Journal.OwnerMethodPlan(work.Owner, method, call) is { } prior
                    && Sto.StorePackage.PlanWorkOpen(p.Journal.LoadPlan(prior, call), projection.Facts.CurrentConstruction));
                continue;
            }
            candidates.Add(wall);
            if (candidates.Count == StoneShellCandidateBound)
            {
                break;
            }
        }
        if (candidates.Count == 0)
        {
            return working ? BuildingReasonExistingWork : WaitFor(Cause.MethodUsed, "stone_shell_wall");
        }
        var unstocked = false;
        foreach (var wall in candidates)
        {
            var (verdict, ok) = await ProposeAsync(work.Owner, state, read, wall, call, work.Epoch).ConfigureAwait(false);
            if (ok)
            {
                return verdict;
            }
            unstocked = unstocked || verdict == BuildingReasonStoneShellUnstocked;
        }
        // A site with no replacement material while Stonecutting is unfinished waits on that research:
        // nothing cuts the blocks a stone wall needs.
        if (unstocked)
        {
            var gate = ResearchGate([StoneShellResearch], projection.Facts.Research);
            if (gate != "")
            {
                return ResearchWait(gate);
            }
        }
        return NoSpace("stone_wall_upgrade_site");
    }

    /// <summary>
    /// Builds and admits one candidate wall's bundle. ok is false only for a structural reason to move on to
    /// the next candidate (no site, no material, unsupported backup geometry); any other outcome, admitted or
    /// refused, is this tick's final result: the first candidate whose bundle fully builds wins.
    /// </summary>
    private async Task<(Verdict Verdict, bool Ok)> ProposeAsync(Sto.IWorkOwner goal, ControlState state, Observation.RoundsReading read, string wall, CancellationToken call, CancellationToken epoch)
    {
        var none = (default(Verdict), false);
        var p = _reviewer.Player;
        var projection = read.Projection;
        var sites = await _stone!.ReadWallUpgradeSitesAsync(Boundary.Identity(state.Snapshot), wall, call).ConfigureAwait(false);
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
        var catalog = await RoundsEquipPawn.PawnCatalogAsync(_stone!, Boundary.Identity(state.Snapshot), call).ConfigureAwait(false);
        if (catalog is null)
        {
            throw new ControlException("propose: the source serves no definition catalog");
        }
        // The candidate stuffs are the stony stuffs the wall can be made from, priced by the catalog rows.
        var materials = catalog.StuffMaterials("Wall", "Stony");
        if (materials.Count == 0)
        {
            return (BuildingReasonStoneShellUnstocked, false);
        }
        var backupCount = site.BackupCells.Count;
        if (backupCount != 0 && backupCount != 3)
        {
            return none;
        }
        // Use the review's shared budget, including committed production before a pawn reserves its ingredients.
        var material = materials[0];
        var funded = false;
        if (_reviewer.Demand.Get(state.Snapshot).MaterialAvailability.TryGetValue(out var available))
        {
            foreach (var option in materials)
            {
                if (MaterialBudgetCovers(available, option.Costs.Select(c => new Pol.Amount(new Pol.Resource(c.Resource), c.Units)), backupCount + 1))
                {
                    material = option;
                    funded = true;
                    break;
                }
            }
        }
        if (!funded)
        {
            return (BuildingReasonStoneShellUnstocked, false);
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
            stock = RoundsBuildingPlanner.MergeRoundsStock(stock, preview.Stock, first);
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
        stock = RoundsBuildingPlanner.MergeRoundsStock(stock, permanentPreview.Stock, first);
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
            actual = await StepScope.StepScopeAsync(_reviewer.Native, call).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ControlException("propose: err != nil || !roundsBuildingBoundary(actual, state.Snapshot, projection.Identity.Tick)");
        }
        if (!RoundsBuildingPlanner.RoundsBuildingBoundary(actual, state.Snapshot, projection.Identity.Tick))
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
        return (reason, true);
    }

    private async Task<BuildingPreview?> PreviewWallAsync(DomainAction action, GenerationSnapshot snapshot, Cell cell, CancellationToken ct)
    {
        var preview = await _stone!.PreviewBuildingAsync(action, snapshot, ct).ConfigureAwait(false);
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
