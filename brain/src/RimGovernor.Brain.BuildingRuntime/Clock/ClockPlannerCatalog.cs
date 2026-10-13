namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>What every planner method result carries: the verdict the planner's step filed.</summary>
public interface IPlannerMethod
{
    Verdict Verdict { get; }
}

/// <summary>Declares a queued planner's priority, dependencies and wake conditions.</summary>
internal sealed record PlannerEntry
{
    public required string Name { get; init; }

    /// <summary>Receives this planner's refusal. Multi-concern planners leave it default and file under their own name (<see cref="FilingKey"/>).</summary>
    public ConcernId Concern { get; init; }

    /// <summary>Says whether the admission cycle waits on the planner: critical for the preempt and critical priority classes and the emergency evidence (fire safety), optional for the development reviews.</summary>
    public PlannerClass Class { get; init; }

    public int Priority { get; init; }

    /// <summary>Keeps shelter planning in the critical wave while NeedsShelter holds development. Optional-wave grace must not discard this work.</summary>
    public bool Startup { get; init; }

    /// <summary>The action kinds the planner dispatches (or, where commented on its entry, deliberately waits on). A terminal outcome of one of these kinds is the planner's own work completing, so a wake carrying it re-runs the planner (and only planners of that kind).</summary>
    public IReadOnlyList<ActionKind> Kinds { get; init; } = [];

    /// <summary>The review census sections the planner consumes: a wake whose invalidations dirty one re-runs the planner, and a step that selects a subset reads only what its planners declare.</summary>
    public IReadOnlyList<FactSection> Sections { get; init; } = [];

    /// <summary>The sectionless families the planner plans from (world).</summary>
    public IReadOnlyList<FactFamily> Families { get; init; } = [];

    /// <summary>The planner's review cadence in game ticks, zero for its priority class's default.</summary>
    public Tick Every { get; init; }

    /// <summary>Reports whether the composition wired this planner.</summary>
    public required Func<ClockSchedulerConfig, bool> Configured { get; init; }

    /// <summary>When set, stores on the result what a priority gate's hold files besides the planner's own verdict (the keys it reports on).</summary>
    public Action<ClockScheduler, Verdict, ClockSchedulerResult>? Held { get; init; }

    /// <summary>Steps the planner and stores its result on the given result; its error is wrapped with the planner's name.</summary>
    public required Func<ClockScheduler, CancellationToken, CancellationToken, ClockSchedulerResult, StepArbiter, Task<Verdict>> Run { get; init; }

    /// <summary>The planner's cadence: its own when set, else its priority class's.</summary>
    public Tick ReviewEvery()
    {
        if (Every.Value > 0)
        {
            return Every;
        }
        return Priority switch
        {
            PlannerPriority.Maintenance => ClockPlannerCatalog.ReviewEveryRounds,
            PlannerPriority.Comfort => ClockPlannerCatalog.ReviewEveryComfort,
            _ => ClockPlannerCatalog.ReviewEveryUrgent,
        };
    }

    /// <summary>
    /// The key a planner's verdict files its cause under, and so the target of its concern_transitions rows: its
    /// concern, or for a planner that serves several concerns (or none) its own name, a subject key beside the
    /// concern ids. A subject key has no progress record, so it never relabels a concern's strip.
    /// </summary>
    public ConcernId FilingKey() => string.IsNullOrEmpty(Concern.Value) ? new ConcernId(Name) : Concern;

    /// <summary>
    /// The fact families the planner may read: the family of each census section it consumes plus its sectionless
    /// families. The wake step re-runs a planner on exactly these invalidations, so a read outside them would plan
    /// on facts nothing wakes it for. Definitions and identity are ambient and declared by no one.
    /// </summary>
    public HashSet<FactFamily> Declared()
    {
        var output = new HashSet<FactFamily> { FactFamily.Definitions, FactFamily.Identity };
        foreach (var section in Sections)
        {
            output.Add(section.Family());
        }
        foreach (var family in Families)
        {
            output.Add(family);
        }
        return output;
    }

    /// <summary>
    /// Opens the planner's read-audit scope when an audit is installed (<see cref="ClockPlannerCatalog.ReadAudit"/>);
    /// null otherwise. Production installs none.
    /// </summary>
    public IDisposable? AuditReads()
    {
        var audit = ClockPlannerCatalog.ReadAudit;
        if (audit is null)
        {
            return null;
        }
        var declared = Declared();
        var name = Name;
        return ReadNoteScope.With((family, source) =>
        {
            if (!declared.Contains(family))
            {
                audit(name, family, source);
            }
        });
    }

    /// <summary>The run of a planner a priority gate holds.</summary>
    public Func<ClockScheduler, CancellationToken, CancellationToken, ClockSchedulerResult, StepArbiter, Task<Verdict>> Awaiting(Verdict v)
    {
        var held = Held;
        return async (s, _, _, output, _) =>
        {
            held?.Invoke(s, v, output);
            return await Task.FromResult(v).ConfigureAwait(false);
        };
    }
}

/// <summary>One rule that lets a higher-priority goal outrank the planner tiers: while <see cref="Owed"/> holds for the review, a planner outside <see cref="Allow"/> is not run, it files <see cref="Pending"/>. A concern the review calls an emergency is exempt.</summary>
internal sealed record PriorityGate(string Goal, Func<RoundsResult, bool> Owed, IReadOnlySet<string> Allow, Verdict Pending);

/// <summary>The routine planners in queue order and the rules that select and hold them.</summary>
internal static class ClockPlannerCatalog
{
    // Review cadences, in game ticks (2500 an hour): how long a planner's last evaluation stands before the due
    // queue re-runs it without evidence. Evidence (an outcome of its kinds, a dirty section it declares) re-runs
    // it sooner.
    public static readonly Tick ReviewEveryUrgent = new(GameTime.TicksPerHour);
    public static readonly Tick ReviewEveryRounds = new(GameTime.TicksPerHour);
    public static readonly Tick ReviewEveryComfort = new(15000);

    /// <summary>Called for every fact family a catalog planner reads that its entry does not declare. Only a test host installs one; production reads unaudited.</summary>
    public static Action<string, FactFamily, string>? ReadAudit { get; set; }

    // Census dependencies let each planner read only its required sections. Building planners need spatial facts
    // as well as the colony summary.
    private static readonly FactSection[] SectionsBuilding = [FactSection.Colony, FactSection.PlanningCells, FactSection.Zones, FactSection.Buildings, FactSection.Rooms];
    // The throne light refuel picks a hauler from the colonists.
    private static readonly FactSection[] SectionsThrone = [FactSection.Colony, FactSection.PlanningCells, FactSection.Zones, FactSection.Buildings, FactSection.Rooms, FactSection.Pawns];
    private static readonly FactSection[] SectionsColony = [FactSection.Colony];
    // The ritual plan reads the ideoligion, the building sites, the colonists and the emergency census.
    private static readonly FactSection[] SectionsRituals = [FactSection.Colony, FactSection.Pawns, FactSection.Emergency, FactSection.Buildings, FactSection.Ideology];
    private static readonly FactSection[] SectionsBills = [FactSection.Colony, FactSection.Bills];
    private static readonly FactSection[] SectionsZones = [FactSection.Colony, FactSection.Zones];
    private static readonly FactSection[] SectionsPawns = [FactSection.Pawns];
    private static readonly FactSection[] SectionsMedical = [FactSection.Pawns, FactSection.Colony];
    private static readonly FactSection[] SectionsThreat = [FactSection.Pawns, FactSection.Emergency];
    // Adds the population census for the owned-pawn names and the colony facts for the reading, drug and food policies.
    private static readonly FactSection[] SectionsWork = [FactSection.Pawns, FactSection.Population, FactSection.Emergency, FactSection.Colony, FactSection.Buildings];
    private static readonly FactSection[] SectionsRecovery = [FactSection.Pawns, FactSection.Emergency, FactSection.Colony];
    private static readonly FactSection[] SectionsCustody = [FactSection.Pawns, FactSection.Population, FactSection.Emergency, FactSection.Colony];
    private static readonly FactSection[] SectionsResearch = [FactSection.Research, FactSection.Colony, FactSection.Buildings, FactSection.Rooms];
    private static readonly FactSection[] SectionsFire = [FactSection.Emergency, FactSection.Colony];
    private static readonly FactSection[] SectionsResource = [FactSection.Colony, FactSection.Bills, FactSection.Buildings, FactSection.Zones];
    // The bills and threat sets plus the research census the armory tier is capped by.
    private static readonly FactSection[] SectionsArmory = [FactSection.Colony, FactSection.Bills, FactSection.Pawns, FactSection.Emergency, FactSection.Research];

    // The sectionless families planners declare beside their sections.
    private static readonly FactFamily[] FamiliesEmergency = [FactFamily.Emergency];
    private static readonly FactFamily[] FamiliesWorld = [FactFamily.World];

    private static PlannerEntry E<T>(
        string name,
        PlannerClass cls,
        int priority,
        ActionKind[] kinds,
        FactSection[] sections,
        Func<ClockSchedulerConfig, bool> configured,
        Func<ClockScheduler, CancellationToken, CancellationToken, StepArbiter, Task<T>> step,
        Action<ClockSchedulerResult, T> store,
        ConcernId concern = default,
        bool startup = false,
        FactFamily[]? families = null,
        Tick every = default,
        Action<ClockScheduler, Verdict, ClockSchedulerResult>? held = null)
        where T : IPlannerMethod => new()
        {
            Name = name,
            Concern = concern,
            Class = cls,
            Priority = priority,
            Startup = startup,
            Kinds = kinds,
            Sections = sections,
            Families = families ?? [],
            Every = every,
            Configured = configured,
            Held = held,
            Run = async (s, ct, epoch, output, arbiter) =>
            {
                var method = await step(s, ct, epoch, arbiter).ConfigureAwait(false);
                store(output, method);
                return method.Verdict;
            },
        };

    private const PlannerClass C = PlannerClass.Critical;
    private const PlannerClass O = PlannerClass.Optional;

    /// <summary>Lists every routine planner in queue order. Priorities follow DetectRounds's class for the planner's goal; queue order breaks priority ties, so the order here is the order Step queued them inline.</summary>
    public static readonly IReadOnlyList<PlannerEntry> Entries =
    [
        // undraft owns no plan: its OwnedDraft kind is a deliberate wake dependency, the end of another
        // planner's draft re-running the sweep.
        new PlannerEntry
        {
            Name = "undraft", Class = C, Priority = PlannerPriority.Critical, Kinds = [ActionKind.OwnedDraft], Sections = SectionsThreat,
            Configured = c => c.Rounds is not null,
            Run = async (s, ct, epoch, _, arbiter) =>
            {
                await s.Config.Rounds!.SweepDraftsAsync(arbiter, ct, epoch).ConfigureAwait(false);
                return default;
            },
        },
        E("work", O, PlannerPriority.Foothold, [ActionKind.WorkAssignment, ActionKind.PawnSettings, ActionKind.ReadingPolicy, ActionKind.DrugPolicy, ActionKind.FoodPolicy, ActionKind.Building], SectionsWork,
            c => c.Work is not null, (s, ct, e, a) => s.Config.Work!.StepAsync(a, ct, e), (o, m) => o.Work = m, EnsureWorkAssignments),
        E("fields", O, PlannerPriority.Foothold, [ActionKind.Building, ActionKind.ZoneCreate], SectionsBuilding,
            c => c.Fields is not null, (s, ct, e, a) => s.Config.Fields!.StepAsync(a, ct, e), (o, m) => o.Fields = m),
        E("foodAcquisition", O, PlannerPriority.Foothold, [ActionKind.Acquisition, ActionKind.AcquisitionWithdraw], SectionsColony,
            c => c.FoodAcquisition is not null, (s, ct, e, a) => s.Config.FoodAcquisition!.StepAsync(a, ct, e), (o, m) => o.FoodAcquisition = m, EnsureFoodSupply),
        E("pestAcquisition", O, PlannerPriority.Foothold, [ActionKind.Acquisition, ActionKind.AcquisitionWithdraw], SectionsColony,
            c => c.PestAcquisition is not null, (s, ct, e, a) => s.Config.PestAcquisition!.StepAsync(a, ct, e), (o, m) => o.PestAcquisition = m, ClearPests),
        E("resourceAcquisition", O, PlannerPriority.Maintenance, [ActionKind.Acquisition, ActionKind.AcquisitionWithdraw], SectionsColony,
            c => c.ResourceAcquisition is not null, (s, ct, e, a) => s.Config.ResourceAcquisition!.StepAsync(a, ct, e), (o, m) => o.ResourceAcquisition = m, MaintainResource),
        E("supplies", C, PlannerPriority.Critical, [ActionKind.SupplyAllow, ActionKind.SupplyForbid], SectionsColony,
            c => c.Supplies is not null, (s, ct, e, a) => s.Config.Supplies!.StepAsync(a, ct, e), (o, m) => o.Supplies = m),
        E("sleeping", O, PlannerPriority.Foothold, [ActionKind.Building, ActionKind.Deconstruction], SectionsBuilding,
            c => c.Sleeping is not null, (s, ct, e, a) => s.Config.Sleeping!.StepAsync(a, ct, e), (o, m) => o.Sleeping = m, MaintainHousing, startup: true),
        E("power", O, PlannerPriority.Foothold, [ActionKind.Building], SectionsBuilding,
            c => c.Power is not null, (s, ct, e, a) => s.Config.Power!.StepAsync(a, ct, e), (o, m) => o.Power = m, EnsureBasicPower),
        E("temperature", O, PlannerPriority.Foothold, [ActionKind.Building, ActionKind.DoorControl, ActionKind.OwnedDraft, ActionKind.Movement], SectionsBuilding,
            c => c.Temperature is not null, (s, ct, e, a) => s.Config.Temperature!.StepAsync(a, ct, e), (o, m) => o.Temperature = m, EnsureTemperatureSafety),
        E("refrigeration", O, PlannerPriority.Maintenance, [ActionKind.Building, ActionKind.BuildingTemperature], SectionsBuilding,
            c => c.Refrigeration is not null, (s, ct, e, a) => s.Config.Refrigeration!.StepAsync(a, ct, e), (o, m) => o.Refrigeration = m, MaintainRefrigeration),
        E("lighting", O, PlannerPriority.Maintenance, [ActionKind.Building], SectionsBuilding,
            c => c.Lighting is not null, (s, ct, e, a) => s.Config.Lighting!.StepAsync(a, ct, e), (o, m) => o.Lighting = m, MaintainLighting),
        E("flooring", O, PlannerPriority.Maintenance, [ActionKind.Building], SectionsBuilding,
            c => c.Flooring is not null, (s, ct, e, a) => s.Config.Flooring!.StepAsync(a, ct, e), (o, m) => o.Flooring = m, MaintainFlooring),
        E("routes", O, PlannerPriority.Maintenance, [ActionKind.Building, ActionKind.DoorControl, ActionKind.OwnedDraft, ActionKind.Movement], SectionsBuilding,
            c => c.Routes is not null, (s, ct, e, a) => s.Config.Routes!.StepAsync(a, ct, e), (o, m) => o.Routes = m, MaintainRoutes),
        E("cooking", O, PlannerPriority.Foothold, [ActionKind.Building], SectionsBuilding,
            c => c.Cooking is not null, (s, ct, e, a) => s.Config.Cooking!.StepAsync(a, ct, e), (o, m) => o.Cooking = m, EnsureCooking),
        E("butcher", O, PlannerPriority.Foothold, [ActionKind.Building], SectionsBuilding,
            c => c.Butcher is not null, (s, ct, e, a) => s.Config.Butcher!.StepAsync(a, ct, e), (o, m) => o.Butcher = m, EnsureFoodSupply),
        E("basicComfort", O, PlannerPriority.Foothold, [ActionKind.Building], SectionsBuilding,
            c => c.BasicComfort is not null, (s, ct, e, a) => s.Config.BasicComfort!.StepAsync(a, ct, e), (o, m) => o.BasicComfort = m, EnsureComfort),
        E("comfort", O, PlannerPriority.Comfort, [ActionKind.Building], SectionsBuilding,
            c => c.Comfort is not null, (s, ct, e, a) => s.Config.Comfort!.StepAsync(a, ct, e), (o, m) => o.Comfort = m, EnsureComfort),
        E("workshop", O, PlannerPriority.Maintenance, [ActionKind.Building], SectionsBuilding,
            c => c.Workshop is not null, (s, ct, e, a) => s.Config.Workshop!.StepWorkshopsAsync(a, ct, e), (o, m) => o.Workshop = m),
        E("hospital", C, PlannerPriority.Critical, [ActionKind.Building, ActionKind.BedUse], SectionsBuilding,
            c => c.Hospital is not null, (s, ct, e, a) => s.Config.Hospital!.StepAsync(a, ct, e), (o, m) => o.Hospital = m),
        E("sleepingUpkeep", C, PlannerPriority.Critical, [ActionKind.Building, ActionKind.Assign, ActionKind.MoveBuilding, ActionKind.UninstallBuilding, ActionKind.RecoveryService], SectionsThrone,
            c => c.SleepingUpkeep is not null, (s, ct, e, a) => s.Config.SleepingUpkeep!.StepAsync(a, ct, e), (o, m) => o.SleepingUpkeep = m, MaintainHousing),
        E("expansion", O, PlannerPriority.Comfort, [ActionKind.Building, ActionKind.Excavation], SectionsBuilding,
            c => c.Expansion is not null, (s, ct, e, a) => s.Config.Expansion!.StepAsync(a, ct, e), (o, m) => o.Expansion = m, MaintainHousing),
        E(DefensePlanner, C, PlannerPriority.Preempt, [ActionKind.OwnedDraft, ActionKind.Subdue, ActionKind.Ability], SectionsThreat,
            c => c.Defense is not null, (s, ct, e, a) => s.Config.Defense!.StepAsync(a, ct, e), (o, m) => o.Defense = m),
        E("medical", C, PlannerPriority.Critical, [ActionKind.Acquisition], SectionsMedical,
            c => c.Medical is not null, (s, ct, e, a) => s.Config.Medical!.StepAsync(a, ct, e), (o, m) => o.Medical = m, MaintainMedicalReserves),
        E("surgery", C, PlannerPriority.Critical, [ActionKind.Surgery], SectionsMedical,
            c => c.Surgery is not null, (s, ct, e, a) => s.Config.Surgery!.StepAsync(a, ct, e), (o, m) => o.Surgery = m, MaintainSurgery),
        E("tend", C, PlannerPriority.Critical, [ActionKind.Tend], SectionsThreat,
            c => c.Tend is not null, (s, ct, e, a) => s.Config.Tend!.StepAsync(a, ct, e), (o, m) => o.Tend = m),
        E("rescue", C, PlannerPriority.Critical, [ActionKind.Rescue], SectionsThreat,
            c => c.Rescue is not null, (s, ct, e, a) => s.Config.Rescue!.StepAsync(a, ct, e), (o, m) => o.Rescue = m),
        E("equip", O, PlannerPriority.Maintenance, [ActionKind.Equip], SectionsMedical,
            c => c.Equip is not null, (s, ct, e, a) => s.Config.Equip!.StepAsync(a, ct, e), (o, m) => o.Equip = m, EnsureBasicDefense, families: FamiliesEmergency),
        E("repair", O, PlannerPriority.Maintenance, [ActionKind.Repair], SectionsBuilding,
            c => c.Repair is not null, (s, ct, e, a) => s.Config.Repair!.StepAsync(a, ct, e), (o, m) => o.Repair = m, MaintainEssentialRepairs, families: FamiliesEmergency),
        // fireSafety takes no arbiter: it dispatches no plan of its own.
        E("fireSafety", C, PlannerPriority.Foothold, [], SectionsFire,
            c => c.FireSafety is not null, (s, ct, e, _) => s.Config.FireSafety!.StepAsync(ct, e), (o, m) => o.FireSafety = m, MaintainFireSafety),
        E("clearance", O, PlannerPriority.Maintenance, [ActionKind.Deconstruction], SectionsColony,
            c => c.Clearance is not null, (s, ct, e, a) => s.Config.Clearance!.StepAsync(a, ct, e), (o, m) => o.Clearance = m, ClearHomeObstructions),
        E("shrine", O, PlannerPriority.Maintenance, [ActionKind.OwnedDraft, ActionKind.Movement, ActionKind.Deconstruction], SectionsColony,
            c => c.Shrine is not null, (s, ct, e, a) => s.Config.Shrine!.StepAsync(a, ct, e), (o, m) => o.Shrine = m, ClearAncientShrine),
        E("pollution", O, PlannerPriority.Maintenance, [ActionKind.Area, ActionKind.SupplyAllow, ActionKind.WastepackHaul], SectionsBuilding,
            c => c.Pollution is not null, (s, ct, e, a) => s.Config.Pollution!.StepAsync(a, ct, e), (o, m) => o.Pollution = m, ManagePollution),
        E("mechcharger", O, PlannerPriority.Maintenance, [ActionKind.Building], SectionsBuilding,
            c => c.MechCharger is not null, (s, ct, e, a) => s.Config.MechCharger!.StepAsync(a, ct, e), (o, m) => o.MechCharger = m, EnsureMechCharger),
        E("genebank", O, PlannerPriority.Maintenance, [ActionKind.Building], SectionsBuilding,
            c => c.GeneBank is not null, (s, ct, e, a) => s.Config.GeneBank!.StepAsync(a, ct, e), (o, m) => o.GeneBank = m, MaintainGeneBank),
        // workLedger also files the verdicts of the bill purposes (RoundsLedgerResult.Reports): when a priority
        // gate holds it, it holds them with it.
        E("workLedger", O, PlannerPriority.Maintenance, [ActionKind.ProductionBill, ActionKind.RemoveProductionBill], SectionsBills,
            c => c.Ledger is not null, (s, ct, e, a) => s.Config.Ledger!.StepAsync(a, ct, e), (o, m) => o.Ledger = m, MaintainWorkLedger,
            held: (s, v, o) => o.Ledger = new RoundsLedgerResult { Verdict = v, Reports = s.Config.Ledger!.Reviewer.HeldReports(v) }),
        E("blight", O, PlannerPriority.Maintenance, [ActionKind.CutPlant], SectionsColony,
            c => c.Blight is not null, (s, ct, e, a) => s.Config.Blight!.StepAsync(a, ct, e), (o, m) => o.Blight = m, RemoveBlight),
        E("armory", O, PlannerPriority.Maintenance, [ActionKind.Building], SectionsArmory,
            c => c.Armory is not null, (s, ct, e, a) => s.Config.Armory!.StepAsync(a, ct, e), (o, m) => o.Armory = m),
        E("burial", O, PlannerPriority.Maintenance, [ActionKind.Building], SectionsBuilding,
            c => c.Burial is not null, (s, ct, e, a) => s.Config.Burial!.StepAsync(a, ct, e), (o, m) => o.Burial = m, MaintainBurial),
        E("training", O, PlannerPriority.Maintenance, [ActionKind.Building], SectionsBuilding,
            c => c.Training is not null, (s, ct, e, a) => s.Config.Training!.StepAsync(a, ct, e), (o, m) => o.Training = m, MaintainTraining),
        E("envelope", O, PlannerPriority.Maintenance, [ActionKind.Building, ActionKind.BuildRoof], SectionsBuilding,
            c => c.Envelope is not null, (s, ct, e, a) => s.Config.Envelope!.StepAsync(a, ct, e), (o, m) => o.Envelope = m, MaintainClimateEnvelope),
        E("incineration", O, PlannerPriority.Maintenance, [ActionKind.Building, ActionKind.Equip, ActionKind.OwnedDraft, ActionKind.Ignite, ActionKind.Clean], SectionsBuilding,
            c => c.Incineration is not null, (s, ct, e, a) => s.Config.Incineration!.StepAsync(a, ct, e), (o, m) => o.Incineration = m, MaintainIncineration, families: FamiliesEmergency),
        E("moodRelief", O, PlannerPriority.Maintenance, [ActionKind.MoodRelief, ActionKind.Ability], SectionsPawns,
            c => c.MoodRelief is not null, (s, ct, e, a) => s.Config.MoodRelief!.StepAsync(a, ct, e), (o, m) => o.MoodRelief = m),
        E("gear", O, PlannerPriority.Maintenance, [ActionKind.GearReplace, ActionKind.ApparelPolicy, ActionKind.PolicyPrune], SectionsMedical,
            c => c.Gear is not null, (s, ct, e, a) => s.Config.Gear!.StepAsync(a, ct, e), (o, m) => o.Gear = m, MaintainEquipment),
        E("foodStorageUpkeep", O, PlannerPriority.Foothold, [ActionKind.Haul, ActionKind.SupplyAllow, ActionKind.SupplyForbid], SectionsZones,
            c => c.FoodStorageUpkeep is not null, (s, ct, e, a) => s.Config.FoodStorageUpkeep!.StepAsync(a, ct, e), (o, m) => o.FoodStorageUpkeep = m, MaintainFoodStorage),
        E("animalContainment", O, PlannerPriority.Maintenance, [ActionKind.Building], SectionsBuilding,
            c => c.AnimalContainment is not null, (s, ct, e, a) => s.Config.AnimalContainment!.StepAsync(a, ct, e), (o, m) => o.AnimalContainment = m, MaintainAnimalContainment),
        E("recovery", C, PlannerPriority.Critical, [ActionKind.RecoveryService, ActionKind.WorkAssignment, ActionKind.Husbandry], SectionsRecovery,
            c => c.Recovery is not null, (s, ct, e, a) => s.Config.Recovery!.StepAsync(a, ct, e), (o, m) => o.Recovery = m),
        E("rules", O, PlannerPriority.Maintenance, [ActionKind.RulesAttach], SectionsWork,
            c => c.Rules is not null, (s, ct, e, a) => s.Config.Rules!.StepAsync(a, ct, e), (o, m) => o.Rules = m, EnsureFoodSupply, every: new Tick(ReviewEveryRounds.Value / 2)),
        E("husbandry", O, PlannerPriority.Maintenance, [ActionKind.Husbandry], [FactSection.Pawns, FactSection.Colony],
            c => c.Husbandry is not null, (s, ct, e, a) => s.Config.Husbandry!.StepAsync(a, ct, e), (o, m) => o.Husbandry = m),
        E("prisonerInteraction", O, PlannerPriority.Maintenance, [ActionKind.PrisonerInteraction], [FactSection.Population, FactSection.Pawns],
            c => c.PrisonerInteraction is not null, (s, ct, e, a) => s.Config.PrisonerInteraction!.StepAsync(a, ct, e), (o, m) => o.PrisonerInteraction = m),
        E("populationCustody", C, PlannerPriority.Preempt, [ActionKind.Capture, ActionKind.UseItem, ActionKind.Rescue, ActionKind.Tend, ActionKind.DoorControl, ActionKind.RecoveryService], SectionsCustody,
            c => c.PopulationCustody is not null, (s, ct, e, a) => s.Config.PopulationCustody!.StepAsync(a, ct, e), (o, m) => o.PopulationCustody = m),
        E("populationJoiner", O, PlannerPriority.Maintenance,
            [ActionKind.Tend, ActionKind.RecoveryService, ActionKind.QuestAccept, ActionKind.QuestShuttle, ActionKind.HackDesignation, ActionKind.GiveItem, ActionKind.Ritual, ActionKind.DialogAnswer, ActionKind.ZoneCreate, ActionKind.Acquisition, ActionKind.MineAcquisition, ActionKind.PrisonerInteraction, ActionKind.Building, ActionKind.MoveBuilding, ActionKind.Haul, ActionKind.CaravanDeparture],
            SectionsMedical,
            c => c.PopulationJoiner is not null, (s, ct, e, a) => s.Config.PopulationJoiner!.StepAsync(a, ct, e), (o, m) => o.PopulationJoiner = m),
        E("research", O, PlannerPriority.Maintenance, [ActionKind.ResearchSelect, ActionKind.Building], SectionsResearch,
            c => c.Research is not null, (s, ct, e, a) => s.Config.Research!.StepAsync(a, ct, e), (o, m) => o.Research = m, EnsureResearch),
        E("dialog", C, PlannerPriority.Preempt, [ActionKind.DialogAnswer], SectionsColony,
            c => c.Dialog is not null, (s, ct, e, a) => s.Config.Dialog!.StepAsync(a, ct, e), (o, m) => o.Dialog = m),
        E("trade", O, PlannerPriority.Foothold, [ActionKind.Trade, ActionKind.CommsTradeRequest], SectionsColony,
            c => c.Trade is not null, (s, ct, e, a) => s.Config.Trade!.StepAsync(a, ct, e), (o, m) => o.Trade = m, families: FamiliesWorld),
        E("resource", O, PlannerPriority.Maintenance, [ActionKind.MineAcquisition, ActionKind.Building, ActionKind.Deconstruction], SectionsResource,
            c => c.Resource is not null, (s, ct, e, a) => s.Config.Resource!.StepAsync(a, ct, e), (o, m) => o.Resource = m, MaintainResource),
        E("maintainShelter", O, PlannerPriority.Comfort, [ActionKind.Area], SectionsBuilding,
            c => c.MaintainShelter is not null, (s, ct, e, a) => s.Config.MaintainShelter!.StepAsync(a, ct, e), (o, m) => o.MaintainShelter = m, MaintainShelter),
        E("firebreak", O, PlannerPriority.Maintenance, [ActionKind.AreaPlantCut, ActionKind.CoverClearance], SectionsBuilding,
            c => c.Firebreak is not null, (s, ct, e, a) => s.Config.Firebreak!.StepAsync(a, ct, e), (o, m) => o.Firebreak = m, MaintainFirebreak),
        E("psylink", O, PlannerPriority.Maintenance, [ActionKind.UseItem], SectionsBuilding,
            c => c.Psylink is not null, (s, ct, e, a) => s.Config.Psylink!.StepAsync(a, ct, e), (o, m) => o.Psylink = m, MaintainPsylink),
        E("creepJoiners", O, PlannerPriority.Maintenance, [ActionKind.DropEquipment, ActionKind.Surgery, ActionKind.WorkAssignment], SectionsMedical,
            c => c.CreepJoiners is not null, (s, ct, e, a) => s.Config.CreepJoiners!.StepAsync(a, ct, e), (o, m) => o.CreepJoiners = m, ManageCreepJoiners),
        E("ideoRoles", O, PlannerPriority.Maintenance, [ActionKind.Assign], SectionsBuilding,
            c => c.IdeoRoles is not null, (s, ct, e, a) => s.Config.IdeoRoles!.StepAsync(a, ct, e), (o, m) => o.IdeoRoles = m, MaintainIdeoRoles),
        E("rituals", O, PlannerPriority.Maintenance, [ActionKind.Ritual], SectionsRituals,
            c => c.Rituals is not null, (s, ct, e, a) => s.Config.Rituals!.StepAsync(a, ct, e), (o, m) => o.Rituals = m, MaintainRituals),
        E("gathering", O, PlannerPriority.Maintenance, [ActionKind.Gathering], SectionsRituals,
            c => c.Gathering is not null, (s, ct, e, a) => s.Config.Gathering!.StepAsync(a, ct, e), (o, m) => o.Gathering = m, HoldGatherings),
        E("ideoligion", O, PlannerPriority.Maintenance, [ActionKind.IdeoligionReform], SectionsRituals,
            c => c.Ideoligion is not null, (s, ct, e, a) => s.Config.Ideoligion!.StepAsync(a, ct, e), (o, m) => o.Ideoligion = m, ImproveIdeoligion),
        E("permits", O, PlannerPriority.Maintenance, [ActionKind.PawnSettings], SectionsBuilding,
            c => c.Permits is not null, (s, ct, e, a) => s.Config.Permits!.StepAsync(a, ct, e), (o, m) => o.Permits = m, MaintainPermits),
        E("homeCoverage", O, PlannerPriority.Comfort, [ActionKind.Area], SectionsBuilding,
            c => c.HomeCoverage is not null, (s, ct, e, a) => s.Config.HomeCoverage!.StepAsync(a, ct, e), (o, m) => o.HomeCoverage = m, MaintainHomeCoverage),
        E("stoneShell", O, PlannerPriority.Comfort, [ActionKind.Building, ActionKind.WallRemoval], SectionsBuilding,
            c => c.StoneShell is not null, (s, ct, e, a) => s.Config.StoneShell!.StepAsync(a, ct, e), (o, m) => o.StoneShell = m, MaintainStoneShell),
        E("stockpiles", O, PlannerPriority.Foothold, [ActionKind.ZoneCellEdit, ActionKind.StockpilePatch, ActionKind.ZoneDelete, ActionKind.ZoneCreate, ActionKind.Building], SectionsBuilding,
            c => c.Stockpiles is not null, (s, ct, e, a) => s.Config.Stockpiles!.StepAsync(a, ct, e), (o, m) => o.Stockpiles = m, MaintainStockpiles),
        E("defenseLayout", O, PlannerPriority.Maintenance, [ActionKind.Building, ActionKind.RecoveryService, ActionKind.CoverClearance, ActionKind.FoundationRemoval], SectionsBuilding,
            c => c.DefenseLayout is not null, (s, ct, e, a) => s.Config.DefenseLayout!.StepAsync(a, ct, e), (o, m) => o.DefenseLayout = m, EnsureDefensiveLayout),
    ];

    /// <summary>
    /// Names the planners that run while the initial shelter is owed: what builds it (work priorities, the start
    /// kit's allow/forbid, the shelter and its bunk assignment), the stockpile zones the kit is stored in and what
    /// answers an event (a raid, a patient, a fire, a dialog, a disaster, custody). A hostile-temperature start
    /// owes the colonists no other action until the shelter stands; the rest are held with
    /// <see cref="BuildingReasonShelterPending"/>.
    /// </summary>
    public static readonly IReadOnlySet<string> RunsBeforeShelter = new HashSet<string>(StringComparer.Ordinal)
    {
        "undraft", "work", "supplies", "sleeping", "sleepingUpkeep",
        "hospital", "tend", "rescue", "recovery", "populationCustody",
        "fireSafety", "dialog", DefensePlanner, "stockpiles",
    };

    /// <summary>Reports whether the review binds an active MaintainHousing goal still in deficit on its starter-shelter phase.</summary>
    public static bool ShelterOwed(RoundsResult review)
    {
        if (review.Review.Latches.Housing != Policy.Phase.HousingShelter)
        {
            return false;
        }
        foreach (var binding in review.Review.Standards)
        {
            if (binding.Concern != MaintainHousing)
            {
                continue;
            }
            foreach (var state in review.Standards)
            {
                if (state.Standard.Id == binding.Standard)
                {
                    return state.Standard.Status == StandardStatus.Open && state.Standard.Finding == Finding.Unmet;
                }
            }
        }
        return false;
    }

    /// <summary>The gates in force, in precedence order: the first row that holds a planner decides its verdict.</summary>
    public static readonly IReadOnlyList<PriorityGate> PriorityGates =
    [
        new PriorityGate("initial_shelter", ShelterOwed, RunsBeforeShelter, BuildingReasonShelterPending),
    ];

    /// <summary>Reports the verdict <paramref name="entry"/> files when a gate of <see cref="PriorityGates"/> holds it under <paramref name="review"/>.</summary>
    public static Verdict? HeldByPriority(PlannerEntry entry, RoundsResult? review)
    {
        if (review is null)
        {
            return null;
        }
        foreach (var gate in PriorityGates)
        {
            if (gate.Allow.Contains(entry.Name) || !gate.Owed(review))
            {
                continue;
            }
            if (!string.IsNullOrEmpty(entry.Concern.Value) && review.Emergency.Contains(entry.Concern))
            {
                continue;
            }
            return gate.Pending;
        }
        return null;
    }
}

public sealed partial class ClockScheduler
{
    /// <summary>
    /// Queues the configured planners <paramref name="pick"/> selects (all when null) onto the wave, returning the
    /// names queued in catalog order. With <paramref name="startup"/> set, a startup planner is queued into the
    /// critical cycle rather than the optional wave. While a priority gate holds under <paramref name="review"/>
    /// every planner outside its allowlist is queued as a hold, so its cadence and filing stay those of a planner
    /// that ran and refused.
    /// </summary>
    internal List<string> QueuePlanners(PlannerWave wave, StepArbiter arbiter, Func<PlannerEntry, bool>? pick, bool startup, RoundsResult? review, CancellationToken ct, CancellationToken epoch)
    {
        var queued = new List<string>();
        foreach (var catalogEntry in _catalog)
        {
            var entry = catalogEntry;
            if (!entry.Configured(Config) || pick is not null && !pick(entry))
            {
                continue;
            }
            if (ClockPlannerCatalog.HeldByPriority(entry, review) is { } pending)
            {
                entry = entry with { Run = entry.Awaiting(pending) };
            }
            if (startup && entry.Startup || ReviewScope.Immediate && ImmediatePlanner(entry))
            {
                entry = entry with { Class = PlannerClass.Critical };
            }
            queued.Add(entry.Name);
            wave.Queue(this, arbiter, entry, epoch);
        }
        return queued;
    }
}
