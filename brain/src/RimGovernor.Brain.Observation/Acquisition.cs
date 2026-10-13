namespace RimGovernor.Brain.Observation;

public static partial class ObservationPackage
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
        if (HasIssue(v.Issues, "acquisition") || !Headed(tables, v.Acquisition, row => row.Source))
        {
            return unknown;
        }
        AnimalRaceCatalog races;
        HuntCensus census;
        DefinitionCatalog catalog;
        try
        {
            catalog = RequireCatalog(definitions);
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
            var offered = new AcquisitionSource
            {
                Id = row.Source?.Id ?? "",
                Resource = row.Resource ?? "",
                Token = row.SourceSnapshot?.Token ?? "",
                Definition = source?.DefName ?? "",
                Cell = CellOf(source?.Position),
                Hunt = row.Hunt.GetValueOrDefault(),
                Food = row.Food.GetValueOrDefault(),
                Designated = row.Designated.GetValueOrDefault(),
                Yield = row.Yield.GetValueOrDefault(),
                HerdSize = (int)row.HerdSize.GetValueOrDefault(),
                Downed = row.Downed.GetValueOrDefault(),
                Sleeping = row.Sleeping.GetValueOrDefault(),
                Pest = race.Pest,
                DesignatedTick = new Tick((long)row.DesignatedTick.GetValueOrDefault()),
                Taken = row.Taken.GetValueOrDefault(),
                Growth = row.Growth.GetValueOrDefault(),
                Plantation = row.Plantation.GetValueOrDefault(),
            };
            if (offered.Hunt)
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
                    if (!race.MeatNutritionPerUnit.TryGetValue(out var perUnit) || row.MeatAmount is null)
                    {
                        return unknown;
                    }
                    offered = offered with { NutritionYield = Math.Max(0, row.MeatAmount.GetValueOrDefault()) * perUnit };
                }
                offered = offered with { MeleeOnly = Meleeable(row, tables.Pawns, race, offered) };
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
                        (nutrition, shown) = catalog.ShownStatValue((row.Resource ?? ""), "", StatNutrition);
                    }
                    catch (DomainException)
                    {
                        return unknown;
                    }
                    if (shown)
                    {
                        offered = offered with { NutritionYield = row.Yield.GetValueOrDefault() * (float)nutrition };
                    }
                }
            }
            if (offered.Hunt)
            {
                offered = offered with { Products = race.Butchery };
                var verdict = census.Gate(new HuntPrey { Source = offered, Fogged = row.Fogged.GetValueOrDefault(), Mental = row.InMentalState.GetValueOrDefault() });
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
    private static bool Meleeable(O.AcquisitionFacts row, Pawns pawns, AnimalRace race, AcquisitionSource prey)
    {
        if (row.InMentalState.GetValueOrDefault() || race.Predator || !race.MeatNutritionPerUnit.TryGetValue(out var nutrition) || !(nutrition > 0))
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
        if (!pawns.TryGetRow(row.Source, out var state) || state.AnimalState is null || !race.BodySize.TryGetValue(out var size))
        {
            return false;
        }
        if (state.AnimalState.LifeStageIndex is null)
        {
            return false;
        }
        var index = state.AnimalState.LifeStageIndex.GetValueOrDefault();
        if (index < 0 || index >= race.LifeStages.Count)
        {
            return false;
        }
        return (float)race.LifeStages[index].BodySizeFactor * (float)size <= 1.0f;
    }

    /// <summary>Decodes native's raw hunt facts.</summary>
    private static HuntCensus HuntCensusOf(O.HuntCensus? v, DefinitionCatalog catalog)
    {
        var benches = new List<HuntBench>();
        var hunters = new List<HuntHunter>();
        foreach (var b in v?.Benches ?? [])
        {
            var bills = new List<HuntBill>();
            foreach (var bill in b.Bills)
            {
                var repeat = bill.RepeatMode.GetValueOrDefault() switch
                {
                    Ops.RepeatMode.Forever => HuntRepeat.Forever,
                    Ops.RepeatMode.Count => HuntRepeat.Count,
                    Ops.RepeatMode.Target => HuntRepeat.Target,
                    _ => HuntRepeat.Unset,
                };
                bills.Add(new HuntBill
                {
                    Suspended = bill.Suspended.GetValueOrDefault(),
                    Paused = bill.Paused.GetValueOrDefault(),
                    Count = (int)bill.RepeatCount.GetValueOrDefault(),
                    Target = (int)bill.TargetCount.GetValueOrDefault(),
                    AllowedCorpses = Set(bill.AllowedCorpses),
                    Repeat = repeat,
                    Product = bill.ProductCount is not null ? (int)bill.ProductCount.GetValueOrDefault() : null,
                });
            }
            benches.Add(new HuntBench { ID = b.BenchId ?? "", Usable = b.Usable.GetValueOrDefault(), Bills = bills });
        }
        foreach (var h in v?.Hunters ?? [])
        {
            var safe = new HashSet<string>(StringComparer.Ordinal);
            var unsafePrey = new HashSet<string>(StringComparer.Ordinal);
            var skipped = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in h.Routes)
            {
                if (r.Skipped.GetValueOrDefault())
                {
                    skipped.Add((r.PreyId ?? ""));
                }
                else if (r.Safe.GetValueOrDefault())
                {
                    safe.Add((r.PreyId ?? ""));
                }
                else
                {
                    unsafePrey.Add((r.PreyId ?? ""));
                }
            }
            hunters.Add(new HuntHunter
            {
                ID = h.PawnId ?? "",
                Cell = CellOf(h.Position),
                Downed = h.Downed.GetValueOrDefault(),
                MentalState = h.InMentalState.GetValueOrDefault(),
                Drafted = h.Drafted.GetValueOrDefault(),
                HuntingPriority = (int)h.HuntingPriority.GetValueOrDefault(),
                HuntingActive = h.HuntingActive.GetValueOrDefault(),
                HuntingDisabled = h.HuntingDisabled.GetValueOrDefault(),
                CookingActive = h.CookingActive.GetValueOrDefault(),
                HasHuntingWeapon = h.HasHuntingWeapon.GetValueOrDefault(),
                RangedBlockingShield = h.RangedBlockingShield.GetValueOrDefault(),
                ReachableBenches = Set(h.ReachableBenches),
                RouteSafePrey = safe,
                RouteUnsafePrey = unsafePrey,
                RouteSkippedPrey = skipped,
                // The weapon's verbs and projectile are the def rows' (Catalog.HuntWeapon).
                Weapon = catalog.HuntWeapon((h.WeaponDef ?? "")),
            });
        }
        return new HuntCensus { Benches = benches, Hunters = hunters };
    }

    internal static HashSet<string> Set(IEnumerable<string> values) => new(values, StringComparer.Ordinal);

    private static void ApplyAcquisition(O.ColonyFactsSnapshot v, Tables tables, DefinitionCatalog? catalog, ref ColonyProjection r)
    {
        var (sources, holds) = DecodeAcquisition(v, tables, catalog);
        r = r with { Acquisition = sources, HuntHolds = holds };
        if (!HasIssue(v.Issues, "pending_food_nutrition"))
        {
            r = r with { PendingFoodNutrition = Optional(v.PendingFoodNutrition is not null, v.PendingFoodNutrition.GetValueOrDefault()) };
        }
        if (!HasIssue(v.Issues, "pending_hunts") && v.PendingHunts is not null)
        {
            r = r with { PendingHunts = Known((int)v.PendingHunts.GetValueOrDefault()) };
        }
        if (!HasIssue(v.Issues, "pending_wood_units"))
        {
            r = r with { PendingWoodUnits = Optional(v.PendingWoodUnits is not null, v.PendingWoodUnits.GetValueOrDefault()) };
        }
    }
}
