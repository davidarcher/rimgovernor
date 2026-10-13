using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace RimGovernor.Brain.BuildingRuntime;

public sealed class RoundsFieldResult
{
    public uint NativeWorkTicks { get; init; }

    public Verdict Verdict { get; init; }

    public PlanId Plan { get; init; }
}

/// <summary>The native zone preview of the field planner.</summary>
public interface IFieldNative
{
    (OP.ZonePreviewReply Reply, BridgeResult Result) PreviewZone(C.Identity identity, ZoneCreate zone, CancellationToken ct);
}

/// <summary>Opens and grows the colony's fields, retargets the grower crops and keeps the other resources' fields in step with the food plan.</summary>
public sealed class RoundsFieldPlanner
{
    internal const double FieldLampGrowthRadius = 5.8;
    internal const int FieldCandidateAttempts = 3;
    internal const int FieldBatchPatches = 6;

    private readonly Rounder _reviewer;
    private readonly IFieldNative _native;

    public RoundsFieldPlanner(Rounder reviewer, IFieldNative native)
    {
        if (reviewer is null || native is null)
        {
            throw new ControlException("NewRoundsFieldPlanner: reviewer == nil || native == nil");
        }
        _reviewer = reviewer;
        _native = native;
    }

    internal RoundsFieldResult Step(CancellationToken call, CancellationToken epoch, StepArbiter arbiter)
    {
        var p = _reviewer.Player;
        var state = p.Session.State();
        if (!state.Enabled)
        {
            return new RoundsFieldResult { Verdict = BuildingReasonDisabled };
        }
        if (!state.ObservationKnown)
        {
            throw new ControlException("step: !state.ObservationKnown");
        }
        var review = p.Journal.LoadRounds(call);
        if (!review.Enabled || review.Snapshot != state.Snapshot)
        {
            return new RoundsFieldResult { Verdict = BuildingReasonNoReview };
        }
        var (goal, workable) = p.Journal.Workable(review, PolicyApi.EnsureFoodSupply, call);
        var idle = BuildingReasonNoDeficit;
        var blocked = false;
        foreach (var method in goal.Methods)
        {
            var plan0 = p.Journal.LoadPlan(method.Plan, call);
            blocked = blocked || FieldBlockingWork(method.Method, plan0.Progress);
        }
        var plans = p.Journal.LoadPlans(call);
        var playerPlans = p.Journal.PlayerPlans(PlayerWorld(state.Snapshot), call);
        var crops = _reviewer.SowableCrops(call, state.Snapshot);
        var definitions = RoundsProjectDefinitions(plans, state.Snapshot, playerPlans).Concat(crops).ToList();
        definitions.AddRange(["SunLamp", "HydroponicsBasin", "Heater"]);
        definitions = UniqueFieldDefinitions(definitions);
        var expected = StepScope(call, _reviewer.Native);
        if (!RoundsBuildingBoundary(expected, state.Snapshot, review.Tick))
        {
            throw new ControlException("step: !roundsBuildingBoundary(expected, state.Snapshot, review.Tick)");
        }
        var claims = p.Journal.ConstructionClaims(call, state.Snapshot, expected.Tick);
        var read = _reviewer.ObserveOwned(call, _reviewer.Native, expected, claims, definitions);
        var projection = read.Projection;
        RecordStepRead("field", PolicyApi.EnsureFoodSupply, state.Snapshot, projection);
        var protectedCells = _reviewer.FieldProtected(call, state, review, projection, claims);
        var others = OtherFieldShortfalls(call, state, review, projection);
        var (anchor, planned) = FieldAnchor(projection);
        if (!planned)
        {
            return new RoundsFieldResult { Verdict = BuildingNoLayoutPlan };
        }

        RoundsFieldResult PlaceOthers(uint wait0, Verdict reason)
        {
            var (result0, tried0) = PlaceLedger(call, epoch, state, projection, read, wait0, others, anchor, protectedCells);
            if (tried0)
            {
                return result0;
            }
            return new RoundsFieldResult { Verdict = reason, NativeWorkTicks = wait0 };
        }

        if (!workable)
        {
            return PlaceOthers(0, idle);
        }
        var (fished, handled) = Fishing(call, epoch, state, goal, read);
        if (handled)
        {
            return fished;
        }
        if (blocked)
        {
            var openPlans = new List<PlanState>();
            foreach (var method in goal.Methods)
            {
                openPlans.Add(p.Journal.LoadPlan(method.Plan, call));
            }
            blocked = !FoodPlanFieldRoom(projection, openPlans);
        }
        var wait = FieldAllowance(call, goal.Standard, state.Snapshot, projection);
        var reserveDays = _reviewer.Seasonal(projection.Facts).FoodTargetDays;
        var (request, choices) = FieldSiteRequest(projection, protectedCells, reserveDays);
        var (selection, known) = PolicyApi.PlanSiteType(request);
        if (projection.Environment.TryGetValue(out var env))
        {
            var recrops = PolicyApi.PlanGrowerCrops(new GrowerCropRequest { Choices = choices, Growers = env.Growers, Urgent = selection.Urgent, Field = request.Field });
            foreach (var recropChoice in recrops)
            {
                var (result1, tried1) = Recrop(call, epoch, state, goal, projection, read, wait, recropChoice, arbiter);
                if (tried1)
                {
                    return result1;
                }
            }
        }
        if (blocked)
        {
            return PlaceOthers(wait, BuildingReasonExistingWork);
        }
        var (shrunk, shrinkHandled) = Shrink(call, epoch, state, goal, projection, read, request.Field);
        if (shrinkHandled)
        {
            return shrunk;
        }
        if (!(projection.Facts.FoodPlan.TryGetValue(out var foodPlan) && FoodPlanOpensField(foodPlan)))
        {
            return PlaceOthers(wait, AwaitingFoodPlan("new-field"));
        }
        if (!known)
        {
            return PlaceOthers(wait, FieldUnavailable("field_plan"));
        }
        TelemetryApi.Decide(call, FieldsSelectDecision(selection));
        var attempts = 0;
        foreach (var candidate in selection.Candidates)
        {
            if (candidate.Cells == 0 || attempts >= FieldCandidateAttempts)
            {
                break;
            }
            attempts++;
            RoundsFieldResult result;
            bool tried;
            if (candidate.Kind == SiteKind.Outdoor && candidate.Buildings.Count == 0)
            {
                var food = new FieldShortfall { Standard = goal, Options = FieldBlockOptions(candidate, selection.Candidates), What = "food" };
                var ledger = new List<FieldShortfall> { food };
                ledger.AddRange(others);
                (result, tried) = PlaceLedger(call, epoch, state, projection, read, wait, ledger, request.Field.Site.Anchor, protectedCells);
                others = [];
            }
            else
            {
                (result, tried) = Enact(call, epoch, state, goal, projection, read, wait, candidate);
            }
            if (tried)
            {
                return result;
            }
            FieldEdit(call, "refused", "candidate_refused", candidate.Crop.Name, new Dictionary<string, object?> { ["kind"] = candidate.Kind, ["cells"] = candidate.Cells, ["refusal"] = result.Verdict });
        }
        return PlaceOthers(wait, NoSpace("field_candidates"));
    }

    internal List<FieldShortfall> OtherFieldShortfalls(CancellationToken call, ControlState state, RoundsReview review, ColonyProjection projection)
    {
        var p = _reviewer.Player;
        (StandardState Goal, bool Ready) Ready(string kind)
        {
            var (goal, workable) = p.Journal.Workable(review, kind, call);
            if (!workable)
            {
                return (goal, false);
            }
            foreach (var method in goal.Methods)
            {
                var plan = p.Journal.LoadPlan(method.Plan, call);
                if (StoreApi.PlanOpen(plan))
                {
                    return (goal, false);
                }
            }
            return (goal, true);
        }
        var output = new List<FieldShortfall>();
        var (hay, hayOk) = HayShortfall(projection);
        if (hayOk)
        {
            var (goal, ready) = Ready(PolicyApi.MaintainResource);
            if (ready)
            {
                output.Add(new FieldShortfall { Standard = goal, Options = [hay], What = "hay" });
            }
        }
        if (review.BrewingFinished && PolicyApi.BrewingFinished(projection.Facts.Research))
        {
            var (opts, known) = SocialShortfalls(projection);
            if (known && opts.Count > 0)
            {
                var (goal, ready) = Ready(PolicyApi.MaintainResource);
                foreach (var opt in opts)
                {
                    if (ready)
                    {
                        output.Add(new FieldShortfall { Standard = goal, Options = [opt], What = "social" });
                    }
                }
            }
        }
        var (rgoal, workable) = Ready(PolicyApi.MaintainResource);
        if (!workable)
        {
            return output;
        }
        var supply = _reviewer.ResourceSupply(call, state, review, rgoal);
        foreach (var plan in supply.OpenedFields())
        {
            output.Add(new FieldShortfall { Standard = rgoal, Options = [new FieldBlockOption { Crop = plan.Crop, Needed = plan.Needed }], What = "resource" });
        }
        return output;
    }

    internal (RoundsFieldResult Result, bool Tried) PlaceLedger(CancellationToken call, CancellationToken epoch, ControlState state, ColonyProjection projection, RoundsReading read, uint wait, IReadOnlyList<FieldShortfall> ledger, Cell anchor, IReadOnlyList<Cell> protectedCells)
    {
        var result = new RoundsFieldResult { Verdict = BuildingReasonNoDeficit, NativeWorkTicks = wait };
        foreach (var s in RankFieldShortfalls(ledger))
        {
            if (s.Options.Count == 0)
            {
                continue;
            }
            var lead = s.Options[0];
            bool tried;
            (result, tried) = EnactBlock(call, epoch, state, s.Standard, projection, read, wait, new SiteTypeCandidate { Kind = SiteKind.Outdoor, Crop = lead.Crop, Needed = lead.Needed }, s.Options, anchor, protectedCells);
            if (tried)
            {
                return (result, tried);
            }
        }
        return (result, false);
    }

    internal (RoundsFieldResult Result, bool Tried) Enact(CancellationToken call, CancellationToken epoch, ControlState state, StandardState goal, ColonyProjection projection, RoundsReading read, uint wait, SiteTypeCandidate candidate)
    {
        var p = _reviewer.Player;
        var crop = candidate.Crop;
        var text = string.Create(CultureInfo.InvariantCulture, $"{candidate.Kind}/{crop.Name}/{string.Join(";", candidate.Sites.Patches)}/{string.Join(";", candidate.Buildings)}");
        var method = new MethodId("fields-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)).AsSpan(0, 16)));
        try
        {
            p.Journal.LoadMethod(call, goal.Standard.Id, goal.Standard.Episode, method);
            return (new RoundsFieldResult { Verdict = WaitFor(PolicyApi.CauseMethodUsed, "field_method"), NativeWorkTicks = wait }, true);
        }
        catch (NotFoundException)
        {
            // Not yet used: enact it.
        }
        var id = PlanIds.MintPlanId();
        var snapshot = state.Snapshot with { Plan = id, Revision = 1 };
        var stock = new StockObservation { Snapshot = snapshot, Tick = projection.Identity.Tick };
        var actions = new List<DomainAction>();
        var previews = new List<Preview>();
        if (candidate.Buildings.Count > 0)
        {
            if (_native is not IFieldBuildingPreview native)
            {
                return (new RoundsFieldResult { Verdict = FieldUnavailable("building_preview"), NativeWorkTicks = wait }, false);
            }
            IEnumerable<FieldBuilding> buildings = candidate.Buildings;
            if (candidate.Buildings.Count > FieldBatchPatches)
            {
                buildings = candidate.Buildings.Take(FieldBatchPatches);
            }
            var spent = new Dictionary<Resource, long>();
            var i = 0;
            foreach (var b in buildings)
            {
                var building = DomainApi.NewBuilding(b.Definition, b.Cell, b.Rotation, "");
                var action = DomainAction.NewBuildingAction(new ActionId(string.Create(CultureInfo.InvariantCulture, $"{id}-{i}")), building, BuildTier.Sustain);
                var (preview, _) = native.PreviewBuilding(action, snapshot, call);
                var v = preview.Preview;
                var lk = v.CanPlace.TryGetValue(out var legal);
                var sk = v.SafeToPlace.TryGetValue(out var safe);
                if (!lk || !sk)
                {
                    return (new RoundsFieldResult { Verdict = FieldUnavailable("field_preview"), NativeWorkTicks = wait }, false);
                }
                if (!legal || !safe)
                {
                    return (new RoundsFieldResult { Verdict = NoSpace("field_building_site"), NativeWorkTicks = wait }, false);
                }
                MergeRoundsStock(stock, preview.Stock, i == 0);
                if (i > 0 && !FieldAffordable(spent, v, preview.Stock))
                {
                    break;
                }
                foreach (var cost in FieldCosts(v))
                {
                    spent[cost.Resource] = spent.GetValueOrDefault(cost.Resource) + cost.Count;
                }
                actions.Add(action);
                previews.Add(v);
                i++;
            }
        }
        else
        {
            IEnumerable<Patch> patches = candidate.Sites.Patches;
            if (candidate.Sites.Patches.Count > FieldBatchPatches)
            {
                patches = candidate.Sites.Patches.Take(FieldBatchPatches);
            }
            var i = 0;
            foreach (var patch in patches)
            {
                var cells = new List<Cell>();
                for (var x = patch.X; x < patch.X + patch.Width; x++)
                {
                    for (var z = patch.Z; z < patch.Z + patch.Height; z++)
                    {
                        cells.Add(new Cell(x, z));
                    }
                }
                var value = DomainApi.NewZoneCreate(ZoneKind.Growing, crop.Name, cells);
                var action = DomainAction.NewZoneCreateAction(new ActionId(string.Create(CultureInfo.InvariantCulture, $"{id}-{i}")), value);
                var (reply, refused) = PreviewZone(call, _native, BoundaryApi.Identity(snapshot), value);
                if (refused != "")
                {
                    FieldEdit(call, "refused", "preview_refused", crop.Name, new Dictionary<string, object?> { ["detail"] = refused });
                    return (new RoundsFieldResult { Verdict = SiteBlocked("field_zone", "preview_refused"), NativeWorkTicks = wait }, false);
                }
                var ev = reply.Evaluated;
                try
                {
                    BoundaryApi.Context(ev.Context, snapshot);
                }
                catch (ControlException)
                {
                    throw new ControlException("enact: err != nil || domain.Tick(v.Context.GetTick()) < projection.Identity.Tick");
                }
                if (new Tick(ev.Context.Tick) < projection.Identity.Tick)
                {
                    throw new ControlException("enact: err != nil || domain.Tick(v.Context.GetTick()) < projection.Identity.Tick");
                }
                actions.Add(action);
                previews.Add(ZonePreview(action, snapshot, projection.Identity.Tick, cells));
                i++;
            }
        }
        return Admit(call, epoch, state, goal, projection, read, wait, method, id, snapshot, stock, actions, previews, candidate.Kind + " " + crop.Name);
    }

    internal static Preview ZonePreview(DomainAction action, GenerationSnapshot snapshot, Tick tick, IReadOnlyList<Cell> cells) => new()
    {
        Action = action,
        Snapshot = snapshot,
        Tick = tick,
        CanPlace = Known(true),
        SafeToPlace = Known(true),
        MadeFromStuff = Known(false),
        WatchCellsAccessible = Known(true),
        Footprint = Known(cells),
        Costs = Known<IReadOnlyList<Amount>>([]),
    };

    internal (RoundsFieldResult Result, bool Tried) Admit(CancellationToken call, CancellationToken epoch, ControlState state, StandardState goal, ColonyProjection projection, RoundsReading read, uint wait, MethodId method, PlanId id, GenerationSnapshot snapshot, StockObservation stock, IReadOnlyList<DomainAction> actions, IReadOnlyList<Preview> previews, string what)
    {
        var p = _reviewer.Player;
        var plan = new PlanSpec(id, new PlanRevision(1), actions);
        p.Current(call, epoch);
        if (p.Session.State() != state)
        {
            throw new ControlException("enact: p.session.State() != state");
        }
        Identity actual;
        try
        {
            actual = StepScope(call, _reviewer.Native);
        }
        catch (ControlException)
        {
            throw new ControlException("enact: err != nil || !roundsBuildingBoundary(actual, state.Snapshot, projection.Identity.Tick)");
        }
        if (!RoundsBuildingBoundary(actual, state.Snapshot, projection.Identity.Tick))
        {
            throw new ControlException("enact: err != nil || !roundsBuildingBoundary(actual, state.Snapshot, projection.Identity.Tick)");
        }
        var now = _reviewer.Clock.Now();
        if (now < read.StartedAt || now - read.StartedAt > _reviewer.MaxAge)
        {
            throw new StaleObservationException();
        }
        var decision = AdmitMethod(call, p.Journal, new BuildingMethodRequest
        {
            Owner = goal,
            Method = method,
            Plan = plan,
            Current = snapshot,
            Tick = projection.Identity.Tick,
            Bounds = Known(projection.Bounds),
            Stock = stock,
            Previews = previews,
            Purpose = PolicyApi.Rounds,
        });
        if (!decision.Admitted)
        {
            return (new RoundsFieldResult { Verdict = AdmissionRefused(decision), NativeWorkTicks = wait }, false);
        }
        return (new RoundsFieldResult { Verdict = BuildingReasonAdmitted, Plan = id }, true);
    }

    internal (RoundsFieldResult Result, bool Tried) EnactBlock(CancellationToken call, CancellationToken epoch, ControlState state, StandardState goal, ColonyProjection projection, RoundsReading read, uint wait, SiteTypeCandidate candidate, IReadOnlyList<FieldBlockOption> options, Cell anchor, IReadOnlyList<Cell> protectedCells)
    {
        var p = _reviewer.Player;
        var (edit, reason, ok) = PlanFieldBlock(projection, anchor, options, protectedCells);
        if (!ok)
        {
            ClockEvent(call, "layout", "fields", "outdoor field refused: " + reason, "crop", candidate.Crop.Name);
            return (new RoundsFieldResult { Verdict = NoSpace("field_block"), NativeWorkTicks = wait }, false);
        }
        var text = string.Create(CultureInfo.InvariantCulture, $"block/{edit.Zone}/{edit.Crop}/{string.Join(";", edit.Cells)}");
        var method = new MethodId("fields-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)).AsSpan(0, 16)));
        try
        {
            p.Journal.LoadMethod(call, goal.Standard.Id, goal.Standard.Episode, method);
            return (new RoundsFieldResult { Verdict = WaitFor(PolicyApi.CauseMethodUsed, "field_block_method"), NativeWorkTicks = wait }, true);
        }
        catch (NotFoundException)
        {
            // Not yet used: grow the block.
        }
        var id = PlanIds.MintPlanId();
        var snapshot = state.Snapshot with { Plan = id, Revision = 1 };
        if (edit.Zone != "")
        {
            var value = DomainApi.NewZoneCellEdit(edit.Zone, ZoneEditKind.Add, edit.Cells);
            var action0 = DomainAction.NewZoneCellEditAction(new ActionId($"{id}-0"), value);
            var plan0 = new PlanSpec(id, new PlanRevision(1), [action0]);
            p.Current(call, epoch);
            var now = _reviewer.Clock.Now();
            if (p.Session.State() != state || now < read.StartedAt || now - read.StartedAt > _reviewer.MaxAge)
            {
                throw new ControlException("enactBlock: p.session.State() != state || now.Before(read.StartedAt) || now.Sub(read.StartedAt) > r.reviewer.maxAge");
            }
            p.Journal.CommitMethod(goal.Standard.Id, goal.Revision, method, plan0, call);
            ClockEvent(call, "layout", "fields", "field block grown", "zone", edit.Zone, "crop", edit.Crop, "cells", edit.Cells.Count, "plan", id.Value);
            return (new RoundsFieldResult { Verdict = BuildingReasonAdmitted, Plan = id }, true);
        }
        var create = DomainApi.NewZoneCreate(ZoneKind.Growing, edit.Crop, edit.Cells);
        var action = DomainAction.NewZoneCreateAction(new ActionId($"{id}-0"), create);
        var (reply, refused) = PreviewZone(call, _native, BoundaryApi.Identity(snapshot), create);
        if (refused != "")
        {
            FieldEdit(call, "refused", "preview_refused", edit.Crop, new Dictionary<string, object?> { ["detail"] = refused });
            return (new RoundsFieldResult { Verdict = SiteBlocked("field_zone", "preview_refused"), NativeWorkTicks = wait }, false);
        }
        var v = reply.Evaluated;
        try
        {
            BoundaryApi.Context(v.Context, snapshot);
        }
        catch (ControlException)
        {
            throw new ControlException("enactBlock: err != nil || domain.Tick(v.Context.GetTick()) < projection.Identity.Tick");
        }
        if (new Tick(v.Context.Tick) < projection.Identity.Tick)
        {
            throw new ControlException("enactBlock: err != nil || domain.Tick(v.Context.GetTick()) < projection.Identity.Tick");
        }
        var stock = new StockObservation { Snapshot = snapshot, Tick = projection.Identity.Tick };
        var preview = ZonePreview(action, snapshot, projection.Identity.Tick, edit.Cells);
        return Admit(call, epoch, state, goal, projection, read, wait, method, id, snapshot, stock, [action], [preview], "block " + edit.Crop);
    }

    internal (RoundsFieldResult Result, bool Tried) Recrop(CancellationToken call, CancellationToken epoch, ControlState state, StandardState goal, ColonyProjection projection, RoundsReading read, uint wait, GrowerCropChoice choice, StepArbiter arbiter)
    {
        if (_native is not IFieldGrowerCrop native)
        {
            return (new RoundsFieldResult { Verdict = FieldUnavailable("grower_crop_target"), NativeWorkTicks = wait }, false);
        }
        var p = _reviewer.Player;
        var method = new MethodId("fields-recrop-" + choice.Grower);
        try
        {
            p.Journal.LoadMethod(call, goal.Standard.Id, goal.Standard.Episode, method);
            return (new RoundsFieldResult { Verdict = WaitFor(PolicyApi.CauseMethodUsed, "recrop_method"), NativeWorkTicks = wait }, false);
        }
        catch (NotFoundException)
        {
            // Not yet used: recrop.
        }
        var (target, _) = native.ReadGrowerCropTarget(BoundaryApi.Identity(state.Snapshot), choice.Grower, call);
        try
        {
            BoundaryApi.Context(target.Context, state.Snapshot);
        }
        catch (ControlException)
        {
            throw new ControlException("recrop: err != nil || target.Context.GetTick() < int64(projection.Identity.Tick)");
        }
        if (target.Context.Tick < (long)projection.Identity.Tick)
        {
            throw new ControlException("recrop: err != nil || target.Context.GetTick() < int64(projection.Identity.Tick)");
        }
        if (target.Crop != choice.Current)
        {
            return (new RoundsFieldResult { Verdict = FieldUnavailable("grower_crop"), NativeWorkTicks = wait }, false);
        }
        var patch = DomainApi.NewGrowerCrop(choice.Grower, choice.Crop.Name);
        if (!arbiter.TryClaim([], "grower:" + choice.Grower))
        {
            return (new RoundsFieldResult { Verdict = WaitFor(PolicyApi.CauseMethodUsed, "grower_claim"), NativeWorkTicks = wait }, false);
        }
        var id = PlanIds.MintPlanId();
        var action = DomainAction.NewGrowerCropAction(new ActionId($"{id}-0"), patch);
        var plan = new PlanSpec(id, new PlanRevision(1), [action]);
        p.Current(call, epoch);
        var now = _reviewer.Clock.Now();
        if (p.Session.State() != state || now < read.StartedAt || now - read.StartedAt > _reviewer.MaxAge)
        {
            throw new ControlException("recrop: p.session.State() != state || now.Before(read.StartedAt) || now.Sub(read.StartedAt) > r.reviewer.maxAge");
        }
        FieldEdit(call, "admitted", "recrop", choice.Grower, new Dictionary<string, object?> { ["from"] = choice.Current, ["to"] = choice.Crop.Name, ["detail"] = choice.Reason });
        p.Journal.CommitMethod(goal.Standard.Id, goal.Revision, method, plan, call);
        return (new RoundsFieldResult { Verdict = BuildingReasonAdmitted, Plan = id }, true);
    }

    /// <summary>The wait, in ticks, until the newest growing zone of the standard should have produced: its crop's grow time with margin.</summary>
    internal uint FieldAllowance(CancellationToken ct, Standard goal, GenerationSnapshot current, ColonyProjection facts)
    {
        var journal = _reviewer.Player.Journal;
        var methods = journal.LoadMethods(goal.Id, goal.Episode, ct);
        uint remaining = 0;
        foreach (var method in methods)
        {
            var plan = journal.LoadPlan(method.Plan, ct);
            var snapshot = current with { Plan = plan.Spec.Id, Revision = plan.Spec.Revision };
            foreach (var progress in plan.Progress)
            {
                var v = progress.View;
                var ok = progress.Action.ZoneCreate(out var zone);
                var created = v.Zone.TryGetValue(out var id);
                if (!ok || !created || v.Stage != Stage.Completed || v.Unresolved || !v.Snapshot.Matches(snapshot) || v.Tick > facts.Identity.Tick)
                {
                    continue;
                }
                long budget = 0;
                foreach (var d in facts.Definitions)
                {
                    if (d.Name == zone.Crop)
                    {
                        if (d.GrowDays.TryGetValue(out var days) && days > 0 && days <= 24)
                        {
                            budget = (long)Math.Ceiling(Math.Min(60, (days * 2.5) + _reviewer.Seasonal(facts.Facts).FoodTargetDays) * 60000);
                        }
                    }
                }
                if (budget == 0 || facts.Identity.Tick.Value - v.Tick.Value >= budget)
                {
                    continue;
                }
                foreach (var farm in facts.Farms)
                {
                    if (farm.Id == id && farm.Crop == zone.Crop)
                    {
                        remaining = Math.Max(remaining, (uint)(budget - (facts.Identity.Tick.Value - v.Tick.Value)));
                    }
                }
            }
        }
        return remaining;
    }

    internal static List<Amount> FieldCosts(Preview v) => v.Costs.TryGetValue(out var costs) ? costs.ToList() : [];

    internal static bool FieldAffordable(IReadOnlyDictionary<Resource, long> spent, Preview v, StockObservation stock)
    {
        var available = new Dictionary<Resource, Fact<long>>();
        foreach (var s in stock.Values)
        {
            available[s.Resource] = s.Available;
        }
        foreach (var cost in FieldCosts(v))
        {
            if (available.TryGetValue(cost.Resource, out var fact) && fact.TryGetValue(out var have) && spent.GetValueOrDefault(cost.Resource) + cost.Count > have)
            {
                return false;
            }
        }
        return true;
    }

    internal static bool FieldBlockingWork(MethodId method, IReadOnlyList<Progress> progress)
    {
        if (RoundsBuildingPlanner.IsShellMethod(method))
        {
            return false;
        }
        foreach (var p in progress)
        {
            var zone = p.Action.ZoneCreate(out _) || p.Action.ZoneCellEdit(out _);
            var isBuilding = p.Action.Building(out var building);
            if (isBuilding && ButcherBenchDefinition(building.Definition))
            {
                continue;
            }
            if ((zone || isBuilding) && PendingWork(p))
            {
                return true;
            }
        }
        return false;
    }

    internal static List<string> UniqueFieldDefinitions(IEnumerable<string> values)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var output = new List<string>();
        foreach (var v in values)
        {
            if (seen.Add(v))
            {
                output.Add(v);
            }
        }
        return output;
    }

    internal static void FieldEdit(CancellationToken ct, string verdict, string reason, string target, Dictionary<string, object?> attrs)
    {
        attrs["family"] = "field";
        TelemetryApi.Decide(ct, new Decision { Kind = "layout_edit", Component = "clock-scheduler", Verdict = verdict, Reason = reason, Target = target, Attrs = attrs });
    }
}

/// <summary>A field native that also previews a building.</summary>
public interface IFieldBuildingPreview
{
    (BridgeBuildingPreview Preview, BridgeResult Result) PreviewBuilding(DomainAction action, GenerationSnapshot snapshot, CancellationToken ct);
}

/// <summary>A field native that reads a grower's crop target.</summary>
public interface IFieldGrowerCrop
{
    (BridgeGrowerCropTarget Target, BridgeResult Result) ReadGrowerCropTarget(C.Identity identity, string grower, CancellationToken ct);
}

internal static partial class BuildingRuntimePackage
{
    internal static List<CropChoice> CropChoices(ColonyProjection projection)
    {
        var choices = new List<CropChoice>();
        foreach (var d in projection.Definitions)
        {
            if (!d.GrowDays.IsKnown)
            {
                continue;
            }
            var choice = new CropChoice
            {
                Name = d.Name,
                Available = d.Available,
                Edible = d.Edible,
                GrowDays = d.GrowDays,
                FertilityMin = d.FertilityMin,
                FertilitySensitivity = d.FertilitySensitivity,
                HarvestNutrition = d.HarvestNutrition,
                Demand = d.NutritionDemandPerDay,
                SowTags = d.SowTags,
                MinGlow = d.GrowMinGlow,
                HarvestWork = d.HarvestWork,
                RawPreferred = d.RawPreferred,
                DietAllowed = d.DietAllowed,
                RequiresPollution = d.RequiresPollution,
                RequiresCleanSoil = d.RequiresCleanSoil,
                RotDays = d.HarvestRotDays,
                Perishable = d.HarvestPerishable,
            };
            choices.Add(WithHarvestFacts(choice, d));
        }
        return choices;
    }

    internal static (FieldRequest Request, List<CropChoice> Choices) FieldRequestOf(ColonyProjection projection, double reserveDays)
    {
        var choices = CropChoices(projection);
        var coverage = PolicyApi.FieldCoverage(projection.Facts.Colonists, projection.FieldCapacityCrops, reserveDays);
        var (growers, cooks) = PolicyApi.CropWorkers(projection.WorkPawns);
        return (new FieldRequest
        {
            Growers = growers,
            Cooks = cooks,
            Calendar = projection.Facts.Calendar,
            Conditions = projection.Facts.DisasterConditions,
            Choices = choices,
            Climate = projection.CropClimate,
            Runway = projection.Facts.FoodDays,
            Colonists = projection.Facts.Colonists,
            ReserveDays = reserveDays,
            Coverage = coverage,
        }, choices);
    }

    internal static (SiteTypeRequest Request, List<CropChoice> Choices) FieldSiteRequest(ColonyProjection projection, IReadOnlyList<Cell> protectedCells, double reserveDays)
    {
        var (field, choices) = FieldRequestOf(projection, reserveDays);
        var (anchor, _) = FieldAnchor(projection);
        field.Site = new FarmSiteRequest { Bounds = projection.Bounds, Anchor = anchor, Cells = projection.Cells, Protected = protectedCells };
        var (fields, ok) = LayoutFieldCells(projection);
        if (ok)
        {
            field.Site.Fields = fields;
        }
        var request = new SiteTypeRequest { Field = field, Environment = projection.Environment, LampGrowthRadius = RoundsFieldPlanner.FieldLampGrowthRadius, Items = projection.Facts.Items, Stock = projection.Resources };
        foreach (var d in projection.Definitions)
        {
            var infrastructure = Known(new Infrastructure { Name = d.Name, Available = d.Available, PowerW = d.PowerW, Fertility = d.GrowerFertility, Costs = d.CheapestCosts() });
            switch (d.Name)
            {
                case "SunLamp":
                    request.Lamp = infrastructure;
                    break;
                case "HydroponicsBasin":
                    request.Basin = infrastructure;
                    break;
                case "Heater":
                    request.Heater = infrastructure;
                    break;
            }
        }
        return (request, choices);
    }

    internal static List<Cell> FirebreakRing(ColonyProjection projection)
    {
        var ring = PolicyApi.FirebreakRing(FirebreakRequest(projection));
        ring.TryGetValue(out var cells);
        return cells?.ToList() ?? [];
    }

    internal static CropChoice WithHarvestFacts(CropChoice crop, PlanningDefinition d)
    {
        if (d.HarvestedThingDef.TryGetValue(out var name))
        {
            crop.Harvests = Known(new Resource(name));
        }
        crop.UnitsPerCell = d.HarvestYield;
        crop.SowMinSkill = d.SowMinSkill;
        crop.HarvestDestroys = d.HarvestDestroysPlant;
        crop.BlockAdjacentSow = d.BlockAdjacentSow;
        crop.MustBeWildToSow = d.MustBeWildToSow;
        crop.HarvestMinGrowth = d.HarvestMinGrowth;
        crop.SowWork = d.SowWork;
        crop.WildBiomes = d.WildBiomes;
        return crop;
    }
}
