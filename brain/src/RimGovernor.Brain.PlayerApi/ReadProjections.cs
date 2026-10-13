using System.Globalization;
using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;

namespace RimGovernor.Brain.PlayerApi;

/// <summary>
/// The projections of internal views onto the read routes' wire shapes (Go <c>httpapi</c> <c>state</c>, <c>plan</c>,
/// <c>roundsStatus</c> and <c>projectColonyStatus</c>). A view that breaks its own invariants is a
/// <see cref="DomainException"/>, which the route answers as an unavailable read, never as a guess.
/// </summary>
internal static class ReadProjections
{
    private const int MaxSessionIdBytes = 256;
    private const int MaxStatusBytes = 2048;
    private const int MaxPlanActions = 1024;

    internal static string? Known<T>(Fact<T> fact, Func<T, string> text) => fact.TryGetValue(out var v) ? text(v) : null;

    internal static T? Value<T>(Fact<T> fact)
        where T : struct => fact.TryGetValue(out var v) ? v : null;

    // state

    internal static StateDto State(ControllerSnapshot snapshot)
    {
        var mode = snapshot.Mode.Length == 0 ? "manual" : snapshot.Mode;
        DomainException.Require(mode is "manual" or "automate", "invalid mode");
        DomainException.Require(
            System.Text.Encoding.UTF8.GetByteCount(snapshot.SessionId) <= MaxSessionIdBytes && System.Text.Encoding.UTF8.GetByteCount(snapshot.Status) <= MaxStatusBytes,
            "oversized snapshot metadata");
        GenerationDto? generation = null;
        if (snapshot.Generation.TryGetValue(out var raw))
        {
            raw.Validate();
            generation = new GenerationDto(raw.Colony.Value, raw.Map.Value, raw.Load.Value, raw.Plan.Value, raw.Revision.Value, raw.Native.Value);
        }

        IdentityDto? identity = null;
        if (snapshot.Identity.TryGetValue(out var observed))
        {
            observed.Validate();
            identity = new IdentityDto(observed.Colony.Value, observed.Map.Value, observed.Load.Value);
        }

        long? tick = snapshot.Tick.TryGetValue(out var t) ? t.Value : null;
        DomainException.Require(tick is null or >= 0, "negative tick");
        bool? paused = snapshot.Paused.TryGetValue(out var p) ? p : null;
        var observedAt = Known(snapshot.ObservedAt, at => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture));
        var stale = snapshot.Stale || !snapshot.Connected || identity is null || tick is null || paused is null || observedAt is null;
        return new StateDto(
            snapshot.SessionId,
            snapshot.Connected,
            mode,
            new StatusDto(snapshot.Status),
            new GameDto(tick, paused, observedAt, stale),
            generation,
            identity,
            Known(snapshot.ActivePlanId, id => id.Value));
    }

    // plan

    internal static PlanDto Plan(PlanState stored)
    {
        stored.Spec.Validate();
        var actions = stored.Spec.Actions;
        DomainException.Require(actions.Count <= MaxPlanActions && actions.Count == stored.Progress.Count, "unavailable or oversized plan progress");
        var result = new List<ActionDto>(actions.Count);
        for (var n = 0; n < actions.Count; n++)
        {
            var action = actions[n];
            var progress = stored.Progress[n];
            var view = progress.View;
            DomainException.Require(
                view.Action == action.Id && view.Plan == stored.Spec.Id && view.Revision == stored.Spec.Revision && Wire.IsDefined(view.Stage),
                "mismatched plan progress");
            DomainException.Require(progress.Action == action, "mismatched action progress");
            IReadOnlyList<string>? held = null;
            if (view.TryGetFreshHeldReason(out var reasons) && reasons.Count > 0)
            {
                held = [.. reasons.Select(r => Wire.Name(r))];
            }

            var projected = new ProgressDto(
                Wire.Name(view.Stage),
                view.Attempt.Value,
                view.Tick.Value,
                view.Unresolved,
                Known(view.Receipt, r => Wire.Name(r)),
                Known(view.Effect, e => Wire.Name(e)),
                Known(view.UnsuccessfulReason, u => Wire.Name(u)),
                held);
            BuildingDto? building = null;
            DraftDto? draft = null;
            if (action.Building is { } b)
            {
                building = new BuildingDto(b.Definition, b.Cell.X, b.Cell.Z, Wire.Name(b.Rotation), b.Stuff);
            }
            else if (action.OwnedDraft is { } d)
            {
                draft = new DraftDto(d.Pawn.Value);
            }

            result.Add(new ActionDto(action.Id.Value, Wire.Name(action.Kind), building, draft, projected));
        }

        return new PlanDto(stored.Spec.Id.Value, stored.Spec.Revision.Value, result);
    }

    // routines

    internal static RoundsStatusDto Rounds(RoundsStatus v)
    {
        var extent = v.Extent.TryGetValue(out var known) ? known : null;
        var extentDto = new RoundsExtentDto(extent is not null, extent?.Regions.Count ?? 0, extent?.Regions.Sum(r => r.Cells.Count) ?? 0);
        var eligibility = PolicyFunctions.ExtentEligibility(v.ExtentEligibility);
        return new RoundsStatusDto(
            [.. v.Emergency.Select(c => c.Value)],
            new ExtentEligibilityDto(
                eligibility.Known,
                eligibility.Reason,
                [.. eligibility.Regions.Select(r => new ExtentRegionEligibilityDto(r.Region, r.Stage, r.Cells, [.. r.Origins.Select(o => Wire.Name(o))], [.. r.Facilities], [.. r.ActiveFacilities], r.Eligible, [.. r.HoldReasons]))]),
            extentDto,
            [.. v.ResourceRunways.Select(Runway)],
            v.ReviewsEnabled,
            v.MethodsEnabled,
            [.. v.ActiveFamilies],
            v.LastReviewTick.TryGetValue(out var last) ? last.Value : null,
            [.. v.Progress.Select(ConcernProgressRow)],
            v.Stage is { } s ? new ColonyStageDto(s.Stage.Name(), s.Since.Value, Wire.Name(s.Blocker), s.Reason, s.Held) : null,
            v.Roster is { } roster ? Roster(roster) : null,
            [.. v.Sections.Select(x => new RoundsSectionDto(Wire.Name(x.Section), Wire.Name(x.Family), x.AsOf, x.Complete, x.Source, x.StoredAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture)))],
            [.. v.NoOps.Select(n => new NoOpDto(n.Concern.Value, Wire.Name(n.Reason)))],
            [.. v.LootHolds.Select(h => new LootHoldDto(h.Thing, h.Definition, h.Cell.X, h.Cell.Z, h.Reason))]);
    }

    private static ResourceRunwayDto Runway(ResourceRunway r) => new(
        r.Resource.Value,
        r.Tick.Value,
        r.WindowDays,
        PolicyFunctions.ProjectionHorizonDays,
        r.Reserve,
        Value(r.Stock),
        Value(r.SurfaceOre),
        Value(r.ConsumptionPerDay),
        Value(r.StockDays),
        Value(r.DaysLeft),
        Value(r.Deficit),
        r.Target);

    private static ConcernProgressDto ConcernProgressRow(ConcernProgress p)
    {
        var subject = p.BlockedSubject();
        return new ConcernProgressDto(
            p.Concern.Value,
            p.Method,
            p.Expected,
            p.LastProgress.Value,
            p.NextReview.Value,
            p.Blocked.Value,
            subject.Length == 0 ? null : subject,
            [.. p.Cooldowns.Select(c => new ProgressCooldownDto(c.Key, c.Until.Value))]);
    }

    private static RoundsRosterDto Roster(WorkRosterReport r)
    {
        var coverage = r.Coverage.Select(c => new RoundsCoverageDto(Wire.Name(c.Work), c.Demand, c.Owners, c.Capable)).OrderBy(c => c.Work, StringComparer.Ordinal).ToList();
        var decaying = r.Decaying.Select(d => new RoundsDecayingDto(d.Pawn.Value, d.Skill, d.Level))
            .OrderBy(d => d.Pawn, StringComparer.Ordinal).ThenBy(d => d.Skill, StringComparer.Ordinal).ToList();
        var pawns = r.Profiles.Select(RosterPawn).OrderBy(p => p.Pawn, StringComparer.Ordinal).ToList();
        return new RoundsRosterDto(r.Tick.Value, coverage, decaying, pawns);
    }

    private static RoundsRosterPawnDto RosterPawn(PawnProfile p)
    {
        var skills = p.Skills.Values
            .Select(s => new RoundsProfileSkillDto(s.Name, s.Level, s.Stored, s.Passion, s.Disabled, s.LearnFactor(p.Effects)))
            .OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
        var incapable = p.Incapable.Where(kv => kv.Value).Select(kv => Wire.Name(kv.Key)).OrderBy(w => w, StringComparer.Ordinal).ToList();
        var e = p.Effects;
        return new RoundsRosterPawnDto(
            p.Id.Value,
            p.Age,
            p.Child,
            p.Ranged,
            [.. p.Traits.Select(t => new RoundsTraitDto(t.Name, t.Degree))],
            new RoundsTraitEffectsDto(e.WorkSpeed, e.LearnRate, e.MoveSpeed, e.Sociable, e.ChemicalInterest, [.. e.Flags()]),
            skills,
            incapable,
            [.. p.ForbiddenWork().Select(w => Wire.Name(w))]);
    }

    // colony

    internal static ColonyStatusDto Colony(ColonyStatusReport v)
    {
        IReadOnlyList<ColonyStockpileDto>? stockpiles = v.Stockpiles.TryGetValue(out var zones)
            ? [.. zones.Select(z => new ColonyStockpileDto(z.Role, z.Zones, z.Cells, z.Used))]
            : null;
        IReadOnlyList<ColonyShrineDto>? shrines = null;
        if (v.Shrines.TryGetValue(out var shrineRows))
        {
            shrines = [.. shrineRows.Select(shrine => Shrine(shrine, v.ShrineReadiness))];
        }

        var downed = 0;
        var moodSum = 0.0;
        var moodCount = 0;
        var pawns = new List<ColonyStatusPawnDto>(v.Pawns.Count);
        foreach (var pawn in v.Pawns)
        {
            var row = new ColonyStatusPawnDto(
                pawn.Id.Value,
                pawn.Label,
                Value(pawn.Downed),
                Value(pawn.Mood),
                Value(pawn.Food),
                Value(pawn.Share),
                Value(pawn.Spent),
                Value(pawn.Remaining));
            if (row.Downed == true)
            {
                downed++;
            }

            if (row.Mood is { } mood)
            {
                moodSum += mood;
                moodCount++;
            }

            pawns.Add(row);
        }

        return new ColonyStatusDto(
            v.FoodPlan.TryGetValue(out var plan) ? FoodPlanRow(plan, v.PetLabels) : null,
            v.FoodPlanTick.TryGetValue(out var planTick) ? planTick.Value : null,
            v.Tick.Value,
            v.RosterTick.Value,
            Value(v.Colonists),
            Value(v.Workers),
            Value(v.FoodNutrition),
            Value(v.NutritionPerDay),
            Value(v.FoodRunwayDays),
            Value(v.PendingFoodNutrition),
            Value(v.Threat.RaidPoints),
            Value(v.Threat.WealthTotal),
            Value(v.Threat.WealthItems),
            Value(v.Threat.WealthBuildings),
            Value(v.Threat.WealthPawns),
            v.PlayerTechLevel.TryGetValue(out var level) ? level : null,
            v.TechTier.TryGetValue(out var tier) ? tier.ToString() : null,
            shrines,
            stockpiles,
            Value(v.ForbiddenSupplies),
            downed,
            moodCount > 0 ? moodSum / moodCount : null,
            v.MoodLedger.TryGetValue(out var ledger) ? MoodLedgerRow(ledger) : null,
            pawns);
    }

    private static ColonyShrineDto Shrine(AncientShrine shrine, IReadOnlyList<ShrineReadinessReport> readiness)
    {
        bool? ready = null;
        string? reason = null;
        string? wall = null;
        var squad = 0;
        var traps = 0;
        foreach (var judged in readiness)
        {
            if (judged.Shrine != shrine.Id)
            {
                continue;
            }

            (ready, reason, squad, traps) = (judged.Readiness.Ready, judged.Readiness.Reason, judged.Readiness.Squad.Count, judged.Readiness.Traps);
            wall = judged.Readiness.Wall.EntityId.Length == 0 ? null : judged.Readiness.Wall.EntityId;
        }

        return new ColonyShrineDto(shrine.Id, shrine.Sealed, shrine.InHome, shrine.Caskets.Count, shrine.FilledCaskets(), shrine.GuardsKnown, shrine.GuardsAlive(), shrine.BreachWalls.Count, ready, reason, wall, squad, traps);
    }

    private static MoodLedgerDto MoodLedgerRow(MoodLedger l)
    {
        static List<MoodSourceDto> Project(IReadOnlyList<MoodLedgerSource> rows) =>
            [.. rows.Select(s => new MoodSourceDto(s.Def, s.Pawns, s.Lost, [.. s.Owners.Select(o => o.Value)], s.Unverified))];
        return new MoodLedgerDto(Project(l.Sources), Project(l.Unowned), l.UnknownPawns, [.. l.Expectation.Select(e => new MoodExpectationDto(e.Level, e.Pawns))]);
    }

    private static FoodPlanDto FoodPlanRow(FoodPlan p, IReadOnlyDictionary<RimGovernor.Brain.Policy.PawnId, string> labels)
    {
        List<FoodPlanEntryDto> Project(IReadOnlyList<FoodPlanEntry> rows) =>
            [.. rows.Select(row =>
            {
                var c = row.Channel;
                return new FoodPlanEntryDto(
                    c.Kind.ToString(),
                    c.Id,
                    row.Decision.ToString(),
                    row.Reason,
                    Value(c.Nutrition().PerDay),
                    Value(c.LaborPerDay),
                    Value(c.LeadDays),
                    Value(c.Open()),
                    row.DeliveredPerDay,
                    [.. row.Terms.Select(t => new CandidateTermDto(t.Name, t.Value))]);
            })];
        var pets = p.Forecast.PetShortfalls
            .Select(row => new PetShortfallDto(row.Id.Value, labels.TryGetValue(row.Id, out var label) ? label : null, row.RunwayDays, row.NutritionPerDay))
            .ToList();
        return new FoodPlanDto(Project(p.Portfolio), Project(p.Unknown), p.DeliveredPerDay, p.DemandPerDay, p.GapPerDay, p.Explain(), pets);
    }
}
