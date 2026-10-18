using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsArmoryPlanner
{
    /// <summary>
    /// Reads the combat pawns and the map's loose weapons and prices the armory's weapon demand: fighters and
    /// hunters short of a weapon, and the unarmed fighters.
    /// </summary>
    internal async Task<(IReadOnlyList<Amount> Fighters, IReadOnlyList<Amount> Hunters, int Unarmed)> WeaponDemandAsync(GenerationSnapshot snapshot, IReadOnlyList<string> ids, long minTick, IReadOnlyList<GearBench> benches, ArmoryTier tier, CancellationToken ct)
    {
        if (_native is not FacadeNativeClient source)
        {
            return ([], [], 0);
        }
        var identity = Boundary.Identity(snapshot);
        // The frame things, the combat pawns and the map bounds read nothing of each other, so their hops share frames
        // (#3566), and the loose weapons are read as soon as the bounds arrive, while the pawn facts are built. The
        // replies are read in the order the sequential reads had, so the first failure in that order still throws.
        var thingsRead = SharedReads.Attempt(RoundsGear.FrameThingsOfAsync(_native, identity, ct));
        var pawnsRead = SharedReads.Attempt(source.ReadCombatPawnsAsync(identity, ids, ct));
        var boundsRead = SharedReads.Attempt(source.ReadMapBoundsAsync(identity, default(Cell), ct));
        var weaponsRead = EquipWeaponsAsync(source, identity, snapshot, boundsRead, ct);
        var things = SharedReads.Reply(await thingsRead.ConfigureAwait(false));
        var reply = SharedReads.Reply(await pawnsRead.ConfigureAwait(false));
        var observed = reply.Outcome?.Observed;
        if (observed is null)
        {
            throw new ControlException("weaponDemand: observed == nil");
        }
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
        var catalog = await source.DefinitionCatalogAsync(identity, ct).ConfigureAwait(false) ?? throw new InvalidOperationException("native served no definition catalog");
        foreach (var p in observed.Pawns)
        {
            pawns.Add(EquipCandidates.EquipCandidatePawnFacts(p, catalog, things));
            if (ArmoryPrimaryOf(p, things, catalog) is { } primary)
            {
                primaries[new Domain.PawnId(p.Pawn.Id ?? "")] = primary;
            }
        }
        var bounds = SharedReads.Reply(await boundsRead.ConfigureAwait(false));
        if (!BoundsKnown(bounds, snapshot))
        {
            throw new ControlException("weaponDemand: err != nil || bounds.Bounds.Width <= 0 || bounds.Bounds.Height <= 0");
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
            if (!b.Recipes.TryGetValue(out var rows))
            {
                throw new ControlException("weaponDemand: !known");
            }
            recipes.AddRange(rows);
            foreach (var recipe in rows)
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
        }
        var (fighters, hunters) = ArmoryRules.ArmoryWeaponDemand(tier, pawns, primaries, candidates, recipes, products);
        _reviewer.Exports.SetWeapons(snapshot, GearSurplusRules.SpareWeaponCounts(tier, pawns, primaries, candidates, recipes, products));
        return (fighters, hunters, WeaponAssignmentRules.UnarmedFighters(pawns, candidates));
    }

    /// <summary>Whether the bounds reply belongs to the snapshot's world and spans cells.</summary>
    private static bool BoundsKnown(MapBounds bounds, GenerationSnapshot snapshot) =>
        ContextMatches(bounds.Context, snapshot) && bounds.Bounds.Width > 0 && bounds.Bounds.Height > 0;

    /// <summary>
    /// The map's loose weapons, read once the bounds read answers. Bounds that failed or are unknown issue no read: the
    /// caller reads the bounds reply first and throws there, so the empty reply returned here is never read.
    /// </summary>
    private static async Task<(EquipRead Value, Exception? Error)> EquipWeaponsAsync(FacadeNativeClient source, C.Identity identity, GenerationSnapshot snapshot, Task<(MapBounds Value, Exception? Error)> boundsRead, CancellationToken ct)
    {
        var (bounds, error) = await boundsRead.ConfigureAwait(false);
        if (error is not null || !BoundsKnown(bounds, snapshot))
        {
            return (default!, error);
        }
        return await SharedReads.Attempt(source.ReadEquipWeaponsAsync(identity, default(Cell), new Cell((int)bounds.Bounds.Width - 1, (int)bounds.Bounds.Height - 1), ct)).ConfigureAwait(false);
    }

    /// <summary>
    /// Places or retires the crafting spot through the building planner under the equipment Need while the last
    /// declaration wanted it; the verdict names a missing weapon bench, and nothing wanted is no deficit.
    /// </summary>
    internal async Task<Verdict> PlaceCraftingSpotAsync(StepArbiter arbiter, CancellationToken call, CancellationToken epoch)
    {
        if (!SpotWanted())
        {
            return BuildingReasonNoDeficit;
        }
        if (_spot is null)
        {
            return BuildingReasonNoWeaponBench;
        }
        var result = await _spot.Copy().StepAsync(arbiter, call, epoch).ConfigureAwait(false);
        return result.Decision?.Admitted == true ? BuildingReasonAdmitted : result.Verdict;
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
        var id = equipment?.PrimaryId ?? "";
        if (equipment is null || id == "")
        {
            return null;
        }
        foreach (var item in equipment.Equipped)
        {
            if (item.Thing is not { } thing || (thing.Id ?? "") != id)
            {
                continue;
            }
            var def = RoundsGear.GearDef(things, thing);
            if (def == "")
            {
                continue;
            }
            var facts = catalog.WeaponOf(def);
            var quality = 2; // QualityCategory.Normal
            if (item.Quality is not null)
            {
                quality = (int)item.Quality.GetValueOrDefault();
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
            foreach (var recipe in rows)
            {
                if (WeaponAssignmentRules.WeaponRecipe(recipe))
                {
                    return true;
                }
            }
        }
        return false;
    }
}
