namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsArmoryPlanner
{
    /// <summary>
    /// Reads the combat pawns and the map's loose weapons and prices the armory's weapon demand: fighters and
    /// hunters short of a weapon, and the unarmed fighters.
    /// </summary>
    internal (IReadOnlyList<Amount> Fighters, IReadOnlyList<Amount> Hunters, int Unarmed) WeaponDemand(GenerationSnapshot snapshot, IReadOnlyList<string> ids, long minTick, IReadOnlyList<GearBench> benches, ArmoryTier tier, CancellationToken ct)
    {
        if (_native is not IRoundsEquipSource source)
        {
            return ([], [], 0);
        }
        var identity = Boundary.Identity(snapshot);
        var things = FrameThings(_native, identity, ct);
        var reply = source.ReadCombatPawns(identity, ids, ct).Reply;
        var observed = reply.Observed;
        if (observed is null)
        {
            throw new ControlException("weaponDemand: observed == nil");
        }
        if (!Boundary.TryContext(observed.Context, snapshot) || observed.Context.Tick < minTick)
        {
            throw new ControlException("weaponDemand: err != nil || observed.Context.GetTick() < minTick");
        }
        if (observed.Pawns.Count != ids.Count)
        {
            throw new ControlException("weaponDemand: len(observed.Pawns) != len(ids)");
        }
        var pawns = new List<EquipCandidatePawn>();
        var primaries = new Dictionary<PawnId, ArmoryPrimary>();
        var catalog = source.DefinitionCatalog(identity, ct);
        foreach (var p in observed.Pawns)
        {
            pawns.Add(EquipCandidatePawnFacts(p, catalog, things));
            if (ArmoryPrimaryOf(p, things, catalog) is { } primary)
            {
                primaries[new PawnId(p.Pawn.Id)] = primary;
            }
        }
        var bounds = source.ReadMapBounds(identity, default(Cell), ct).Reply;
        if (!Boundary.TryContext(bounds.Context, snapshot) || bounds.Bounds.Width <= 0 || bounds.Bounds.Height <= 0)
        {
            throw new ControlException("weaponDemand: err != nil || bounds.Bounds.Width <= 0 || bounds.Bounds.Height <= 0");
        }
        var weapons = source.ReadEquipWeapons(identity, default(Cell), new Cell((int)bounds.Bounds.Width - 1, (int)bounds.Bounds.Height - 1), ct).Reply;
        if (!Boundary.TryContext(weapons.Context, snapshot))
        {
            throw new ControlException("weaponDemand: err != nil");
        }
        var candidates = new List<EquipCandidateWeapon>();
        foreach (var w in weapons.Targets)
        {
            candidates.Add(EquipCandidateWeaponOf(catalog, w));
        }
        var recipes = new List<GearRecipe>();
        var products = new Dictionary<Resource, WeaponDef>();
        foreach (var b in benches)
        {
            if (!b.Recipes.TryGetValue(out var rows))
            {
                throw new ControlException("weaponDemand: !known");
            }
            recipes.AddRange(rows);
            foreach (var recipe in rows)
            {
                if (!PolicyApi.WeaponRecipe(recipe))
                {
                    continue;
                }
                foreach (var def in recipe.Products)
                {
                    products[def] = catalog.WeaponOf(def.Value);
                }
            }
        }
        var (fighters, hunters) = PolicyApi.ArmoryWeaponDemand(tier, pawns, primaries, candidates, recipes, products);
        _reviewer.Exports.SetWeapons(snapshot, PolicyApi.SpareWeaponCounts(tier, pawns, primaries, candidates, recipes, products));
        return (fighters, hunters, PolicyApi.UnarmedFighters(pawns, candidates));
    }

    /// <summary>Places the crafting spot through the building planner; the verdict names a missing weapon bench.</summary>
    internal RoundsArmoryResult PlaceCraftingSpot(CancellationToken call, CancellationToken epoch, StepArbiter arbiter)
    {
        if (_spot is null)
        {
            return new RoundsArmoryResult { Verdict = BuildingNoWeaponBench };
        }
        var result = _spot.Step(call, epoch, arbiter);
        if (result.Decision.Admitted)
        {
            return new RoundsArmoryResult { Verdict = BuildingReasonAdmitted };
        }
        return new RoundsArmoryResult { Verdict = result.Verdict };
    }
}

internal static partial class BuildingRuntimePackage
{
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
        var id = equipment?.PrimaryId ?? "";
        if (id == "")
        {
            return null;
        }
        foreach (var item in equipment!.Equipped)
        {
            var def = GearDef(things, item.Thing);
            if (item.Thing?.Id != id || def == "")
            {
                continue;
            }
            var facts = catalog.WeaponOf(def);
            var quality = 2; // QualityCategory.Normal
            if (item.HasQuality)
            {
                quality = (int)item.Quality;
            }
            return new ArmoryPrimary { Definition = def, Ranged = facts.Ranged, Quality = quality, Facts = facts };
        }
        return null;
    }

    /// <summary>Whether any bench's recipes make a weapon.</summary>
    internal static bool WeaponBenchHosted(IReadOnlyList<GearBench> benches)
    {
        foreach (var b in benches)
        {
            b.Recipes.TryGetValue(out var rows);
            foreach (var recipe in rows ?? [])
            {
                if (PolicyApi.WeaponRecipe(recipe))
                {
                    return true;
                }
            }
        }
        return false;
    }
}
