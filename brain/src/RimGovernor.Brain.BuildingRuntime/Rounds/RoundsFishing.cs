using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.BuildingRuntime;

partial class RoundsFieldPlanner
{
    internal static IReadOnlyList<WorkRequirement> FishingWork(ColonyReading p)
    {
        if (p.Facts.FoodPlan is not { } plan)
        {
            return [];
        }

        foreach (var row in plan.Portfolio)
        {
            if (row.Channel.Kind == CandidateKind.Fishing && row.Decision != FoodPlanDecision.Close && row.Selected)
            {
                return [new WorkRequirement { Work = WorkType.Fishing }];
            }
        }

        return [];
    }
}

public sealed partial class Rounder
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _researchWanted = new(StringComparer.Ordinal);

    /// <summary>The research the player's goals asked for in the latest review (#3494); replaced each review, so a finished or removed goal stops asking.</summary>
    private volatile IReadOnlyList<string> _goalResearch = [];

    /// <summary>Records the projects a planner's research wait names: a planner that cannot proceed for want of research is the evidence that the research is owed.</summary>
    internal void NoteResearchWait(Verdict v)
    {
        if (v.Outcome != Outcome.Refused || v.Refusal.Kind != Cause.AwaitingPlan || v.Refusal.Subject != "research")
        {
            return;
        }
        foreach (var project in v.Refusal.Detail.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            _researchWanted[project] = 0;
        }
    }

    /// <summary>Adds the research planners reported waiting on to what the census asks for.</summary>
    internal IReadOnlyList<string> CensusResearchNeeds(IReadOnlyList<string> needs)
    {
        var census = CensusResearchNeedsCore(needs);
        // The research the player's goals ask for (the latest review's list, replaced each review).
        var goals = _goalResearch.Where(g => !census.Contains(g)).ToList();
        if (goals.Count != 0)
        {
            census = [.. census, .. goals];
        }
        var wanted = _researchWanted.Keys.Where(k => !census.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        return wanted.Count == 0 ? census : [.. census, .. wanted];
    }

    /// <summary>Adds the research the latest census asks for: the fishing request and, once everyone has a bedroom, ComplexFurniture.</summary>
    private IReadOnlyList<string> CensusResearchNeedsCore(IReadOnlyList<string> needs)
    {
        lock (Census.Mu)
        {
            var census = Census.Latest;
            if (census is null || census.Generation != Census.Generation)
            {
                return needs;
            }

            var p = census.Reading.Reading;
            var state = Player.Session.State();
            if (!RoundsBuildingPlanner.RoundsBuildingBoundary(p.Identity, state.Snapshot, new Tick(0)))
            {
                return needs;
            }

            var plan = p.Facts.FoodPlan;
            if (p.Facts.FoodChannels.FishableWater is { } w && plan is not null)
            {
                var target = FishingRules.FishingResearchRequest(plan, w.Research);
                if (target.Length != 0)
                {
                    needs = [.. needs, target];
                }
            }

            if (p.Facts.LayoutPlan is { } layout)
            {
                needs = SleepingRules.BedResearchRequest(needs, layout, p.Facts.Upkeep.Rooms, p.Facts.Sleeping, RoundsSleepingUpkeepPlanner.BedroomTargets(p));
            }

            return needs;
        }
    }
}

public sealed partial class RoundsFieldPlanner
{
    /// <summary>
    /// Fishing is the food/supply Need's work, committed under it through the ordinary zone Hands handler. The footprint offers access;
    /// native population-floor settings govern catches.
    /// </summary>
    internal async Task<(RoundsFieldResult Result, bool Handled)> FishingAsync(ControlState state, NeedOwner goal, RoundsReading read, CancellationToken call, CancellationToken epoch)
    {
        var p = read.Reading;
        var plan = p.Facts.FoodPlan;
        if (plan is null || p.Facts.FoodChannels.FishableWater is not { } water || water.Research is not FishingResearch.Researched)
        {
            return (new RoundsFieldResult(), false);
        }

        foreach (var entry in plan.Portfolio)
        {
            if (entry.Channel.Kind != CandidateKind.Fishing || entry.Decision == FoodPlanDecision.Close || !entry.Selected)
            {
                continue;
            }

            foreach (var region in water.Regions)
            {
                if (entry.Channel.Id != FishingRules.FishingRegionId(region.Observed.Root))
                {
                    continue;
                }

                // An open zone is fished by the game; it must not hold the rest of the field planner, or crops
                // are never planned while the fish run down to their population floor.
                if (region.Delivering)
                {
                    continue;
                }

                if (entry.Decision != FoodPlanDecision.Open || region.Observed.Zoned || region.ProposedCells.Count == 0)
                {
                    continue;
                }

                var method = new MethodId("fishing-" + entry.Channel.Id);
                var journal = _reviewer.Player.Journal;
                if (journal.OwnerMethodPlan(goal, method, call) is not null)
                {
                    continue;
                }

                var zone = ZoneCreate.NewFishingZone(region.ProposedCells);
                var id = PlanIds.MintPlanId();
                var action = DomainAction.NewZoneCreateAction(new ActionId(id.Value + "-0"), zone);
                var (reply, refused) = await ZonePreviews.PreviewZoneAsync(_native, Boundary.Identity(state.Snapshot), zone, call).ConfigureAwait(false);
                if (refused.Length != 0)
                {
                    continue;
                }

                Boundary.Context(reply?.Outcome?.Evaluated?.Context, state.Snapshot);
                var spec = new PlanSpec(id, new PlanRevision(1), [action]);
                _reviewer.Player.Current(call, epoch);
                Identity actual;
                try
                {
                    actual = await StepScope.StepScopeAsync(_reviewer.Native, call).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    throw new ControlException("fishing: step scope unreadable", e);
                }

                if (!RoundsBuildingPlanner.RoundsBuildingBoundary(actual, state.Snapshot, p.Identity.Tick) || _reviewer.Player.Session.State() != state)
                {
                    throw new ControlException("fishing: boundary moved or session state changed");
                }

                var now = _reviewer.Clock.Now();
                if (now < read.StartedAt || now - read.StartedAt > _reviewer.MaxAge)
                {
                    throw new ObservationStaleException();
                }

                var snapshot = state.Snapshot with { Plan = id, Revision = new PlanRevision(1) };
                var preview = PreviewRules.Zone(action, snapshot, zone.Cells);
                var decision = await AdmissionLog.AdmitMethodAsync(journal, new BuildingMethodRequest
                {
                    Owner = goal,
                    Method = method,
                    Plan = spec,
                    Current = snapshot,
                    Tick = p.Identity.Tick,
                    Bounds = p.Facts.MapBounds,
                    Stock = new StockObservation { Snapshot = snapshot, Tick = p.Identity.Tick },
                    Previews = [preview],
                    Purpose = Purpose.Rounds,
                }, call).ConfigureAwait(false);
                if (!decision.Admitted)
                {
                    return (new RoundsFieldResult { Verdict = AdmissionLog.AdmissionRefused(decision) }, true);
                }

                return (new RoundsFieldResult { Verdict = BuildingReasonAdmitted, Plan = id }, true);
            }
        }

        return (new RoundsFieldResult(), false);
    }
}
