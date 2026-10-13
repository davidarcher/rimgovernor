namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsPopulationJoinerPlanner
{
    /// <summary>Works a royal decree quest: plant a harvest field or hunt animals; not handled when no decree is owed.</summary>
    internal (RoundsPopulationJoinerResult Result, bool Handled) AdmitDecree(CancellationToken call, CancellationToken epoch, ControlState state, StandardState goal, Rounds review, RoundsReading read, StepArbiter arbiter, DateTimeOffset started)
    {
        var f = read.Projection.Facts;
        if (!f.QuestOffers.TryGetValue(out var known))
        {
            return (default, false);
        }

        var offers = known.OrderBy(o => o.Quest, StringComparer.Ordinal).ToList();
        var lending = false;
        foreach (var offer in offers)
        {
            var (objective, remaining, owed) = PolicyFunctions.DecreeObjective(offer);
            if (!owed)
            {
                continue;
            }

            if (!offer.AskerFactionPlayer.TryGetValue(out var player) || !player)
            {
                DecreeSkip(call, offer, "asker_faction");
                continue;
            }

            if (objective.DeadlineTicks.TryGetValue(out var deadline) && deadline <= read.Projection.Identity.Tick.Value)
            {
                DecreeSkip(call, offer, "deadline");
                continue;
            }

            if (objective.Kind == O.QuestObjectiveKind.ProduceItem)
            {
                lending = lending || DecreeLending();
                continue;
            }

            var id = PlanIds.MintPlanId();
            var snapshot = state.Snapshot with { Plan = id, Revision = new PlanRevision(1) };
            var actions = new List<DomainAction>();
            var previews = new List<Preview>();
            var reason = "";
            switch (objective.Kind)
            {
                case O.QuestObjectiveKind.HarvestPlant:
                {
                    var field = reviewer.NewResourceFieldPlanner(call, state, review, read.Projection.Identity, read.Projection);
                    var (site, sited) = field.SiteOf();
                    if (!sited)
                    {
                        reason = "plant_site_unknown";
                        break;
                    }

                    long standing = 0;
                    foreach (var farm in read.Projection.Farms)
                    {
                        if (farm.Crop == objective.Def && farm.UsableCells.TryGetValue(out var cellCount))
                        {
                            standing += cellCount;
                        }
                    }

                    var (plan0, why) = PolicyFunctions.PlanDecreeHarvest(objective, remaining, standing, field.Choices, field.Climate, site, field.Skill);
                    reason = why;
                    if (reason != "")
                    {
                        break;
                    }

                    if (reviewer.ResourceNative is null)
                    {
                        reason = "plant_site_unknown";
                        break;
                    }

                    for (var i = 0; i < plan0.Sites.Patches.Count; i++)
                    {
                        var patch = plan0.Sites.Patches[i];
                        var cells = new List<Cell>();
                        for (var x = patch.X; x < patch.X + patch.Width; x++)
                        {
                            for (var z = patch.Z; z < patch.Z + patch.Height; z++)
                            {
                                cells.Add(new Cell(x, z));
                            }
                        }

                        var zone = ZoneCreate.NewZoneCreate(ZoneKind.Growing, objective.Def, cells);
                        var (reply, zoneWhy) = BuildingRuntimePackage.PreviewZone(call, reviewer.ResourceNative, Boundary.Identity(snapshot), zone);
                        if (zoneWhy != "")
                        {
                            reason = "plant_site_refused";
                            break;
                        }

                        Boundary.Context(reply.Evaluated?.Context, snapshot);
                        var action = DomainAction.NewZoneCreateAction(new ActionId($"{id}-{i}"), zone);
                        actions.Add(action);
                        previews.Add(new Preview
                        {
                            Action = action,
                            Snapshot = snapshot,
                            Tick = read.Projection.Identity.Tick,
                            CanPlace = Known(true),
                            SafeToPlace = Known(true),
                            MadeFromStuff = Known(false),
                            WatchCellsAccessible = Known(true),
                            Footprint = Known<IReadOnlyList<Cell>>(cells),
                            Costs = Known<IReadOnlyList<Amount>>([]),
                        });
                    }

                    break;
                }

                case O.QuestObjectiveKind.KillAnimals:
                {
                    var (prey, why) = PolicyFunctions.PlanDecreeHunt(objective, remaining, offer.ViolentQuestsAllowed, read.Projection.Acquisition);
                    reason = why;
                    for (var i = 0; i < prey.Count; i++)
                    {
                        var source = prey[i];
                        var acquire = Acquisition.NewAcquisition(source.Id, source.Resource, source.Cell);
                        actions.Add(DomainAction.NewAcquisitionAction(new ActionId($"{id}-{i}"), acquire));
                    }

                    break;
                }
            }

            if (reason == "existing_work")
            {
                return (new RoundsPopulationJoinerResult { Verdict = BuildingRuntimePackage.BuildingReasonExistingWork, NativeWorkTicks = BuildingRuntimePackage.StockWaitTicks }, true);
            }

            if (reason != "")
            {
                DecreeSkip(call, offer, reason);
                continue;
            }

            if (actions.Count == 0)
            {
                continue;
            }

            var prefix = $"decree-{offer.Quest}-{(int)objective.Kind}-{remaining}-";
            var (method, verdict, admitted) = BuildingRuntimePackage.AdmitStandardMethod(call, reviewer.Player.Journal, goal, prefix, state.Snapshot);
            if (!admitted)
            {
                return (new RoundsPopulationJoinerResult { Verdict = verdict }, true);
            }

            var claims = new List<string>();
            foreach (var action in actions)
            {
                if (action.TryProductionBill(out var bill))
                {
                    claims.Add("bench:" + bill.Bench);
                }

                if (action.TryAcquisition(out var acquisition))
                {
                    claims.Add("source:" + acquisition.Thing);
                }
            }

            if (!arbiter.TryClaim([], [.. claims]))
            {
                return (new RoundsPopulationJoinerResult { Verdict = BuildingRuntimePackage.WaitFor(Cause.Claim, "decree_work") }, true);
            }

            var plan = PlanSpec.NewPlan(id, new PlanRevision(1), actions);
            reviewer.Player.Current(call, epoch);
            var elapsed = reviewer.Clock.Now() - started;
            if (reviewer.Player.Session.State() != state || elapsed < TimeSpan.Zero || elapsed > reviewer.MaxAge)
            {
                throw new ControlException("admitDecree: session state changed or the read took too long");
            }

            if (previews.Count > 0)
            {
                var decision = BuildingRuntimePackage.AdmitMethod(call, reviewer.Player.Journal, new BuildingMethodRequest
                {
                    Owner = goal,
                    Method = method,
                    Plan = plan,
                    Current = snapshot,
                    Tick = read.Projection.Identity.Tick,
                    Bounds = Known(read.Projection.Bounds),
                    Stock = new StockObservation { Snapshot = snapshot, Tick = read.Projection.Identity.Tick },
                    Previews = previews,
                    Purpose = Purpose.Rounds,
                });
                if (!decision.Admitted)
                {
                    return (new RoundsPopulationJoinerResult { Verdict = BuildingRuntimePackage.AdmissionRefused(decision) }, true);
                }
            }
            else
            {
                reviewer.Player.Journal.CommitMethod(call, goal.Standard.Id, goal.Revision, method, plan);
            }

            return (new RoundsPopulationJoinerResult { Verdict = BuildingRuntimePackage.BuildingReasonAdmitted, Plan = id, NativeWorkTicks = BuildingRuntimePackage.StockWaitTicks }, true);
        }

        if (lending)
        {
            return (new RoundsPopulationJoinerResult { Verdict = BuildingRuntimePackage.BuildingReasonExistingWork, NativeWorkTicks = BuildingRuntimePackage.StockWaitTicks }, true);
        }

        return (default, false);
    }

    private void DecreeSkip(CancellationToken ct, JoinerOffer offer, string reason) =>
        reviewer.LogQuestSkip(ct, new QuestSkip { Quest = offer.Quest, ScriptDef = offer.ScriptDef, Reason = new QuestSkipReason(reason) });
}

internal static partial class BuildingRuntimePackage
{
    internal static bool ContainsString(IReadOnlyList<string> values, string want) => values.Contains(want);
}
