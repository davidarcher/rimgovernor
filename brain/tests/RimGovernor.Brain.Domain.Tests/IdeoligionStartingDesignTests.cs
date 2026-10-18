using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>#3511: a new colony's theme targets are all held by the starting design, which is fluid only with one normal meme.</summary>
public sealed class IdeoligionStartingDesignTests
{
    private const string Structure = "Structure_Ideal";
    private const string Rancher = "Rancher";
    private const string Nudism = "Nudism";
    private const string Cannibal = "Cannibal";
    private const string Covered = "Nudity_Covered";
    private const string Mandatory = "Nudity_Mandatory";
    private const string Despised = "Cannibalism_Despised";
    private const string Preferred = "Cannibalism_Preferred";

    private static DesignOptions Options() => new()
    {
        Memes =
        [
            new DesignMeme { Name = Structure, Structure = true, Allowed = true, Supported = true },
            new DesignMeme { Name = Rancher, Allowed = true, Supported = true, InitialFluid = true },
            // Impact 3 memes: never in an initial fluid ideoligion.
            new DesignMeme { Name = Nudism, Allowed = true, Supported = true, RequireOne = [[Mandatory]] },
            new DesignMeme { Name = Cannibal, Allowed = true, Supported = true, RequireOne = [[Preferred]] },
        ],
        Precepts =
        [
            new DesignPrecept { Name = Covered, Issue = "Nudity", Default = true, Allowed = true, Supported = true, ConflictingMemes = [Nudism] },
            new DesignPrecept { Name = Mandatory, Issue = "Nudity", Allowed = true, Supported = true, RequiredMemes = [Nudism] },
            new DesignPrecept { Name = Despised, Issue = "Cannibalism", Default = true, Allowed = true, Supported = true, ConflictingMemes = [Cannibal] },
            new DesignPrecept { Name = Preferred, Issue = "Cannibalism", Allowed = true, Supported = true },
        ],
        VeneratedAnimals = ["Monkey", "Muffalo"],
        MaxVeneratedAnimals = 1,
    };

    private static readonly IdeoGoalTarget[] NakedCannibals =
    [
        new(1, IdeoGoalKind.Meme, Nudism),
        new(2, IdeoGoalKind.Meme, Cannibal),
        new(3, IdeoGoalKind.Precept, Mandatory),
        new(4, IdeoGoalKind.Precept, Preferred),
        new(5, IdeoGoalKind.Animal, "Monkey"),
    ];

    [Fact]
    public void WithoutATargetTheDesignIsAnInitialFluidOneWithOneNormalMeme()
    {
        var design = IdeoligionDesignRules.ChooseStartingIdeoligion(Options()).Value.Design;

        Assert.Equal([Rancher, Structure], design.Memes);
        Assert.Equal([Despised, Covered], design.Precepts);
        Assert.True(design.Fluid);
        Assert.Empty(design.VeneratedAnimals);
    }

    [Fact]
    public void ATwoMemeThemeIsHeldWholeByAFixedDesign()
    {
        var design = IdeoligionDesignRules.ChooseStartingIdeoligion(Options(), NakedCannibals).Value.Design;

        Assert.Equal([Cannibal, Nudism, Structure], design.Memes);
        Assert.Equal([Preferred, Mandatory], design.Precepts);
        Assert.Equal(["Monkey"], design.VeneratedAnimals);
        Assert.False(design.Fluid);
    }

    [Fact]
    public void AOneMemeThemeOfAnInitialFluidMemeStaysFluid()
    {
        var design = IdeoligionDesignRules.ChooseStartingIdeoligion(Options(), [new(1, IdeoGoalKind.Meme, Rancher), new(2, IdeoGoalKind.Precept, Preferred)]).Value.Design;

        Assert.Equal([Rancher, Structure], design.Memes);
        Assert.Equal([Preferred, Covered], design.Precepts);
        Assert.True(design.Fluid);
    }

    [Fact]
    public void ATargetNoLegalDesignHoldsIsUnknown()
    {
        // The mandatory nudity precept needs Nudism, which the theme does not name.
        Assert.False(IdeoligionDesignRules.ChooseStartingIdeoligion(Options(), [new(1, IdeoGoalKind.Meme, Rancher), new(2, IdeoGoalKind.Precept, Mandatory)]).IsKnown);
        Assert.False(IdeoligionDesignRules.ChooseStartingIdeoligion(Options(), [new(1, IdeoGoalKind.Animal, "Thrumbo")]).IsKnown);
        Assert.False(IdeoligionDesignRules.ChooseStartingIdeoligion(Options(), [new(1, IdeoGoalKind.Animal, "Monkey"), new(2, IdeoGoalKind.Animal, "Muffalo")]).IsKnown);
        Assert.False(IdeoligionDesignRules.ChooseStartingIdeoligion(Options(), [new(1, IdeoGoalKind.Meme, "Transhumanist")]).IsKnown);
    }
}
