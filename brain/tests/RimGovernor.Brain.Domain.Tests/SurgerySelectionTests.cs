using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The surgery planners' choices (#3402, from the deleted medical/surgery-* and medical/peg-training acceptance cases):
/// restores and chronic replacements (SelectSurgery), life-saving amputation, organ harvest and artificial part recovery
/// from an unrecruitable prisoner and the prisoner care limit's refusal, over hand-built reads.
/// </summary>
public sealed class SurgerySelectionTests
{
    private static readonly PawnId Colonist = new("colonist");
    private static readonly PawnId Captive = new("captive");

    private const int LegPart = 30;
    private const int EyePart = 12;
    private const int HandPart = 20;
    private const int KidneyPart = 8;
    private const int OtherKidneyPart = 9;
    private const int ArmPart = 17;

    /// <summary>An operation a single eligible doctor performs at 95%, neither a violation nor lethal.</summary>
    private static CareOperation Op(SurgeryKind kind, string recipe, int part, string body, bool stocked, string item = "", double tier = 0) => new()
    {
        Item = new Resource(item),
        Tier = tier,
        Operation = new SurgeryOperation
        {
            Recipe = recipe,
            Part = new BodyPartRef { Index = part, DefName = body },
            Kind = kind,
            Chance = new SurgeryChance.Rated(0.95, 0.95, new Dictionary<string, double>()),
            EligibleDoctors = 1,
            IngredientsOnMap = stocked,
            Violation = false,
            Lethal = false,
            Yield = null,
            AddedPartHediff = null,
            MedicineMarketValue = 0,
            MedicineCareLimited = false,
        },
    };

    /// <summary>A visible, untended condition on a part.</summary>
    private static Hediff Condition(string def, int part, string body, HediffImmunity? immunity = null, double severity = 0) => new()
    {
        DefName = def,
        Part = new BodyPartRef { Index = part, DefName = body },
        Severity = severity,
        Visible = true,
        Tend = new HediffTend.Untended(),
        Immunizable = immunity,
    };

    private static CarePawn Patient(IReadOnlyList<CareOperation> ops, IReadOnlyList<Hediff>? conditions = null) => new()
    {
        Id = Colonist,
        Care = MedicalCare.Best,
        LifeThreatening = false,
        Dead = false,
        NeedsRest = false,
        BadConditions = false,
        QueuedSurgeries = 0,
        Operations = EquatableArray.From(ops),
        Conditions = EquatableArray.From(conditions ?? []),
    };

    private static EquatableArray<CarePawn> Census(CarePawn pawn) => EquatableArray.From([pawn]);

    /// <summary>A living prisoner the colony would not recruit, held under no interaction, with no faction goodwill at stake.</summary>
    private static PrisonerFacts Prisoner(IReadOnlyList<CareOperation> ops) => new()
    {
        Pawn = Captive,
        Dead = false,
        CreepJoiner = false,
        Recruitable = false,
        CurrentInteraction = new PrisonerHeldInteraction.Settable(PrisonerInteractionMode.Maintain),
        Resistance = 0.0,
        HeldTicks = 0L,
        Prospect = new PrisonerProspect { Age = 30, Health = 1 },
        HarvestGoodwill = 0,
        Operations = EquatableArray.From(ops),
    };

    private static PrisonerCensus Population(PrisonerFacts row, PrisonerColony colony) => PopulationFixtures.Census([row], colony: colony);

    /// <summary>A three-colonist colony without Ideology.</summary>
    private static readonly PrisonerColony Colony = new() { Colonists = 3 };

    private static readonly RecipeFacts Recipes = new()
    {
        HarvestOrgans = ["Kidney", "Lung"],
    };

    [Fact]
    public void AMissingLegQueuesTheStockedProstheticOverThePegLeg()
    {
        var pawn = Patient([
            Op(SurgeryKind.Restore, "InstallPegLeg", LegPart, "Leg", stocked: true, "PegLeg", tier: 0.6),
            Op(SurgeryKind.Restore, "InstallSimpleProstheticLeg", LegPart, "Leg", stocked: true, "SimpleProstheticLeg", tier: 0.85),
        ]);

        var choice = Assert.Single(SurgeryRules.SelectSurgery(Census(pawn), null, new SurgeryContext()).Queue);

        Assert.Equal((Colonist, "InstallSimpleProstheticLeg", LegPart, false), (choice.Pawn, choice.Recipe, choice.Part, choice.Elective));
    }

    [Fact]
    public void ACataractEyeQueuesTheStockedBionicEyeOnThatEye()
    {
        var pawn = Patient(
            [Op(SurgeryKind.Install, "InstallBionicEye", EyePart, "Eye", stocked: true, "BionicEye", tier: 1.25)],
            [Condition("Cataract", EyePart, "Eye")]);

        var choice = Assert.Single(SurgeryRules.SelectSurgery(Census(pawn), null, new SurgeryContext()).Queue);

        Assert.Equal(("InstallBionicEye", EyePart, false), (choice.Recipe, choice.Part, choice.Elective));
        // Without the chronic condition a healthy eye is no served operation (and no elective without a hospital bed).
        Assert.Empty(SurgeryRules.SelectSurgery(Census(pawn with { Conditions = [] }), null, new SurgeryContext()).Queue);
    }

    [Fact]
    public void AHandInfectionLosingItsImmunityRaceAmputatesTheHand()
    {
        var infection = Condition("WoundInfection", HandPart, "Hand", new HediffImmunity { Immunity = 0.2, SeverityPerDay = 0.8, Gain = new ImmunityGain.PerDay(0.3) }, severity: 0.6);
        var pawn = Patient([Op(SurgeryKind.Amputate, "RemoveBodyPart", HandPart, "Hand", stocked: true)], [infection]);

        var amputation = Assert.Single(EmergencyRules.LifeSavingAmputations(pawn));

        Assert.Equal((Colonist, "RemoveBodyPart", HandPart), (amputation.Pawn, amputation.Recipe, amputation.Part));
        // Winning the race (immunity outpaces severity) cuts nothing.
        Assert.Empty(EmergencyRules.LifeSavingAmputations(pawn with { Conditions = [infection with { Immunizable = infection.Immunizable! with { Gain = new ImmunityGain.PerDay(2.0) } }] }));
    }

    /// <summary>A colonist missing a kidney, none stocked: a part-short want for InstallNaturalKidney.</summary>
    private static CarePawn KidneyPatient() =>
        Patient([Op(SurgeryKind.Restore, "InstallNaturalKidney", KidneyPart, "Kidney", stocked: false, "Kidney", tier: 1)]);

    private static CareOperation Harvest(int part, bool careLimited = false)
    {
        var op = Op(SurgeryKind.Harvest, "Harvest_Kidney", part, "Kidney", stocked: !careLimited);
        return op with { Operation = op.Operation with { Violation = true, MedicineCareLimited = careLimited } };
    }

    [Fact]
    public void AColonistMissingAKidneyHarvestsOneFromTheUnrecruitablePrisoner()
    {
        var pawns = Census(KidneyPatient());
        var want = Assert.Single(SurgeryRules.SelectSurgery(pawns, null, new SurgeryContext()).Wants);
        Assert.Equal(SurgeryWantReason.PartShort, want.Reason);
        var needs = OrganHarvestRules.OrganNeeds(pawns, [want], false, new Dictionary<Resource, long>(), Recipes);
        Assert.Equal(("Kidney", Colonist), (Assert.Single(needs).Organ, needs[0].For));

        var harvest = OrganHarvestRules.SelectOrganHarvest(Population(Prisoner([Harvest(KidneyPart), Harvest(OtherKidneyPart)]), Colony), needs, null);

        Assert.NotNull(harvest);
        Assert.Equal((Captive, "Harvest_Kidney", Colonist, true), (harvest.Prisoner, harvest.Recipe, harvest.For, harvest.Violation));
        Assert.True(harvest.Gain > harvest.Cost);
        // A stocked kidney leaves no need to harvest for.
        Assert.Empty(OrganHarvestRules.OrganNeeds(pawns, [want], false, new Dictionary<Resource, long> { [new("Kidney")] = 1 }, Recipes));
    }

    [Fact]
    public void ForbiddingOrganHarvestProposesNoHarvestOrPartRecovery()
    {
        var pawns = Census(KidneyPatient());
        var wants = SurgeryRules.SelectSurgery(pawns, null, new SurgeryContext()).Wants;
        var needs = OrganHarvestRules.OrganNeeds(pawns, wants, false, new Dictionary<Resource, long>(), Recipes);
        var prisoners = Prisoner([Harvest(KidneyPart), Harvest(OtherKidneyPart)]);
        var forbidding = Colony with { Preferences = new PreferencesRecord { Forbidden = [ForbiddenMethod.OrganHarvest] } };

        Assert.NotNull(OrganHarvestRules.SelectOrganHarvest(Population(prisoners, Colony), needs, null));
        Assert.Null(OrganHarvestRules.SelectOrganHarvest(Population(prisoners, forbidding), needs, null));
        Assert.Null(OrganHarvestRules.SelectPartRecovery(Population(prisoners, forbidding), needs, null, Recipes));
    }

    [Fact]
    public void ForbiddingSlaveryStopsTheColonyEnslaving()
    {
        Assert.False((Colony with { Ideology = new ColonyIdeology.Installed(false, null), Preferences = new PreferencesRecord { Forbidden = [ForbiddenMethod.Slavery] } }).SlaveryAllowed());
    }

    [Fact]
    public void ACareLimitedHarvestIsRefusedAndReportedAsCareLimited()
    {
        var pawns = Census(KidneyPatient());
        var needs = OrganHarvestRules.OrganNeeds(pawns, SurgeryRules.SelectSurgery(pawns, null, new SurgeryContext()).Wants, false, new Dictionary<Resource, long>(), Recipes);
        var limited = Prisoner([
            Harvest(KidneyPart, careLimited: true),
            Harvest(OtherKidneyPart, careLimited: true),
        ]);

        Assert.Null(OrganHarvestRules.SelectOrganHarvest(Population(limited, Colony), needs, null));
        var blocked = PrisonerMedicineRules.CareLimitedHarvest(Population(limited, Colony), needs, [], null, Recipes);
        Assert.NotNull(blocked);
        Assert.Equal((Captive, "Harvest_Kidney"), (blocked.Prisoner, blocked.Recipe));
    }

    [Fact]
    public void AColonistMissingAnArmRecoversTheBionicArmFromTheUnrecruitablePrisoner()
    {
        var pawns = Census(Patient([Op(SurgeryKind.Restore, "InstallBionicArm", ArmPart, "Shoulder", stocked: false, "BionicArm", tier: 1.25)]));
        var parts = OrganHarvestRules.PartRecoveryNeeds(pawns, SurgeryRules.SelectSurgery(pawns, null, new SurgeryContext()).Wants, Recipes);
        Assert.Equal(("BionicArm", Colonist), (Assert.Single(parts).Organ, parts[0].For));
        var amputation = Op(SurgeryKind.Amputate, "RemoveBodyPart", ArmPart, "Shoulder", stocked: true);
        var removal = amputation with
        {
            Operation = amputation.Operation with
            {
                AddedPartHediff = "BionicArm",
                Yield = new SurgeryYield { ThingDef = "BionicArm", MarketValue = 1200 },
                MedicineMarketValue = 18,
            },
        };

        var recovery = OrganHarvestRules.SelectPartRecovery(Population(Prisoner([removal]), Colony), parts, null, Recipes);

        Assert.NotNull(recovery);
        Assert.Equal((Captive, "RemoveBodyPart", ArmPart, Colonist), (recovery.Prisoner, recovery.Recipe, recovery.Part, recovery.For));
    }
}
