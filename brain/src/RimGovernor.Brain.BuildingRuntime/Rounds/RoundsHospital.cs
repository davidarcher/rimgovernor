using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>The medical/care Need's work (#3258): a hospital bed built, converted or already standing, under the Need and admitted by claims.</summary>
public sealed partial class RoundsHospitalPlanner
{
    internal Rounder Reviewer { get; init; } = null!;

    internal FacadeNativeClient Native { get; init; } = null!;

    internal RoundsBuildingPlanner Building { get; init; } = null!;

    public static RoundsHospitalPlanner NewRoundsHospitalPlanner(Rounder reviewer, IRoundsBuildingSource native)
    {
        if (reviewer is null || native is null)
        {
            throw new ControlException("NewRoundsHospitalPlanner: reviewer or native is missing");
        }

        if (native is not FacadeNativeClient source)
        {
            throw new ControlException("NewRoundsHospitalPlanner: native cannot read bed use");
        }

        if (native is not IRoundsSource)
        {
            throw new ControlException("NewRoundsHospitalPlanner: native is not a rounds source");
        }

        var building = new RoundsBuildingPlanner { Reviewer = reviewer, Native = native, Concern = MedicalRules.MedicalCareNeed.FilingKey, Need = MedicalRules.MedicalCareNeed };
        return new RoundsHospitalPlanner { Reviewer = reviewer, Native = source, Building = building };
    }
}

partial class RoundsHospitalPlanner
{
    internal static HospitalRequest HospitalRequestOf(ColonyProjection facts)
    {
        var definitions = facts.Definitions
            .Select(d => new BenchDefinition { Name = d.Name, Available = d.Available, NeedsPower = d.NeedsPower, ConstructionSkill = d.ConstructionSkill })
            .ToList();
        var f = facts.Facts;
        var doctors = facts.WorkPawns is WorkPawnsRead.Read { Pawns: var pawns } ? LaborRules.WorkersFor(pawns, WorkType.Doctor) : Unknown<int>();

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
            Surgical = SurgeryRules.SurgeryBedShortPatients(f.MedicalPawns),
            MinBeds = f.GoalBuild.HospitalBeds,
        };
    }
}

public sealed partial class RoundsBuildingPlanner
{
    internal (RoundsBuildingPlanner? Planner, Verdict Verdict) SelectHospital(ColonyProjection facts)
    {
        var choice = HospitalRules.SelectHospitalBed(RoundsHospitalPlanner.HospitalRequestOf(facts));
        switch (choice.Method)
        {
            case HospitalMethod.Unknown:
                return (null, FieldUnavailable("hospital"));
            case HospitalMethod.NoDemand:
                return (null, BuildingReasonNoDeficit);
            case HospitalMethod.Existing:
                return (null, BuildingReasonExistingFacility);
            case HospitalMethod.Convert:
                return (null, BuildingReasonHospitalConvert);
            case HospitalMethod.Unavailable:
                return (null, BuildingReasonHospitalUnavailable);
        }

        var facility = RoomsRules.Facility(RoomRole.Hospital);
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
    /// <summary>
    /// The care Need's hospital step: the bed the hospital ladder selects is built through the shared building
    /// planner, or an existing bed is flagged medical once per bed. A convert is issued again only after its attempt
    /// is released and the bed reads non-medical (decision 19), through its refusal budget.
    /// </summary>
    internal async Task<RoundsBuildingResult> StepAsync(NeedWork work, CancellationToken call)
    {
        var p = Reviewer.Player;
        var state = work.State;
        if (!state.ObservationKnown || !state.Snapshot.IsValid || state.Snapshot.Native.Value == 0)
        {
            throw new ControlException("step: observation unknown, snapshot invalid or native generation unset");
        }

        var review = p.Journal.LoadRounds(call);
        if (!review.Enabled || !review.Snapshot.Matches(state.Snapshot))
        {
            return new RoundsBuildingResult { Verdict = BuildingReasonNoReview };
        }

        var started = Reviewer.Clock.Now();
        var identity = await Native.IdentityAsync(call).ConfigureAwait(false);
        var expected = DecodeReads.DecodeIdentity(identity);
        if (!RoundsBuildingPlanner.RoundsBuildingBoundary(expected, state.Snapshot, review.Tick))
        {
            throw new ControlException("step: boundary moved");
        }

        var reading = await Reviewer.ObserveRoomsAsync((IRoundsSource)Native, expected, Unknown<EquatableArray<ConstructionClaim>>(), call).ConfigureAwait(false);
        var facts = reading.Projection;
        PlannerSnapshot.RecordStepRead("hospital", MedicalRules.MedicalCareNeed.FilingKey, state.Snapshot, facts);
        var choice = HospitalRules.SelectHospitalBed(HospitalRequestOf(facts));
        switch (choice.Method)
        {
            case HospitalMethod.Unknown:
                return new RoundsBuildingResult { Verdict = FieldUnavailable("hospital") };
            case HospitalMethod.NoDemand:
                return new RoundsBuildingResult { Verdict = BuildingReasonNoDeficit };
            case HospitalMethod.Existing:
                return new RoundsBuildingResult { Verdict = BuildingReasonExistingFacility };
            case HospitalMethod.Unavailable:
                return new RoundsBuildingResult { Verdict = BuildingReasonHospitalUnavailable };
            case HospitalMethod.Build:
                return await Building.Copy().StepAsync(work.Arbiter!, call, work.Epoch).ConfigureAwait(false);
        }

        var target = await Native.ReadBedUseTargetAsync(Boundary.Identity(state.Snapshot), choice.Bed, call).ConfigureAwait(false);
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

        if (target.Medical)
        {
            return new RoundsBuildingResult { Verdict = BuildingReasonExistingFacility };
        }

        var method = new MethodId("hospital-convert-" + choice.Bed);
        var attempts = p.Journal.OwnerMethodHistory(work.Owner, call).Where(m => m.Method == method).Select(m => m.Plan).ToList();
        if (!work.Owner.Methods.Any(m => m.Method == method))
        {
            var (refused, admitted) = RoundsRefusalBudget.AdmitSubject(p.Journal, method.Value, attempts, state.Snapshot, call);
            if (!admitted)
            {
                return new RoundsBuildingResult { Verdict = refused };
            }
        }

        var patch = BedUse.NewBedMedical(choice.Bed, true);
        if (!work.Arbiter!.TryClaim([], "bed:" + choice.Bed))
        {
            return new RoundsBuildingResult { Verdict = WaitFor(Cause.MethodUsed, "bed_claim") };
        }

        var commit = p.CommitBedPatch(state, work.Owner, method, patch, () =>
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
        }, call, work.Epoch);
        return new RoundsBuildingResult { Verdict = RoundsBuildingPlanner.OwnerCommitVerdict(commit) };
    }
}

public sealed partial class Player
{
    /// <summary>Commits one bed-use plan under a method id; the guard, when given, runs after the session check and may throw to abort.</summary>
    internal Sto.WorkCommit CommitBedPatch(ControlState state, Sto.NeedOwner owner, MethodId method, BedUse patch, Action? guard, CancellationToken call, CancellationToken epoch)
    {
        var id = PlanIds.MintPlanId();
        var action = DomainAction.NewBedUseAction(new ActionId($"{id}-0"), patch);
        var plan = new PlanSpec(id, new PlanRevision(1), [action]);
        Current(call, epoch);
        if (Session.State() != state)
        {
            throw new ControlException("commitBedPatch: session state changed");
        }

        guard?.Invoke();
        return Journal.CommitOwnerWork(owner, method, "", plan, call);
    }
}
