using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Facts;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Host;

public sealed partial class BrainComposition
{
    // Budgets census and composed planner reads independently of epoch renewal.
    private static readonly TimeSpan StepTimeout = TimeSpan.FromMinutes(1);

    /// <summary>Called once, after the player exists and before the Worker does.</summary>
    public PlannerParts ComposePlanners(PlannerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var client = _client ?? throw new InvalidOperationException("ComposePlanners runs after ComposeSession");
        var writer = _writer!;
        var sc = _options.Families;
        var log = new HostLog(context.Telemetry, "clock-scheduler");

        // ClockSchedulerConfig is init-only, so the planners are built into locals and the config is made last.
        RoundsBuildingPlanner? roomsComfort = null;
        RoundsBuildingPlanner? foodButcher = null;
        var families = new List<FamilyRunner>();
        RoundsBuildingPlanner? roomsHosting = null;
        RoundsBuildingPlanner? roomsPartySpot = null;
        RoundsBuildingPlanner? roomsWorkChairs = null;
        RoundsDefensePlanner? cfgDefense = null;
        RoundsFieldPlanner? cfgFields = null;
        RoundsBuildingPlanner? roomsFlooring = null;
        RoundsAcquisitionPlanner? foodAcquisition = null;
        RoundsBuildingPlanner? roomsLighting = null;
        RoundsResourcePlanner? cfgResource = null;
        RoundsAcquisitionPlanner? cfgResourceAcquisition = null;
        Rounder? cfgRounds = null;
        FactStore? cfgStore = null;
        RoundsTradePlanner? cfgTrade = null;
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
        var supplies = sc.Supply;
        var sleeping = sc.Sleeping;
        var cooking = sc.Cooking;
        var shelter = sc.Shelter;
        var comfort = sc.Comfort;
        var hospital = sc.Hospital;
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

        if (!routine && (bills || fields || acquisition || sc.Work || supplies || sleeping || cooking || shelter || comfort || hospital || power || temperature || defense || tend || rescue || equip || repair || blight || pollution || mechCharger || geneBank || armory || clearance || sc.Mood || gear || medical || foodStorageUpkeep || refrigeration || lighting || sc.Art || sc.Mechs || flooring || routes || animalContainment || recovery || husbandry || prisonerInteraction || populationCustody || sc.PopulationJoiner || homeCoverage || sc.Sheltering || stoneShell || stockpiles || defensiveLayout || dialog || trade || resourceTargets || sc.Goals))
        {
            throw new InvalidOperationException("building plans require rounds");
        }

        cfgStore = context.Sections;
        var (thresholds, capabilities) = RoundsCapabilitiesFor(sc, _options.LayoutOverlay);
        // The undraft sweep releases drafts no live plan needs.
        capabilities = capabilities with { Undraft = writer };
        var reviewer = new Rounder(context.Player, client, SystemClock.Instance, thresholds, StepTimeout, capabilities);
        cfgRounds = reviewer;
        // Any toggle that declares production orders composes the admin family's work-ledger Need (#3304).
        var workLedger = bills || gear || armory || sc.Art || sc.PopulationJoiner || foodStorageUpkeep || refrigeration || medical || sc.Mechs || resourceTargets;
        var biotech = new BiotechNeeds { Mechs = sc.Mechs, MechCharger = mechCharger, GeneBank = geneBank, Pollution = pollution };
        if (medical)
        {
            // The surgery part bills are the ledger's, declare-only (#2604).
            reviewer.AddOrderDeclarer(new RoundsCareBillDeclarer(reviewer, client, medical));
        }
        if (sc.Mechs)
        {
            // The biotech/mechs Need's gestation bill is the ledger's too, filed under the Need (#3261).
            reviewer.AddOrderDeclarer(new MechBillDeclarer(reviewer, client, biotech));
        }
        if (bills)
        {
            // The food/baby-feeding Need's baby food bill is the ledger's too, filed under the Need (#3254).
            reviewer.AddOrderDeclarer(new BabyFoodDeclarer());
            foreach (var purpose in new[] { ProductionRules.CookFood, ProductionRules.PreserveFood, ProductionRules.ButcherFood })
            {
                reviewer.AddOrderDeclarer(new RoundsBillPlanner(reviewer, purpose));
            }
            foodButcher = RoundsBuildingPlanner.NewRoundsButcherPlanner(reviewer, client);
        }
        if (sc.Art)
        {
            // The rooms/art Need's pinned sculpture bills are the ledger's, filed under the Need (#3255).
            reviewer.AddOrderDeclarer(new RoundsArtPlanner(reviewer));
            // The economy/trade Need's export batches are the ledger's too: the silver gap answered with sale goods (#3316).
            reviewer.AddOrderDeclarer(new RoundsTradeExportPlanner(reviewer));
        }
        if (fields)
        {
            cfgFields = new RoundsFieldPlanner(reviewer, client);
        }
        if (acquisition)
        {
            foodAcquisition = new RoundsAcquisitionPlanner(reviewer, FoodRules.FoodSupplyNeed.FilingKey);
            cfgResourceAcquisition = new RoundsAcquisitionPlanner(reviewer, EconomyFamily.Resource.FilingKey);
        }
        if (defense)
        {
            var defensePlanner = new RoundsDefensePlanner(reviewer, client, new SessionCombatHands(context.Session));
            // Test-only tactic override for calibration sweeps; invalid fails the start loudly, unset is inert.
            var tuning = CombatTuningRules.ParseCombatTuning(Environment.GetEnvironmentVariable(CombatTuningRules.CombatTuningEnv) ?? "");
            if (tuning is not null)
            {
                log.Warn("combat_tuning_override", "defense: combat tuning override active", ("tuning", tuning.ToString()));
            }
            cfgDefense = defensePlanner.WithCombatTuning(tuning);
        }
        if (tend || rescue || medical || hospital)
        {
            // The tend and rescue toggles compose the medical family's critical Needs' care orders, the medical toggle
            // their amputations with the reserves and surgery Needs, the hospital toggle the care Need (#3258).
            families.Add(new MedicalRounds(reviewer, client, tend, rescue, medical, hospital).Runner());
        }
        if (armory)
        {
            reviewer.AddOrderDeclarer(new RoundsArmoryDeclarer(new RoundsArmoryPlanner(reviewer, client)));
        }
        if (gear)
        {
            reviewer.AddOrderDeclarer(new RoundsGearDeclarer());
        }
        // The equip, acquisition, training and gear toggles compose the military family's defense, pests, training
        // and equipment Needs (#3259); the equipment Need walks the workshop ladder for replacement gear.
        // Restore-workers, the undraft sweep, is always composed (#3311).
        families.Add(new MilitaryRounds(reviewer, equip, acquisition, training, gear, gear && workshop).Runner());
        if (repair || clearance)
        {
            // The repair and clearance toggles compose the home-upkeep family's two Needs (#3253).
            families.Add(HomeUpkeepReader.Runner(reviewer, client, repair, clearance));
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
                reviewer.AddOrderDeclarer(new RoundsBillPlanner(reviewer, ProductionRules.CookAheadFood));
            }
            families.Add(foodStores.Runner());
        }
        if (cooking || bills || acquisition || fields || blight)
        {
            // The acquisition, field and bill toggles compose the food family's supply Need with its hunts and
            // forage, food fields and butcher bench; the cooking and bill toggles its cooking Need; the bill toggle
            // its baby feeding Need and the blight toggle its blight Need (#3254).
            var supply = acquisition || fields || bills;
            families.Add(new FoodRounds(reviewer, supply, foodButcher, foodAcquisition, supply ? cfgFields : null,
                cooking || bills ? RoundsBuildingPlanner.NewRoundsCookingPlanner(reviewer, client) : null, babyFeeding: bills, blight: blight).Runner());
        }
        if (husbandry || animalContainment)
        {
            // The husbandry toggle composes the animals family's herd and sterilize Needs, the containment toggle its
            // containment and ward Needs (#3260).
            families.Add(AnimalsReader.Runner(reviewer, client, husbandry, animalContainment));
        }
        if (recovery)
        {
            // The recovery toggle composes the disaster family's sheltering and services Needs (#3304).
            families.Add(new DisasterRounds(reviewer).Runner());
        }
        if (prisonerInteraction || populationCustody)
        {
            // The prisoner-interaction and population-custody toggles compose the prisoners family's two Needs (#3262).
            families.Add(new PrisonersRounds(reviewer, prisonerInteraction, populationCustody).Runner());
        }
        if (sc.PopulationJoiner)
        {
            // The population-joiner toggle composes the joiners family; its decree batches are declared to the ledger (#3302).
            var joiners = new JoinersRounds(reviewer);
            reviewer.AddOrderDeclarer(new JoinersDeclarer(joiners));
            families.Add(joiners.Runner());
        }
        if (populationCustody)
        {
            // The population-custody toggle also composes the anomaly family (#3302).
            families.Add(new AnomalyRounds(reviewer, client).Runner());
        }
        if (sc.Permits || sc.Psylink)
        {
            // The permit and psylink toggles compose the royalty family's two Needs (#3261).
            families.Add(new RoyaltyRounds(reviewer, sc.Permits, sc.Psylink ? client : null).Runner());
        }
        if (biotech.Any)
        {
            // The mech, mech charger, gene bank and pollution toggles compose the biotech family (#3261).
            families.Add(new BiotechRounds(reviewer, biotech).Runner());
        }
        if (sc.IdeoRoles || sc.Rituals || sc.Ideoligion || sc.Goals)
        {
            // The role, ritual and reform toggles compose the ideology family's three Needs (#3260); the goals toggle adds the conversions a believer goal owes (#3501).
            families.Add(IdeologyReader.Runner(reviewer, sc.IdeoRoles, sc.Rituals, sc.Ideoligion, sc.Goals));
        }
        var admin = new AdminNeeds { CreepJoiners = sc.CreepJoiner, SupplySafety = supplies, Burial = burial, WorkLedger = workLedger, Stockpiles = stockpiles, WorkAssignments = sc.Work };
        if (admin.CreepJoiners || admin.SupplySafety || admin.Burial || admin.WorkLedger || admin.Stockpiles || admin.WorkAssignments)
        {
            // The colony admin family (#3264, #3304): creep joiners, supply safety, burial, the work ledger, stockpiles
            // and work assignments.
            families.Add(new AdminRounds(reviewer, admin, supplies ? client : null, stockpiles ? client : null).Runner());
        }
        if (sleeping || shelter || temperature || sc.Sheltering)
        {
            // The housing, temperature and safe-area toggles compose the shelter family's Needs (#3257): sleeping with
            // the shelter or sleeping toggle (its stand-in spots are the day-one floor, #3659),
            // temperature and the safe area.
            families.Add(new ShelterRounds(reviewer, sleeping || shelter || temperature ? client : null,
                sleeping || shelter, temperature, sc.Sheltering).Runner());
        }
        if (power)
        {
            // The power toggle composes the power family's basic Need (#3257).
            families.Add(new PowerRounds(reviewer, client).Runner());
        }
        var layout = new LayoutNeeds { HomeCoverage = homeCoverage, Firebreak = sc.Firebreak, StoneShell = stoneShell, ClimateEnvelope = envelope, Defense = defensiveLayout, Routes = routes };
        LayoutRounds? layoutRounds = null;
        if (layout.Any)
        {
            // The home-coverage, firebreak, stone-shell, envelope, defensive-layout and routes toggles compose the
            // layout family's Needs (#3256, #3314, #3265).
            layoutRounds = new LayoutRounds(reviewer, layout, sc.Firebreak ? client : null, stoneShell ? client : null, defensiveLayout ? client : null);
            families.Add(layoutRounds.Runner());
        }
        if (dialog || sc.Mood)
        {
            // The people family (#3304): the dialog toggle composes its dialog Need, which answers the force-pausing
            // choice dialogs, and the mood toggle its per-pawn mood Needs.
            families.Add(new PeopleRounds(reviewer, new PeopleNeeds { Dialog = dialog, Mood = sc.Mood }, dialog ? client : null,
                dialog ? new DialogAnswerPolicy(EquatableArray.From(DialogAnswerRules.DefaultDialogAnswerPrefer)) : null).Runner());
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
        if (sc.Goals)
        {
            // The goals toggle composes the goals family: status for the player's goals, whose demand the review injects (#3494).
            families.Add(new GoalsRounds(reviewer, context.GoalStatuses).Runner());
        }
        if (sleeping || cooking || shelter || comfort || workshop || hospital || power || temperature || lighting || flooring || routes)
        {
            if (lighting)
            {
                roomsLighting = RoundsBuildingPlanner.NewRoundsLightingPlanner(reviewer, client);
            }
            if (flooring)
            {
                roomsFlooring = RoundsBuildingPlanner.NewRoundsFlooringPlanner(reviewer, client);
            }
            if (comfort)
            {
                roomsComfort = RoundsBuildingPlanner.NewRoundsBasicComfortPlanner(reviewer, client);
                roomsHosting = RoundsBuildingPlanner.NewRoundsComfortPlanner(reviewer, client);
                roomsPartySpot = RoundsBuildingPlanner.NewRoundsPartySpotPlanner(reviewer, client);
                roomsWorkChairs = RoundsBuildingPlanner.NewRoundsWorkChairPlanner(reviewer, client);
            }
            if (workshop)
            {
                cfgWorkshop = RoundsBuildingPlanner.NewRoundsWorkshopPlanner(reviewer, client);
            }
        }
        if (roomsFlooring is not null && layout.Firebreak && layoutRounds is { } layoutPave)
        {
            // The rooms/flooring Need paves the settled ring cells the firebreak review found.
            roomsFlooring.SetFirebreakPave(() => layoutPave.Pave());
        }
        if (roomsLighting is not null || roomsFlooring is not null || roomsComfort is not null || sc.Art || sc.Gathering)
        {
            // The lighting, flooring, art, comfort and gathering toggles compose the rooms family's Needs (#3255).
            families.Add(new RoomsRounds(reviewer, roomsLighting, roomsFlooring, roomsComfort, roomsHosting, roomsPartySpot, roomsWorkChairs, sc.Art, sc.Gathering).Runner());
        }
        if (research || resourceTargets || acquisition || sc.Art || trade)
        {
            // The research toggle composes the economy family's research Need (#3263); the resource targets and
            // acquisition toggles its resource Need, whose mines, chops and hunts, hay, social and supply fields
            // and production bench are the Need's Methods (#3303). The art toggle composes its trade Need, whose
            // export batches the ledger declares, and the trade toggle its caravan and trade-mission Needs (#3316).
            var resourceSteps = resourceTargets || acquisition
                ? new EconomyResourceSteps { Resource = cfgResource, Acquisition = cfgResourceAcquisition, Fields = cfgFields, Workshop = cfgWorkshop }
                : null;
            families.Add(new EconomyRounds(reviewer, client, research, resourceSteps, sc.Art, cfgTrade).Runner());
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
            Defense = cfgDefense,
            Rounds = cfgRounds,
            Store = cfgStore,
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
            OpenFrames = client.OpenFramesAsync,
            Clock = clock,
            TargetSpeed = TargetSpeed,
            Rounds = new RoundsDiagnostics(context.Journal, true, context.Session.RoundsMethodsEnabled(), _options.Families.Active(), context.Sections),
        };
    }

    // The run driver's configuration: the base colony watch policy every Run carries, the call bound and the
    // autosave hold.
    private ClockRunDriver CreateClock(PlannerContext context, FacadeNativeClient client, ClockScheduler scheduler, IClockWriter clockWriter)
    {
        var lease = TimeSpan.FromTicks(Math.Min(Math.Min(_options.NativeTimeout.Ticks, TimeSpan.FromSeconds(10).Ticks), TimeSpan.FromSeconds(7).Ticks));
        var config = new ClockRunConfig
        {
            TargetSpeed = RunTargetSpeed(),
            PlayerSpeed = TargetSpeed,
            Policy = new PbClock.WatchPolicy
            {
                SeriousSingleHitDamage = RimGovernor.Brain.Policy.HazardThresholdsRules.SeriousSingleHitDamage,
                SeriousSummaryHealthFloor = RimGovernor.Brain.Policy.HazardThresholdsRules.SeriousSummaryHealthFloor,
                SeriousBleedRateFloor = RimGovernor.Brain.Policy.HazardThresholdsRules.SeriousBleedRateFloor,
                SeriousVitalPartFloor = RimGovernor.Brain.Policy.HazardThresholdsRules.SeriousVitalPartFloor,
                ExplosiveNearMarginCells = RimGovernor.Brain.Policy.HazardThresholdsRules.ExplosiveNearMarginCells,
                MeleeReachCells = RimGovernor.Brain.Policy.HazardThresholdsRules.MeleeReachCells,
                InjurySeverityFloorTicks = RimGovernor.Brain.Policy.HazardThresholdsRules.InjurySeverityFloorTicks,
                HungerHoldCategory = RimGovernor.Brain.Policy.HazardThresholdsRules.HungerHoldCategory,
                Mode = PbClock.WatchMode.Colony,
                HealthDropFraction = .1f,
                MinHealthFraction = .5f,
                HostileWithin = 20f,
                InjuryStopCooldownMs = 0,
            },
            // The host's autosaver: saves while the clock is held for it, flushing governor state first.
            Autosave = context.Autosave,
            AutosaveEveryTicks = ClockRunConfig.DefaultAutosaveEveryTicks,
            CallTimeout = lease,
        };
        return ClockRunDriver.Create(context.Player, context.Session, scheduler, client, clockWriter, config, SystemClock.Instance, context.Wake, CancellationToken.None);
    }

    // The routine policy thresholds and the method capabilities a composed serve declares from
    // its enabled families. Every family whose planner acts only while the development ranking selected its goal
    // must declare that goal here, or the review ranks it method_unavailable and the planner never runs.
    internal static (RoundsPolicy Thresholds, RoundsCapabilities Capabilities) RoundsCapabilitiesFor(BrainFamilies sc, bool layoutOverlay)
    {
        var thresholds = RoundsRules.DefaultRoundsPolicy();
        // The migrated Needs whose facts the review reads: refrigeration's rooms, training's range and ring definitions,
        // food supply's plan, fields and butchery, cooking's kitchen and meal review, equipment's gear census, the
        // rooms family's room census, beauty definitions, floors and sculpture rooms, and psylink's royalty read and
        // neuroformer candidates.
        var needs = new List<NeedId>();
        if (sc.Refrigeration)
        {
            needs.Add(FoodStoresRules.RefrigerationNeed);
        }
        if (sc.Training)
        {
            needs.Add(MilitaryFamily.Training);
        }
        if (sc.Gear)
        {
            needs.Add(MilitaryFamily.Equipment);
        }
        if (sc.Acquisition || sc.Field || sc.Bill)
        {
            needs.Add(FoodRules.FoodSupplyNeed);
        }
        if (sc.Cooking || sc.Bill)
        {
            needs.Add(FoodRules.CookingNeed);
        }
        if (sc.ResourceTargetsConfigured || sc.Acquisition)
        {
            // The resource Need's derived stock targets and supply plan (#3303).
            needs.Add(EconomyFamily.Resource);
        }
        if (sc.Art)
        {
            // The trade Need's silver gap and export batches (#3316).
            needs.Add(EconomyFamily.Trade);
        }
        if (sc.Trade)
        {
            // The caravan Need's trader census and the trade-mission Need's settlement trips (#3316).
            needs.AddRange([EconomyFamily.Caravan, EconomyFamily.TradeMission]);
        }
        if (sc.Lighting)
        {
            needs.Add(RoomsFamily.Lighting);
        }
        if (sc.Flooring)
        {
            needs.Add(RoomsFamily.Flooring);
        }
        if (sc.Art)
        {
            needs.Add(RoomsFamily.Art);
        }
        if (sc.Comfort)
        {
            needs.AddRange([RoomsFamily.Comfort, RoomsFamily.Hosting, RoomsFamily.PartySpot, RoomsFamily.WorkChairs]);
        }
        if (sc.Gathering)
        {
            needs.Add(RoomsFamily.Gatherings);
        }
        if (sc.Burial)
        {
            needs.Add(AdminFamily.Burial);
        }
        if (sc.Stockpiles)
        {
            // The stockpile review's zones, rooms and shelves (#3304).
            needs.Add(AdminFamily.Stockpiles);
        }
        if (sc.Work)
        {
            // The work-assignments Need's coupled activities and coverage (#3304).
            needs.Add(AdminFamily.WorkAssignments);
        }
        if (sc.Psylink)
        {
            needs.Add(RoyaltyFamily.Psylink);
        }
        if (sc.ResearchPlans)
        {
            // The coupled path's research block is gated on this Need (#3527); without it a composed brain never offers one.
            needs.Add(EconomyFamily.Research);
        }
        // The shelter family's Needs (#3257): the room census, sleeping review, temperature and safe-area reads.
        if (sc.Sleeping || sc.Shelter)
        {
            needs.Add(ShelterFamily.Sleeping);
        }
        if (sc.Temperature)
        {
            needs.Add(ShelterFamily.Temperature);
        }
        if (sc.Sheltering)
        {
            needs.Add(ShelterFamily.SafeArea);
        }
        if (sc.Power)
        {
            needs.Add(PowerFamily.Basic);
        }
        if (sc.Envelope)
        {
            needs.Add(LayoutFamily.ClimateEnvelope);
        }
        if (sc.Routes)
        {
            // The routes Need's room census and door definition (#3265).
            needs.Add(LayoutFamily.Routes);
        }
        return (thresholds, new RoundsCapabilities { Needs = needs, LayoutOverlay = layoutOverlay, ClearanceCensus = sc.Clearance, JailShell = sc.PrisonerInteraction, Goals = sc.Goals });
    }
}
