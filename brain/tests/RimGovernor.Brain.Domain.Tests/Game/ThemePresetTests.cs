using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Policy;
using RimWorld;
using Verse;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests.Game;

// #3493: the preset themes resolve over the installed definition catalog.
public sealed class ThemePresetTests
{
    private static readonly ThemeRecord Theme = new() { Id = 7, Name = "Theme", Importance = Importance.High, DeadlineDay = 40 };

    private static IEnumerable<PresetDefRef> AllRequirements() =>
        ThemePresetCatalog.All.SelectMany(p => p.Members).Where(m => m.Goal is not null).SelectMany(m => PresetRules.Requirements(m.Goal!)).Distinct();

    private static Def Make(PresetDefRef def) =>
        def.Class switch
        {
            PresetDefClass.Thing => new ThingDef { defName = def.Name },
            PresetDefClass.Research => new ResearchProjectDef { defName = def.Name },
            PresetDefClass.Precept => new RimWorld.PreceptDef { defName = def.Name },
            PresetDefClass.Meme => new RimWorld.MemeDef { defName = def.Name },
            PresetDefClass.RoomRole => new RoomRoleDef { defName = def.Name },
            _ => throw new ArgumentOutOfRangeException(nameof(def)),
        };

    // A catalog carrying every def the presets name, except the ones in missing.
    private static DefinitionCatalog Catalog(params PresetDefRef[] missing) =>
        new(
            [
                new ThingDef { defName = nameof(ThingDefOf.Silver) },
                new TerrainDef { defName = "Soil" },
                .. AllRequirements().Except(missing).Where(d => d.Name != nameof(ThingDefOf.Silver)).Select(Make),
            ],
            DefStatTable.Empty,
            new CatalogDerived(nameof(ThingDefOf.Silver), "Wort", 10f));

    [Fact]
    public void ADozenPresetsWithUniqueIdsAndMemberKeys()
    {
        Assert.True(ThemePresetCatalog.All.Count >= 12);
        Assert.Equal(ThemePresetCatalog.All.Count, ThemePresetCatalog.All.Select(p => p.Id).Distinct().Count());
        foreach (var preset in ThemePresetCatalog.All)
        {
            Assert.NotEmpty(preset.Members);
            Assert.Equal(preset.Members.Count, preset.Members.Select(m => m.Key).Distinct().Count());
        }
    }

    [Fact]
    public void EveryPresetResolvesWithEveryMemberAvailableOnAFullCatalog()
    {
        var catalog = Catalog();

        foreach (var preset in ThemePresetCatalog.All)
        {
            var resolved = PresetRules.Resolve(preset, catalog, Theme);

            Assert.Equal(preset.Members.Count, resolved.Members.Count);
            Assert.Empty(resolved.Unavailable());
            Assert.All(resolved.Goals(), g =>
            {
                Assert.Equal(Importance.High, g.Importance);
                Assert.Equal(40, g.DeadlineDay);
                Assert.Equal(7, g.Theme);
            });
        }
    }

    [Fact]
    public void MissingDlcDefsMakeExactlyThatPresetsMembersUnavailableAndNameTheDef()
    {
        var bioferrite = new PresetDefRef(PresetDefClass.Thing, nameof(ThingDefOf.Bioferrite));
        var sculptures = new PresetDefRef(PresetDefClass.Research, "VoidSculptures");
        var catalog = Catalog(bioferrite, sculptures);

        foreach (var preset in ThemePresetCatalog.All)
        {
            var unavailable = PresetRules.Resolve(preset, catalog, Theme).Unavailable().ToList();

            if (preset.Id != "cult-of-the-void")
            {
                Assert.Empty(unavailable);
                continue;
            }

            Assert.Equal(["research.VoidSculptures", "stock.Bioferrite"], unavailable.Select(m => m.Key).Order(StringComparer.Ordinal));
            Assert.Contains("VoidSculptures", unavailable.Single(m => m.Key == "research.VoidSculptures").UnavailableReason);
            Assert.Contains("Bioferrite", unavailable.Single(m => m.Key == "stock.Bioferrite").UnavailableReason);
        }
    }

    [Fact]
    public void AnOverrideReplacesOneMembersImportanceAndARemovedMemberDisappears()
    {
        var preset = ThemePresetCatalog.All.Single(p => p.Id == "fortress");
        var theme = Theme with { RemovedPresetMembers = ["research.Mortars"] };
        var overrides = new MemberOverride[] { new MemberOverride.ImportanceOf("built.Turret_Mortar", Importance.Low), new MemberOverride.DeadlineOf("built.Turret_Mortar", 90) };

        var resolved = PresetRules.Resolve(preset, Catalog(), theme, overrides);

        Assert.DoesNotContain(resolved.Members, m => m.Key == "research.Mortars");
        Assert.Equal(preset.Members.Count - 1, resolved.Members.Count);
        var mortar = resolved.Members.Single(m => m.Key == "built.Turret_Mortar").Goal!;
        Assert.Equal(Importance.Low, mortar.Importance);
        Assert.Equal(90, mortar.DeadlineDay);
        var other = resolved.Members.Single(m => m.Key == "built.Turret_MiniTurret").Goal!;
        Assert.Equal(Importance.High, other.Importance);
        Assert.Equal(40, other.DeadlineDay);
    }

    [Fact]
    public void NakedCannibalsResolveToPreceptsAVeneratedMonkeyABelieverGoalNudeApparelAndACannibalDiet()
    {
        var resolved = PresetRules.Resolve(ThemePresetCatalog.All.Single(p => p.Id == "naked-cannibals"), Catalog(), Theme);

        var precepts = resolved.Goals().Where(g => g.Kind == GoalKind.Precept).Select(g => g.Target).ToList();
        Assert.Contains(precepts, p => p.StartsWith("Nudity", StringComparison.Ordinal));
        Assert.Contains(precepts, p => p.StartsWith("Cannibalism", StringComparison.Ordinal));
        Assert.Contains(precepts, p => p.StartsWith("InsectMeat", StringComparison.Ordinal));
        Assert.Equal("Monkey", resolved.Goals().Single(g => g.Kind == GoalKind.VeneratedAnimal).Target);
        Assert.Single(resolved.Goals(), g => g.Kind == GoalKind.Believers);
        Assert.Contains(resolved.Members, m => m.Preference is PresetPreference.Apparel { Preference: ApparelPreference.Nude });
        Assert.Contains(resolved.Members, m => m.Preference is PresetPreference.Diet diet && diet.Overrides.Contains(DietOverride.Cannibal));
    }
}
