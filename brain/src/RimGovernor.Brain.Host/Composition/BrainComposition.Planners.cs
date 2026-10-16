using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Facts;
using RimGovernor.Brain.Policy;
using static RimGovernor.Brain.Policy.PolicyConstants;
using static RimGovernor.Brain.Policy.PolicyFunctions;

namespace RimGovernor.Brain.Host;

public sealed partial class BrainComposition
{
    // Budgets census and composed planner reads independently of epoch renewal (Go serviceClockStepTimeout).
    private static readonly TimeSpan StepTimeout = TimeSpan.FromMinutes(1);

    /// <inheritdoc />
    public PlannerParts ComposePlanners(PlannerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var client = _client ?? throw new InvalidOperationException("ComposePlanners runs after ComposeSession");
        var writer = _writer!;
        var sc = _options.Families;
        var log = new HostLog(context.Telemetry, "clock-scheduler");

        // ClockSchedulerConfig is init-only, so the planners are built into locals and the config is made last.
        RoundsAnimalContainmentPlanner? cfgAnimalContainment = null;
        RoundsArmoryPlanner? cfgArmory = null;
        RoundsBuildingPlanner? cfgBasicComfort = null;
        RoundsBlightPlanner? cfgBlight = null;
        RoundsBurialPlanner? cfgBurial = null;
        RoundsBuildingPlanner? cfgButcher = null;
        var families = new List<FamilyRunner>();
        RoundsBuildingPlanner? cfgComfort = null;
        RoundsBuildingPlanner? cfgCooking = null;
        RoundsCreepJoinerPlanner? cfgCreepJoiners = null;
        RoundsDefensePlanner? cfgDefense = null;
        RoundsDefenseLayoutPlanner? cfgDefenseLayout = null;
        RoundsDialogPlanner? cfgDialog = null;
        RoundsEnvelopePlanner? cfgEnvelope = null;
        RoundsEquipPlanner? cfgEquip = null;
        RoundsBuildingPlanner? cfgExpansion = null;
        RoundsFieldPlanner? cfgFields = null;
        RoundsFirebreakPlanner? cfgFirebreak = null;
        RoundsBuildingPlanner? cfgFlooring = null;
        RoundsAcquisitionPlanner? cfgFoodAcquisition = null;
        RoundsGatheringPlanner? cfgGathering = null;
        RoundsGearPlanner? cfgGear = null;
        RoundsGeneBankPlanner? cfgGeneBank = null;
        RoundsHomeCoveragePlanner? cfgHomeCoverage = null;
        RoundsHospitalPlanner? cfgHospital = null;
        RoundsHusbandryPlanner? cfgHusbandry = null;
        RoundsIdeoRolesPlanner? cfgIdeoRoles = null;
        RoundsIdeoligionPlanner? cfgIdeoligion = null;
        RoundsLedgerPlanner? cfgLedger = null;
        RoundsBuildingPlanner? cfgLighting = null;
        MaintainShelterPlanner? cfgMaintainShelter = null;
        RoundsMechChargerPlanner? cfgMechCharger = null;
        RoundsMedicalPlanner? cfgMedical = null;
        RoundsMoodReliefPlanner? cfgMoodRelief = null;
        RoundsPermitsPlanner? cfgPermits = null;
        RoundsAcquisitionPlanner? cfgPestAcquisition = null;
        RoundsPollutionPlanner? cfgPollution = null;
        RoundsPopulationCustodyPlanner? cfgPopulationCustody = null;
        RoundsPopulationJoinerPlanner? cfgPopulationJoiner = null;
        RoundsBuildingPlanner? cfgPower = null;
        RoundsPrisonerInteractionPlanner? cfgPrisonerInteraction = null;
        RoundsPsylinkPlanner? cfgPsylink = null;
        RoundsRecoveryPlanner? cfgRecovery = null;
        RoundsRescuePlanner? cfgRescue = null;
        RoundsResearchPlanner? cfgResearch = null;
        RoundsResourcePlanner? cfgResource = null;
        RoundsAcquisitionPlanner? cfgResourceAcquisition = null;
        RoundsRitualsPlanner? cfgRituals = null;
        Rounder? cfgRounds = null;
        RoundsBuildingPlanner? cfgRoutes = null;
        RoundsBuildingPlanner? cfgSleeping = null;
        RoundsSleepingUpkeepPlanner? cfgSleepingUpkeep = null;
        RoundsStockpilePlanner? cfgStockpiles = null;
        RoundsStoneShellPlanner? cfgStoneShell = null;
        FactStore? cfgStore = null;
        RoundsSupplyPlanner? cfgSupplies = null;
        RoundsSurgeryPlanner? cfgSurgery = null;
        RoundsBuildingPlanner? cfgTemperature = null;
        RoundsTendPlanner? cfgTend = null;
        RoundsTradePlanner? cfgTrade = null;
        RoundsTrainingPlanner? cfgTraining = null;
        RoundsWorkPlanner? cfgWork = null;
        RoundsBuildingPlanner? cfgWorkshop = null;

        // Acceptance fault injection: a failing or hanging planner. Off unless the environment names one.
        var faults = Faults.Parse(Environment.GetEnvironmentVariable(Faults.FaultsEnv) ?? "");
        faults.Validate();
        if (!faults.Empty)
        {
            log.Warn("fault_injection", "clock: fault injection active", ("faults", faults.ToString()));
        }

        var routine = true;
        var workshop = sc.WorkshopPlans;
        var research = sc.ResearchPlans;
        var resourceTargets = sc.ResourceTargetsConfigured;
        var bills = sc.Bill;
        var fields = sc.Field;
        var acquisition = sc.Acquisition;
        var work = sc.Work;
        var supplies = sc.Supply;
        var sleeping = sc.Sleeping;
        var cooking = sc.Cooking;
        var shelter = sc.Shelter;
        var comfort = sc.Comfort;
        var hospital = sc.Hospital;
        var expansion = sc.Expansion;
        var power = sc.Power;
        var temperature = sc.Temperature;
        var defense = sc.Defense;
        var tend = sc.Tend;
        var rescue = sc.Rescue;
        var equip = sc.Equip;
        var repair = sc.Repair;
        var blight = sc.Blight;
        var pollution = sc.Pollution;
        var mechCharger = sc.MechCharger;
        var geneBank = sc.GeneBank;
        var armory = sc.Armory;
        var clearance = sc.Clearance;
        var moodRelief = sc.Mood;
        var gear = sc.Gear;
        var medical = sc.Medical;
        var foodStorageUpkeep = sc.FoodStorageUpkeep;
        var refrigeration = sc.Refrigeration;
        var lighting = sc.Lighting;
        var flooring = sc.Flooring;
        var routes = sc.Routes;
        var animalContainment = sc.AnimalContainment;
        var recovery = sc.Recovery;
        var husbandry = sc.Husbandry;
        var homeCoverage = sc.HomeCoverage;
        var prisonerInteraction = sc.PrisonerInteraction;
        var populationCustody = sc.PopulationCustody;
        var stoneShell = sc.StoneShell;
        var defensiveLayout = sc.DefensiveLayout;
        var dialog = sc.Dialog;
        var trade = sc.Trade;
        var stockpiles = sc.Stockpiles;
        var burial = sc.Burial;
        var training = sc.Training;
        var envelope = sc.Envelope;

        if (!routine && (bills || fields || acquisition || work || supplies || sleeping || cooking || shelter || comfort || hospital || expansion || power || temperature || defense || tend || rescue || equip || repair || blight || pollution || mechCharger || geneBank || armory || clearance || moodRelief || gear || medical || foodStorageUpkeep || refrigeration || lighting || sc.Art || sc.Mechs || flooring || routes || animalContainment || recovery || husbandry || prisonerInteraction || populationCustody || sc.PopulationJoiner || homeCoverage || sc.Sheltering || stoneShell || stockpiles || defensiveLayout || dialog || trade || resourceTargets))
        {
            throw new InvalidOperationException("building plans require rounds");
        }

        cfgStore = context.Sections;
        var (thresholds, capabilities) = RoundsCapabilitiesFor(sc, _options.LayoutOverlay);
        // The undraft sweep releases drafts no live plan needs.
        capabilities = capabilities with { Undraft = writer };
        var reviewer = new Rounder(context.Player, client, SystemClock.Instance, thresholds, StepTimeout, capabilities);
        cfgRounds = reviewer;
        if (bills || gear || armory || sc.Art || sc.PopulationJoiner || foodStorageUpkeep || refrigeration || medical || sc.Mechs)
        {
            cfgLedger = RoundsLedgerPlanner.NewRoundsLedgerPlanner(reviewer);
        }
        if (bills || medical || sc.Mechs)
        {
            // The surgery part, baby food and mech gestation bills are the ledger's, declare-only (#2604).
            reviewer.AddOrderDeclarer(new RoundsCareBillDeclarer(reviewer, client, medical, bills, sc.Mechs));
        }
        if (bills)
        {
            foreach (var purpose in new[] { CookFood, PreserveFood, ButcherFood })
            {
                reviewer.AddOrderDeclarer(new RoundsBillPlanner(reviewer, purpose));
            }
            cfgButcher = RoundsBuildingPlanner.NewRoundsButcherPlanner(reviewer, client);
        }
        if (sc.Art)
        {
            // MaintainArt's pinned sculpture bills are the ledger's.
            reviewer.AddOrderDeclarer(new RoundsArtPlanner(reviewer));
            // MaintainTrade's export batches are the ledger's too: the silver gap answered with sale goods (#2607).
            reviewer.AddOrderDeclarer(new RoundsTradeExportPlanner(reviewer));
        }
        if (fields)
        {
            cfgFields = new RoundsFieldPlanner(reviewer, client);
        }
        if (acquisition)
        {
            cfgFoodAcquisition = new RoundsAcquisitionPlanner(reviewer, EnsureFoodSupply);
            cfgPestAcquisition = new RoundsAcquisitionPlanner(reviewer, ClearPests);
            cfgResourceAcquisition = new RoundsAcquisitionPlanner(reviewer, MaintainResource);
        }
        if (work)
        {
            cfgWork = new RoundsWorkPlanner(reviewer);
        }
        if (defense)
        {
            var defensePlanner = new RoundsDefensePlanner(reviewer, client, new SessionCombatHands(context.Session));
            // Test-only tactic override for calibration sweeps; invalid fails the start loudly, unset is inert.
            var tuning = ParseCombatTuning(Environment.GetEnvironmentVariable(CombatTuningEnv) ?? "");
            if (tuning is not null)
            {
                log.Warn("combat_tuning_override", "defense: combat tuning override active", ("tuning", tuning.ToString()));
            }
            cfgDefense = defensePlanner.WithCombatTuning(tuning);
        }
        if (tend)
        {
            cfgTend = RoundsTendPlanner.Create(reviewer, client);
        }
        if (rescue)
        {
            cfgRescue = RoundsRescuePlanner.NewRoundsRescuePlanner(reviewer, client);
        }
        if (equip)
        {
            cfgEquip = new RoundsEquipPlanner(reviewer);
        }
        if (repair || clearance)
        {
            // The repair and clearance toggles compose the home-upkeep family's two Needs (#3253).
            families.Add(HomeUpkeepReader.Runner(reviewer, client, repair, clearance));
        }
        if (armory)
        {
            cfgArmory = new RoundsArmoryPlanner(reviewer, client);
            reviewer.AddOrderDeclarer(new RoundsArmoryDeclarer(cfgArmory));
        }
        if (blight)
        {
            cfgBlight = new RoundsBlightPlanner(reviewer, client);
        }
        if (burial)
        {
            cfgBurial = new RoundsBurialPlanner(reviewer);
        }
        if (training)
        {
            cfgTraining = RoundsTrainingPlanner.Create(reviewer);
        }
        if (envelope)
        {
            cfgEnvelope = new RoundsEnvelopePlanner(reviewer);
        }
        if (moodRelief)
        {
            cfgMoodRelief = RoundsMoodReliefPlanner.NewRoundsMoodReliefPlanner(reviewer);
        }
        if (gear)
        {
            cfgGear = RoundsGearPlanner.NewRoundsGearPlanner(reviewer, client);
            reviewer.AddOrderDeclarer(new RoundsGearDeclarer());
        }
        if (medical)
        {
            cfgMedical = RoundsMedicalPlanner.NewRoundsMedicalPlanner(reviewer, client);
            cfgSurgery = RoundsSurgeryPlanner.Create(reviewer);
        }
        if (foodStorageUpkeep || refrigeration)
        {
            // The storage and refrigeration toggles compose the food-stores family's two Needs (#3251, #3280).
            var foodStores = new FoodStoresRounds(reviewer, client, foodStorageUpkeep, refrigeration ? RoundsBuildingPlanner.NewRoundsRefrigerationPlanner(reviewer, client) : null);
            if (foodStorageUpkeep)
            {
                reviewer.AddOrderDeclarer(new FoodStoresDeclarer(foodStores));
            }
            if (refrigeration)
            {
                // The solar-flare cook-ahead bill answers the refrigeration Need, wired with its family rather than
                // the cooking bills so a refrigeration-only service serves it.
                reviewer.AddOrderDeclarer(new RoundsBillPlanner(reviewer, CookAheadFood));
            }
            families.Add(foodStores.Runner());
        }
        if (animalContainment)
        {
            cfgAnimalContainment = new RoundsAnimalContainmentPlanner(reviewer, client);
        }
        if (recovery)
        {
            cfgRecovery = RoundsRecoveryPlanner.NewRoundsRecoveryPlanner(reviewer);
        }
        if (husbandry)
        {
            cfgHusbandry = RoundsHusbandryPlanner.NewRoundsHusbandryPlanner(reviewer);
        }
        if (prisonerInteraction)
        {
            cfgPrisonerInteraction = RoundsPrisonerInteractionPlanner.NewRoundsPrisonerInteractionPlanner(reviewer);
        }
        if (sc.PopulationJoiner)
        {
            cfgPopulationJoiner = RoundsPopulationJoinerPlanner.NewRoundsPopulationJoinerPlanner(reviewer);
            reviewer.AddOrderDeclarer(new RoundsPopulationJoinerDeclarer(cfgPopulationJoiner));
        }
        if (populationCustody)
        {
            cfgPopulationCustody = RoundsPopulationCustodyPlanner.NewRoundsPopulationCustodyPlanner(reviewer, client);
        }
        if (sc.Firebreak)
        {
            cfgFirebreak = new RoundsFirebreakPlanner(reviewer, client);
        }
        if (sc.Psylink)
        {
            cfgPsylink = RoundsPsylinkPlanner.NewRoundsPsylinkPlanner(reviewer, client);
        }
        if (sc.Permits)
        {
            // MaintainPermits plans from the review's royalty read.
            cfgPermits = RoundsPermitsPlanner.NewRoundsPermitsPlanner(reviewer);
        }
        if (sc.IdeoRoles)
        {
            // MaintainIdeoRoles plans from the review's ideology section and pawn rows.
            cfgIdeoRoles = RoundsIdeoRolesPlanner.NewRoundsIdeoRolesPlanner(reviewer);
        }
        if (sc.Rituals)
        {
            // MaintainRituals plans from the review's ideology section, pawn rows, building table and emergency census.
            cfgRituals = RoundsRitualsPlanner.NewRoundsRitualsPlanner(reviewer);
        }
        if (sc.Gathering)
        {
            // HoldGatherings plans from the mood ledger, pawn rows, building table and emergency census.
            cfgGathering = RoundsGatheringPlanner.NewRoundsGatheringPlanner(reviewer);
        }
        if (sc.Ideoligion)
        {
            cfgIdeoligion = RoundsIdeoligionPlanner.NewRoundsIdeoligionPlanner(reviewer);
        }
        if (sc.CreepJoiner)
        {
            // ManageCreepJoiners plans from the review's frame; no read of its own.
            cfgCreepJoiners = new RoundsCreepJoinerPlanner(reviewer);
        }
        if (sc.Sheltering)
        {
            // MaintainShelter plans from the review's rooms; no read of its own.
            cfgMaintainShelter = MaintainShelterPlanner.NewMaintainShelterPlanner(reviewer);
        }
        if (pollution)
        {
            cfgPollution = RoundsPollutionPlanner.NewRoundsPollutionPlanner(reviewer);
        }
        if (homeCoverage)
        {
            cfgHomeCoverage = RoundsHomeCoveragePlanner.NewRoundsHomeCoveragePlanner(reviewer);
        }
        if (stoneShell)
        {
            cfgStoneShell = RoundsStoneShellPlanner.Create(reviewer, client);
        }
        if (stockpiles)
        {
            cfgStockpiles = new RoundsStockpilePlanner(reviewer, client);
        }
        if (defensiveLayout)
        {
            cfgDefenseLayout = new RoundsDefenseLayoutPlanner(reviewer, client);
        }
        if (research)
        {
            cfgResearch = RoundsResearchPlanner.NewRoundsResearchPlanner(reviewer, client);
        }
        if (dialog)
        {
            cfgDialog = new RoundsDialogPlanner(reviewer, client, new DialogAnswerPolicy(EquatableArray.From(DefaultDialogAnswerPrefer)));
        }
        if (trade)
        {
            cfgTrade = new RoundsTradePlanner(reviewer, client);
        }
        if (resourceTargets)
        {
            cfgResource = RoundsResourcePlanner.NewRoundsResourcePlanner(reviewer, client);
            reviewer.AddOrderDeclarer(new RoundsResourceDeclarer(cfgResource));
        }
        if (supplies)
        {
            cfgSupplies = RoundsSupplyPlanner.Create(reviewer, client);
        }
        if (sleeping || cooking || shelter || comfort || workshop || hospital || expansion || power || temperature || lighting || flooring || routes || mechCharger || geneBank)
        {
            if (sleeping || shelter)
            {
                cfgSleeping = RoundsBuildingPlanner.NewRoundsShelterPlanner(reviewer, client);
            }
            if (sleeping)
            {
                cfgSleepingUpkeep = RoundsSleepingUpkeepPlanner.Create(reviewer, client);
            }
            if (temperature)
            {
                cfgTemperature = RoundsBuildingPlanner.NewRoundsTemperaturePlanner(reviewer, client);
            }
            if (power)
            {
                cfgPower = RoundsBuildingPlanner.NewRoundsPowerPlanner(reviewer, client);
            }
            if (mechCharger)
            {
                cfgMechCharger = RoundsMechChargerPlanner.NewRoundsMechChargerPlanner(reviewer, client);
            }
            if (geneBank)
            {
                cfgGeneBank = RoundsGeneBankPlanner.NewRoundsGeneBankPlanner(reviewer, client);
            }
            if (lighting)
            {
                cfgLighting = RoundsBuildingPlanner.NewRoundsLightingPlanner(reviewer, client);
            }
            if (flooring)
            {
                cfgFlooring = RoundsBuildingPlanner.NewRoundsFlooringPlanner(reviewer, client);
            }
            if (routes)
            {
                cfgRoutes = RoundsBuildingPlanner.NewRoundsRoutesPlanner(reviewer, client);
            }
            if (cooking || bills)
            {
                cfgCooking = RoundsBuildingPlanner.NewRoundsCookingPlanner(reviewer, client);
            }
            if (expansion)
            {
                cfgExpansion = RoundsBuildingPlanner.NewRoundsExpansionPlanner(reviewer, client);
            }
            if (comfort)
            {
                cfgComfort = RoundsBuildingPlanner.NewRoundsComfortPlanner(reviewer, client);
                cfgBasicComfort = RoundsBuildingPlanner.NewRoundsBasicComfortPlanner(reviewer, client);
            }
            if (workshop)
            {
                cfgWorkshop = RoundsBuildingPlanner.NewRoundsWorkshopPlanner(reviewer, client);
            }
            if (hospital)
            {
                cfgHospital = RoundsHospitalPlanner.NewRoundsHospitalPlanner(reviewer, client);
            }
        }
        if (cfgFlooring is not null && cfgFirebreak is { } firebreak)
        {
            // MaintainFlooring paves the settled ring cells the firebreak review found.
            cfgFlooring.SetFirebreakPave(() => firebreak.Pave());
        }

        var config = new ClockSchedulerConfig
        {
            // MaxAge bounds admission-read staleness; planner facts use FactsTick.
            MaxAge = StepTimeout,
            Worker = true,
            WorldReady = context.WorldReady,
            RoundsMethods = context.Session.RoundsMethodsEnabled(),
            Faults = faults,
            Families = families,
            AnimalContainment = cfgAnimalContainment,
            Armory = cfgArmory,
            BasicComfort = cfgBasicComfort,
            Blight = cfgBlight,
            Burial = cfgBurial,
            Butcher = cfgButcher,
            Comfort = cfgComfort,
            Cooking = cfgCooking,
            CreepJoiners = cfgCreepJoiners,
            Defense = cfgDefense,
            DefenseLayout = cfgDefenseLayout,
            Dialog = cfgDialog,
            Envelope = cfgEnvelope,
            Equip = cfgEquip,
            Expansion = cfgExpansion,
            Fields = cfgFields,
            Firebreak = cfgFirebreak,
            Flooring = cfgFlooring,
            FoodAcquisition = cfgFoodAcquisition,
            Gathering = cfgGathering,
            Gear = cfgGear,
            GeneBank = cfgGeneBank,
            HomeCoverage = cfgHomeCoverage,
            Hospital = cfgHospital,
            Husbandry = cfgHusbandry,
            IdeoRoles = cfgIdeoRoles,
            Ideoligion = cfgIdeoligion,
            Ledger = cfgLedger,
            Lighting = cfgLighting,
            MaintainShelter = cfgMaintainShelter,
            MechCharger = cfgMechCharger,
            Medical = cfgMedical,
            MoodRelief = cfgMoodRelief,
            Permits = cfgPermits,
            PestAcquisition = cfgPestAcquisition,
            Pollution = cfgPollution,
            PopulationCustody = cfgPopulationCustody,
            PopulationJoiner = cfgPopulationJoiner,
            Power = cfgPower,
            PrisonerInteraction = cfgPrisonerInteraction,
            Psylink = cfgPsylink,
            Recovery = cfgRecovery,
            Rescue = cfgRescue,
            Research = cfgResearch,
            Resource = cfgResource,
            ResourceAcquisition = cfgResourceAcquisition,
            Rituals = cfgRituals,
            Rounds = cfgRounds,
            Routes = cfgRoutes,
            Sleeping = cfgSleeping,
            SleepingUpkeep = cfgSleepingUpkeep,
            Stockpiles = cfgStockpiles,
            StoneShell = cfgStoneShell,
            Store = cfgStore,
            Supplies = cfgSupplies,
            Surgery = cfgSurgery,
            Temperature = cfgTemperature,
            Tend = cfgTend,
            Trade = cfgTrade,
            Training = cfgTraining,
            Work = cfgWork,
            Workshop = cfgWorkshop,
        };
        var scheduler = new ClockScheduler(context.Player, client, config, SystemClock.Instance);
        var clock = CreateClock(context, client, scheduler, new FacadeClockWriter(client.Facade));
        return new PlannerParts
        {
            Scheduler = scheduler,
            BreakSource = client,
            Pawns = client,
            Moves = writer,
            Flush = client.FlushSnapshotAsync,
            Placement = reviewer.NotePlacement,
            OpenFrames = client.OpenFramesAsync,
            Clock = clock,
            TargetSpeed = TargetSpeed,
            Rounds = new RoundsDiagnostics(context.Journal, true, context.Session.RoundsMethodsEnabled(), _options.Families.Active(), context.Sections),
        };
    }

    // The run driver's configuration: the base colony watch policy every Run carries, the poll cadence and the
    // autosave hold. ComposePlanners is synchronous, so the driver's journal binding is waited on from a thread-pool
    // thread (it holds no thread-affine state).
    private ClockRunHostDriver CreateClock(PlannerContext context, FacadeNativeClient client, ClockScheduler scheduler, IClockWriter clockWriter)
    {
        var testAcceleration = _options.ClockTestAcceleration;
        var lease = TimeSpan.FromTicks(Math.Min(Math.Min(_options.NativeTimeout.Ticks, TimeSpan.FromSeconds(10).Ticks), TimeSpan.FromSeconds(7).Ticks));
        // The dev tick boost runs Ultrafast at the one ceiling's rate; otherwise the clock runs at the live target
        // speed whatever the native speed controls say.
        var config = new ClockRunConfig
        {
            Profile = _options.Profile,
            TargetSpeed = testAcceleration ? new ClockTargetSpeed(PbClock.Speed.Ultrafast) : TargetSpeed,
            Policy = BridgeFunctions.WithHazardThresholds(new PbClock.WatchPolicy
            {
                Mode = PbClock.WatchMode.Colony,
                HealthDropFraction = .1f,
                MinHealthFraction = .5f,
                HostileWithin = 20f,
                InjuryStopCooldownMs = 0,
            }),
            // The host's autosaver: saves while the clock is held for it, flushing governor state first.
            Autosave = context.Autosave,
            AutosaveEveryTicks = ClockRunConfig.DefaultAutosaveEveryTicks,
            PageLimit = 128,
            PollInterval = TimeSpan.FromSeconds(1),
            CallTimeout = lease,
        };
        var driver = Task.Run(() => ClockRunDriver.CreateAsync(context.Player, context.Session, scheduler, client, clockWriter, config, SystemClock.Instance, context.Wake, CancellationToken.None))
            .GetAwaiter().GetResult();
        return new ClockRunHostDriver(driver);
    }

    // Go roundsCapabilities: the routine policy thresholds and the method capabilities a composed serve declares from
    // its enabled families. Every family whose planner acts only while the development ranking selected its goal
    // must declare that goal here, or the review ranks it method_unavailable and the planner never runs.
    internal static (RoundsPolicy Thresholds, RoundsCapabilities Capabilities) RoundsCapabilitiesFor(BrainFamilies sc, bool layoutOverlay)
    {
        var thresholds = DefaultRoundsPolicy();
        var methods = new List<ConcernId>();
        void Add(bool enabled, params ConcernId[] ids)
        {
            if (enabled)
            {
                methods.AddRange(ids);
            }
        }

        Add(sc.Acquisition || sc.Field || sc.Bill, EnsureFoodSupply);
        Add(sc.Work, EnsureWorkAssignments);
        Add(sc.Acquisition, MaintainResource, ClearPests);
        Add(sc.Bill, EnsureCooking, MaintainBabyFeeding);
        Add(sc.Bill || sc.Gear || sc.Armory || sc.Art || sc.PopulationJoiner || sc.FoodStorageUpkeep || sc.Refrigeration || sc.Medical || sc.Mechs, MaintainWorkLedger);
        Add(sc.Temperature, EnsureTemperatureSafety);
        Add(sc.Power, EnsureBasicPower);
        Add(sc.Lighting, MaintainLighting);
        Add(sc.Art, MaintainArt, MaintainTrade);
        Add(sc.Mechs, MaintainMechs);
        Add(sc.Flooring, MaintainFlooring);
        Add(sc.Routes, MaintainRoutes);
        Add(sc.Comfort, EnsureComfort);
        Add(sc.Sleeping || sc.Expansion, MaintainHousing);
        Add(sc.AnimalContainment, MaintainAnimalContainment);
        Add(sc.Burial, MaintainBurial);
        Add(sc.Training, MaintainTraining);
        Add(sc.Envelope, MaintainClimateEnvelope);
        Add(sc.Gear, MaintainEquipment);
        Add(sc.Blight, RemoveBlight);
        Add(sc.Recovery, RecoverDisasterServices);
        Add(sc.Husbandry, MaintainHerd);
        Add(sc.PrisonerInteraction || sc.PopulationCustody || sc.PopulationJoiner, MaintainPopulation);
        Add(sc.Pollution, ManagePollution);
        Add(sc.MechCharger, EnsureMechCharger);
        Add(sc.GeneBank, MaintainGeneBank);
        Add(sc.HomeCoverage, MaintainHomeCoverage);
        Add(sc.Sheltering, MaintainShelter);
        Add(sc.Firebreak, MaintainFirebreak);
        Add(sc.Psylink, MaintainPsylink);
        Add(sc.CreepJoiner, ManageCreepJoiners);
        Add(sc.Permits, MaintainPermits);
        Add(sc.IdeoRoles, MaintainIdeoRoles);
        Add(sc.Rituals, MaintainRituals);
        Add(sc.Gathering, HoldGatherings);
        Add(sc.Ideoligion, ImproveIdeoligion);
        Add(sc.StoneShell, MaintainStoneShell);
        Add(sc.Stockpiles, MaintainStockpiles);
        if (sc.DefensiveLayout)
        {
            thresholds = thresholds with { DefensiveLayout = true };
            methods.Add(EnsureDefensiveLayout);
        }
        if (sc.ResearchPlans)
        {
            methods.Add(EnsureResearch);
        }
        if (sc.ResourceTargetsConfigured && !methods.Contains(MaintainResource))
        {
            // Acquisition already declares it (the construction demand).
            methods.Add(MaintainResource);
        }
        Add(sc.Medical, MaintainMedicalReserves, MaintainSurgery);
        Add(sc.Trade, TradeWithCaravan);
        // The equip planner supplies EnsureBasicDefense's method capability.
        Add(sc.Equip, EnsureBasicDefense);
        return (thresholds, new RoundsCapabilities { Methods = methods, Needs = sc.Refrigeration ? [RefrigerationNeed] : [], LayoutOverlay = layoutOverlay, ClearanceCensus = sc.Clearance });
    }
}
