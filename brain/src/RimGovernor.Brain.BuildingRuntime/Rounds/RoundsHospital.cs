namespace RimGovernor.Brain.BuildingRuntime;

public interface IRoundsHospitalSource : IRoundsBuildingSource
{
    (BedUseTarget Target, BridgeResult Result) ReadBedUseTarget(CancellationToken ct, Common.Identity identity, string bed);
}

/// <summary>Composes MaintainMedicalReserves' building method: a hospital bed built, converted or already standing.</summary>
public sealed partial class RoundsHospitalPlanner
{
    internal Rounder Reviewer { get; init; } = null!;

    internal IRoundsHospitalSource Native { get; init; } = null!;

    internal RoundsBuildingPlanner Building { get; init; } = null!;

    public static RoundsHospitalPlanner NewRoundsHospitalPlanner(Rounder reviewer, IRoundsBuildingSource native)
    {
        if (reviewer is null || native is null)
        {
            throw new ControlException("NewRoundsHospitalPlanner: reviewer or native is missing");
        }

        if (native is not IRoundsHospitalSource source)
        {
            throw new ControlException("NewRoundsHospitalPlanner: native cannot read bed use");
        }

        if (native is not IRoundsSource)
        {
            throw new ControlException("NewRoundsHospitalPlanner: native is not a rounds source");
        }

        var building = new RoundsBuildingPlanner { Reviewer = reviewer, Native = native, Concern = ConcernId.MaintainMedicalReserves };
        return new RoundsHospitalPlanner { Reviewer = reviewer, Native = source, Building = building };
    }
}

internal static partial class BuildingRuntimePackage
{
    internal static HospitalRequest HospitalRequestOf(ColonyProjection facts)
    {
        var definitions = facts.Definitions
            .Select(d => new BenchDefinition { Name = d.Name, Available = d.Available, NeedsPower = d.NeedsPower, ConstructionSkill = d.ConstructionSkill })
            .ToList();
        var f = facts.Facts;
        var doctors = Unknown<int>();
        if (f.Labor.TryGetValue(out var census))
        {
            census.TryGetValue(WorkType.Doctor, out var doctorCount);
            doctors = Known(doctorCount);
        }

        return new HospitalRequest
        {
            Patients = f.MedicalPawns,
            Sleeping = f.Sleeping,
            Rooms = facts.Rooms,
            Definitions = definitions,
            Furniture = facts.Shapes.Furniture,
            Colonists = f.Colonists,
            HousingTarget = f.HousingTarget,
            BedCapacity = f.BedCapacity,
            IndoorCapacity = f.IndoorCapacity,
            Doctors = doctors,
            Surgical = PolicyFunctions.SurgeryBedShortPatients(f.MedicalPawns),
        };
    }
}

public sealed partial class RoundsBuildingPlanner
{
    internal (RoundsBuildingPlanner? Planner, Verdict Verdict) SelectHospital(ColonyProjection facts)
    {
        var choice = PolicyFunctions.SelectHospitalBed(HospitalRequestOf(facts));
        switch (choice.Method)
        {
            case HospitalMethod.Unknown:
                return (null, FieldUnavailable("hospital"));
            case HospitalMethod.NoDemand:
                return (null, BuildingReasonNoDeficit);
            case HospitalMethod.Existing:
                return (null, BuildingExistingFacility);
            case HospitalMethod.Convert:
                return (null, BuildingHospitalConvert);
            case HospitalMethod.Unavailable:
                return (null, BuildingHospitalUnavailable);
        }

        var facility = PolicyFunctions.Facility(RoomRole.Hospital);
        var resolved = Copy();
        resolved.Definition = choice.Definition;
        resolved.Environment = PlacementEnvironment.Indoors;
        resolved.Facility = facility;
        resolved.Stuff = facts.BuildStuff(resolved.Definition);
        return (resolved, default);
    }
}

public sealed partial class RoundsHospitalPlanner
{
    internal RoundsBuildingResult Step(CancellationToken call, CancellationToken epoch, StepArbiter arbiter)
    {
        var p = Reviewer.Player;
        var state = p.Session.State();
        if (!state.Enabled)
        {
            return new RoundsBuildingResult { Verdict = BuildingReasonDisabled };
        }

        if (!state.ObservationKnown || !state.Snapshot.IsValid || state.Snapshot.Native.Value == 0)
        {
            throw new ControlException("step: observation unknown, snapshot invalid or native generation unset");
        }

        var review = p.Journal.LoadRounds(call);
        if (!review.Enabled || !review.Snapshot.Matches(state.Snapshot))
        {
            return new RoundsBuildingResult { Verdict = BuildingReasonNoReview };
        }

        var (goal, workable) = p.Journal.Workable(call, review, ConcernId.MaintainMedicalReserves);
        if (!workable || review.Latches.Medical != MedicalCare.Care)
        {
            return new RoundsBuildingResult { Verdict = BuildingReasonNoDeficit };
        }

        foreach (var m in goal.Methods)
        {
            var open = p.Journal.LoadPlan(call, m.Plan);
            if (StorePackage.PlanOpen(open))
            {
                return new RoundsBuildingResult { Verdict = BuildingReasonExistingWork };
            }
        }

        var started = Reviewer.Clock.Now();
        var (identity, _) = Native.Identity(call);
        var expected = ObservationPackage.DecodeIdentity(identity);
        if (!RoundsBuildingBoundary(expected, state.Snapshot, review.Tick))
        {
            throw new ControlException("step: boundary moved");
        }

        var reading = Reviewer.ObserveRooms(call, (IRoundsSource)Native, expected, Unknown<IReadOnlyList<ConstructionClaim>>());
        var facts = reading.Projection;
        RecordStepRead("hospital", ConcernId.MaintainMedicalReserves, state.Snapshot, facts);
        var choice = PolicyFunctions.SelectHospitalBed(HospitalRequestOf(facts));
        switch (choice.Method)
        {
            case HospitalMethod.Unknown:
                return new RoundsBuildingResult { Verdict = FieldUnavailable("hospital") };
            case HospitalMethod.NoDemand:
                return new RoundsBuildingResult { Verdict = BuildingReasonNoDeficit };
            case HospitalMethod.Existing:
                return new RoundsBuildingResult { Verdict = BuildingExistingFacility };
            case HospitalMethod.Unavailable:
                return new RoundsBuildingResult { Verdict = BuildingHospitalUnavailable };
            case HospitalMethod.Build:
                return Building.Step(call, epoch, arbiter);
        }

        var method = new MethodId("hospital-convert-" + choice.Bed);
        try
        {
            p.Journal.LoadMethod(call, goal.Standard.Id, goal.Standard.Episode, method);
            return new RoundsBuildingResult { Verdict = WaitFor(Cause.MethodUsed, "hospital_convert_method") };
        }
        catch (NotFoundException)
        {
        }

        var (target, _) = Native.ReadBedUseTarget(call, BoundaryPackage.Identity(state.Snapshot), choice.Bed);
        try
        {
            BoundaryPackage.Context(target.Context, state.Snapshot);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ControlException("step: bed read context", e);
        }

        if (target.Context.Tick < facts.Identity.Tick.Value)
        {
            throw new ControlException("step: bed read older than the facts");
        }

        if (target.Medical)
        {
            return new RoundsBuildingResult { Verdict = BuildingExistingFacility };
        }

        var patch = BedUse.NewBedMedical(choice.Bed, true);
        if (!arbiter.TryClaim([], "bed:" + choice.Bed))
        {
            return new RoundsBuildingResult { Verdict = WaitFor(Cause.MethodUsed, "bed_claim") };
        }

        p.CommitBedPatch(call, epoch, state, goal, method, patch, () =>
        {
            if (Reviewer.Clock.Now() - started is var elapsed && (elapsed < TimeSpan.Zero || elapsed > Reviewer.MaxAge))
            {
                throw new ControlException("step: the bed read took too long");
            }

            var latest = p.Journal.LoadRounds(call);
            if (latest.Revision != review.Revision || !latest.Enabled)
            {
                throw new ControlException("step: the review moved or was disabled");
            }
        });
        return new RoundsBuildingResult { Verdict = BuildingReasonAdmitted };
    }
}

public sealed partial class Player
{
    /// <summary>Commits one bed-use plan under a method id; the guard, when given, runs after the session check and may throw to abort.</summary>
    internal void CommitBedPatch(CancellationToken call, CancellationToken epoch, ControlState state, StandardState goal, MethodId method, BedUse patch, Action? guard)
    {
        var id = PlanIds.MintPlanId();
        var action = DomainAction.NewBedUseAction(new ActionId($"{id}-0"), patch);
        var plan = PlanSpec.NewPlan(id, new PlanRevision(1), [action]);
        Current(call, epoch);
        if (Session.State() != state)
        {
            throw new ControlException("commitBedPatch: session state changed");
        }

        guard?.Invoke();
        Journal.CommitMethod(call, goal.Standard.Id, goal.Revision, method, plan);
    }
}
