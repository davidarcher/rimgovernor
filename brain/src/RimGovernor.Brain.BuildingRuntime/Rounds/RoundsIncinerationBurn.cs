namespace RimGovernor.Brain.BuildingRuntime;

internal static partial class BuildingRuntimePackage
{
    /// <summary>How many cells of the incinerator interior hold stored items.</summary>
    internal static int IncineratorStored(ColonyProjection facts, Rectangle interior)
    {
        var inside = new HashSet<Cell>(PolicyFunctions.RectangleCells(interior));
        var stored = 0;
        foreach (var cell in facts.Cells)
        {
            if (inside.Contains(cell.Cell) && cell.StorageEmpty.TryGetValue(out var empty) && !empty)
            {
                stored++;
            }
        }

        return stored;
    }

    /// <summary>Whether a fire is burning; an unread fire census counts as burning.</summary>
    internal static bool FireBurning(ColonyProjection facts) => !facts.Facts.Upkeep.Fires.TryGetValue(out var fires) || fires.Count > 0;

    internal static IReadOnlyList<EquipCandidateWeapon> Molotovs(IReadOnlyList<EquipCandidate> targets) =>
        targets
            .Where(w => w.Definition == PolicyFunctions.MolotovDef)
            .Select(w => new EquipCandidateWeapon { Thing = w.Thing, Definition = w.Definition, Cell = w.Cell, BiocodedTo = w.BiocodedTo, Biocoded = w.Biocoded })
            .ToList();

    internal static CleanCandidateFacts CleanCandidateFactsOf(PawnId pawn, O.PawnState row)
    {
        var facts = new CleanCandidateFacts
        {
            Pawn = pawn,
            Dead = Boundary.FactBool(row.HasDead, row.Dead),
            Downed = Boundary.FactBool(row.HasDowned, row.Downed),
            Drafted = Boundary.FactBool(row.HasDrafted, row.Drafted),
            MentalState = Boundary.FactPresence(row.HasMentalState, row.Issues, "mental_state"),
        };
        if (row.Job is not null && !Boundary.IssueField(row.Job.Issues, "player_forced"))
        {
            facts = facts with { PlayerForced = Boundary.FactBool(row.Job.HasPlayerForced, row.Job.PlayerForced) };
        }

        if (row.Health is { } health && !Boundary.IssueField(health.Issues, "health"))
        {
            facts = facts with { NeedsTend = Boundary.FactBool(health.HasNeedsTend, health.NeedsTend), Bleeding = Boundary.FactBool(health.HasBleeding, health.Bleeding) };
        }

        if (row.Settings is { } settings && !Boundary.IssueField(settings.Issues, "work"))
        {
            facts = facts with { CleaningEnabled = CleaningWorkEnabled(settings.Work) };
        }

        return facts;
    }

    internal static Fact<bool> CleaningWorkEnabled(IReadOnlyList<O.WorkSetting> work)
    {
        foreach (var w in work)
        {
            if (w is null || w.DefName != "Cleaning")
            {
                continue;
            }

            if (!w.HasDisabled || !w.HasPriority)
            {
                return Unknown<bool>();
            }

            return Known(!w.Disabled);
        }

        return Unknown<bool>();
    }
}

public sealed partial class RoundsIncinerationPlanner
{
    /// <summary>Burns the standing incinerator's waste or cleans its ash; not handled when neither is due.</summary>
    internal async Task<(RoundsIncinerationResult Result, bool Handled)> StageBurnAsync(ControlState state, Rounds review, StandardState goal, StepArbiter arbiter, RoundsReading reading, CancellationToken call, CancellationToken epoch)
    {
        var room = StandingIncinerator(reading.Projection);
        if (room is null)
        {
            return (default, false);
        }

        reading.Projection.Facts.Upkeep.Filth.TryGetValue(out var filth);
        var ash = PolicyFunctions.IncineratorAsh(filth, room.Region.Bounds());
        var burning = FireBurning(reading.Projection);
        var stored = IncineratorStored(reading.Projection, room.Region.Bounds());
        var cleaning = ash.Count > 0 && !burning;
        if (!cleaning && (stored < PolicyFunctions.BurnStoredCells || burning))
        {
            return (default, false);
        }

        var (rows, ok) = await ColonistRowsAsync(Native, state, review, call).ConfigureAwait(false);
        if (!ok)
        {
            return (new RoundsIncinerationResult { Verdict = WaitFor(Cause.MethodUsed, "colonist_rows") }, true);
        }

        if (cleaning)
        {
            return CleanAsh(state, goal, arbiter, ash, rows, call, epoch);
        }

        return await BurnRoomAsync(state, goal, arbiter, room, stored, rows, call, epoch).ConfigureAwait(false);
    }

    internal (RoundsIncinerationResult Result, bool Handled) CleanAsh(ControlState state, StandardState goal, StepArbiter arbiter, IReadOnlyList<UpkeepFilth> ash, IReadOnlyList<O.PawnState> rows, CancellationToken call, CancellationToken epoch)
    {
        var pawns = new List<CleanCandidateFacts>();
        foreach (var row in rows)
        {
            if (row is null || row.Pawn is null)
            {
                throw new ControlException("cleanAsh: row or its pawn is missing");
            }

            pawns.Add(CleanCandidateFactsOf(new PawnId(row.Pawn.Id), row));
        }

        var selected = PolicyFunctions.SelectClean(ash, pawns);
        var ok = selected is not null;
        var (target, pawn) = selected ?? default;
        if (ok && !arbiter.TryClaim([pawn], "clean-target:" + target.Id))
        {
            ok = false;
        }

        if (!ok)
        {
            return (new RoundsIncinerationResult { Verdict = WaitFor(Cause.MethodUsed, "clean_target") }, true);
        }

        var clean = new Clean(new Domain.PawnId(pawn.Value), target.Id, target.Cell);
        var prefix = $"burn-ash-{target.Id}-";
        var (verdict, admitted) = AdmitSubject(Reviewer.Player.Journal, prefix, StandardMethodPlans(goal.History, goal.Standard.Episode, prefix), state.Snapshot, call);
        if (!admitted)
        {
            return (new RoundsIncinerationResult { Verdict = verdict }, true);
        }

        var id = PlanIds.MintPlanId();
        var action = DomainAction.NewCleanAction(new ActionId($"{id}-0"), clean);
        return CommitBurnPlan(state, goal, NextMethodId(prefix, HistoryMethodIds(goal.History, goal.Standard.Episode)), id, [action], [], call, epoch);
    }

    internal async Task<(RoundsIncinerationResult Result, bool Handled)> BurnRoomAsync(ControlState state, StandardState goal, StepArbiter arbiter, PlannedRoom room, int stored, IReadOnlyList<O.PawnState> rows, CancellationToken call, CancellationToken epoch)
    {
        var identity = Boundary.Identity(state.Snapshot);
        var things = await FrameThingsOfAsync(Native, identity, call).ConfigureAwait(false);
        var catalog = await PawnCatalogAsync(Native, identity, call).ConfigureAwait(false) ?? throw new ControlException("burn room: no definition catalog");
        var candidates = new List<BurnCandidate>();
        foreach (var row in rows)
        {
            if (row is null || row.Pawn is null)
            {
                throw new ControlException("burnRoom: row or its pawn is missing");
            }

            var pawn = new PawnId(row.Pawn.Id);
            var arm = RoundsEquipPlanner.EquipCandidatePawnFacts(row, catalog, things);
            candidates.Add(new BurnCandidate { Arm = arm, Fire = FireSafetyPawnFactsOf(new Domain.PawnId(pawn.Value), row), Holds = PrimaryDef(row, things) == PolicyFunctions.MolotovDef });
        }

        var request = new BurnRequest { Interior = room.Region.Bounds(), Stored = stored, Pawns = [.. candidates] };
        if (Native is ILoadoutWeaponSource source)
        {
            var (bounds, _) = await source.ReadMapBoundsAsync(identity, default, call).ConfigureAwait(false);
            try
            {
                Boundary.Context(bounds.Context, state.Snapshot);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                throw new ControlException("burnRoom: bounds", e);
            }

            if (bounds.Bounds.Width <= 0 || bounds.Bounds.Height <= 0)
            {
                throw new ControlException("burnRoom: bounds");
            }

            var (read, _) = await source.ReadEquipWeaponsAsync(identity, default, new Cell(bounds.Bounds.Width - 1, bounds.Bounds.Height - 1), call).ConfigureAwait(false);
            try
            {
                Boundary.Context(read.Context, state.Snapshot);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                throw new ControlException("burnRoom: context", e);
            }

            request = request with { Molotovs = [.. Molotovs(read.Targets)] };
        }

        var (order, verdictKind) = PolicyFunctions.PlanBurn(request);
        if (verdictKind != BurnVerdict.Ready)
        {
            return (new RoundsIncinerationResult { Verdict = Refuse(Cause.NoWorker, "incinerator_" + Wire.Name(verdictKind), "") }, true);
        }

        var claims = new List<string>();
        if (order.Molotov is not null)
        {
            claims.Add("equip-weapon:" + order.Molotov.Thing);
        }

        if (!arbiter.TryClaim([new PawnId(order.Burner.Value)], [.. claims]))
        {
            return (new RoundsIncinerationResult { Verdict = WaitFor(Cause.MethodUsed, "burn_claim") }, true);
        }

        var id = PlanIds.MintPlanId();
        var actions = new List<DomainAction>();
        var dependencies = new List<ActionDependency>();
        ActionId previous = default;

        void Chain(DomainAction action)
        {
            if (!previous.IsEmpty)
            {
                dependencies.Add(new ActionDependency(action.Id, previous));
            }

            previous = action.Id;
            actions.Add(action);
        }

        if (order.Molotov is not null)
        {
            var equip = new Equip(order.Burner, order.Molotov.Thing, order.Molotov.Definition, order.Molotov.Cell);
            Chain(DomainAction.NewEquipAction(new ActionId($"{id}-equip"), equip));
        }

        var draft = new OwnedDraft(order.Burner);
        Chain(DomainAction.NewOwnedDraftAction(new ActionId($"{id}-draft"), draft));
        var ignite = new Ignite(order.Burner, order.Target);
        Chain(DomainAction.NewIgniteAction(new ActionId($"{id}-ignite"), ignite));
        return CommitBurnPlan(state, goal, NextWaveMethod(goal, "burn-wave-"), id, actions, dependencies, call, epoch);
    }

    internal (RoundsIncinerationResult Result, bool Handled) CommitBurnPlan(ControlState state, StandardState goal, MethodId method, PlanId id, IReadOnlyList<DomainAction> actions, IReadOnlyList<ActionDependency> dependencies, CancellationToken call, CancellationToken epoch)
    {
        var p = Reviewer.Player;
        var plan = new PlanSpec(id, new PlanRevision(1), actions, [.. dependencies]);
        p.Current(call, epoch);
        if (p.Session.State() != state)
        {
            throw new ControlException("commitBurnPlan: session state changed");
        }

        p.Journal.CommitMethod(goal.Standard.Id, goal.Revision, method, plan, call);
        return (new RoundsIncinerationResult { Verdict = BuildingReasonAdmitted, Plan = id }, true);
    }
}
