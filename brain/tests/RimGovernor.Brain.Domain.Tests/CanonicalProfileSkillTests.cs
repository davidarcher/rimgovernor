using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Store.Codec;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class CanonicalProfileSkillTests
{
    [Fact]
    public void NativeSkillIdentityAndLearningSurviveProfileJournal()
    {
        var observed = new RimGovernor.Brain.GameFacts.Observations.Skill
        {
            DefName = "Shooting", Level = 3, StoredLevel = 1, Passion = RimGovernor.Brain.GameFacts.Observations.Passion.Major,
            Learning = CapabilityMaintenanceTests.Learning(),
        };
        var pawn = PawnFixtures.Row("pawn");
        pawn = pawn with { Biography = pawn.Biography with { Skills = [observed] } };
        var profile = PawnRules.BuildProfile(PawnFixtures.Worker("pawn") with { Observed = pawn });
        Assert.Same(pawn, profile.Observed);
        Assert.Same(observed, profile.Skills[observed.DefName]);
        Assert.Same(observed, Assert.IsType<ProfileSkill.Present>(profile.Skill(observed.DefName)).Observed);
        var restored = StoreJson.Deserialize<PawnProfile>(StoreJson.Serialize(profile));
        Assert.Equal(pawn, restored.Observed);
        Assert.Equal(observed, restored.Skills[observed.DefName]);
        Assert.Equal(observed.Learning, restored.Skills[observed.DefName].Learning);
        Assert.Equal(1.5, profile.Skill(observed.DefName).LearnFactor(new TraitEffects()));
        Assert.IsType<ProfileSkill.Missing>(profile.Skill("Melee"));
        Assert.True(profile.Skill("Melee").Disabled);
        Assert.Equal(0, profile.Skill("Melee").LearnFactor(new TraitEffects()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void JournalRetainsNamedBiotechAndNativeProfileInputs(bool active)
    {
        var pawn = PawnFixtures.Row("profile");
        pawn = pawn with
        {
            Inspiration = "Inspired_Creativity",
            Biography = pawn.Biography with { BiologicalAgeYears = 9, IncapableWorkTypes = ["Cooking"] },
            Settings = pawn.Settings with { Schedule = EquatableArray.From(Enumerable.Range(0, 24).Select(hour => new RimGovernor.Brain.GameFacts.Observations.TimetableSlot
            { Hour = (uint)hour, AssignmentDefName = hour < 8 ? PawnRules.ScheduleWork : PawnRules.ScheduleSleep })) },
        };
        var biotech = new PawnBiotech { Observed = new RimGovernor.Brain.GameFacts.Observations.PawnBiotech
        {
            DevelopmentalStage = RimGovernor.Brain.GameFacts.Defs.DevelopmentalStage.Child,
            Genes = [], Mechanitor = null, Mech = null, Deathrest = null,
        }, WorkMinAges = new Dictionary<WorkType, int> { [WorkType.Cooking] = 12 } };
        PawnBiotechStatus status = active ? new PawnBiotechStatus.Active(biotech) : new PawnBiotechStatus.Inactive();
        pawn = pawn with { Biotech = active ? new RimGovernor.Brain.GameFacts.Observations.PawnBiotechState.Active(biotech.Observed) : new RimGovernor.Brain.GameFacts.Observations.PawnBiotechState.Inactive() };
        var profile = PawnRules.BuildProfile(PawnFixtures.Worker("profile") with { Observed = pawn, Biotech = status });
        var restored = StoreJson.Deserialize<PawnProfile>(StoreJson.Serialize(profile));
        Assert.Equal(pawn, restored.Observed);
        if (active)
        {
            var restoredBiotech = Assert.IsType<PawnBiotechStatus.Active>(restored.Biotech).Facts;
            Assert.Equal(biotech.Observed, restoredBiotech.Observed);
            Assert.Equal(12, restoredBiotech.WorkMinAges[WorkType.Cooking]);
            Assert.Single(restoredBiotech.WorkMinAges);
            Assert.Empty(restoredBiotech.Effects.Stats);
            Assert.Empty(restoredBiotech.Effects.DisabledNeeds);
            Assert.Empty(restoredBiotech.Effects.EnabledNeeds);
        }
        else
        {
            Assert.IsType<PawnBiotechStatus.Inactive>(restored.Biotech);
        }
        Assert.Equal(active, restored.Child);
        Assert.Equal(9, restored.Age);
        Assert.Equal(pawn.Settings.Schedule, restored.Observed.Settings.Schedule);
        Assert.Equal(pawn.Inspiration, restored.Inspiration);
        Assert.True(restored.Incapable[WorkType.Cooking]);
        Assert.Equal(profile.Id, restored.Id);
    }
}
