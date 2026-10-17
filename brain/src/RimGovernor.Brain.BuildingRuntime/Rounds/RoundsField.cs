using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace RimGovernor.Brain.BuildingRuntime;

public sealed class RoundsFieldResult : IPlannerMethod
{
    public Verdict Verdict { get; init; }

    public PlanId Plan { get; init; }
}

/// <summary>
/// Opens and grows the colony's fields. The food/supply Need's part (fishing zones, grower crops, shrinking surplus
/// zones and new food fields) runs in the food family's work step under the Need, admitted by claims; the economy
/// family's resource Need places the other resources' fields (hay, social crops and the supply plan's opened fields).
/// </summary>
public sealed partial class RoundsFieldPlanner
{
    internal const double FieldLampGrowthRadius = 5.8;
    internal const int FieldCandidateAttempts = 3;
    internal const int FieldBatchPatches = 6;

    private readonly Rounder _reviewer;
    internal Rounder Reviewer => _reviewer;
    private readonly FacadeNativeClient _native;

    public RoundsFieldPlanner(Rounder reviewer, FacadeNativeClient native)
    {
        if (reviewer is null || native is null)
        {
            throw new ControlException("NewRoundsFieldPlanner: reviewer == nil || native == nil");
        }
        _reviewer = reviewer;
        _native = native;
    }

    /// <summary>
    /// One step under <paramref name="owner"/>: the economy/resource Need's owner places only the other resources'
    /// fields; the food/supply Need's owner places only the Need's food work.
    /// </summary>
    internal async Task<RoundsFieldResult> StepAsync(StepArbiter arbiter, NeedOwner owner, CancellationToken call, CancellationToken epoch)
    {
        NeedOwner? food = owner switch
        {
            NeedOwner { Need: var n } when n == PolicyFunctions.FoodSupplyNeed => owner,
            NeedOwner { Need: var n } when n == EconomyFamily.Resource => null,
            _ => throw new ControlException("field step: the step runs only under the food/supply or economy/resource Need"),
        };
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
        var plans = p.Journal.LoadPlans(call);
        var playerPlans = p.Journal.PlayerPlans(Player.PlayerWorld(state.Snapshot), call);
        var crops = await _reviewer.SowableCropsAsync(state.Snapshot, call).ConfigureAwait(false);
        var definitions = RoundsProjectBill.RoundsProjectDefinitions(plans, state.Snapshot, playerPlans).Concat(crops).ToList();
        definitions.AddRange(["SunLamp", "HydroponicsBasin", "Heater"]);
        definitions = UniqueFieldDefinitions(definitions);
        var expected = await StepScope.StepScopeAsync(_reviewer.Native, call).ConfigureAwait(false);
        if (!RoundsBuildingPlanner.RoundsBuildingBoundary(expected, state.Snapshot, review.Tick))
        {
            throw new ControlException("step: !roundsBuildingBoundary(expected, state.Snapshot, review.Tick)");
        }
        var claims = p.Journal.ConstructionClaims(state.Snapshot, expected.Tick, call);
        var read = await _reviewer.ObserveOwnedAsync(_reviewer.Native, expected, claims, definitions, call).ConfigureAwait(false);
        var projection = read.Projection;
        PlannerSnapshot.RecordStepRead("field", food is null ? EconomyFamily.Resource.FilingKey : PolicyFunctions.FoodSupplyNeed.FilingKey, state.Snapshot, projection);
        var protectedCells = _reviewer.FieldProtected(state, review, projection, claims, call);
        var others = food is null ? await OtherFieldShortfallsAsync(state, review, projection, owner, call).ConfigureAwait(false) : [];
        if (LayoutProtected.FieldAnchor(projection) is not { } anchor)
        {
            return new RoundsFieldResult { Verdict = BuildingReasonNoLayoutPlan };
        }

        async Task<RoundsFieldResult> PlaceOthersAsync(Verdict reason)
        {
            var (result0, tried0) = await PlaceLedgerAsync(state, projection, read, others, anchor, protectedCells, call, epoch).ConfigureAwait(false);
            if (tried0)
            {
                return result0;
            }
            return new RoundsFieldResult { Verdict = reason };
        }

        if (food is not { } goal)
        {
            return await PlaceOthersAsync(BuildingReasonNoDeficit).ConfigureAwait(false);
        }
        var (fished, handled) = await FishingAsync(state, goal, read, call, epoch).ConfigureAwait(false);
        if (handled)
        {
            return fished;
        }
        var reserveDays = _reviewer.Seasonal(projection.Facts).FoodTargetDays;
        var (request, choices) = FieldSiteRequest(projection, protectedCells, reserveDays);
        var (selection, known) = PolicyApi.PlanSiteType(request);
        if (projection.Environment.TryGetValue(out var env))
        {
            var recrops = PolicyApi.PlanGrowerCrops(new GrowerCropRequest { Choices = choices, Growers = env.Growers, Urgent = selection.Urgent, Field = request.Field });
            foreach (var recropChoice in recrops)
            {
                var (result1, tried1) = await RecropAsync(state, goal, projection, read, recropChoice, arbiter, call, epoch).ConfigureAwait(false);
                if (tried1)
                {
                    return result1;
                }
            }
        }
        var shrunk = await ShrinkAsync(state, goal, projection, read, request.Field, call, epoch).ConfigureAwait(false);
        if (shrunk is not null)
        {
            return shrunk;
        }
        if (!(projection.Facts.FoodPlan.TryGetValue(out var foodPlan) && Rounder.FoodPlanOpensField(foodPlan)))
        {
            return await PlaceOthersAsync(AwaitingFoodPlan("new-field")).ConfigureAwait(false);
        }
        if (!known)
        {
            return await PlaceOthersAsync(FieldUnavailable("field_plan")).ConfigureAwait(false);
        }
        Tel.Decide(FieldsSelectLog.FieldsSelectDecision(selection));
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
                var shortfall = new FieldShortfall(goal, LayoutFieldPlanning.FieldBlockOptions(candidate, selection.Candidates), "food");
                (result, tried) = await PlaceLedgerAsync(state, projection, read, [shortfall], request.Field.Site.Anchor, protectedCells, call, epoch).ConfigureAwait(false);
            }
            else
            {
                (result, tried) = await EnactAsync(state, goal, projection, read, candidate, call, epoch).ConfigureAwait(false);
            }
            if (tried)
            {
                return result;
            }
            FieldEdit("refused", "candidate_refused", candidate.Crop.Name, new Dictionary<string, object?> { ["kind"] = candidate.Kind, ["cells"] = candidate.Cells, ["refusal"] = result.Verdict }, call);
        }
        return await PlaceOthersAsync(NoSpace("field_candidates")).ConfigureAwait(false);
    }

    /// <summary>
    /// The economy/resource Need's field shortfalls (hay, social crops, the supply plan's opened fields). While one of
    /// the Need's field Methods is still open the next waits for it, as the field's growth is what the next shortfall
    /// reads; the Need's other work (mines, chops, bills) runs beside it.
    /// </summary>
    internal async Task<List<FieldShortfall>> OtherFieldShortfallsAsync(ControlState state, RoundsReview review, ColonyProjection projection, NeedOwner rgoal, CancellationToken call)
    {
        var p = _reviewer.Player;
        var output = new List<FieldShortfall>();
        foreach (var method in rgoal.OpenMethods)
        {
            if (method.Method.Value.StartsWith("fields-", StringComparison.Ordinal) && StorePackage.PlanOpen(p.Journal.LoadPlan(method.Plan, call)))
            {
                return output;
            }
        }
        var (hay, hayOk) = RoundsHay.HayShortfall(projection);
        if (hayOk)
        {
            output.Add(new FieldShortfall(rgoal, [hay], "hay"));
        }
        if (review.BrewingFinished && PolicyApi.BrewingFinished(projection.Facts.Research))
        {
            var (opts, known) = RoundsSocialFields.SocialShortfalls(projection);
            if (known && opts.Count > 0)
            {
                foreach (var opt in opts)
                {
                    output.Add(new FieldShortfall(rgoal, [opt], "social"));
                }
            }
        }
        var supply = await _reviewer.ResourceSupplyAsync(state, review, rgoal, call).ConfigureAwait(false);
        foreach (var plan in supply.OpenedFields())
        {
            output.Add(new FieldShortfall(rgoal, [new FieldBlockOption(plan.Crop, plan.Needed)], "resource"));
        }
        return output;
    }

    internal async Task<(RoundsFieldResult Result, bool Tried)> PlaceLedgerAsync(ControlState state, ColonyProjection projection, RoundsReading read, IReadOnlyList<FieldShortfall> ledger, Cell anchor, IReadOnlyList<Cell> protectedCells, CancellationToken call, CancellationToken epoch)
    {
        var result = new RoundsFieldResult { Verdict = BuildingReasonNoDeficit };
        foreach (var s in LayoutFieldPlanning.RankFieldShortfalls(ledger))
        {
            if (s.Options.Count == 0)
            {
                continue;
            }
            var lead = s.Options[0];
            bool tried;
            (result, tried) = await EnactBlockAsync(state, s.Owner, projection, read, new SiteTypeCandidate { Kind = SiteKind.Outdoor, Crop = lead.Crop, Needed = lead.Needed }, s.Options, anchor, protectedCells, call, epoch).ConfigureAwait(false);
            if (tried)
            {
                return (result, tried);
            }
        }
        return (result, false);
    }

    internal async Task<(RoundsFieldResult Result, bool Tried)> EnactAsync(ControlState state, NeedOwner goal, ColonyProjection projection, RoundsReading read, SiteTypeCandidate candidate, CancellationToken call, CancellationToken epoch)
    {
        var p = _reviewer.Player;
        var crop = candidate.Crop;
        var text = string.Create(CultureInfo.InvariantCulture, $"{candidate.Kind}/{crop.Name}/{string.Join(";", candidate.Sites.Patches)}/{string.Join(";", candidate.Buildings)}");
        var method = new MethodId("fields-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)).AsSpan(0, 16)));
        if (p.Journal.OwnerMethodPlan(goal, method, call) is not null)
        {
            return (new RoundsFieldResult { Verdict = WaitFor(Cause.MethodUsed, "field_method") }, true);
        }
        var id = PlanIds.MintPlanId();
        var snapshot = state.Snapshot with { Plan = id, Revision = new PlanRevision(1) };
        var stock = new StockObservation { Snapshot = snapshot, Tick = projection.Identity.Tick };
        var actions = new List<DomainAction>();
        var previews = new List<Preview>();
        if (candidate.Buildings.Count > 0)
        {
            if (_native is not FacadeNativeClient native)
            {
                return (new RoundsFieldResult { Verdict = FieldUnavailable("building_preview") }, false);
            }
            IEnumerable<SiteBuilding> buildings = candidate.Buildings;
            if (candidate.Buildings.Count > FieldBatchPatches)
            {
                buildings = candidate.Buildings.Take(FieldBatchPatches);
            }
            var spent = new Dictionary<Resource, long>();
            var i = 0;
            foreach (var b in buildings)
            {
                var building = new Building(b.Definition, b.Cell, b.Rotation, "");
                var action = DomainAction.NewBuildingAction(new ActionId(string.Create(CultureInfo.InvariantCulture, $"{id}-{i}")), building, ConstructionTier.Sustain);
                var preview = await native.PreviewBuildingAsync(action, snapshot, call).ConfigureAwait(false);
                var v = preview.Preview;
                var lk = v.CanPlace.TryGetValue(out var legal);
                var sk = v.SafeToPlace.TryGetValue(out var safe);
                if (!lk || !sk)
                {
                    return (new RoundsFieldResult { Verdict = FieldUnavailable("field_preview") }, false);
                }
                if (!legal || !safe)
                {
                    return (new RoundsFieldResult { Verdict = NoSpace("field_building_site") }, false);
                }
                RoundsBuildingPlanner.MergeRoundsStock(stock, preview.Stock, i == 0);
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
            IEnumerable<Rectangle> patches = candidate.Sites.Patches;
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
                var value = new ZoneCreate(Domain.ZoneKind.Growing, crop.Name, cells);
                var action = DomainAction.NewZoneCreateAction(new ActionId(string.Create(CultureInfo.InvariantCulture, $"{id}-{i}")), value);
                var (reply, refused) = await ZonePreviews.PreviewZoneAsync(_native, Boundary.Identity(snapshot), value, call).ConfigureAwait(false);
                if (refused != "")
                {
                    FieldEdit("refused", "preview_refused", crop.Name, new Dictionary<string, object?> { ["detail"] = refused }, call);
                    return (new RoundsFieldResult { Verdict = SiteBlocked("field_zone", "preview_refused") }, false);
                }
                var ev = reply?.Outcome?.Evaluated ?? throw new ControlException("enact: accepted preview carries no evaluation");
                try
                {
                    Boundary.Context(ev.Context, snapshot);
                }
                catch (ControlException)
                {
                    throw new ControlException("enact: err != nil || domain.Tick(v.Context.GetTick()) < projection.Identity.Tick");
                }
                if (new Tick(ev.Context?.Tick ?? 0) < projection.Identity.Tick)
                {
                    throw new ControlException("enact: err != nil || domain.Tick(v.Context.GetTick()) < projection.Identity.Tick");
                }
                actions.Add(action);
                previews.Add(ZonePreview(action, snapshot, projection.Identity.Tick, cells));
                i++;
            }
        }
        return await AdmitAsync(state, goal, projection, read, method, id, snapshot, stock, actions, previews, candidate.Kind + " " + crop.Name, call, epoch).ConfigureAwait(false);
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
        Footprint = Known(EquatableArray.From(cells)),
        Costs = Known(EquatableArray.From(new List<Amount>())),
    };

    internal async Task<(RoundsFieldResult Result, bool Tried)> AdmitAsync(ControlState state, NeedOwner goal, ColonyProjection projection, RoundsReading read, MethodId method, PlanId id, GenerationSnapshot snapshot, StockObservation stock, IReadOnlyList<DomainAction> actions, IReadOnlyList<Preview> previews, string what, CancellationToken call, CancellationToken epoch)
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
            actual = await StepScope.StepScopeAsync(_reviewer.Native, call).ConfigureAwait(false);
        }
        catch (ControlException)
        {
            throw new ControlException("enact: err != nil || !roundsBuildingBoundary(actual, state.Snapshot, projection.Identity.Tick)");
        }
        if (!RoundsBuildingPlanner.RoundsBuildingBoundary(actual, state.Snapshot, projection.Identity.Tick))
        {
            throw new ControlException("enact: err != nil || !roundsBuildingBoundary(actual, state.Snapshot, projection.Identity.Tick)");
        }
        var now = _reviewer.Clock.Now();
        if (now < read.StartedAt || now - read.StartedAt > _reviewer.MaxAge)
        {
            throw new ObservationStaleException();
        }
        var decision = await AdmissionLog.AdmitMethodAsync(p.Journal, new BuildingMethodRequest
        {
            Owner = goal,
            Method = method,
            Plan = plan,
            Current = snapshot,
            Tick = projection.Identity.Tick,
            Bounds = Known(projection.Bounds),
            Stock = stock,
            Previews = previews,
            Purpose = Purpose.Rounds,
        }, call).ConfigureAwait(false);
        if (!decision.Admitted)
        {
            return (new RoundsFieldResult { Verdict = AdmissionLog.AdmissionRefused(decision) }, false);
        }
        return (new RoundsFieldResult { Verdict = BuildingReasonAdmitted, Plan = id }, true);
    }

    internal async Task<(RoundsFieldResult Result, bool Tried)> EnactBlockAsync(ControlState state, NeedOwner goal, ColonyProjection projection, RoundsReading read, SiteTypeCandidate candidate, IReadOnlyList<FieldBlockOption> options, Cell anchor, IReadOnlyList<Cell> protectedCells, CancellationToken call, CancellationToken epoch)
    {
        var p = _reviewer.Player;
        var (planned, reason) = LayoutFieldPlanning.PlanFieldBlock(projection, anchor, options, protectedCells);
        if (planned is not { } edit)
        {
            ClockEvents.Event("layout", "fields", "outdoor field refused: " + reason, ("crop", candidate.Crop.Name));
            return (new RoundsFieldResult { Verdict = NoSpace("field_block") }, false);
        }
        var text = string.Create(CultureInfo.InvariantCulture, $"block/{edit.Zone}/{edit.Crop}/{string.Join(";", edit.Cells)}");
        var method = new MethodId("fields-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)).AsSpan(0, 16)));
        if (p.Journal.OwnerMethodPlan(goal, method, call) is not null)
        {
            return (new RoundsFieldResult { Verdict = WaitFor(Cause.MethodUsed, "field_block_method") }, true);
        }
        var id = PlanIds.MintPlanId();
        var snapshot = state.Snapshot with { Plan = id, Revision = new PlanRevision(1) };
        if (edit.Zone != "")
        {
            var value = new ZoneCellEdit(edit.Zone, CellEditMode.Add, edit.Cells);
            var action0 = DomainAction.NewZoneCellEditAction(new ActionId($"{id}-0"), value);
            var plan0 = new PlanSpec(id, new PlanRevision(1), [action0]);
            p.Current(call, epoch);
            var now = _reviewer.Clock.Now();
            if (p.Session.State() != state || now < read.StartedAt || now - read.StartedAt > _reviewer.MaxAge)
            {
                throw new ControlException("enactBlock: p.session.State() != state || now.Before(read.StartedAt) || now.Sub(read.StartedAt) > r.reviewer.maxAge");
            }
            var grown = p.Journal.CommitOwnerWork(goal, method, "", plan0, call);
            if (grown.Outcome != ClaimOutcome.Admitted)
            {
                return (new RoundsFieldResult { Verdict = RoundsBuildingPlanner.OwnerCommitVerdict(grown) }, false);
            }
            ClockEvents.Event("layout", "fields", "field block grown", ("zone", edit.Zone), ("crop", edit.Crop), ("cells", edit.Cells.Count), ("plan", id.Value));
            return (new RoundsFieldResult { Verdict = BuildingReasonAdmitted, Plan = id }, true);
        }
        var create = new ZoneCreate(Domain.ZoneKind.Growing, edit.Crop, edit.Cells);
        var action = DomainAction.NewZoneCreateAction(new ActionId($"{id}-0"), create);
        var (reply, refused) = await ZonePreviews.PreviewZoneAsync(_native, Boundary.Identity(snapshot), create, call).ConfigureAwait(false);
        if (refused != "")
        {
            FieldEdit("refused", "preview_refused", edit.Crop, new Dictionary<string, object?> { ["detail"] = refused }, call);
            return (new RoundsFieldResult { Verdict = SiteBlocked("field_zone", "preview_refused") }, false);
        }
        var v = reply?.Outcome?.Evaluated ?? throw new ControlException("enactBlock: accepted preview carries no evaluation");
        try
        {
            Boundary.Context(v.Context, snapshot);
        }
        catch (ControlException)
        {
            throw new ControlException("enactBlock: err != nil || domain.Tick(v.Context.GetTick()) < projection.Identity.Tick");
        }
        if (new Tick(v.Context?.Tick ?? 0) < projection.Identity.Tick)
        {
            throw new ControlException("enactBlock: err != nil || domain.Tick(v.Context.GetTick()) < projection.Identity.Tick");
        }
        var stock = new StockObservation { Snapshot = snapshot, Tick = projection.Identity.Tick };
        var preview = ZonePreview(action, snapshot, projection.Identity.Tick, edit.Cells);
        return await AdmitAsync(state, goal, projection, read, method, id, snapshot, stock, [action], [preview], "block " + edit.Crop, call, epoch).ConfigureAwait(false);
    }

    internal async Task<(RoundsFieldResult Result, bool Tried)> RecropAsync(ControlState state, NeedOwner goal, ColonyProjection projection, RoundsReading read, GrowerCropChoice choice, StepArbiter arbiter, CancellationToken call, CancellationToken epoch)
    {
        if (_native is not FacadeNativeClient native)
        {
            return (new RoundsFieldResult { Verdict = FieldUnavailable("grower_crop_target") }, false);
        }
        var p = _reviewer.Player;
        var method = new MethodId("fields-recrop-" + choice.Grower);
        if (p.Journal.OwnerMethodPlan(goal, method, call) is not null)
        {
            return (new RoundsFieldResult { Verdict = WaitFor(Cause.MethodUsed, "recrop_method") }, false);
        }
        var target = await native.ReadGrowerCropTargetAsync(Boundary.Identity(state.Snapshot), choice.Grower, call).ConfigureAwait(false);
        try
        {
            Boundary.Context(target.Context, state.Snapshot);
        }
        catch (ControlException)
        {
            throw new ControlException("recrop: err != nil || target.Context.GetTick() < int64(projection.Identity.Tick)");
        }
        if ((target.Context?.Tick ?? 0) < projection.Identity.Tick.Value)
        {
            throw new ControlException("recrop: err != nil || target.Context.GetTick() < int64(projection.Identity.Tick)");
        }
        if (target.Crop != choice.Current)
        {
            return (new RoundsFieldResult { Verdict = FieldUnavailable("grower_crop") }, false);
        }
        var patch = new GrowerCrop(choice.Grower, choice.Crop.Name);
        if (!arbiter.TryClaim([], "grower:" + choice.Grower))
        {
            return (new RoundsFieldResult { Verdict = WaitFor(Cause.MethodUsed, "grower_claim") }, false);
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
        var commit = p.Journal.CommitOwnerWork(goal, method, "", plan, call);
        if (commit.Outcome != ClaimOutcome.Admitted)
        {
            return (new RoundsFieldResult { Verdict = RoundsBuildingPlanner.OwnerCommitVerdict(commit) }, false);
        }
        FieldEdit("admitted", "recrop", choice.Grower, new Dictionary<string, object?> { ["from"] = choice.Current, ["to"] = choice.Crop.Name, ["detail"] = choice.Reason }, call);
        return (new RoundsFieldResult { Verdict = BuildingReasonAdmitted, Plan = id }, true);
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

    internal static void FieldEdit(string verdict, string reason, string target, Dictionary<string, object?> attrs, CancellationToken ct)
    {
        attrs["family"] = "field";
        Tel.Decide(new Decision { Kind = TelemetryKinds.LayoutEditKind, Component = "clock-scheduler", Verdict = verdict, Reason = reason, Target = target, Attrs = attrs });
    }
}

partial class RoundsFieldPlanner
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
            Choices = [.. choices],
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
        var anchor = LayoutProtected.FieldAnchor(projection) ?? default;
        var site = new FarmSiteRequest { Anchor = anchor, Cells = projection.Cells, Protected = protectedCells };
        if (LayoutFieldPlanning.LayoutFieldCells(projection) is { } fields)
        {
            site = site with { Fields = fields };
        }
        field = field with { Site = site };
        var request = new SiteTypeRequest { Field = field, Environment = projection.Environment, LampGrowthRadius = RoundsFieldPlanner.FieldLampGrowthRadius, Items = projection.Facts.Items, Stock = projection.Resources };
        foreach (var d in projection.Definitions)
        {
            var infrastructure = Known(new Infrastructure { Name = d.Name, Available = d.Available, PowerW = d.PowerW, Fertility = d.GrowerFertility, Costs = d.CheapestCosts() });
            switch (d.Name)
            {
                case "SunLamp":
                    request = request with { Lamp = infrastructure };
                    break;
                case "HydroponicsBasin":
                    request = request with { Basin = infrastructure };
                    break;
                case "Heater":
                    request = request with { Heater = infrastructure };
                    break;
            }
        }
        return (request, choices);
    }

    internal static List<Cell> FirebreakRing(ColonyProjection projection)
    {
        var ring = PolicyApi.FirebreakRing(FirebreakPlan.FirebreakRequest(projection));
        return ring.TryGetValue(out var cells) ? cells.ToList() : [];
    }

    internal static CropChoice WithHarvestFacts(CropChoice crop, PlanningDefinition d)
    {
        if (d.HarvestedThingDef.TryGetValue(out var name))
        {
            crop = crop with { Harvests = Known(new Resource(name)) };
        }
        crop = crop with { UnitsPerCell = d.HarvestYield };
        crop = crop with { SowMinSkill = d.SowMinSkill };
        crop = crop with { HarvestDestroys = d.HarvestDestroysPlant };
        crop = crop with { BlockAdjacentSow = d.BlockAdjacentSow };
        crop = crop with { MustBeWildToSow = d.MustBeWildToSow };
        crop = crop with { HarvestMinGrowth = d.HarvestMinGrowth };
        crop = crop with { SowWork = d.SowWork };
        crop = crop with { WildBiomes = d.WildBiomes };
        return crop;
    }
}
