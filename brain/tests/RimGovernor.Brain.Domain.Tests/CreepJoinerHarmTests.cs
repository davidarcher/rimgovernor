using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Policy;
using RimWorld;
using Verse;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using O = RimGovernor.Contracts.Wire.Observations;
using DiseasePartRead = RimGovernor.Contracts.Wire.Observations.DiseasePartRead;
using NeedsForecastPawn = RimGovernor.Contracts.Wire.Observations.NeedsForecastPawn;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// #3844 D12: the harm term prices a colonist creepjoiner's unshown downside over the game's draw. The table has four drawable
/// downsides of weight 1 and a 30-day MTB that add no trait (the agony pulse, an aggressive turn, leaving, and one that does
/// nothing) and one that adds a trait. The creepjoiner "cj" joined 2 days ago, fights at 1000 points (the exposure table's
/// calibrated 1–2 cell at 1000 defense) and was valued 1.5 capability-days a day by the previous review. The horizon is 10 days.
/// </summary>
public sealed class CreepJoinerHarmTests
{
    private const double Day = GameTime.TicksPerDay, Mtb = 30, Horizon = 10, Joined = 2, Points = 1000;

    private static CreepJoinerDownsideRow Downside(string def, CreepJoinerResponse response = CreepJoinerResponse.None, bool trait = false) => new()
    {
        Def = def, Weight = 1, CanOccurRandomly = true, AddsTrait = trait, TriggerMtbDays = Mtb, Response = response,
    };

    private static readonly AgonyPulse Agony = new()
    {
        Range = 14.9, MaxSeverity = 1, SeverityPerSecond = (0.1, 0.15), DisappearsTicks = (2000 * 60, 4000 * 60),
        Table = new HediffPenaltyTable
        {
            Known = true, Hediff = "AgonyPulse", InitialSeverity = 0.001, Stages = [new PenaltyStage(0, 1, []), new PenaltyStage(0.95, 0.25, [])],
        },
    };

    private static CreepJoinerDownsideTable Table(bool agony = true) => new()
    {
        Downsides =
        [
            Downside("Aggressive", CreepJoinerResponse.Aggressive), Downside("Leaves", CreepJoinerResponse.Leaves),
            Downside("PsychicAgony", agony ? CreepJoinerResponse.PsychicAgony : CreepJoinerResponse.None), Downside("Nothing"),
            Downside("Disturbing", trait: true),
        ],
        Agony = agony ? Agony : null,
    };

    private static readonly PawnValue Value = new("cj", 2, 0.5, 0, 0);

    private static CreepJoinerHarmInputs Inputs(CreepJoinerDownsideTable? table = null, bool valued = true) => new()
    {
        Unshown = [new UnshownCreepJoiner("cj", Joined, Points)], Table = table ?? Table(), Values = valued ? [Value] : [],
    };

    internal static readonly GearOption Rifle = new() { ID = "rifle", Slot = GearSlot.Primary, Ranged = true };

    internal static readonly GearOption Longsword = new() { ID = "longsword", Slot = GearSlot.Primary };

    private static NeedsForecastPawn Cj =>ForecastValueTests.HarmPawn() with { Pawn = "cj" };

    private static ForecastCoupledContext Context(CreepJoinerHarmInputs inputs, NeedsForecastPawn? cj = null)
    {
        var context = ForecastValueTests.Context(ForecastValueTests.HarmPawn(), ForecastValueTests.HarmTables(),
            ForecastValueTests.RaidDay(raidsPerDay: 0) with { CreepJoiners = inputs },
            new Dictionary<string, EquatableArray<GearOption>> { ["pawn"] = ForecastValueTests.Cloth, ["cj"] = ForecastValueTests.Cloth });
        return context with { Needs = context.Needs with { Pawns = [ForecastValueTests.HarmPawn(), cj ?? Cj] } };
    }

    private static ForecastCoupledOutcome Outcome(NeedsForecastPawn? cj = null) => new(new([], 0, 0), Known(true), new Dictionary<string, ForecastNeedsResult>
    {
        ["pawn"] = ForecastValueTests.Timeline(ForecastValueTests.HarmPawn()), // The needs result is keyed by pawn; its own identity is the fixture pawn's.
        ["cj"] = ForecastValueTests.Timeline(ForecastValueTests.HarmPawn()),
    }, []);

    private static CreepJoinerTerm Term(ForecastCoupledContext context, NeedsForecastPawn? cj = null) =>
        ForecastValue.Of(Outcome(cj), context, Horizon * Day).HarmDetail!.CreepJoiner;

    // A 30-day MTB checked four times a day: (1 − 1/120) a check, as a continuous rate.
    private static double Left(double days) => Math.Exp(Math.Log(1 - (0.25 / Mtb)) / 0.25 * days);

    private static (double Chance, double ValueDays) Fired(CreepJoinerDownsideTable table, string def)
    {
        var firings = CreepJoinerHarm.Firings(table, Joined, Horizon).Where(f => f.Row.Def == def).ToArray();
        return (firings.Sum(f => f.Chance), firings.Sum(f => f.Chance * f.Left));
    }

    [Fact]
    public void EachDownsideFiresByItsWeightAndTimerOverTheHorizon()
    {
        // Four hidden downsides alike, the trait one shown: each is a quarter of the posterior, firing in the horizon at its MTB.
        var (chance, valueDays) = Fired(Table(), "Leaves");
        Assert.Equal((Left(Joined) - Left(Joined + Horizon)) / (4 * Left(Joined)), chance, 12);
        Assert.InRange(valueDays, 0, chance * Horizon);
        Assert.DoesNotContain(CreepJoinerHarm.Firings(Table(), Joined, Horizon), f => f.Row.Def == "Disturbing");
        Assert.Throws<RequiredFactException>(() => CreepJoinerHarm.Firings(Table() with { Unmirrored = "a form requires Leaves" }, Joined, Horizon).ToList());

        // A memoryless 30-day MTB renews once per 30 days: a repeating pulse fires a third more in the ten days after one.
        Assert.Equal(Horizon / Mtb, CreepJoinerHarm.Renewals(Downside("PsychicAgony", CreepJoinerResponse.PsychicAgony) with { Repeats = true }, Horizon), 9);
    }

    [Fact]
    public void AgonyAggressionAndLeavingArePricedOverTheColony()
    {
        var context = Context(Inputs());
        var term = Term(context);
        var row = Assert.Single(term.Pawns);
        var pulse = ForecastIsolation.PulseLoss(ForecastValueTests.HarmPawn(), Agony);

        // The pulse strikes both colonists, the creepjoiner with them.
        Assert.Equal(Fired(Table(), "PsychicAgony").Chance * 2 * pulse, row.Agony, 9);
        // Leaving loses its value from the firing to the horizon.
        Assert.Equal(Fired(Table(), "Leaves").ValueDays * Value.PerDay, row.Leaves, 9);
        // Aggression is one fight at its points against the other colonist, and its value lost as leaving's.
        var fight = HarmLedger.FightLoss(Points, ["pawn"], Outcome(), context, Horizon * Day, new(), context.Needs.Pawns.ToDictionary(p => p.Pawn),
            null, new SortedSet<string>(StringComparer.Ordinal));
        Assert.True(fight.Loss > 0);
        var (aggressive, aggressiveDays) = Fired(Table(), "Aggressive");
        Assert.Equal(aggressive * fight.Loss + aggressiveDays * Value.PerDay, row.Aggressive, 9);
        Assert.Equal(0, row.Hediffs);
        Assert.Equal(row.Loss, term.Loss, 12);
        Assert.Equal(aggressive * fight.Deaths / Horizon, term.DeathsPerDay, 12);

        // The term joins the harm total and names what it leaves out.
        var ledger = ForecastValue.Of(Outcome(), context, Horizon * Day);
        Assert.All(CreepJoinerHarm.NotModelled, f => Assert.Contains(f, ledger.HarmFindings));
        Assert.Equal(term.DeathsPerDay, ledger.HarmDetail!.DeathsPerDay, 12);
    }

    [Fact]
    public void ACreepjoinerNoReviewValuedYetLeavesForANamedZero()
    {
        var context = Context(Inputs(valued: false));
        var row = Assert.Single(Term(context).Pawns);
        Assert.Equal(0, row.Leaves);
        var fight = HarmLedger.FightLoss(Points, ["pawn"], Outcome(), context, Horizon * Day, new(), context.Needs.Pawns.ToDictionary(p => p.Pawn),
            null, new SortedSet<string>(StringComparer.Ordinal));
        Assert.Equal(Fired(Table(), "Aggressive").Chance * fight.Loss, row.Aggressive, 9);
        Assert.Contains(CreepJoinerHarm.ValueAwaitingPlan + ":cj", ForecastValue.Of(Outcome(), context, Horizon * Day).HarmFindings);
    }

    [Fact]
    public void NoCreepjoinerOrAShownOneAddsNothingAndAnUnreadCensusThrows()
    {
        var none = Term(Context(new()));
        Assert.Equal((0d, 0d), (none.Loss, none.DeathsPerDay));
        Assert.Empty(none.Pawns);
        Assert.DoesNotContain(CreepJoinerHarm.NotModelled[0], ForecastValue.Of(Outcome(), Context(new()), Horizon * Day).HarmFindings);

        var shown = Context(new() { Shown = ["cj"] });
        Assert.Equal(0, Term(shown).Loss);
        Assert.Contains("creepjoiner_shown_downside_unmodelled:cj", ForecastValue.Of(Outcome(), shown, Horizon * Day).HarmFindings);

        Assert.Throws<RequiredFactException>(() => ForecastValue.Of(Outcome(), Context(new() { Unread = "the colonist census is incomplete" }), Horizon * Day));
    }

    [Fact]
    public void IsolationCreditsOnlyThePulse()
    {
        // A creepjoiner held apart whose downsides cannot pulse: the harm term prices its aggression and leaving, the isolation
        // room is credited nothing (a hostile or leaving pawn has a lord and ignores its area).
        var context = Context(Inputs(Table(agony: false))) with
        {
            PlannedRoomRoles = new Dictionary<int, PlannedRole> { [1] = PlannedRole.IsolationRoom },
            CreepJoiners = new() { Unread = "", Held = [new HeldCreepJoiner("cj", Joined)], Table = Table(agony: false) },
            InvestmentHorizonDays = Horizon,
        };
        Assert.True(Term(context).Loss > 0);
        Assert.Empty(ForecastIsolation.IsolationCredit(new(1, "", 0), Outcome(), [], context, Day));
        Assert.Equal([ForecastIsolation.NoAgony], ForecastIsolation.Findings(new(1, "", 0), Outcome(), [], context, Day));
    }

    // ----- the production path: the catalog's Anomaly-shaped downside defs, the native tracker row, and the hediffs they trigger.

    private static readonly PawnCapacityDef Consciousness = new() { defName = "Consciousness" };

    private static HediffStage Stage(float min, float factor, params WorkTags[] disabled)
    {
        var stage = new HediffStage { minSeverity = min, capMods = [new PawnCapacityModifier { capacity = Consciousness, postFactor = factor }] };
        foreach (var tag in disabled) stage.disabledWorkTags |= tag;
        return stage;
    }

    private static HediffDef Undiagnosed(string name, HediffCompProperties_ReplaceHediff.TriggeredHediff triggered) => new()
    {
        defName = name, comps = [new HediffCompProperties_ReplaceHediff { manuallyTriggered = true, hediffs = [triggered] }],
    };

    private static CreepJoinerDownsideDef Def(string name, Type? worker = null, HediffDef? hediff = null) => new()
    {
        defName = name, workerType = worker, triggerMtbDays = (float)Mtb, hediffs = hediff is null ? [] : [hediff],
    };

    private static List<Def> AnomalyDefs()
    {
        // Hediffs_Global_Misc.xml: CrumblingMind rises 0.33 a day and becomes CrumbledMind at 1; OrganDecayCreepjoiner strikes
        // 3~5 of the heart, lungs and kidneys at 0.02~0.15 and rises 0.05~0.1 a day.
        var crumbled = new HediffDef { defName = "CrumbledMind", initialSeverity = 0.5f, stages = [Stage(0, 0.6f, WorkTags.Caring, WorkTags.Intellectual)] };
        var crumbling = new HediffDef
        {
            defName = "CrumblingMind", initialSeverity = 0.001f, stages = [Stage(0, 0.9f), Stage(0.4f, 0.75f), Stage(0.8f, 0.6f)],
            comps =
            [
                new HediffCompProperties_SeverityPerDay { severityPerDay = 0.33f },
                new HediffCompProperties_ReplaceHediff { severity = 1, hediffs = [new() { hediff = crumbled }] },
            ],
        };
        var defs = Game.DiseaseTableTests.CoreDefs().ToList();
        var body = (BodyDef)defs.Single(d => d is BodyDef);
        var organs = body.corePart.parts.Select(p => p.def).Distinct().ToList();
        var decay = Game.DiseaseTableTests.Hediff("OrganDecayCreepjoiner", 1, 0.001f,
            new HediffCompProperties_SeverityPerDay { severityPerDayRange = new FloatRange(0.05f, 0.1f) }, new HediffCompProperties_DestroyOrgan());
        decay.maxSeverity = 1;
        decay.onlyLifeThreateningTo = [organs[0]];
        decay.stages[0].partEfficiencyOffset = -0.1f;
        var crumblingUndiagnosed = Undiagnosed("CrumblingMindUndiagnosedCreepjoiner", new() { hediff = crumbling });
        var decayUndiagnosed = Undiagnosed("OrganDecayUndiagnosedCreepjoiner",
            new() { hediff = decay, partsToAffect = organs, countRange = new IntRange(3, 5), severityRange = new FloatRange(0.02f, 0.15f) });
        defs.AddRange([
            crumbled, crumbling, decay, crumblingUndiagnosed, decayUndiagnosed, Consciousness,
            Def("Leaves", typeof(CreepJoinerWorker_DepartDownside)), Def("Aggressive", typeof(CreepJoinerWorker_AggressiveDownside)),
            Def("CrumblingMind", hediff: crumblingUndiagnosed), Def("OrganDecay", hediff: decayUndiagnosed),
        ]);
        return defs;
    }

    private static O.PawnState Row(string id, bool creepjoiner) => PawnFixtures.Row(id) with
    {
        Anomaly = creepjoiner
            ? new O.PawnAnomalyState.Active(new O.PawnAnomaly
            {
                Entity = false, Mutant = false, MinContainmentStrength = 0, HiddenFromPlayer = false, PsychicRitualInvoker = false, MeleeOnly = false,
                Creepjoiner = new O.CreepJoinerState { DownsideTriggered = false, JoinedTick = (int)Day, CombatPower = Points }, Held = null, Study = null,
            })
            : PawnFixtures.Row(id).Anomaly,
    };

    private static RimGovernor.Brain.Bridge.Contracts.RoundsFrame Frame(params O.PawnState[] rows) => new()
    {
        Pawns = new O.PawnSnapshot { Context = new() { Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 0 }, Pawns = [.. rows] },
        Emergency = new(null, new EmergencyFacts { Colonists = [.. rows.Select(r => EmergencyFixtures.Colonist(r.Pawn.Id!))] }),
    };

    [Fact]
    public void TheRoundsReadTheCreepjoinerAndTheCatalogsTriggeredHediffs()
    {
        var catalog = Game.DiseaseTableTests.Catalog([.. AnomalyDefs()]);
        var table = catalog.CreepJoinerDownsideTable();
        var rows = table.Downsides.ToDictionary(d => d.Def);
        Assert.Equal((CreepJoinerResponse.Leaves, CreepJoinerResponse.Aggressive, CreepJoinerResponse.None),
            (rows["Leaves"].Response, rows["Aggressive"].Response, rows["OrganDecay"].Response));
        var mind = Assert.Single(rows["CrumblingMind"].Hediffs);
        Assert.Equal(("CrumblingMind", 1d, "CrumbledMind"), (mind.Course.Table.Hediff, mind.ThenAt, mind.Then!.Course.Table.Hediff));
        Assert.Empty(mind.Course.Unmodelled);
        Assert.Equal(["disabledWorkTags"], mind.Then.Course.Unmodelled);
        var organ = Assert.Single(rows["OrganDecay"].Hediffs);
        Assert.Equal(((3, 5), (0.02, 0.15), true), (organ.Count, organ.Severity, organ.Course.DestroysOrgan));
        Assert.Equal(["Heart", "Lung", "Kidney"], organ.Course.Parts.Select(p => p.Part));
        Assert.All(table.Downsides, d => Assert.Empty(d.Unmodelled));

        // The native tracker row gives the creepjoiner's joinedTick and combat power; the other colonist is none.
        var projection = ColonyFactsCoreTests.Projection() with { Identity = new() { Tick = new((long)(3.5 * Day)) } };
        var frame = Frame(Row("cj", creepjoiner: true), Row("pawn", creepjoiner: false));
        var inputs = RoundsCreepJoiner.CreepJoinerHarmOf(projection, frame, catalog);
        Assert.Equal("", inputs.Unread);
        Assert.Equal([new UnshownCreepJoiner("cj", 2.5, Points)], inputs.Unshown);
        Assert.Empty(inputs.Shown);
        Assert.Same(table, inputs.Table);

        // The creepjoiner's read lists its five organs open to the decay: both hediffs cost capability-days, and the decay may kill.
        var parts = new[] { ("Heart", 0.02), ("Lung", 0.025), ("Lung", 0.025), ("Kidney", 0.017), ("Kidney", 0.017) };
        var cj = Cj with
        {
            Disease = NeedsFixtures.Disease with
            {
                Parts = [.. parts.Select(p => new DiseasePartRead(p.Item1, p.Item2, 1) { Incidents = [], Hediffs = ["OrganDecayCreepjoiner"] })],
                Replacements = [new("Lung", "InstallNaturalLung")
                {
                    PartOnMap = true, MedicineOnMap = true, EligibleDoctors = 1, SuccessChance = 0.9, DoctorSuccessChance = 0.9,
                    Outcomes = [new("success", 0, 0, 0)], Environment = true, QualityMax = 0.98, Quality = 0.9,
                }],
            },
        };
        // A lung the decay destroys is replaced: the hospital books the surgery at its chance over the horizon (#3830's rate list).
        var surgeries = CreepJoinerHarm.Surgeries(Context(inputs, cj), cj, new HarmConsequences.Care(true, 1, 0, 1), Horizon * Day);
        // A kidney has no recipe here: it is listed short of a part, and books no surgery.
        Assert.Contains(surgeries, r => r is { Part: "Kidney", Recipe: null, Reason: SurgeryWantReason.PartShort });
        var lung = Assert.Single(surgeries, r => r.Part == "Lung");
        Assert.Equal(("Lung", "InstallNaturalLung"), (lung.Part, lung.Recipe?.Recipe));
        Assert.InRange(lung.Probability, 1e-9, Fired(inputs.Table, "OrganDecay").Chance);
        var rate = Assert.Single(ForecastHospital.Surgeries("cj", surgeries, 1 / Horizon));
        Assert.Equal(lung.Probability / Horizon, rate.PerDay, 12);
        var term = Term(Context(inputs with { Values = [Value] }, cj), cj);
        var row = Assert.Single(term.Pawns);
        Assert.True(row.Hediffs > 0);        Assert.True(row.Leaves > 0 && row.Aggressive > 0);
        Assert.True(term.DeathsPerDay > 0);
        var findings = ForecastValue.Of(Outcome(cj), Context(inputs with { Values = [Value] }, cj), Horizon * Day).HarmFindings;
        Assert.Contains("creepjoiner_hediff_unmodelled:CrumbledMind:disabledWorkTags", findings);

        // A game creepjoiner's 55 points against 1000 defense is a small fight: the committed table's 0-0.5 cells price it
        // against an armed defender (#3880), and an unarmed one is a noncombatant the table does not calibrate (#3835).
        var small = Context(inputs with { Values = [Value], Unshown = [new UnshownCreepJoiner("cj", 2.5, 55)] }, cj);
        small = small with { Harm = small.Harm with { Exposure = RaidExposure.Load() } };
        foreach (var weapon in new[] { Rifle, Longsword })
        {
            var armed = small with { Outfits = new Dictionary<string, EquatableArray<GearOption>> { ["pawn"] = [.. ForecastValueTests.Cloth, weapon], ["cj"] = ForecastValueTests.Cloth } };
            Assert.True(Assert.Single(Term(armed, cj).Pawns).Aggressive > 0);
        }

        Assert.Throws<RequiredFactException>(() => Term(small, cj));

        // The creepjoiner's read without its organs is a required fact.
        Assert.Throws<RequiredFactException>(() => Term(Context(inputs, Cj)));

        // A fresh colony has no creepjoiner: nothing priced, nothing thrown.
        var fresh = RoundsCreepJoiner.CreepJoinerHarmOf(projection, Frame(Row("pawn", creepjoiner: false)), catalog);
        Assert.Equal(("", 0, 0), (fresh.Unread, fresh.Unshown.Count, fresh.Shown.Count));
        Assert.Equal(0, Term(Context(fresh)).Loss);
        // An incomplete colonist census is unread.
        var unseen = frame with { Emergency = new(null, new EmergencyFacts { Colonists = [EmergencyFixtures.Colonist("cj")] }) };
        Assert.NotEqual("", RoundsCreepJoiner.CreepJoinerHarmOf(projection, unseen, catalog).Unread);
    }

    [Fact]
    public void AHandReadsItsWeaponAndHungerWholeAndAnUnrevealedCreepjoinerDropsItsWeapon()
    {
        var hungry = new O.FoodNeed.Has(0.1, RimGovernor.Contracts.Wire.Defs.HungerCategory.Hungry);
        var fed = new O.FoodNeed.Has(0.9, RimGovernor.Contracts.Wire.Defs.HungerCategory.Fed);
        O.PawnState With(O.PawnState row, bool armed, string? primary, O.FoodNeed food) =>
            row with { Equipment = row.Equipment with { Armed = armed, PrimaryId = primary }, Needs = row.Needs with { Food = food } };
        var cj = Row("cj", creepjoiner: true);

        // The weapon is the primary's thing id, null for none; a pawn is hungry below the fed band and never without a food need.
        var armed = RoundsCreepJoiner.CreepJoinerHandOf(With(cj, true, "Thing_Gun1", hungry));
        Assert.Equal(("Thing_Gun1", true), (armed.Weapon, armed.Hungry));
        var unarmed = RoundsCreepJoiner.CreepJoinerHandOf(With(cj, false, null, new O.FoodNeed.Lacks()));
        Assert.Equal(((string?)null, false), (unarmed.Weapon, unarmed.Hungry));
        Assert.False(RoundsCreepJoiner.CreepJoinerHandOf(With(cj, false, null, fed)).Hungry);
        // An armed pawn without a primary id breaks the contract.
        Assert.Throws<RimGovernor.Brain.Bridge.Contracts.BridgeContractException>(() => RoundsCreepJoiner.CreepJoinerHandOf(With(cj, true, null, fed)));

        // An unrevealed creepjoiner holding a weapon drops it; an unarmed one or a plain colonist does not.
        var downsides = new CreepJoinerDownsides();
        var colonist = RoundsCreepJoiner.CreepJoinerHandOf(With(Row("pawn", creepjoiner: false), true, "Thing_Gun2", fed));
        Assert.Equal([new WeaponDrop(armed.Pawn)], downsides.WeaponDrops([armed, unarmed with { Pawn = new("cj2") }, colonist]));
        Assert.Empty(downsides.WeaponDrops([unarmed, colonist]));

        // A held-apart creepjoiner moves into the isolation area only while fed, and a hungry one there is released.
        var record = new CreepJoinerRecord();
        var fedHand = armed with { Hungry = false };
        Assert.Equal([new IsolationMove(armed.Pawn, "Area_1")], downsides.IsolationMoves([fedHand], record, "Area_1", ready: true));
        Assert.Empty(downsides.IsolationMoves([armed], record, "Area_1", ready: true));
        Assert.Equal([new IsolationMove(armed.Pawn, "")], downsides.IsolationMoves([armed with { Area = "Area_1" }], record, "Area_1", ready: true));
    }
}
