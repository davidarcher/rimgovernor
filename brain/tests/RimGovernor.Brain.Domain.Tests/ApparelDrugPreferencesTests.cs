using RimGovernor.Contracts.Wire.Defs;
using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The apparel and drug preferences (#3498): the outfit rule, the clothing demand that follows it and the drug-policy
/// planner. Default preferences leave the outfit and the policies as the game has them.
/// </summary>
public sealed class ApparelDrugPreferencesTests
{
    private static ApparelPolicyState State(ApparelPreference preference = ApparelPreference.Dressed, params string[] required) => new()
    {
        Token = "t",
        PawnName = "pawn",
        PolicyID = "outfit-1",
        Preference = preference,
        Required = [.. required],
        Definitions =
        [
            new() { Name = "Apparel_Shirt", Adult = true, CoversBody = true },
            new() { Name = "Apparel_Parka", Adult = true, CoversBody = true },
            new() { Name = "Apparel_FlakVest", Adult = true, Armor = true, CoversBody = true },
            new() { Name = "Apparel_Collar", Adult = true },
        ],
    };

    private static IReadOnlyList<string> Allowed(ApparelPolicyState state, GearRole role = GearRole.Soldier) =>
        ApparelPolicyRules.RoleApparelDefinitions(role, state);

    [Fact]
    public void DressedKeepsTheOutfitRule()
    {
        Assert.Equal(["Apparel_Shirt", "Apparel_Parka", "Apparel_FlakVest", "Apparel_Collar"], Allowed(State()));
        Assert.Equal(["Apparel_Shirt", "Apparel_Parka", "Apparel_Collar"], Allowed(State(), GearRole.Worker));
    }

    [Fact]
    public void NudeKeepsOnlyWhatAPreceptOrTitleRequires()
    {
        Assert.Empty(Allowed(State(ApparelPreference.Nude)));
        Assert.Equal(["Apparel_Collar"], Allowed(State(ApparelPreference.Nude, "Apparel_Collar")));
    }

    [Fact]
    public void ArmorOnlyKeepsAFlakVestAndDropsAShirt()
    {
        Assert.Equal(["Apparel_FlakVest"], Allowed(State(ApparelPreference.ArmorOnly), GearRole.Worker));
    }

    private static RoundsFacts GearFacts() => new()
    {
        Garments =
        [
            new() { Definition = new("Apparel_Shirt") },
            new() { Definition = new("Apparel_FlakVest") },
        ],
        Gear = Known(ColonyModelFixtures.NoGear() with { Pawns = [new GearPawn { Pawn = new("pawn"), Policy = State(), LoadoutModel = new GearModel.NoOutfit() }] }),
    };

    [Fact]
    public void ANudeColonyOwesNoClothingDemand()
    {
        var facts = GearFacts().WithPreferences(new PreferencesRecord { Apparel = ApparelPreference.Nude });
        Assert.Empty(facts.Garments);
        Assert.Equal(ApparelPreference.Nude, facts.Gear.TryGetValue(out var gear) ? gear.Pawns[0].Policy is { } state ? state.Preference : default : default);
    }

    [Fact]
    public void AnArmorOnlyColonyKeepsTheArmorDemand()
    {
        var facts = GearFacts().WithPreferences(new PreferencesRecord { Apparel = ApparelPreference.ArmorOnly });
        Assert.Equal(["Apparel_FlakVest"], facts.Garments.Select(g => g.Definition.Value));
    }

    [Fact]
    public void DefaultPreferencesLeaveTheApparelFacts()
    {
        var before = GearFacts();
        Assert.Equal(before.Garments, before.WithPreferences(new PreferencesRecord()).Garments);
    }

    private static ItemFacts Items() => new()
    {
        Drugs =
        [
            new(new("Beer"), Social: true),
            new(new("SmokeleafJoint"), Social: true),
            new(new("Flake")),
            new(new("Penoxycyline"), Medical: true),
        ],
    };

    private static WorkPawn Pawn(string id, GuestStatus? status = null) =>
        PawnFixtures.Worker(id) with { PolicyInputs = PawnFixtures.Inputs(guest: status) };

    private static OwnedDrugPolicy Owned(IEnumerable<DrugPolicyEntry> entries, params string[] pawns) =>
        new(DrugPolicyPlanRules.Label, [.. pawns.Select(p => new PawnId(p))], [.. entries]);

    [Fact]
    public void NoneWritesAPolicyWithNoRecreationalEntries()
    {
        var plan = DrugPolicyPlanRules.Changes(new PreferencesRecord { DrugPolicy = DrugPolicyPreference.None }, Items(), [Pawn("a")], []);
        Assert.Empty(plan.Write!.Entries);
        Assert.Equal(DrugPolicyPlanRules.Label, Assert.Single(plan.Assign).DrugPolicy);
    }

    [Fact]
    public void ForbiddenDrugsPlanAsNone()
    {
        var plan = DrugPolicyPlanRules.Changes(
            new PreferencesRecord { DrugPolicy = DrugPolicyPreference.SocialOnly, Forbidden = [ForbiddenMethod.Drugs] }, Items(), [Pawn("a")], []);
        Assert.Empty(plan.Write!.Entries);
    }

    [Fact]
    public void SocialOnlyAllowsTheCatalogsSocialDrugsForJoy()
    {
        var plan = DrugPolicyPlanRules.Changes(new PreferencesRecord { DrugPolicy = DrugPolicyPreference.SocialOnly }, Items(), [Pawn("a")], []);
        Assert.Equal(["Beer", "SmokeleafJoint"], plan.Write!.Entries.Select(e => e.Drug));
        Assert.All(plan.Write.Entries, e => Assert.True(e.Joy && !e.Scheduled && !e.Addiction));
    }

    [Fact]
    public void MedicalSchedulesTheMedicalDrugs()
    {
        var plan = DrugPolicyPlanRules.Changes(new PreferencesRecord { DrugPolicy = DrugPolicyPreference.Medical }, Items(), [Pawn("a")], []);
        var entry = Assert.Single(plan.Write!.Entries);
        Assert.Equal("Penoxycyline", entry.Drug);
        Assert.True(entry.Scheduled && !entry.Joy);
    }

    [Fact]
    public void ThePlannerIsIdempotentOnceTheColonyHoldsThePolicy()
    {
        var preferences = new PreferencesRecord { DrugPolicy = DrugPolicyPreference.SocialOnly };
        var written = DrugPolicyPlanRules.Changes(preferences, Items(), [Pawn("a"), Pawn("b")], []).Write!;
        var settled = DrugPolicyPlanRules.Changes(preferences, Items(), [Pawn("a"), Pawn("b")], [Owned(written.Entries, "a", "b")]);
        Assert.Null(settled.Write);
        Assert.Empty(settled.Assign);

        var newcomer = DrugPolicyPlanRules.Changes(preferences, Items(), [Pawn("a"), Pawn("c")], [Owned(written.Entries, "a")]);
        Assert.Null(newcomer.Write);
        Assert.Equal("c", Assert.Single(newcomer.Assign).Pawn.Value);
    }

    [Fact]
    public void ADriftedPolicyIsRewritten()
    {
        var plan = DrugPolicyPlanRules.Changes(new PreferencesRecord { DrugPolicy = DrugPolicyPreference.None }, Items(), [Pawn("a")],
            [Owned(DrugPolicyPlanRules.Entries(DrugPolicyPreference.SocialOnly, Items()), "a")]);
        Assert.Empty(plan.Write!.Entries);
        Assert.Empty(plan.Assign);
    }

    [Fact]
    public void CaptivesKeepTheirPolicyAndDefaultsPlanNothing()
    {
        var plan = DrugPolicyPlanRules.Changes(new PreferencesRecord { DrugPolicy = DrugPolicyPreference.None }, Items(), [Pawn("a"), Pawn("p", GuestStatus.Prisoner)], []);
        Assert.Equal("a", Assert.Single(plan.Assign).Pawn.Value);

        var none = DrugPolicyPlanRules.Changes(new PreferencesRecord(), Items(), [Pawn("a")], []);
        Assert.Null(none.Write);
        Assert.Empty(none.Assign);
    }

    [Fact]
    public void ForbiddenDrugsDropTheDrugRunways()
    {
        var items = Items();
        var users = new Dictionary<Resource, long> { [new("Beer")] = 3 };
        Assert.NotEmpty(DrugRunwayRules.DrugRunwayReserves(items, users, new PreferencesRecord()));
        Assert.Empty(DrugRunwayRules.DrugRunwayReserves(items, users, new PreferencesRecord { Forbidden = [ForbiddenMethod.Drugs] }));
    }
}
