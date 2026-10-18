using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The surgery planners' choices (#3402, from the deleted medical/surgery-* and medical/peg-training acceptance cases):
/// restores and chronic replacements (SelectSurgery), life-saving amputation, organ harvest and artificial part recovery
/// from an unrecruitable prisoner, the prisoner care limit's refusal and peg-leg doctor training, over hand-built reads.
/// </summary>
public sealed class SurgerySelectionTests
{
    private static readonly PawnId Colonist = new("colonist");
    private static readonly PawnId Captive = new("captive");
    private static readonly PawnId Doctor = new("doctor");

    private const int LegPart = 30;
    private const int EyePart = 12;
    private const int HandPart = 20;
    private const int KidneyPart = 8;
    private const int OtherKidneyPart = 9;
    private const int ArmPart = 17;

    /// <summary>An operation a single eligible doctor performs at 95%, neither a violation nor lethal.</summary>
    private static SurgeryOperation Op(SurgeryKind kind, string recipe, int part, string body, bool stocked, string item = "", double tier = 0) => new()
    {
        Kind = kind,
        Recipe = Known(recipe),
        PartIndex = Known(part),
        PartDefName = Known(body),
        Item = new Resource(item),
        Tier = tier,
        IngredientsOnMap = Known(stocked),
        EligibleDoctors = Known(1),
        SuccessChance = Known(0.95),
        Violation = Known(false),
        Lethal = Known(false),
    };

    private static CarePawn Patient(IReadOnlyList<SurgeryOperation> ops, IReadOnlyList<CareCondition>? conditions = null) => new()
    {
        Id = Colonist,
        Dead = Known(false),
        QueuedSurgeries = Known(0),
        Operations = Known(EquatableArray.From(ops)),
        Conditions = Known(EquatableArray.From(conditions ?? [])),
    };

    private static Fact<EquatableArray<CarePawn>> Census(CarePawn pawn) => Known(EquatableArray.From([pawn]));

    /// <summary>A living prisoner the colony would not recruit, held under no interaction, with no faction goodwill at stake.</summary>
    private static PrisonerFacts Prisoner(IReadOnlyList<SurgeryOperation> ops, IReadOnlyList<MissingPart>? missing = null) => new()
    {
        Pawn = Captive,
        Dead = Known(false),
        Prisoner = Known(true),
        Recruitable = Known(false),
        CurrentInteraction = Known(PrisonerInteractionMode.Maintain),
        Resistance = Known(0.0),
        HeldTicks = Known(0L),
        Prospect = Known(new PrisonerProspect { Age = 30, Health = 1 }),
        QueuedSurgeries = Known(0),
        HarvestGoodwill = Known(0),
        Withdrawal = Known(false),
        MissingParts = Known(EquatableArray.From(missing ?? [])),
        Operations = Known(EquatableArray.From(ops)),
    };

    private static Fact<EquatableArray<PrisonerFacts>> Prisoners(PrisonerFacts row) => Known(EquatableArray.From([row]));

    /// <summary>A three-colonist colony without Ideology whose one doctor is at Medicine 8, below the training floor.</summary>
    private static readonly PrisonerColony Colony = new() { Colonists = 3, Medicine = new Dictionary<PawnId, int> { [Doctor] = 8 } };

    private static readonly RecipeFacts Recipes = new()
    {
        HarvestOrgans = ["Kidney", "Lung"],
        MaterialInstalls = [new MaterialInstall { Recipe = "InstallPegLeg", Bodies = ["Leg"], Part = "PegLeg", Work = 1000, Value = 5 }],
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
            [new CareCondition { DefName = Known("Cataract"), PartIndex = Known(EyePart) }]);

        var choice = Assert.Single(SurgeryRules.SelectSurgery(Census(pawn), null, new SurgeryContext()).Queue);

        Assert.Equal(("InstallBionicEye", EyePart, false), (choice.Recipe, choice.Part, choice.Elective));
        // Without the chronic condition a healthy eye is no served operation (and no elective without a hospital bed).
        Assert.Empty(SurgeryRules.SelectSurgery(Census(pawn with { Conditions = Known(EquatableArray.From(new List<CareCondition>())) }), null, new SurgeryContext()).Queue);
    }

    [Fact]
    public void AHandInfectionLosingItsImmunityRaceAmputatesTheHand()
    {
        var infection = new CareCondition
        {
            DefName = Known("WoundInfection"),
            PartIndex = Known(HandPart),
            Severity = Known(0.6),
            SeverityPerDay = Known(0.8),
            Immunity = Known(0.2),
            ImmunityPerDay = Known(0.3),
        };
        var pawn = Patient([Op(SurgeryKind.Amputate, "RemoveBodyPart", HandPart, "Hand", stocked: true)], [infection]);

        var amputation = Assert.Single(EmergencyRules.LifeSavingAmputations(pawn));

        Assert.Equal((Colonist, "RemoveBodyPart", HandPart), (amputation.Pawn, amputation.Recipe, amputation.Part));
        // Winning the race (immunity outpaces severity) cuts nothing.
        Assert.Empty(EmergencyRules.LifeSavingAmputations(pawn with { Conditions = Known(EquatableArray.From([infection with { ImmunityPerDay = Known(2.0) }])) }));
    }

    /// <summary>A colonist missing a kidney, none stocked: a part-short want for InstallNaturalKidney.</summary>
    private static CarePawn KidneyPatient() =>
        Patient([Op(SurgeryKind.Restore, "InstallNaturalKidney", KidneyPart, "Kidney", stocked: false, "Kidney", tier: 1)]);

    private static SurgeryOperation Harvest(int part) => Op(SurgeryKind.Harvest, "Harvest_Kidney", part, "Kidney", stocked: true) with { Violation = Known(true) };

    [Fact]
    public void AColonistMissingAKidneyHarvestsOneFromTheUnrecruitablePrisoner()
    {
        var pawns = Census(KidneyPatient());
        var want = Assert.Single(SurgeryRules.SelectSurgery(pawns, null, new SurgeryContext()).Wants);
        Assert.Equal(SurgeryWantReason.PartShort, want.Reason);
        var needs = OrganHarvestRules.OrganNeeds(pawns, [want], false, new Dictionary<Resource, long>(), Recipes);
        Assert.Equal(("Kidney", Colonist), (Assert.Single(needs).Organ, needs[0].For));

        var harvest = OrganHarvestRules.SelectOrganHarvest(Prisoners(Prisoner([Harvest(KidneyPart), Harvest(OtherKidneyPart)])), Known(Colony), needs, null);

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
        var prisoners = Prisoners(Prisoner([Harvest(KidneyPart), Harvest(OtherKidneyPart)]));
        var forbidding = Colony with { Preferences = new PreferencesRecord { Forbidden = [ForbiddenMethod.OrganHarvest] } };

        Assert.NotNull(OrganHarvestRules.SelectOrganHarvest(prisoners, Known(Colony), needs, null));
        Assert.Null(OrganHarvestRules.SelectOrganHarvest(prisoners, Known(forbidding), needs, null));
        Assert.Null(OrganHarvestRules.SelectPartRecovery(prisoners, Known(forbidding), needs, null, Recipes));
    }

    [Fact]
    public void ForbiddingSlaveryStopsTheColonyEnslaving()
    {
        Assert.False((Colony with { IdeologyActive = true, Preferences = new PreferencesRecord { Forbidden = [ForbiddenMethod.Slavery] } }).SlaveryAllowed());
    }

    [Fact]
    public void ACareLimitedHarvestIsRefusedAndReportedAsCareLimited()
    {
        var pawns = Census(KidneyPatient());
        var needs = OrganHarvestRules.OrganNeeds(pawns, SurgeryRules.SelectSurgery(pawns, null, new SurgeryContext()).Wants, false, new Dictionary<Resource, long>(), Recipes);
        var limited = Prisoners(Prisoner([
            Harvest(KidneyPart) with { IngredientsOnMap = Known(false), CareLimited = Known(true) },
            Harvest(OtherKidneyPart) with { IngredientsOnMap = Known(false), CareLimited = Known(true) },
        ]));

        Assert.Null(OrganHarvestRules.SelectOrganHarvest(limited, Known(Colony), needs, null));
        var blocked = PrisonerMedicineRules.CareLimitedHarvest(limited, Known(Colony), needs, [], null, Recipes);
        Assert.NotNull(blocked);
        Assert.Equal((Captive, "Harvest_Kidney"), (blocked.Prisoner, blocked.Recipe));
    }

    [Fact]
    public void AColonistMissingAnArmRecoversTheBionicArmFromTheUnrecruitablePrisoner()
    {
        var pawns = Census(Patient([Op(SurgeryKind.Restore, "InstallBionicArm", ArmPart, "Shoulder", stocked: false, "BionicArm", tier: 1.25)]));
        var parts = OrganHarvestRules.PartRecoveryNeeds(pawns, SurgeryRules.SelectSurgery(pawns, null, new SurgeryContext()).Wants, Recipes);
        Assert.Equal(("BionicArm", Colonist), (Assert.Single(parts).Organ, parts[0].For));
        var removal = Op(SurgeryKind.Amputate, "RemoveBodyPart", ArmPart, "Shoulder", stocked: true) with
        {
            AddedPart = Known("BionicArm"),
            YieldThing = Known("BionicArm"),
            YieldValue = Known(1200.0),
            MedicineValue = Known(18.0),
        };

        var recovery = OrganHarvestRules.SelectPartRecovery(Prisoners(Prisoner([removal])), Known(Colony), parts, null, Recipes);

        Assert.NotNull(recovery);
        Assert.Equal((Captive, "RemoveBodyPart", ArmPart, Colonist), (recovery.Prisoner, recovery.Recipe, recovery.Part, recovery.For));
    }

    [Fact]
    public void DoctorsBelowTheFloorTrainOnAPegLegInstallThenItsRemoval()
    {
        var policy = new PrisonerPolicy { ReleaseAfterDays = 10, FoodTargetDays = 5 };
        var legless = Prisoner(
            [Op(SurgeryKind.Restore, "InstallPegLeg", LegPart, "Leg", stocked: true, "PegLeg", tier: 0.6)],
            [new MissingPart { PartIndex = Known(LegPart), PartDefName = Known("Leg") }]);

        var install = DoctorTrainingRules.SelectPegCycle(Prisoners(legless), Known(Colony), Known(20.0), policy, [], Recipes, null);

        Assert.NotNull(install);
        Assert.Equal((Captive, "InstallPegLeg", LegPart, PegCycleStep.Training), (install.Prisoner, install.Recipe, install.Part, install.Step));

        var pegged = legless with
        {
            MissingParts = Known(EquatableArray.From(new List<MissingPart>())),
            Operations = Known(EquatableArray.From([Op(SurgeryKind.Amputate, "RemoveBodyPart", LegPart, "Leg", stocked: true) with { AddedPart = Known("PegLeg") }])),
        };
        var removal = DoctorTrainingRules.SelectPegCycle(Prisoners(pegged), Known(Colony), Known(20.0), policy, [], Recipes, null);

        Assert.NotNull(removal);
        Assert.Equal(("RemoveBodyPart", LegPart, PegCycleStep.Training), (removal.Recipe, removal.Part, removal.Step));
        // A doctor at the floor trains nobody.
        Assert.Null(DoctorTrainingRules.SelectPegCycle(Prisoners(legless), Known(Colony with { Medicine = new Dictionary<PawnId, int> { [Doctor] = 10 } }), Known(20.0), policy, [], Recipes, null));
    }
}
