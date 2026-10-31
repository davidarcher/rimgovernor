using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.Runtime;

/// <summary>The medical/care Need's work (#3258): the care capacity comparison's bed orders, field spots and walking patients' transports issued and its planned medical beds built, under the Need and admitted by claims.</summary>
public sealed partial class HospitalPlanner
{
    internal ColonyReview Reviewer { get; init; } = null!;

    internal GameClient Native { get; init; } = null!;

    internal BuildingPlanner Building { get; init; } = null!;

    public static HospitalPlanner NewReviewHospitalPlanner(ColonyReview reviewer, IReviewBuildingSource native)
    {
        if (reviewer is null || native is null)
        {
            throw new ControlException("NewReviewHospitalPlanner: reviewer or native is missing");
        }

        if (native is not GameClient source)
        {
            throw new ControlException("NewReviewHospitalPlanner: native cannot read bed use");
        }

        if (native is not IReviewSource)
        {
            throw new ControlException("NewReviewHospitalPlanner: native is not a rounds source");
        }

        var building = new BuildingPlanner { Reviewer = reviewer, Native = native, Concern = MedicalRules.MedicalCareNeed.FilingKey, Need = MedicalRules.MedicalCareNeed };
        return new HospitalPlanner { Reviewer = reviewer, Native = source, Building = building };
    }
}

partial class HospitalPlanner
{
    /// <summary>The method id prefix of a bed made medical (CareBedOrder.Medical).</summary>
    internal const string ConvertMethod = "hospital-convert-";

    /// <summary>The method id prefix of a medical bed restored to sleeping once care ended.</summary>
    internal const string RestoreMethod = "hospital-restore-";

    /// <summary>The method id prefix of a walking patient's lay-down in its chosen bed (CareTransport.Walk): patient, then bed.</summary>
    internal const string TransportMethod = "hospital-transport-";

    /// <summary>The method id prefix of a critical patient's rescue to its chosen bed (CareTransport.Carried): patient, then bed.</summary>
    internal const string RescueMethod = "rescue-";

    /// <summary>The method id prefix of a patient's field spot placement (task 4.4): the patient follows, one spot a patient.</summary>
    internal const string FieldMethod = "hospital-field-";

    internal static MethodId TransportMethodOf(CareTransport t) =>
        new((t is CareTransport.Carried ? RescueMethod : TransportMethod) + t.Patient + "/" + t.Bed);

    internal static MethodId FieldMethodOf(string patient) => new(FieldMethod + patient);

    /// <summary>The care_capacity rows of one comparison: each patient's chosen alternative with every alternative's effects, exclusion or unread fact, then each bed order with its reason.</summary>
    internal static void RecordCareCapacity(CareCapacityPlan plan, CareCapacityRequest request)
    {
        static object? Assessment(CareAssessment a) => a is CareAssessment.Assessed { Effect: var e }
            ? new Dictionary<string, object?>
            {
                ["infection_weight"] = e.InfectionWeight, ["tend_ticks"] = e.TendTicks, ["tend_quality"] = e.TendQuality, ["room_infection"] = e.RoomInfection, ["orders"] = e.Orders,
                ["exposure"] = e.Exposure, ["heal_per_day"] = e.HealPerDay, ["surgery_loss"] = e.SurgeryLoss,
            }
            : a is CareAssessment.Excluded { Reason: var reason } ? "excluded:" + reason
            : "unassessed:" + ((CareAssessment.Unassessed)a).Fact;
        foreach (var d in plan.Decisions)
        {
            Tel.Decide(new Decision
            {
                Kind = "care_capacity",
                Component = "medical",
                Verdict = d.Chosen.Kind.ToString(),
                Reason = d.Chosen.Bed,
                Target = d.Patient,
                Attrs = new Dictionary<string, object?>
                {
                    ["demand"] = d.Demand,
                    ["options"] = d.Options.Select(o => new Dictionary<string, object?> { ["kind"] = o.Kind.ToString(), ["bed"] = o.Bed, ["assessment"] = Assessment(o.Assessment) }).ToList(),
                    ["placement"] = plan.Placements.FirstOrDefault(f => f.Patient == d.Patient) is { } spot ? spot.Cell.X + "," + spot.Cell.Z : null,
                },
            });
        }
        foreach (var o in plan.Orders)
        {
            Tel.Decide(new Decision
            {
                Kind = "care_bed_order", Component = "medical", Verdict = o.Medical ? "medical" : "sleeping", Reason = o.Reason, Target = o.Bed,
            });
        }
        foreach (var t in CareTransportRules.Of(plan, request.Support))
        {
            Tel.Decide(new Decision
            {
                Kind = "care_transport", Component = "medical", Verdict = t is CareTransport.Carried ? "carried" : "walk", Reason = t.Bed, Target = t.Patient,
                Attrs = new Dictionary<string, object?> { ["held"] = request.Holds.Any(h => h.Patient == t.Patient && h.Bed == t.Bed) },
            });
        }
    }

    internal static MethodId OrderMethod(CareBedOrder order) => new((order.Medical ? ConvertMethod : RestoreMethod) + order.Bed);

    /// <summary>
    /// The care capacity comparison's request from one reading: the native care census, the sleeping and room census, the
    /// goal's bed floor, the plan's medical bed cells, the orders and transports the refusal budget now refuses and the beds the
    /// patients' transports hold. Null when the care census is unread. <paramref name="care"/> is the care Need's owner in
    /// <paramref name="world"/>; each census patient's critical Need is read for its rescues.
    /// </summary>
    internal static CareCapacityRequest? CareCapacityRequestOf(StoreJournal journal, Sto.NeedOwner care, ColonyReading facts, GenerationSnapshot world, CancellationToken call)
    {
        var f = facts.Facts;
        if (f.ForecastObserved.Coupled?.Support is not { } support) return null;
        var transports = Transports(journal, care, support, world, call);
        var spots = FieldSpots(journal, care, world, call);
        return new CareCapacityRequest
        {
            Support = support,
            Sleeping = f.Sleeping,
            Rooms = f.Upkeep.Rooms,
            Surgical = SurgeryRules.SurgeryBedShortPatients(f.MedicalPawns),
            MinBeds = f.GoalBuild.HospitalBeds,
            Refused = RefusedOrders(journal, care, world, call),
            Holds = CareTransportRules.Holds(transports.Select(t => t.Issued), support),
            RefusedTransports = [.. transports.Where(t => t.Refused is not null).Select(t => new RefusedCareTransport(t.Issued.Patient, t.Issued.Bed, t.Refused!))],
            PlannedMedical = HospitalRules.PlannedMedicalCells(f.LayoutPlan),
            FieldSpots = [.. spots.Where(s => s.Chain is not null).Select(s => s.Chain!)],
            RefusedFieldSpots = [.. spots.Where(s => s.Refused is not null).Select(s => s.Refused!)],
        };
    }

    /// <summary>
    /// The field spot placements the journal holds in this world (task 4.4), the latest attempt of each patient's Method: in flight
    /// while its action is open, placed once accepted under the current native authority (a load or an authority change ends that
    /// evidence, as for a transport); a Refused attempt places nothing and, while the refusal budget refuses its Method, carries the
    /// budget's reason. Re-derived each review (Decision 4: no owner store).
    /// </summary>
    internal static IReadOnlyList<(CareFieldChain? Chain, RefusedCareFieldSpot? Refused)> FieldSpots(StoreJournal journal, Sto.NeedOwner care, GenerationSnapshot world, CancellationToken call)
    {
        var found = new List<(CareFieldChain?, RefusedCareFieldSpot?)>();
        foreach (var group in journal.OwnerMethodHistory(care, call).Where(m => m.Method.Value.StartsWith(FieldMethod, StringComparison.Ordinal))
            .GroupBy(m => m.Method.Value).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var patient = group.Key[FieldMethod.Length..];
            if (journal.OwnerMethodPlan(care, new MethodId(group.Key), call) is not { } latest) continue;
            var plan = journal.LoadPlan(latest, call);
            if (plan.Progress.FirstOrDefault(p => p.Action.Building is not null) is not { } progress) continue;
            if (!plan.Retired && WorkProgress.Open([progress]))
                found.Add((new CareFieldChain(patient, progress.Action.Building!.Cell, false), null));
            else if (progress.View.Stage == Stage.Completed && progress.View.Snapshot.Native == world.Native)
                found.Add((new CareFieldChain(patient, progress.Action.Building!.Cell, true), null));
            else if (progress.View.Stage == Stage.Unsuccessful)
            {
                var budget = ReviewRefusalBudget.RefusalLedger(journal, [latest], _ => group.Key, call);
                var (allowed, why) = budget.Allowed(group.Key, ReviewRefusalBudget.BudgetWorld(world));
                if (!allowed) found.Add((null, new RefusedCareFieldSpot(patient, why.Reason)));
            }
        }
        return found;
    }

    /// <summary>
    /// The sleeping census with each bed a care patient's transport holds counted as that patient's (<see cref="CareCapacityRules.Holding"/>):
    /// the census every sleeping rule reads, so a held bed is no free slot, assignment or medical-rest slot for any other sleeper.
    /// The census unchanged while the care census is unread.
    /// </summary>
    internal static SleepingObservation HeldSleeping(StoreJournal journal, ColonyReading facts, GenerationSnapshot world, CancellationToken call)
    {
        if (facts.Facts.ForecastObserved.Coupled?.Support is not { } support) return facts.Facts.Sleeping;
        var care = journal.LoadNeed(MedicalRules.MedicalCareNeed, world, call);
        return CareCapacityRules.Holding(facts.Facts.Sleeping, CareTransportRules.Holds(Transports(journal, care, support, world, call).Select(t => t.Issued), support));
    }

    /// <summary>
    /// The transports the journal holds in this world, oldest first: the care Need's lay-downs and each census patient's critical
    /// rescues, the latest attempt of each Method. Each holds while its action is open, or once accepted while the native authority
    /// it was accepted under is the current one (a load or an authority change ends that evidence); a Refused attempt holds nothing
    /// and, while the refusal budget refuses its Method, carries the budget's reason.
    /// </summary>
    internal static IReadOnlyList<(IssuedCareTransport Issued, string? Refused)> Transports(StoreJournal journal, Sto.NeedOwner care, RimGovernor.Brain.GameFacts.Observations.SupportActivityRead support,
        GenerationSnapshot world, CancellationToken call)
    {
        var owners = new List<Sto.NeedOwner> { care };
        foreach (var patient in support.Patients.Select(p => p.Patient).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            owners.Add(journal.LoadNeed(MedicalRules.CriticalNeed(new PawnId(patient)), world, call));
        }
        var found = new List<(IssuedCareTransport Issued, string? Refused, Tick Tick)>();
        foreach (var owner in owners)
        {
            foreach (var m in journal.OwnerMethodHistory(owner, call))
            {
                if (!m.Method.Value.StartsWith(TransportMethod, StringComparison.Ordinal) && !m.Method.Value.StartsWith(RescueMethod, StringComparison.Ordinal)) continue;
                var plan = journal.LoadPlan(m.Plan, call);
                var progress = plan.Progress.FirstOrDefault(p => p.Action.BedRest is not null || p.Action.Rescue is not null);
                if (progress is null) continue;
                var (patient, bed) = progress.Action.BedRest is { } rest ? (rest.Patient.Value, rest.Bed) : (progress.Action.Rescue!.Patient.Value, progress.Action.Rescue.Bed);
                var holding = !plan.Retired && WorkProgress.Open([progress])
                    || progress.View.Stage == Stage.Completed && progress.View.Snapshot.Native == world.Native;
                string? refused = null;
                if (progress.View.Stage == Stage.Unsuccessful)
                {
                    var budget = ReviewRefusalBudget.RefusalLedger(journal, [m.Plan], _ => m.Method.Value, call);
                    var (allowed, why) = budget.Allowed(m.Method.Value, ReviewRefusalBudget.BudgetWorld(world));
                    if (!allowed) refused = why.Reason;
                }
                found.Add((new(patient, bed, holding), refused, progress.View.Tick));
            }
        }
        return [.. found.OrderBy(t => t.Tick.Value).Select(t => (t.Issued, t.Refused))];
    }

    /// <summary>
    /// The bed orders a Refused receipt keeps from being issued again in this world: each convert or restore method of the
    /// owner whose refusal budget does not allow it now, with the budget's reason. A fact the comparison reads, not a retry.
    /// </summary>
    internal static IReadOnlyList<RefusedCareOrder> RefusedOrders(StoreJournal journal, Sto.NeedOwner owner, GenerationSnapshot world, CancellationToken call)
    {
        var refused = new List<RefusedCareOrder>();
        foreach (var group in journal.OwnerMethodHistory(owner, call).GroupBy(m => m.Method.Value).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var medical = group.Key.StartsWith(ConvertMethod, StringComparison.Ordinal);
            if (!medical && !group.Key.StartsWith(RestoreMethod, StringComparison.Ordinal)) continue;
            var budget = ReviewRefusalBudget.RefusalLedger(journal, [.. group.Select(m => m.Plan)], _ => group.Key, call);
            var (allowed, why) = budget.Allowed(group.Key, ReviewRefusalBudget.BudgetWorld(world));
            if (!allowed) refused.Add(new(group.Key[(medical ? ConvertMethod : RestoreMethod).Length..], medical, why.Reason));
        }
        return refused;
    }
}

public sealed partial class BuildingPlanner
{
    /// <summary>
    /// The care Need's construction: the planned medical bed still unbuilt (HospitalRules.UnbuiltMedicalBed), bound to its
    /// room with the def the layout comparison chose (LayoutHospitalBedRules). Nothing is re-selected here.
    /// </summary>
    internal (BuildingPlanner? Planner, Verdict Verdict) SelectHospital(ColonyReading facts)
    {
        if (HospitalRules.UnbuiltMedicalBed(facts.Facts.LayoutPlan, facts.Facts.CurrentConstruction) is not { } unbuilt)
        {
            return (null, BuildingReasonExistingFacility);
        }
        var resolved = Copy();
        resolved.Definition = unbuilt.Def;
        resolved.StationRoom = unbuilt.Room;
        resolved.Environment = PlacementEnvironment.Indoors;
        resolved.Stuff = ColonyPlanningRules.BuildStuff(facts.Facts, ResourceStockRules.Count(facts.Snapshot.Resources), resolved.Definition);
        return (resolved, default);
    }
}

public sealed partial class HospitalPlanner
{
    /// <summary>
    /// The care Need's hospital step. It binds the care capacity comparison (CareCapacityRules.Plan): its first bed order
    /// (a conversion for a patient or the goal's floor, or a post-care restore) is issued as one bed-use intent, read back
    /// first; with none, the first field spot to place (task 4.4) is placed as one building action the native preview accepts;
    /// with none, the first walking patient's transport (CareTransport.Walk, a bed rest in its exact bed) not already
    /// in flight is issued; with none, the planned medical bed still unbuilt is built through the shared building planner. A
    /// carried patient's rescue is its critical Need's. A Refused receipt is read by the next review as a RefusedCareOrder,
    /// a RefusedCareFieldSpot or a RefusedCareTransport, so the comparison chooses without it rather than retrying. A field
    /// spot binds in steps, each on the census the last one left: placed (a zero-work spot stands once accepted), made
    /// medical by the bed order of the review that lists it, then the patient moved there, then tended there.
    /// </summary>
    internal async Task<BuildingResult> StepAsync(NeedWork work, CancellationToken call)
    {
        var p = Reviewer.Player;
        var state = work.State;
        if (!state.ObservationKnown || !state.Snapshot.IsValid || state.Snapshot.Native.Value == 0)
        {
            throw new ControlException("step: observation unknown, snapshot invalid or native generation unset");
        }

        var review = p.Journal.LoadReview(call);
        if (!review.Enabled || !review.Snapshot.Matches(state.Snapshot))
        {
            return new BuildingResult { Verdict = BuildingReasonNoReview };
        }

        var started = Reviewer.Clock.Now();
        var identity = await Native.IdentityAsync(call).ConfigureAwait(false);
        var expected = new ObservationIdentity { Context = identity.Context, Paused = identity.Paused };
        if (!BuildingPlanner.BuildingBoundary(expected, state.Snapshot, review.Tick))
        {
            throw new ControlException("step: boundary moved");
        }

        var reading = await Reviewer.ObserveOwnedAsync((IReviewSource)Native, expected, call).ConfigureAwait(false);
        var facts = reading.Reading;
        PlannerSnapshot.RecordStepRead("hospital", MedicalRules.MedicalCareNeed.FilingKey, state.Snapshot, facts);
        if (CareCapacityRequestOf(p.Journal, work.Owner, facts, state.Snapshot, call) is not { } request)
        {
            return new BuildingResult { Verdict = FieldUnavailable("support_activities") };
        }

        var plan = CareCapacityRules.Plan(request);
        RecordCareCapacity(plan, request);
        if (plan.Orders.Count == 0 && plan.Placements.Count != 0)
        {
            return await PlaceFieldSpotAsync(work, facts, plan.Placements[0], review, started, call).ConfigureAwait(false);
        }

        if (plan.Orders.Count == 0)
        {
            var walks = CareTransportRules.Of(plan, request.Support).OfType<CareTransport.Walk>().ToArray();
            if (walks.Length != 0)
            {
                return Transport(work, walks, review, started, call);
            }

            if (HospitalRules.UnbuiltMedicalBed(facts.Facts.LayoutPlan, facts.Facts.CurrentConstruction) is not null)
            {
                return await Building.Copy().StepAsync(work.Arbiter!, call, work.Epoch).ConfigureAwait(false);
            }

            return new BuildingResult { Verdict = request.Support.Patients.Count == 0 && request.MinBeds == 0 ? BuildingReasonNoDeficit : BuildingReasonExistingFacility };
        }

        var order = plan.Orders[0];
        var target = await Native.ReadBedUseTargetAsync(Boundary.Identity(state.Snapshot), order.Bed, call).ConfigureAwait(false);
        try
        {
            Boundary.Context(target.Context, state.Snapshot);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ControlException("step: bed read context", e);
        }

        if ((target.Context?.Tick ?? 0) < facts.Identity.Tick.Value)
        {
            throw new ControlException("step: bed read older than the facts");
        }

        // The bed already reads as the order wants (an Unconfirmed receipt landed after the facts): wait for the census to show it.
        if (target.Medical == order.Medical)
        {
            return new BuildingResult { Verdict = BuildingReasonHospitalConvert };
        }

        var method = OrderMethod(order);
        if (p.Journal.OwnerMethodPlan(work.Owner, method, call) is { } prior && Sto.StorePackage.PlanWorkOpen(p.Journal.LoadPlan(prior, call), null))
        {
            return new BuildingResult { Verdict = WaitFor(Cause.MethodUsed, "care_bed_order_in_flight") };
        }

        var patch = BedUse.NewBedMedical(order.Bed, order.Medical);
        if (!work.Arbiter!.TryClaim([], "bed:" + order.Bed))
        {
            return new BuildingResult { Verdict = WaitFor(Cause.MethodUsed, "bed_claim") };
        }

        var commit = p.CommitOwnerAction(state, work.Owner, method, id => DomainAction.NewBedUseAction(new ActionId($"{id}-0"), patch), Guard(review, started, call), call, work.Epoch);
        return new BuildingResult { Verdict = BuildingPlanner.OwnerCommitVerdict(commit) };
    }

    /// <summary>
    /// A patient's field spot (task 4.4): one SleepingSpot facing south at its site, admitted as one building method of the care
    /// Need when the native preview accepts it, claiming the patient and the spot's cell. A placement in flight waits; one the
    /// refusal budget refuses is a fact the next review reads. The receipt (Accepted, Refused, Unsent or Unconfirmed) is the
    /// journal's; the spot is made medical only once a later census lists it.
    /// </summary>
    private async Task<BuildingResult> PlaceFieldSpotAsync(NeedWork work, ColonyReading facts, CareFieldPlacement placement, Sto.ReviewState review, DateTimeOffset started, CancellationToken call)
    {
        var p = Reviewer.Player;
        var state = work.State;
        var method = FieldMethodOf(placement.Patient);
        if (p.Journal.OwnerMethodPlan(work.Owner, method, call) is { } prior && Sto.StorePackage.PlanWorkOpen(p.Journal.LoadPlan(prior, call), null))
        {
            return new BuildingResult { Verdict = WaitFor(Cause.MethodUsed, "field_spot_in_flight") };
        }

        var (refused, admitted) = ReviewRefusalBudget.AdmitLatestAttempt(p.Journal, work.Owner, method, state.Snapshot, call);
        if (!admitted)
        {
            return new BuildingResult { Verdict = refused };
        }

        var planId = PlanIds.MintPlanId();
        var snapshot = state.Snapshot with { Plan = planId, Revision = new PlanRevision(1) };
        var action = DomainAction.NewBuildingAction(new ActionId(planId.Value + "-0"), new Building(SleepingRules.SleepingSpotDefinition, placement.Cell, Rotation.South, ""), ConstructionTier.Expand);
        var got = await Building.Native.PreviewBuildingAsync(action, snapshot, call).ConfigureAwait(false);
        if (!got.Preview.CanPlace || !got.Preview.SafeToPlace)
        {
            return new BuildingResult { Verdict = NoSpace("field_spot_cell") };
        }

        if (!work.Arbiter!.TryClaim([new PawnId(placement.Patient)], "cell/" + placement.Cell.X + "/" + placement.Cell.Z))
        {
            return new BuildingResult { Verdict = WaitFor(Cause.MethodUsed, "field_spot_claim") };
        }

        p.Current(call, work.Epoch);
        if (p.Session.State() != state)
        {
            throw new ControlException("field spot: session state changed");
        }

        Guard(review, started, call)();
        var decision = await AdmissionLog.AdmitMethodAsync(p.Journal, new BuildingMethodRequest
        {
            Owner = work.Owner,
            Method = method,
            Plan = new PlanSpec(planId, new PlanRevision(1), [action]),
            Current = snapshot,
            Tick = facts.Identity.Tick,
            Bounds = facts.Facts.MapBounds,
            Stock = got.Stock,
            Previews = [got.Preview],
            Purpose = Purpose.ReviewState,
        }, call).ConfigureAwait(false);
        return new BuildingResult { Verdict = decision.Admitted ? BuildingReasonAdmitted : AdmissionLog.AdmissionRefused(decision) };
    }

    /// <summary>The commit guard of a step: the step's reads within the review's age bound and the review unmoved.</summary>
    private Action Guard(Sto.ReviewState review, DateTimeOffset started, CancellationToken call) => () =>
    {
        var elapsed = Reviewer.Clock.Now() - started;
        if ((elapsed < TimeSpan.Zero || elapsed > Reviewer.MaxAge))
        {
            throw new ControlException("step: the bed read took too long");
        }

        var latest = Reviewer.Player.Journal.LoadReview(call);
        if (latest.Revision != review.Revision || !latest.Enabled)
        {
            throw new ControlException("step: the review moved or was disabled");
        }
    };

    /// <summary>
    /// A walking patient's transport: the first whose Method is not in flight is committed as one bed rest in its exact bed,
    /// claiming the patient and the bed (MethodClaims: <c>care/&lt;patient&gt;</c> and the bed), so no second patient's transport,
    /// bed order or assignment takes the bed while it is open. An accepted transport whose patient is not yet seen in the bed is
    /// ordered again (native applies a running lay-down again); only the patient observed in the bed lets its tend be credited.
    /// </summary>
    private BuildingResult Transport(NeedWork work, IReadOnlyList<CareTransport.Walk> walks, Sto.ReviewState review, DateTimeOffset started, CancellationToken call)
    {
        var p = Reviewer.Player;
        foreach (var walk in walks)
        {
            var method = TransportMethodOf(walk);
            if (p.Journal.OwnerMethodPlan(work.Owner, method, call) is { } prior && Sto.StorePackage.PlanWorkOpen(p.Journal.LoadPlan(prior, call), null))
            {
                continue;
            }

            var (refused, admitted) = ReviewRefusalBudget.AdmitLatestAttempt(p.Journal, work.Owner, method, work.State.Snapshot, call);
            if (!admitted)
            {
                return new BuildingResult { Verdict = refused };
            }

            var patient = new PawnId(walk.Patient);
            if (!work.Arbiter!.TryClaim([patient], "bed:" + walk.Bed))
            {
                return new BuildingResult { Verdict = WaitFor(Cause.MethodUsed, "care_transport_claim") };
            }

            var rest = new BedRest((Domain.PawnId)patient, walk.Bed);
            var commit = p.CommitOwnerAction(work.State, work.Owner, method, id => DomainAction.NewBedRestAction(new ActionId($"{id}-0"), rest), Guard(review, started, call), call, work.Epoch);
            return new BuildingResult { Verdict = BuildingPlanner.OwnerCommitVerdict(commit) };
        }

        return new BuildingResult { Verdict = WaitFor(Cause.MethodUsed, "care_transport_in_flight") };
    }
}

public sealed partial class Player
{
    /// <summary>Commits one single-action plan under a method id; the guard, when given, runs after the session check and may throw to abort.</summary>
    internal Sto.WorkCommit CommitOwnerAction(ControlState state, Sto.NeedOwner owner, MethodId method, Func<PlanId, DomainAction> action, Action? guard, CancellationToken call, CancellationToken epoch)
    {
        var id = PlanIds.MintPlanId();
        var plan = new PlanSpec(id, new PlanRevision(1), [action(id)]);
        Current(call, epoch);
        if (Session.State() != state)
        {
            throw new ControlException("commitBedPatch: session state changed");
        }

        guard?.Invoke();
        return Journal.CommitOwnerWork(owner, method, "", plan, call);
    }
}
