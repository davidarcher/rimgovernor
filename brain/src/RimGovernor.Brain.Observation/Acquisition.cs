namespace RimGovernor.Brain.Observation;

public static class AcquisitionReads
{
    /// <summary>
    /// Decodes the native-approved harvest/hunt source census of one colony facts read; unknown when the
    /// acquisition section failed. Hunt rows <c>HuntGate</c> holds are left out (<c>ColonyProjection.HuntHolds</c>).
    /// </summary>
    public static Fact<EquatableArray<AcquisitionSource>> ColonyAcquisition(O.ColonyFactsSnapshot v, Tables tables, DefinitionCatalog? catalog) =>
        DecodeAcquisition(v, tables, catalog).Sources;

    /// <summary>
    /// Maps the census rows. A native read carries the raw hunt census, and every hunt row passes the hunt gate:
    /// a held row is not a source but a hold.
    /// </summary>
    internal static (Fact<EquatableArray<AcquisitionSource>> Sources, IReadOnlyList<HuntHold> Holds) DecodeAcquisition(O.ColonyFactsSnapshot v, Tables tables, DefinitionCatalog? definitions)
    {
        var unknown = (Unknown<EquatableArray<AcquisitionSource>>(), (IReadOnlyList<HuntHold>)[]);
        if (!ColonyDecodeReads.Headed(tables, v.Acquisition, row => row.Source))
        {
            return unknown;
        }
        AnimalRaceCatalog races;
        HuntCensus census;
        DefinitionCatalog catalog;
        try
        {
            catalog = ColonyDecodeReads.RequireCatalog(definitions);
            races = catalog.AnimalRaces();
            census = HuntCensusOf(v.HuntCensus, catalog);
        }
        catch (DomainException)
        {
            return unknown;
        }
        var rows = new List<AcquisitionSource>();
        var holds = new List<HuntHold>();
        foreach (var row in v.Acquisition)
        {
            var source = tables.Entity(row.Source);
            races.Race(new Resource(source?.DefName ?? ""), out var race);
            var hunt = row.Kind as O.AcquisitionKind.Hunt;
            var plant = row.Kind as O.AcquisitionKind.Plant;
            var offered = new AcquisitionSource
            {
                Id = row.Source.Id ?? "",
                Resource = row.Resource,
                Token = row.Token,
                Definition = source?.DefName ?? "",
                Cell = source is null ? default : ColonyDecodeReads.MapCellOf(source),
                Hunt = hunt is not null,
                Food = plant?.Food ?? false,
                Designated = row.Designation is O.AcquisitionDesignation.Designated,
                Yield = row.Yield,
                HerdSize = (int)(hunt?.HerdSize ?? 0),
                Downed = hunt?.Downed ?? false,
                Sleeping = hunt?.Sleeping ?? false,
                Pest = race.Pest,
                DesignatedTick = row.Designation is O.AcquisitionDesignation.Designated { Since: var since } ? since : default,
                Taken = row.Taken,
                Growth = plant?.Growth ?? 0,
                Plantation = plant?.Plantation ?? false,
            };
            if (hunt is not null)
            {
                // The race row's own flags and numbers: an inedible hunt is only ever a pest; the meat's
                // nutrition is the live MeatAmount stat times the meat def's Nutrition.
                offered = offered with { Food = !race.Pest, Predator = race.Predator };
                if (!race.ManhunterOnDamage.TryGetValue(out var chance))
                {
                    return unknown;
                }
                offered = offered with { RevengeChance = chance };
                if (!race.Pest)
                {
                    if (!race.MeatNutritionPerUnit.TryGetValue(out var perUnit))
                    {
                        return unknown;
                    }
                    offered = offered with { NutritionYield = Math.Max(0, hunt.MeatAmount) * perUnit };
                }
                offered = offered with { MeleeOnly = Meleeable(row.Source, hunt, tables.Pawns, race, offered) };
            }
            else
            {
                offered = offered with { Tree = catalog.PlantIsTree(source?.DefName ?? "") };
                if (offered.Food)
                {
                    double nutrition;
                    bool shown;
                    try
                    {
                        (nutrition, shown) = catalog.ShownStatValue(row.Resource, "", CatalogStats.Nutrition);
                    }
                    catch (DomainException)
                    {
                        return unknown;
                    }
                    if (shown)
                    {
                        offered = offered with { NutritionYield = row.Yield * (float)nutrition };
                    }
                }
            }
            if (hunt is not null)
            {
                offered = offered with { Products = race.Butchery };
                var verdict = census.Gate(new HuntPrey { Source = offered, Fogged = hunt.Fogged, Mental = hunt.InMentalState });
                if (verdict.Hold is { } held)
                {
                    holds.Add(held with { Source = offered });
                    continue;
                }
                offered = offered with { WeaponRange = verdict.WeaponRange };
            }
            rows.Add(offered);
        }
        return (Known(EquatableArray.From(rows)), holds);
    }

    /// <summary>
    /// The day-one interim food rule over the race row: safe prey (not in a mental state, its meat nourishing)
    /// that is no predator, and downed or docile (no manhunter chance on damage) and no bigger than a colonist,
    /// which flees rather than fights back, so a melee weapon or bare hands can run it down. Pawn.BodySize is the
    /// current life stage's bodySizeFactor (the prey's life stage index, the pawn row's) times the race's
    /// baseBodySize; a prey whose row or stage is not held is not known to be small, so not meleeable. The
    /// comparison is in float32, as the game's.
    /// </summary>
    private static bool Meleeable(C.Ref source, O.AcquisitionKind.Hunt row, Pawns pawns, AnimalRace race, AcquisitionSource prey)
    {
        if (row.InMentalState || race.Predator || !race.MeatNutritionPerUnit.TryGetValue(out var nutrition) || !(nutrition > 0))
        {
            return false;
        }
        if (prey.Downed)
        {
            return true;
        }
        if (!race.ManhunterOnDamage.TryGetValue(out var chance) || chance != 0)
        {
            return false;
        }
        if (!pawns.TryGetRow(source, out var state) || state.AnimalState is not O.PawnAnimalState.Animal { State: var animal } || !race.BodySize.TryGetValue(out var size))
        {
            return false;
        }
        var index = animal.LifeStageIndex;
        if (index < 0 || index >= race.LifeStages.Count)
        {
            return false;
        }
        return (float)race.LifeStages[index].BodySizeFactor * (float)size <= 1.0f;
    }

    /// <summary>Decodes native's raw hunt facts.</summary>
    private static HuntCensus HuntCensusOf(O.HuntCensus v, DefinitionCatalog catalog)
    {
        var hunters = new List<HuntHunter>();
        foreach (var h in v.Hunters)
        {
            hunters.Add(new HuntHunter
            {
                ID = h.Pawn.Value,
                Cell = h.Position,
                Downed = h.Downed,
                MentalState = h.InMentalState,
                HuntingActive = h.HuntingActive,
                RangedBlockingShield = h.RangedBlockingShield,
                // The weapon's verbs and projectile are the def rows' (Catalog.HuntWeapon).
                Weapon = catalog.HuntWeapon(h.WeaponDef ?? ""),
            });
        }
        return new HuntCensus { Hunters = hunters };
    }

    internal static HashSet<string> Set(IEnumerable<string> values) => new(values, StringComparer.Ordinal);

    internal static void ApplyAcquisition(O.ColonyFactsSnapshot v, Tables tables, DefinitionCatalog? catalog, ref ColonyProjection r)
    {
        var (sources, holds) = DecodeAcquisition(v, tables, catalog);
        r = r with { Acquisition = sources, HuntHolds = holds };
    }
}
