using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>A clothing read with no recipe option and no garment in progress.</summary>
internal static class ClothingFixtures
{
    internal static RecipeActivityRead NoClothing => new() { Context = new() { NativeGeneration = new RimGovernor.Contracts.Wire.Common.ObservedGeneration.UnclaimedCase(), Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 100 }, Options = [], Retained = [] };
}
