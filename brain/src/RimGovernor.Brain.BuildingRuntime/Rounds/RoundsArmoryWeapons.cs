using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsArmoryPlanner
{
    /// <summary>
    /// Reads the combat pawns and the map's loose weapons and prices the armory's weapon demand: fighters and
    /// hunters short of a weapon. The recipes are the benches' and the catalog's crafts no bench lists (#3659), so an
    /// unarmed colonist with no bench standing still wants the weapon a craft makes.
    /// </summary>
    internal async Task<(IReadOnlyList<Amount> Fighters, IReadOnlyList<Amount> Hunters)> WeaponDemandAsync(GenerationSnapshot snapshot, IReadOnlyList<string> ids, long minTick, Bounds bounds, IReadOnlyList<GearBench> benches, EquatableArray<GearRecipe> crafts, ArmoryTier tier, CancellationToken ct)
    {
        if (_native is not FacadeNativeClient source)
        {
            return ([], []);
        }
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new ControlException("weaponDemand: bounds.Width <= 0 || bounds.Height <= 0");
        }
        var identity = Boundary.Identity(snapshot);
        // The map bounds are the reading's (Facts.MapBounds, read once per step), so the frame things, the combat pawns
        // and the loose weapons read nothing of each other and their hops share one frame (#3566). The replies are read
        // in the order the sequential reads had, so the first failure in that order still throws.
        var thingsRead = SharedReads.Attempt(RoundsGear.FrameThingsOfAsync(_native, identity, ct));
        var pawnsRead = SharedReads.Attempt(source.ReadPawnsAsync(identity, ids, ct));
        var weaponsRead = SharedReads.Attempt(source.ReadEquipWeaponsAsync(identity, default(Cell), new Cell(bounds.Width - 1, bounds.Height - 1), ct));
        var things = SharedReads.Reply(await thingsRead.ConfigureAwait(false));
        var reply = SharedReads.Reply(await pawnsRead.ConfigureAwait(false));
        var observed = reply;
        if (!ContextMatches(observed.Context, snapshot) || observed.Context.Tick < minTick)
        {
            throw new ControlException("weaponDemand: err != nil || observed.Context.GetTick() < minTick");
        }
        if (observed.Pawns.Count != ids.Count)
        {
            throw new ControlException("weaponDemand: len(observed.Pawns) != len(ids)");
        }
        var pawns = new List<EquipCandidatePawn>();
        var primaries = new Dictionary<Domain.PawnId, ArmoryPrimary>();
        var catalog = await source.DefinitionCatalogAsync(identity, ct).ConfigureAwait(false);
        foreach (var p in observed.Pawns)
        {
            pawns.Add(EquipCandidates.EquipCandidatePawnFacts(p, catalog, things));
            if (ArmoryPrimaryOf(p, things, catalog) is { } primary)
            {
                primaries[new Domain.PawnId(p.Pawn.Id ?? "")] = primary;
            }
        }
        var weapons = SharedReads.Reply(await weaponsRead.ConfigureAwait(false));
        if (!ContextMatches(weapons.Context, snapshot))
        {
            throw new ControlException("weaponDemand: err != nil");
        }
        var candidates = new List<EquipCandidateWeapon>();
        foreach (var w in weapons.Targets)
        {
            candidates.Add(EquipCandidates.EquipCandidateWeaponOf(catalog, w));
        }
        var recipes = new List<GearRecipe>();
        var products = new Dictionary<Resource, WeaponDef>();
        foreach (var b in benches)
        {
            recipes.AddRange(b.Recipes);
        }
        recipes.AddRange(GearRules.UnhostedCrafts(crafts, benches));
        foreach (var recipe in recipes)
        {
            if (!WeaponAssignmentRules.WeaponRecipe(recipe))
            {
                continue;
            }
            foreach (var def in recipe.Products)
            {
                products[def] = catalog.WeaponOf(def.Value);
            }
        }
        var (fighters, hunters) = ArmoryRules.ArmoryWeaponDemand(tier, pawns, primaries, candidates, recipes, products);
        _reviewer.Exports.SetWeapons(snapshot, GearSurplusRules.SpareWeaponCounts(tier, pawns, primaries, candidates, recipes, products));
        return (fighters, hunters);
    }
}

partial class RoundsArmoryPlanner
{
    /// <summary>Whether the observation context belongs to the snapshot's world: it fails only for an invalid context or a foreign generation.</summary>
    internal static bool ContextMatches([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] PbCommon.ObservationContext? context, GenerationSnapshot snapshot)
    {
        try
        {
            Boundary.Context(context, snapshot);
            return true;
        }
        catch (Exception ex) when (ex is BridgeContractException or AuthorityException)
        {
            return false;
        }
    }

    /// <summary>Sums two amount lists per resource, sorted by resource.</summary>
    internal static IReadOnlyList<Amount> MergeAmounts(IReadOnlyList<Amount> a, IReadOnlyList<Amount> b)
    {
        if (b.Count == 0)
        {
            return a;
        }
        var sum = new Dictionary<Resource, long>();
        foreach (var d in a.Concat(b))
        {
            sum[d.Resource] = (sum.TryGetValue(d.Resource, out var n) ? n : 0) + d.Count;
        }
        return [.. sum.Select(kv => new Amount { Resource = kv.Key, Count = kv.Value }).OrderBy(x => x.Resource.Value, StringComparer.Ordinal)];
    }

    /// <summary>The pawn's primary weapon with its catalog facts; null when it holds none.</summary>
    internal static ArmoryPrimary? ArmoryPrimaryOf(O.PawnState row, Things things, DefinitionCatalog catalog)
    {
        var equipment = row.Equipment;
        if (equipment.PrimaryId is not { } id)
        {
            return null;
        }
        foreach (var item in equipment.Equipped)
        {
            var thing = item.Thing;
            if (thing.Id != id)
            {
                continue;
            }
            var def = RoundsGear.GearDef(things, thing);
            if (def == "")
            {
                continue;
            }
            var facts = catalog.WeaponOf(def);
            // A qualityless weapon counts as Normal.
            var quality = item.Quality is O.ItemQuality.Graded graded ? (int)graded.Category : (int)RimGovernor.Contracts.Wire.Defs.QualityCategory.Normal;
            return new ArmoryPrimary { Ranged = facts.Ranged, Quality = quality, Facts = facts };
        }
        return null;
    }
}
