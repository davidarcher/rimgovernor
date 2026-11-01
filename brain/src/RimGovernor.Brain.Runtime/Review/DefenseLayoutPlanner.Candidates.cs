using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace RimGovernor.Brain.Runtime;

public sealed partial class DefenseLayoutPlanner
{
    /// <summary>The killbox tiers' census request needs the ranged defenders read; named while it is not.</summary>
    internal const string DefenseTiersNoDefender = "defense_tiers:ranged_defender";

    /// <summary>The step's verdict subject while the chosen alternative owes protection it has nothing left to build for (an unpowered turret, a staffing gap).</summary>
    internal const string DefenseProtectionOwed = "defense_protection";

    /// <summary>The reason of the chosen alternative's row; a costlier one's is <see cref="DefenseCostlier"/>.</summary>
    internal const string DefenseChosen = "defense_chosen";

    internal const string DefenseCostlier = "defense_costlier";

    private StoreWorld candidatesWorld;
    private Tick candidatesProbed;
    private string candidatesStanding = "";
    private DefenseDecision? candidatesDecision;
    internal string CandidatesLogged = "";

    /// <summary>
    /// Generates the defense alternatives over the plan, probes each with its pieces placed and as it stands, and compares
    /// them (DefenseChoiceRules.Choose) at the reading's harm, free builders, material budget, standing buildings and power
    /// network. The decision is kept for the re-verify window while what it read does not change (DefenseBindingRules.ReviewKey:
    /// a piece lost or completed, turret power, the raid tracks and points, the able shooters, the bound node), and
    /// each alternative is surfaced as a <c>defense_action</c> row with its price terms.
    /// </summary>
    internal async Task<DefenseDecision> CompareAsync(ControlState state, ReviewReading read, DefenseCut cut, StoreWorld world, Tick tick, CancellationToken call)
    {
        var projection = read.Reading;
        var standing = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(DefenseBindingRules.ReviewKey(projection.Facts, cut.Plan, read.Emergency.Colonists.Count(c => !c.Dead && !c.Downed)))));
        if (candidatesDecision is { } kept && candidatesWorld == world && candidatesStanding == standing && tick.Value - candidatesProbed.Value < DefenseReverifyTicks)
        {
            return kept;
        }
        var (survey, _) = await _reviewer.LayoutSurveyAsync(state.Snapshot, projection, call).ConfigureAwait(false);
        var traced = DefenseCandidateRules.Trace(cut.Plan, survey);
        var (tiers, dig, missing) = await DefenseTracedTiersAsync(state, read, traced, call).ConfigureAwait(false);
        var facts = await WithWorkViewAsync(state, read, call).ConfigureAwait(false);
        var inputs = new DefenseCandidateInputs
        {
            Plan = cut.Plan,
            Survey = survey,
            Facts = facts,
            Threat = DefenseCandidateRules.ThreatOf(facts),
            Definitions = DefenseDefinitionNames,
            Built = LayoutPlan.BuiltRooms(cut.Plan, BuildingPlanner.ColonyGround(projection)),
            Tiers = tiers,
            TiersMissing = missing,
            Dig = dig,
            Turret = DefenseTurretRung(read),
            Conduit = DefenseConduitDefinition,
            Bridge = cut.Bridge,
        };
        var candidates = DefenseCandidateRules.Generate(inputs, traced);
        List<DefenseCandidate> asked = [.. candidates, .. DefenseChoiceRules.Shadows(candidates)];
        var probes = DefenseProtectionRules.Probes(asked);
        var probed = probes.Count == 0 ? null
            : await _native.ReadDefenseProbeAsync(Boundary.Identity(state.Snapshot), probes, call).ConfigureAwait(false);
        if (probed is not null)
        {
            SameTick(probed.Context, state, projection.Identity.Tick);
        }
        // A reply row no probe asked for is refused (RequiredFactException), skipping the step.
        var protections = DefenseProtectionRules.Protections(asked, probes, probed, DefenseGround.Of(cut.Plan));
        var values = await _reviewer.MaterialValuesAsync(state.Snapshot, DefenseChoiceRules.Materials(candidates), facts.Stations, facts.Items, call).ConfigureAwait(false);
        var decision = DefenseChoiceRules.Choose(DefenseChoiceRules.Alternatives(candidates, protections), DefenseChoiceRules.InputsOf(facts, cut.Plan, _reviewer.RaidHarm.Get(state.Snapshot), values));
        (candidatesWorld, candidatesProbed, candidatesStanding, candidatesDecision) = (world, tick, standing, decision);
        LogDecision(decision, call);
        return decision;
    }

    /// <summary>Forgets the kept decision: an admission changes what the next comparison reads.</summary>
    internal void ForgetDecision() => candidatesDecision = null;

    /// <summary>
    /// The reading's facts with the work view the colony review derives (WorkRules.ReviewWorkResources): the running Methods'
    /// pawn and material claims, so the comparison's builders and materials are net of them.
    /// </summary>
    private async Task<ReviewFacts> WithWorkViewAsync(ControlState state, ReviewReading read, CancellationToken call)
    {
        var p = _reviewer.Player;
        var facts = read.Reading.Facts;
        if (facts.WorkView is not null)
        {
            return facts;
        }
        var catalog = read.Catalog ?? throw new RequiredFactException("definition_catalog");
        facts = facts with
        {
            WorkAdmission = new()
            {
                Buildings = catalog.WorkBuildingCosts(),
                Recipes = catalog.ProductionRecipeInputs(read.Frame.Tables.Buildings.Table.Values().Select(b => b.Building.DefName)),
                Bills = ConstructionReads.ProductionBatchStates(read.Frame.Bills),
            },
        };
        var batches = await p.Journal.ProductionBatchesAsync(state.Snapshot, read.Reading.Identity.Tick, call).ConfigureAwait(false);
        return WorkRules.ReviewWorkResources(facts, batches, p.Journal.WorkActions(state.Snapshot, call));
    }

    /// <summary>The killbox tiers sited on the traced plan's opening, the rock it mines first, and the missing fact while the census request is unread.</summary>
    internal async Task<(DefenseTiers? Tiers, IReadOnlyList<Cell> Dig, string Missing)> DefenseTracedTiersAsync(ControlState state, ReviewReading read, LayoutPlan traced, CancellationToken call)
    {
        var (killbox, region, home, ok) = DefenseKillboxOf(traced, read.Reading.Facts.MapBounds);
        if (!ok)
        {
            return (null, [], DefenseCandidateRules.MissingTiers);
        }
        var (request, _, cok) = await DefenseCensusRequestAsync(state, read, killbox, region, home, call).ConfigureAwait(false);
        if (!cok)
        {
            return (null, [], DefenseTiersNoDefender);
        }
        DefenseRecorder.RecordLayoutSnapshot(state.Snapshot, read.Reading.Identity.Tick, new SnapshotLayout { Point = SnapshotLayoutReplay.LayoutPropose, Request = request }, call);
        IReadOnlyList<Cell> dig;
        try
        {
            dig = [.. DefenseLayout.DefenseRockCells(request).Select(r => r.Cell)];
        }
        catch (Exception e) when (e is DefenseException or DomainException)
        {
            dig = [];
        }
        try
        {
            return (DefenseTiersPolicy.PlanDefenseTiers(traced, request, new DefenseTierPins()), dig, "");
        }
        catch (Exception e) when (e is DefenseException or DomainException)
        {
            return (new DefenseTiers { Refusal = e.Message }, dig, "");
        }
    }

    /// <summary>
    /// Admits the bound choice's free unbuilt pieces as one Method of the Need (<c>defense-choice-&lt;kind&gt;-&lt;digest&gt;</c>),
    /// each previewed natively over its definition's footprint: a piece whose cell already holds native work is left to it, a
    /// refused preview refuses the admission. Admission claims the pieces' builders, materials and ground; a Method that
    /// Hands reports refused spends the kind's refusal budget (ReviewRefusalBudget). The protection they add is read once
    /// they stand.
    /// </summary>
    internal async Task<DefenseLayoutResult> AdmitChoiceAsync(NeedOwner goal, ControlState state, ReviewReading read, DefenseChoiceRow chosen, CancellationToken call, CancellationToken epoch)
    {
        var p = _reviewer.Player;
        var projection = read.Reading;
        var tick = projection.Identity.Tick;
        var kind = chosen.Alternative.Candidate.Kind;
        var name = Wire.Name(kind);
        var text = new StringBuilder();
        foreach (var s in chosen.Unbuilt)
        {
            text.Append(s.Definition).Append('@').Append(s.Cell.X).Append(',').Append(s.Cell.Z).Append(';');
        }
        var prefix = DefenseBindingRules.ChoicePrefix + name + "-";
        var (budget, admitted) = ReviewRefusalBudget.AdmitOwnerSubject(p.Journal, goal, prefix, state.Snapshot, call);
        if (!admitted)
        {
            return new DefenseLayoutResult { Verdict = budget, Tier = name };
        }
        var method = new MethodId($"{prefix}{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())).AsSpan(0, 8))}");
        var id = PlanIds.MintPlanId();
        var snapshot = state.Snapshot with { Plan = id, Revision = new PlanRevision(1) };
        var promoted = DefensePromoted(projection);
        var core = kind is DefenseCandidateKind.PerimeterDoored or DefenseCandidateKind.PerimeterOpen or DefenseCandidateKind.Killbox;
        var roamer = DefenseRoamerOwned(projection);
        var candidates = new List<DomainAction>(chosen.Unbuilt.Count);
        for (var i = 0; i < chosen.Unbuilt.Count; i++)
        {
            var s = chosen.Unbuilt[i];
            var building = new Building(s.Definition, s.Cell, Rotation.North, s.Stuff);
            candidates.Add(DomainAction.NewBuildingAction(new ActionId(string.Create(CultureInfo.InvariantCulture, $"{id}-{i}")), building, DefenseTier.DefenseBuildTier(promoted, core, roamer)));
        }
        var evaluated = await _native.PreviewBuildingsAsync(candidates, snapshot, call).ConfigureAwait(false);
        var actions = new List<DomainAction>();
        var previews = new List<Preview>();
        var stock = new StockObservation { Snapshot = snapshot, Tick = tick };
        for (var i = 0; i < candidates.Count; i++)
        {
            var cell = chosen.Unbuilt[i].Cell;
            var (preview, ok) = ClassifyDefensePreview(evaluated[i], candidates[i].Building!, DefenseSize(projection, chosen.Unbuilt[i].Definition));
            if (!ok && preview.NativeWorkPending)
            {
                continue;
            }
            if (!ok)
            {
                DefensePipeline.DefenseAction("defense-layout", TelemetryLevel.Warn, "refused", "placement_preview_refused", chosen.Key, new Dictionary<string, object?>
                {
                    ["definition"] = chosen.Unbuilt[i].Definition, ["x"] = cell.X, ["z"] = cell.Z, ["native_reason"] = evaluated[i].Reason,
                }, call);
                return new DefenseLayoutResult { Verdict = FieldUnavailable("placement_preview"), Tier = name };
            }
            actions.Add(candidates[i]);
            previews.Add(preview.Preview);
            BuildingPlanner.MergeReviewStock(stock, preview.Stock);
        }
        if (actions.Count == 0)
        {
            return new DefenseLayoutResult { Verdict = BuildingReasonExistingWork, Tier = name };
        }
        var plan = new PlanSpec(id, new PlanRevision(1), actions);
        p.Current(call, epoch);
        if (p.Session.State() != state)
        {
            throw DefenseControlErr(358);
        }
        var scope = await StepScope.StepScopeAsync(_reviewer.Native, call).ConfigureAwait(false);
        if (!BuildingPlanner.BuildingBoundary(scope, state.Snapshot, tick))
        {
            throw DefenseControlErr(366);
        }
        var now = _reviewer.Clock.Now();
        if (now < read.StartedAt || now - read.StartedAt > _reviewer.MaxAge)
        {
            throw new RimGovernor.Brain.GameAccess.ObservationStaleException();
        }
        var decision = await AdmissionLog.AdmitMethodAsync(p.Journal, new BuildingMethodRequest
        {
            Owner = goal,
            Method = method,
            Plan = plan,
            Current = snapshot,
            Tick = tick,
            Bounds = projection.Facts.MapBounds,
            Stock = stock,
            Previews = previews,
            Purpose = Purpose.Defense,
        }, call).ConfigureAwait(false);
        if (!decision.Admitted)
        {
            return new DefenseLayoutResult { Verdict = AdmissionLog.AdmissionRefused(decision), Plan = id, Tier = name };
        }
        ForgetDecision();
        DefensePipeline.DefenseAction("defense-layout", TelemetryLevel.Info, "applied", "builds", chosen.Key, new Dictionary<string, object?> { ["builds"] = actions.Count, ["method"] = method.Value }, call);
        return new DefenseLayoutResult { Verdict = BuildingReasonAdmitted, Plan = id, Tier = name };
    }

    /// <summary>One <c>defense_action</c> row per compared alternative with its protection and price terms, once per distinct decision.</summary>
    internal void LogDecision(DefenseDecision decision, CancellationToken call)
    {
        var digest = new StringBuilder();
        foreach (var r in decision.Rows)
        {
            digest.Append(r.Key).Append('/').Append(r.Price).Append('/').Append(r.Alternative.Protection).Append('/').Append(r.Alternative.Standing).Append(';');
        }
        digest.Append(decision.Chosen.Key);
        var key = digest.ToString();
        if (key == CandidatesLogged)
        {
            return;
        }
        CandidatesLogged = key;
        foreach (var r in decision.Rows)
        {
            var c = r.Alternative.Candidate;
            var guard = r.Alternative.Protection;
            var (work, costs, unpriced) = DefenseCandidateRules.Price(c);
            var chosen = ReferenceEquals(r, decision.Chosen);
            var (verdict, reason) = r.Price is DefensePrice.Excluded x ? ("refused", x.Reason)
                : r.Price is DefensePrice.Unassessed u ? ("waiting", u.Fact)
                : ("waiting", chosen ? DefenseChosen : DefenseCostlier);
            DefensePipeline.DefenseAction("defense-layout", TelemetryLevel.Info, verdict, reason, c.Key, new Dictionary<string, object?>
            {
                ["kind"] = Wire.Name(c.Kind),
                ["chosen"] = decision.Chosen.Key,
                ["choice_reason"] = decision.Reason,
                ["price"] = PriceText(r.Price),
                ["construction"] = TermText(r.Construction),
                ["lead_days"] = TermText(r.LeadDays),
                ["route_cost"] = TermText(r.Routes),
                ["interruption"] = TermText(r.Interruption),
                ["downtime"] = TermText(r.Downtime),
                ["removes"] = string.Join(" ", r.Removes),
                ["unbuilt"] = r.Unbuilt.Count,
                ["deficit"] = chosen ? decision.Deficit : 0,
                ["cells"] = c.Cells.Count,
                ["sites"] = c.Sites.Count,
                ["structures"] = c.Structures.Count,
                ["work"] = work,
                ["costs"] = string.Join(" ", costs.Select(a => string.Create(CultureInfo.InvariantCulture, $"{a.Resource.Value}:{a.Count}"))),
                ["unpriced"] = unpriced,
                ["dig"] = c.Dig.Count,
                ["closes"] = c.Closes.Count,
                ["narrows"] = c.Narrows.Count,
                ["routes"] = string.Join(" ", c.Routes.Select(RouteText)),
                ["exclusions"] = string.Join(" ", c.Exclusions),
                ["missing"] = string.Join(" ", c.Missing.Concat(c.Threat.Missing)),
                ["exposure"] = ExposureText(c.Threat.Exposure),
                ["arrival_edges"] = c.Threat.ArrivalEdges.Count,
                ["firing"] = c.Firing.Count,
                ["barriers"] = c.Barriers.Count,
                ["covered"] = TermText(guard.Covered),
                ["covered_standing"] = r.Alternative.Standing is { } s ? TermText(s.Covered) : "",
                ["shots"] = TermText(guard.Shots),
                ["cover"] = TermText(guard.Cover),
                ["staffing"] = TermText(guard.Staffing),
                ["ammunition"] = TermText(guard.Ammunition),
                ["power"] = TermText(guard.Power),
                ["civilians_exposed"] = TermText(guard.Exposure),
                ["civilians_before"] = string.Join(" ", guard.Civilians.Before),
                ["civilians_after"] = string.Join(" ", guard.Civilians.After),
                ["protection_states"] = string.Join(" ", guard.States),
            }, call);
        }
    }

    private static string PriceText(DefensePrice p) =>
        p is DefensePrice.Assessed a ? a.Days.ToString("0.###", CultureInfo.InvariantCulture)
        : p is DefensePrice.Excluded x ? x.Reason
        : ((DefensePrice.Unassessed)p).Fact;

    private static string ExposureText(DefenseExposure e) =>
        e is DefenseExposure.Binned b ? string.Create(CultureInfo.InvariantCulture, $"{b.Bin}:{b.RaidPoints:0.#}/{b.Capacity:0.#}")
        : e is DefenseExposure.ZeroCapacity z ? string.Create(CultureInfo.InvariantCulture, $"zero_capacity:{z.RaidPoints:0.#}")
        : ((DefenseExposure.Unread)e).Fact;

    private static string TermText(DefenseTerm t) =>
        t is DefenseTerm.Assessed a ? a.Value.ToString("0.###", CultureInfo.InvariantCulture) : ((DefenseTerm.Unassessed)t).Fact;

    private static string RouteText(DefenseRouteChange r) =>
        r.Effect is DefenseRouteEffect.Longer l ? string.Create(CultureInfo.InvariantCulture, $"{r.Route}+{l.AddedCells}")
        : r.Effect is DefenseRouteEffect.Closed ? r.Route + ":closed"
        : r.Route + ":unprobed";
}
