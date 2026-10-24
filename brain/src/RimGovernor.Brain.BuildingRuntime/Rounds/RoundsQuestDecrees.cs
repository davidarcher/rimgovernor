namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class JoinersRounds
{
    /// <summary>Works a royal decree quest: plant a harvest field or hunt animals; not handled when no decree is owed.</summary>
    internal async Task<(Verdict Verdict, bool Handled)> AdmitDecreeAsync(NeedWork work, Rounds review, RoundsReading read, DateTimeOffset started, CancellationToken call)
    {
        var state = work.State;
        var f = read.Projection.Facts;
        if (!f.QuestOffers.TryGetValue(out var known))
        {
            return (default, false);
        }

        var offers = known.OrderBy(o => o.Quest.Value, StringComparer.Ordinal).ToList();
        var lending = false;
        foreach (var offer in offers)
        {
            var (objective, remaining, owed) = QuestDecrees.DecreeObjective(offer);
            if (!owed || objective is null)
            {
                continue;
            }

            if (offer.Asker != O.DecreeAsker.PlayerFaction)
            {
                DecreeSkip(offer, "asker_faction", call);
                continue;
            }

            if (objective.Timing is O.QuestObjectiveTiming.Deadline due && due.Tick <= read.Projection.Identity.Tick.Value)
            {
                DecreeSkip(offer, "deadline", call);
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
                    var field = reviewer.NewResourceFieldPlanner(state, review, read.Projection.Identity, read.Projection, call);
                    var (site, sited) = field.SiteOf();
                    if (!sited)
                    {
                        reason = "plant_site_unknown";
                        break;
                    }

                    long standing = 0;
                    foreach (var farm in read.Projection.Farms)
                    {
                        if (farm.Crop == objective.Def)
                        {
                            standing += farm.UsableCells;
                        }
                    }

                    var (plan0, why) = QuestDecrees.PlanDecreeHarvest(objective, remaining, standing, field.Choices, field.Climate, site, field.Skill);
                    reason = why;
                    if (reason != "")
                    {
                        break;
                    }

                    if (plan0 is null)
                    {
                        throw new ControlException("decree harvest: the plan is missing without a reason");
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

                        var zone = new ZoneCreate(Domain.ZoneKind.Growing, objective.Def, cells);
                        var (reply, zoneWhy) = await ZonePreviews.PreviewZoneAsync(reviewer.ResourceNative, Boundary.Identity(snapshot), zone, call).ConfigureAwait(false);
                        if (zoneWhy != "")
                        {
                            reason = "plant_site_refused";
                            break;
                        }

                        Boundary.Context(reply?.Outcome?.Evaluated?.Context, snapshot);
                        var action = DomainAction.NewZoneCreateAction(new ActionId($"{id}-{i}"), zone);
                        actions.Add(action);
                        previews.Add(new Preview
                        {
                            Action = action,
                            Snapshot = snapshot,
                            CanPlace = Known(true),
                            SafeToPlace = Known(true),
                            MadeFromStuff = Known(false),
                            WatchCellsAccessible = Known(true),
                            Footprint = Known<EquatableArray<Cell>>([.. cells]),
                            Costs = Known<EquatableArray<Amount>>([]),
                        });
                    }

                    break;
                }

                case O.QuestObjectiveKind.KillAnimals:
                {
                    var (prey, why) = QuestDecrees.PlanDecreeHunt(objective, remaining, offer.ViolentQuestsAllowed, read.Projection.Acquisition);
                    reason = why;
                    for (var i = 0; i < prey.Count; i++)
                    {
                        var source = prey[i];
                        var acquire = new Acquisition(source.Id, source.Resource, source.Cell);
                        actions.Add(DomainAction.NewAcquisitionAction(new ActionId($"{id}-{i}"), acquire));
                    }

                    break;
                }
            }

            if (reason == "existing_work")
            {
                return (Verdict.BuildingReasonExistingWork, true);
            }

            if (reason != "")
            {
                DecreeSkip(offer, reason, call);
                continue;
            }

            if (actions.Count == 0)
            {
                continue;
            }

            var method = new MethodId($"decree-{offer.Quest}-{(int)objective.Kind}-{remaining}");
            var (refused, admitted) = AdmitMethod(work, method, call);
            if (!admitted)
            {
                return (refused, true);
            }

            var claims = new List<string>();
            foreach (var action in actions)
            {
                if (action.ProductionBill is { } bill)
                {
                    claims.Add("bench:" + bill.Bench);
                }

                if (action.Acquisition is { } acquisition)
                {
                    claims.Add("source:" + acquisition.Thing);
                }
            }

            if (!Claim(work, [], [.. claims]))
            {
                return (Verdict.WaitFor(Cause.Claim, "decree_work"), true);
            }

            var plan = new PlanSpec(id, new PlanRevision(1), actions);
            Fence(work, started, "admitDecree", call);
            if (previews.Count == 0)
            {
                return (RoundsBuildingPlanner.OwnerCommitVerdict(reviewer.Player.Journal.CommitOwnerWork(work.Owner, method, "", plan, call)), true);
            }

            var decision = await AdmissionLog.AdmitMethodAsync(reviewer.Player.Journal, new BuildingMethodRequest
            {
                Owner = work.Owner,
                Method = method,
                Plan = plan,
                Current = snapshot,
                Tick = read.Projection.Identity.Tick,
                Bounds = Known(read.Projection.Bounds),
                Stock = new StockObservation { Snapshot = snapshot, Tick = read.Projection.Identity.Tick },
                Previews = previews,
                Purpose = Purpose.Rounds,
            }, call).ConfigureAwait(false);
            return (decision.Admitted ? Verdict.BuildingReasonAdmitted : AdmissionLog.AdmissionRefused(decision), true);
        }

        if (lending)
        {
            return (Verdict.BuildingReasonExistingWork, true);
        }

        return (default, false);
    }

    private void DecreeSkip(JoinerOffer offer, string reason, CancellationToken ct) =>
        reviewer.LogQuestSkip(new QuestSkip(offer.Quest, offer.ScriptDef, new QuestSkipReason(reason), ""), ct);
}

partial class JoinersRounds
{
    internal static bool ContainsString(IReadOnlyList<string> values, string want) => values.Contains(want);
}
