using System.Text.Json;
using System.Reflection;
using System.Runtime.Loader;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.PlayerApi;
using RimGovernor.Launcher;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class LauncherPresetTests
{
    private sealed class GameFreeContext() : AssemblyLoadContext(nameof(GameFreeContext), isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name is "Assembly-CSharp" or "UnityStubs" or "Waylith" || assemblyName.Name?.StartsWith("UnityEngine", StringComparison.Ordinal) == true)
                throw new FileNotFoundException("Launcher must not load game assemblies: " + assemblyName.Name);
            if (assemblyName.Name?.StartsWith("RimGovernor", StringComparison.Ordinal) != true) return null;
            var path = Path.Combine(Path.GetDirectoryName(typeof(NewColony).Assembly.Location)!, assemblyName.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }

    [Fact]
    public void ScenarioChoicesLoadWithoutGameAssemblies()
    {
        var context = new GameFreeContext();
        try
        {
            var assembly = context.LoadFromAssemblyPath(typeof(NewColony).Assembly.Location);
            var colony = assembly.GetType(typeof(NewColony).FullName!, throwOnError: true)!;
            Assert.NotNull(colony.GetProperty(nameof(NewColony.Choices))!.GetValue(null));
            Assert.NotNull(colony.GetProperty(nameof(NewColony.Presets))!.GetValue(null));
        }
        finally { context.Unload(); }
    }

    [Fact]
    public void RhadamantPresetSelectsTheSeaIceBiome()
    {
        var preset = Assert.Single(NewColony.Presets, p => p.Name == "rhadamant");

        Assert.Equal(["SeaIce"], preset.Spec.Biomes);
        Assert.Equal(["SeaIce"], preset.Spec.ForWire("any-seed").Biomes);
    }

    [Fact]
    public void LauncherAndRouteKeepTheSameAuthoredRhadamantStartingSupplies()
    {
        var preset = Assert.Single(NewColony.Presets, p => p.Name == "rhadamant");
        var launcher = NewColony.WireSpec("preset", preset.Spec, "seed");
        var route = ScenarioParts.ApplyRhadamant(NewColony.WireSpec("preset", NewColonySpec.Default, "seed"));
        Assert.Equal(launcher.StartingThings, route.StartingThings);
        Assert.Equal(["Silver", "MealSurvivalPack", "WoodLog", "Steel", "ComponentIndustrial", "MedicineIndustrial", "Gun_BoltActionRifle", "Steel", "MealSurvivalPack", "ShipChunk"],
            route.StartingThings.Select(thing => thing.ThingDef));
        Assert.Equal([800u, 50u, 300u, 450u, 30u, 30u, 1u, 720u, 7u, 3u], route.StartingThings.Select(thing => thing.Count));
        Assert.Equal(14, NewColony.Choices.Things.Count);
    }

    [Fact]
    public void MapOptionsOfferSeaIceBesideIceSheet()
    {
        var biomes = NewColony.Options.Biomes.Select(b => b.Value).ToList();

        Assert.Contains("SeaIce", biomes);
        Assert.Contains("IceSheet", biomes);
    }

    [Fact]
    public void ThemeOptionsAreNoneThenEveryBrainPresetNeedingIdeology()
    {
        var themes = NewColony.Options.Themes;

        Assert.Equal(("", "None", (string?)null), (themes[0].Value, themes[0].Label, themes[0].Requires));
        Assert.Equal(ThemePresetCatalog.All.Select(p => (p.Id, p.Name)), themes.Skip(1).Select(t => (t.Value, t.Label)));
        Assert.All(themes.Skip(1), t => Assert.Equal("Ideology", t.Requires));
    }

    [Fact]
    public void NakedCannibalsPresetStartsNakedCannibalNudistsFromItsTheme()
    {
        var spec = Assert.Single(NewColony.Presets, p => p.Name == "naked-cannibals").Spec;

        Assert.Equal("naked-cannibals", spec.Theme);
        Assert.Equal(["Cannibal", "Nudist"], (spec.ForcedTraits ?? []).Select(t => t.Trait));
        Assert.All(spec.ForcedTraits ?? [], t => Assert.Equal((0, 1f, "player_starter"), (t.Degree, t.Chance, t.Context)));
        Assert.Equal("player_starter", Assert.Single(spec.Naked ?? []).Context);
        Assert.Equal("naked-cannibals", NewColony.WireSpec("RimGovernor-x", spec, "seed").Theme);
    }

    [Fact]
    public void AThemeTheListLacksIsRefused()
    {
        var refused = Assert.Throws<LauncherException>(() => NewColony.Validate(NewColonySpec.Default with { Theme = "no-such-theme" }));

        Assert.Contains("unknown theme", refused.Message, StringComparison.Ordinal);
        Assert.Null(NewColony.WireSpec("RimGovernor-x", NewColonySpec.Default with { Theme = "" }, "seed").Theme);
    }

    [Fact]
    public void TheGamesRouteDecodesTheThemeTheLauncherPosts()
    {
        // The body NewColonyRunner posts: the launcher's own spec in its compact JSON.
        var spec = Assert.Single(NewColony.Presets, p => p.Name == "naked-cannibals").Spec.ForWire("seed") with { SaveName = "RimGovernor-x" };
        var body = JsonSerializer.SerializeToUtf8Bytes(new { requestId = "r1", timeoutMs = 60_000u, spec }, LauncherJson.Compact);

        var decoded = NewColonyJson.Decode(body);
        NewColonyValidation.ValidateNewColonyRequest(decoded);

        Assert.Equal("naked-cannibals", decoded.Spec!.Theme);
        Assert.Equal(["Cannibal", "Nudist"], decoded.Spec.ForcedTraits.Select(t => t.Trait));
        Assert.Null(decoded.Spec.Ideoligion);
    }

    [Fact]
    public void AWireRequestWithAThemeDecodesOnTheGamesRoute()
    {
        // lifecycle/theme-colony posts the wire record's JSON to the same route.
        var request = new RimGovernor.Brain.GameOrders.Lifecycle.NewColonyRequest
        {
            RequestId = "theme-create", TimeoutMs = 600_000,
            Spec = NewColony.WireSpec("theme-colony-accept", NewColonySpec.Default with { Theme = "naked-cannibals", FlatTile = true }, "seed"),
        };

        var decoded = NewColonyJson.Decode(RimGovernor.Brain.GameOrders.OrdersJson.SerializeToUtf8Bytes(request));

        Assert.Equal(request.Spec, decoded.Spec);
    }

    [Fact]
    public void AThemeWithAnExplicitIdeoligionIsRefused()
    {
        var spec = NewColony.WireSpec("RimGovernor-x", NewColonySpec.Default with { Theme = "naked-cannibals" }, "seed")
            with { Ideoligion = new RimGovernor.Brain.GameFacts.Common.IdeoligionDesign { Fluid = false, Memes = ["Nudism"] } };

        var refused = Assert.Throws<BridgeContractException>(() => NewColonyValidation.ValidateNewColonySpec(spec));

        Assert.Contains("theme", refused.Message, StringComparison.Ordinal);
    }
}
