namespace RimGovernor.Brain.BuildingRuntime;

internal static partial class BuildingRuntimePackage
{
    internal static IReadOnlyList<WorkRequirement> FishingWork(ColonyProjection p)
    {
        if (!p.Facts.FoodPlan.TryGetValue(out var plan))
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

    /// <summary>
    /// The DarkStudy owner the held entities owe; an unread held or studiable fact is logged loudly and
    /// leaves the requirement unknown, so the work planner waits.
    /// </summary>
    internal static Fact<EquatableArray<WorkRequirement>> StudyWork(ColonyProjection p, CancellationToken ct)
    {
        var (work, reason) = PolicyFunctions.StudyWork(p.Facts.Containment);
        if (!work.IsKnown)
        {
            Tel.Decide(new Decision
            {
                Kind = "routine_skip",
                Component = "routine-work",
                Level = TelemetryLevel.Warn,
                Verdict = "waiting",
                Reason = "entity_study_unread",
                Target = "entity_study",
                Attrs = new Dictionary<string, object?> { ["detail"] = reason },
            });
        }

        return work;
    }
}

public sealed partial class Rounder
{
    /// <summary>Adds the research the latest census asks for: the fishing request and, once everyone has a bedroom, ComplexFurniture.</summary>
    internal IReadOnlyList<string> CensusResearchNeeds(IReadOnlyList<string> needs)
    {
        lock (Census.Mu)
        {
            var census = Census.Latest;
            if (census is null || census.Generation != Census.Generation)
            {
                return needs;
            }

            var p = census.Reading.Projection;
            var state = Player.Session.State();
            if (!RoundsBuildingBoundary(p.Identity, state.Snapshot, new Tick(0)))
            {
                return needs;
            }

            var ck = p.FoodChannels.TryGetValue(out var c);
            var wk = false;
            FishableWater w = default!;
            if (ck)
            {
                wk = c!.FishableWater.TryGetValue(out w!);
            }

            var pk = p.Facts.FoodPlan.TryGetValue(out var plan);
            if (ck && wk && pk)
            {
                var target = PolicyFunctions.FishingResearchRequest(plan!, w.FishingResearched);
                if (target.Length != 0)
                {
                    needs = [.. needs, target];
                }
            }

            if (p.LayoutPlan.TryGetValue(out var layout)
                && p.Rooms.TryGetValue(out var rooms)
                && p.Facts.Sleeping.TryGetValue(out var sleeping))
            {
                needs = PolicyFunctions.BedResearchRequest(needs, layout, rooms, sleeping, BedroomTargets(p));
            }

            return needs;
        }
    }
}

public sealed partial class RoundsFieldPlanner
{
    /// <summary>
    /// Fishing uses the shared food goal and the ordinary zone Hands handler. The footprint offers access;
    /// native population-floor settings govern catches.
    /// </summary>
    internal async Task<(RoundsFieldResult Result, bool Handled)> FishingAsync(ControlState state, StandardState goal, RoundsReading read, CancellationToken call, CancellationToken epoch)
    {
        var p = read.Projection;
        var pk = p.Facts.FoodPlan.TryGetValue(out var plan);
        var ck = p.FoodChannels.TryGetValue(out var census);
        if (!pk || !ck || !census!.FishableWater.TryGetValue(out var water)
            || !water.FishingResearched.TryGetValue(out var researched) || !researched)
        {
            return (new RoundsFieldResult(), false);
        }

        foreach (var entry in plan!.Portfolio)
        {
            if (entry.Channel.Kind != CandidateKind.Fishing || entry.Decision == FoodPlanDecision.Close || !entry.Selected)
            {
                continue;
            }

            foreach (var region in water.Regions)
            {
                if (entry.Channel.Id != PolicyFunctions.FishingRegionId(region.Root))
                {
                    continue;
                }

                // An open zone is fished by the game; it must not hold the rest of the field planner, or crops
                // are never planned while the fish run down to their population floor.
                if (region.Delivering.TryGetValue(out var open) && open)
                {
                    continue;
                }

                var zk = region.Zoned.TryGetValue(out var zoned);
                if (entry.Decision != FoodPlanDecision.Open || !zk || zoned || region.ProposedCells.Count == 0)
                {
                    continue;
                }

                var method = new MethodId("fishing-" + entry.Channel.Id);
                var journal = _reviewer.Player.Journal;
                try
                {
                    journal.LoadMethod(goal.Standard.Id, goal.Standard.Episode, method, call);
                    continue;
                }
                catch (NotFoundException)
                {
                }

                var zone = ZoneCreate.NewFishingZone(region.ProposedCells);
                var id = PlanIds.MintPlanId();
                var action = DomainAction.NewZoneCreateAction(new ActionId(id.Value + "-0"), zone);
                var (reply, refused) = await PreviewZoneAsync(_native, Boundary.Identity(state.Snapshot), zone, call).ConfigureAwait(false);
                if (refused.Length != 0)
                {
                    continue;
                }

                Boundary.Context(reply?.Evaluated?.Context, state.Snapshot);
                var spec = new PlanSpec(id, new PlanRevision(1), [action]);
                _reviewer.Player.Current(call, epoch);
                Identity actual;
                try
                {
                    actual = await StepScopeAsync(_reviewer.Native, call).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    throw new ControlException("fishing: step scope unreadable", e);
                }

                if (!RoundsBuildingBoundary(actual, state.Snapshot, p.Identity.Tick) || _reviewer.Player.Session.State() != state)
                {
                    throw new ControlException("fishing: boundary moved or session state changed");
                }

                var now = _reviewer.Clock.Now();
                if (now < read.StartedAt || now - read.StartedAt > _reviewer.MaxAge)
                {
                    throw new ObservationStaleException();
                }

                var snapshot = state.Snapshot with { Plan = id, Revision = new PlanRevision(1) };
                var preview = new Preview
                {
                    Action = action,
                    Snapshot = snapshot,
                    Tick = p.Identity.Tick,
                    CanPlace = Known(true),
                    SafeToPlace = Known(true),
                    MadeFromStuff = Known(false),
                    WatchCellsAccessible = Known(true),
                    Footprint = Known(zone.Cells),
                    Costs = Known<EquatableArray<Amount>>([]),
                };
                var decision = await AdmissionLog.AdmitMethodAsync(journal, new BuildingMethodRequest
                {
                    Owner = goal,
                    Method = method,
                    Plan = spec,
                    Current = snapshot,
                    Tick = p.Identity.Tick,
                    Bounds = Known(p.Bounds),
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
