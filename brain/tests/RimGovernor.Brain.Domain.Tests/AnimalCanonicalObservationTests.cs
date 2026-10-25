using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Policy;
using O = RimGovernor.Contracts.Wire.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class AnimalCanonicalObservationTests
{
    [Fact]
    public void OwnedAndWildReadsRetainTheCanonicalPawnAndTrainingRows()
    {
        var training = new O.TrainingEntry { DefName = "Obedience", Learned = true, Wanted = false, Available = true };
        var state = AnimalFixtures.Adult with { Training = [training] };
        var owned = AnimalFixtures.Row("cow", "Cow", state);
        var wild = AnimalFixtures.Row("wild", "Cow", state with { Tameable = true });
        var membership = new O.ColonyAnimal { Pawn = new("cow"), Roping = new O.AnimalRoping.Unroped() };
        var snapshot = ColonyFactsCoreTests.Snapshot() with
        {
            Upkeep = UpkeepFactsTests.NoUpkeep with { Animals = [membership], WildAnimals = [new("wild")] },
        };
        var pawns = Pawns.Of(owned, wild);
        var animal = Assert.Single(AnimalUpkeepReads.ColonyAnimals(snapshot, pawns, new()));
        Assert.Same(owned, animal.Pawn);
        Assert.Same(state, animal.State);
        Assert.Same(training, Assert.Single(animal.Training));
        Assert.Same(membership, animal.Colony);
        var untamed = Assert.Single(AnimalUpkeepReads.ColonyWildAnimals(snapshot, pawns, new()));
        Assert.Same(wild, untamed.Pawn);
        Assert.Null(untamed.Colony);
        Assert.True(untamed.Tameable);
    }

    [Fact]
    public void SlaughterAndReleaseReadEveryProtectionFromTheObservedAnimalState()
    {
        var clear = AnimalFixtures.Adult;
        Assert.True(AnimalFixtures.Owned("cow", "Cow", clear).SafeToSlaughter());
        Assert.True(AnimalFixtures.Owned("cow", "Cow", clear).ReleaseAllowed());
        O.AnimalState[] protectedStates =
        [
            clear with { Downed = true },
            clear with { InMentalState = true },
            clear with { Pregnancy = new O.AnimalPregnancy.Gestating(120000) },
            clear with { Pregnancy = new O.AnimalPregnancy.Stalled() },
            clear with { MasterId = "handler" },
            clear with { ColonistBonded = true },
            clear with { Release = true },
        ];
        foreach (var state in protectedStates)
        {
            var animal = AnimalFixtures.Owned("cow", "Cow", state);
            Assert.Same(state, animal.State);
            Assert.False(animal.SafeToSlaughter());
            Assert.False(animal.ReleaseAllowed());
        }
        Assert.False(AnimalFixtures.Owned("cow", "Cow", clear with { SlaughterDesignatable = false }).SafeToSlaughter());
        Assert.False(AnimalFixtures.Owned("cow", "Cow", clear with { SafeToRelease = false }).ReleaseAllowed());
        Assert.False(AnimalFixtures.Owned("cow", "Cow", clear with { Slaughter = true }).ReleaseAllowed());
    }

    [Fact]
    public void AnAnimalWithoutItsNativeStateCannotSupplyPolicyFacts()
    {
        var animal = new UpkeepAnimal { Pawn = PawnFixtures.Row("cow") };
        Assert.Equal("upkeep animal state", Assert.Throws<RequiredFactException>(() => animal.SafeToSlaughter()).Fact);
    }
}
