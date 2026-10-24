using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.Observation;

public static class ColonyGearReads
{
    /// <summary>
    /// Decodes the gear census against the frame's definitions: the catalog's apparel rows and stat table give every
    /// garment's layers, groups and stats, and the finished research its bill options need. Each bill option carries the
    /// arrival of its stuff (<see cref="StuffArrivalFacts"/>), counting the growing <paramref name="fields"/> of the
    /// <paramref name="crops"/>' plants. A frame without a catalog, finished research or stock census leaves the census
    /// unknown; a census whose rows are not the frame's colonists raises <see cref="RequiredFactException"/>. A pawn the
    /// loadout model refuses carries the cause in GearPawn.ModelRefusal.
    /// </summary>
    public static Fact<GearObservation> GearFacts(O.ColonyFactsSnapshot v, Tables tables, GearDefinitions defs, Fact<EquatableArray<FoodField>> fields, IReadOnlyList<PlanningDefinition> crops)
    {
        var gear = PlanningWindowReads.RequirePlanning(v).Gear;
        if (defs.Catalog is not { } catalog || !defs.Finished.TryGetValue(out var finished) || !ColonyDecodeReads.StockCensus(v).TryGetValue(out var stock))
        {
            return Unknown<GearObservation>();
        }
        if ((uint)gear.Pawns.Count != v.ColonistCount)
        {
            throw new RequiredFactException("gear census of every colonist");
        }
        var arrival = StuffArrivalFacts(v, tables, catalog, stock, fields, crops);
        var threat = RaidThreatFacts(v.Threat.Observed, catalog);
        var pawns = new List<GearPawn>(gear.Pawns.Count);
        foreach (var p in gear.Pawns)
        {
            var policy = ApparelPolicyFacts(p, catalog);
            var row = new GearPawn
            {
                Pawn = p.Pawn,
                Loadout = p.Token,
                Blocked = p.Blocked,
                Candidates = GearCandidateFacts(p, tables, catalog),
                Apparel = GearApparelFacts(p.Equipment, tables, catalog),
                Policy = policy,
            };
            try
            {
                row = row with { LoadoutModel = GearLoadoutModelFacts(defs, tables, v.OutdoorTemperatureC, p, policy, threat, arrival) };
            }
            catch (DomainException e)
            {
                row = row with { ModelRefusal = e.Message };
            }
            pawns.Add(row);
        }
        var result = new GearObservation
        {
            Pawns = pawns,
            Climate = gear.Climate,
            Stored = GearStorageFacts(gear.StoredApparel),
            Outfits = OutfitIds(v.Policies),
            KnowledgeBills = KnowledgeBillOptions(catalog, finished, gear.LockedBills),
        };
        return Known(result);
    }

    /// <summary>
    /// The bill options of apparel that only unfinished knowledge projects (Anomaly) keep from the colony (#3848): every apparel
    /// recipe of the catalog (<see cref="DefinitionCatalog.ApparelBills"/>) whose unfinished research is all knowledge projects, one
    /// option per stuff a bill of it takes, as the census writes a bill option (whole, at its best crafter's quality odds, #3850).
    /// The census lists only recipes available now, so these are what finishing those projects adds. A def no free colonist could
    /// craft offers nothing; one the <paramref name="locked"/> bills lack raises <see cref="RequiredFactException"/>.
    /// </summary>
    public static EquatableArray<GearOption> KnowledgeBillOptions(DefinitionCatalog catalog, IReadOnlySet<string> finished, IEnumerable<O.GearLockedBill> locked)
    {
        var crafters = locked.ToDictionary(b => b.DefName, StringComparer.Ordinal);
        var options = new List<GearOption>();
        foreach (var (def, research) in catalog.ApparelBills())
        {
            var open = research.Where(p => !finished.Contains(p)).ToArray();
            if (open.Length == 0 || !open.All(p => catalog.Research.TryGetValue(p, out var project) && project.KnowledgeCategory.Length > 0))
            {
                continue;
            }

            var bill = crafters.TryGetValue(def, out var b) ? b : throw new RequiredFactException("locked bill crafters");
            if (!bill.Craftable)
            {
                continue;
            }

            var stuffs = catalog.ApparelBillStuffs(def);
            foreach (var stuff in stuffs.Count == 0 && !catalog.ThingRow(def).MadeFromStuff ? [""] : stuffs)
            {
                // A craftable def with a quality always has a crafter; one without CompQuality lists none (GearLockedBill).
                var row = new O.GearLoadoutOption
                {
                    Id = $"bill:{def}/{stuff}", DefName = def, Stuff = stuff.Length == 0 ? null : stuff, Source = GearSource.Bill, Condition = 1,
                    Tainted = false, Locked = false, Crafters = bill.Crafters,
                    Quality = bill.Crafters.Count == 0 ? new O.ItemQuality.Qualityless() : new O.ItemQuality.Graded(D.QualityCategory.Normal),
                };
                if (GearLoadoutOption(row, catalog) is { } option)
                {
                    options.Add(option);
                }
            }
        }

        return EquatableArray.From(options);
    }

    /// <summary>
    /// When each gear stuff arrives (#3619): the stock census; the huntable animals of the frame's acquisition census at
    /// their butchery yields (none when that census is unknown); the unforbidden animal corpses of the food census (#3622),
    /// each its race's butchery yields after one butchering's work at speed 1 (none without the census, the butcher
    /// recipe's work or the race's row); and the growing <paramref name="fields"/>, each its standing crop (the growing
    /// cells times the plant's harvest yield) at its harvest lead. A field whose crop the <paramref name="crops"/> lack,
    /// or whose product or yield is unread, offers nothing; unknown fields offer nothing.
    /// </summary>
    internal static StuffArrival StuffArrivalFacts(O.ColonyFactsSnapshot v, Tables tables, DefinitionCatalog catalog, IReadOnlyDictionary<Resource, long> stock,
        Fact<EquatableArray<FoodField>> fields, IReadOnlyList<PlanningDefinition> crops)
    {
        var supply = new List<SupplyCandidate>();
        foreach (var s in AcquisitionReads.ColonyAcquisition(v, tables, catalog).ValueOrDefault)
        {
            if (ClothingDemandRules.HuntYieldCandidate(s) is { } hunt)
            {
                supply.Add(hunt);
            }
        }
        if (v.FoodSupply?.Outcome is { } food)
        {
            var butcher = catalog.ButcherWork();
            var races = catalog.AnimalRaces();
            foreach (var held in food.Stocks)
            {
                if (tables.Things.TryRow(held.Item, out var row) && row.Corpse && !row.Forbidden && !catalog.HumanlikeCorpse(row.Entity.DefName)
                    && races.Race(new Resource(catalog.CorpseRace(row.Entity.DefName)), out var race)
                    && ClothingDemandRules.CorpseYieldCandidate(row.Entity.Id, race.Butchery, butcher / GameTime.TicksPerDay) is { } corpse)
                {
                    supply.Add(corpse);
                }
            }
        }
        foreach (var f in fields.ValueOrDefault)
        {
            var crop = crops.FirstOrDefault(d => d.Name == f.Plan.Crop.Name);
            if (crop is not null && crop.HarvestedThingDef.TryGetValue(out var product) && crop.HarvestYield.TryGetValue(out var yield)
                && ClothingDemandRules.GrowingFieldCandidate(new Resource(product), f.Id, f.Plan.Sites.Cells * yield, f.RemainingGrowDays) is { } field)
            {
                supply.Add(field);
            }
        }
        return new StuffArrival(stock, supply);
    }

    /// <summary>
    /// The expected raid the gear planner values armor against (#3545): the composition of the observed raid points from the
    /// hostile factions, with the catalog's body coverage and the race's natural armor. Unknown without a catalog or hostile
    /// factions, or when the composition cannot be decoded.
    /// </summary>
    internal static Fact<RaidThreat> RaidThreatFacts(O.ThreatFacts threat, DefinitionCatalog? catalog)
    {
        if (catalog is null || threat.HostileFactions.Count == 0)
        {
            return Unknown<RaidThreat>();
        }
        var composition = RaidCompositionRules.Of(threat.RaidPoints, [.. threat.HostileFactions], catalog.RaidFactions());
        return composition.Pawns.IsKnown
            ? Known(new RaidThreat { Bins = composition.Bins, Coverage = catalog.BodyCoverage(), Natural = catalog.NaturalArmor() })
            : Unknown<RaidThreat>();
    }

    /// <summary>
    /// Maps one pawn's loadout-model inputs. The role is the apparel-policy role (with the model's traits), and
    /// unworn options are narrowed to the definitions that role's apparel policy permits, so the model never plans a
    /// garment DesiredApparelPolicy would forbid. A pawn with no apparel policy cannot wear apparel: unknown with no
    /// error. Any other pawn the model cannot be built for (no catalog or finished research, a garment the catalog
    /// lacks a row or stat values for, or a model outside PlanGearLoadout's bounds such as two worn garments on one
    /// slot) throws a <see cref="DomainException"/> naming the cause. Each option carries its <see cref="GearOption.Arrival"/>
    /// by <paramref name="arrival"/>.
    /// </summary>
    public static Fact<GearLoadoutInput> GearLoadoutModelFacts(GearDefinitions defs, Tables tables, double outdoor, O.GearLoadout p, ApparelPolicyState? role, Fact<RaidThreat> threat, StuffArrival arrival)
    {
        var m = p.LoadoutModel;
        if (role is null)
        {
            return Unknown<GearLoadoutInput>();
        }
        var catalog = defs.Catalog ?? throw new DomainException("gear loadout model needs a definition catalog");
        if (!defs.Finished.TryGetValue(out var finished))
        {
            throw new DomainException("gear loadout model needs the finished research");
        }
        var research = new List<string>(finished);
        research.Sort(StringComparer.Ordinal);
        var input = new GearLoadoutInput
        {
            Role = role.Role,
            Female = p.Gender == D.Gender.Female,
            Research = EquatableArray.From(research),
            Threat = threat,
            Ambient = outdoor,
            ComfortableMin = p.ComfortableMinC,
            ComfortableMax = p.ComfortableMaxC,
        };
        var traits = new List<PawnTrait>();
        foreach (var t in m.Traits)
        {
            traits.Add(RoundsWorkReads.ResolveTrait(catalog, new PawnTrait { Name = t.DefName, Degree = (int)t.Degree }));
        }
        var work = input.Role.Work with { Traits = Known(EquatableArray.From(traits)) };
        if (work.Work.TryGetValue(out var rows))
        {
            var resolved = new List<WorkPriority>(rows.Count);
            foreach (var entry in rows)
            {
                resolved.Add(catalog.ResolveWorkRow(entry));
            }
            work = work with { Work = Known(EquatableArray.From(resolved)) };
        }
        input = input with { Role = input.Role with { Work = work } };
        HashSet<string>? allowed = null;
        if (role.Definitions.Count > 0)
        {
            allowed = new HashSet<string>(ApparelPolicyRules.RoleApparelDefinitions(GearLoadoutRules.DeriveGearRole(role.Role), role), StringComparer.Ordinal);
        }
        var worn = new List<GearOption>();
        foreach (var x in m.Worn)
        {
            if (GearLoadoutOption(x, catalog) is { } option)
            {
                worn.Add(option);
            }
        }
        if (PrimaryWeaponOption(p.Equipment, tables, catalog) is { } primary)
        {
            worn.Add(primary);
        }
        var options = new List<GearOption>();
        foreach (var x in m.Options)
        {
            if (GearLoadoutOption(x, catalog) is { } option && (allowed is null || allowed.Contains(x.DefName)))
            {
                options.Add(option with { Arrival = arrival.Of(option) });
            }
        }
        input = input with { Worn = EquatableArray.From(worn), Options = EquatableArray.From(options) };
        input.Validate();
        return Known(input);
    }

    /// <summary>
    /// The pawn's equipped primary weapon as a worn <see cref="GearSlot.Primary"/> option (#3562), read from the equipment the
    /// census already carries: the exposure role is its only consumer (<c>HarmLedger.RoleOf</c>), so it carries the def and
    /// whether the def's verb is ranged. Null for an unarmed pawn; throws for a primary the frame's tables or the catalog cannot resolve.
    /// </summary>
    private static GearOption? PrimaryWeaponOption(O.PawnEquipment equipment, Tables tables, DefinitionCatalog catalog)
    {
        if (equipment.PrimaryId is not { } id)
        {
            return null;
        }
        var item = equipment.Equipped.FirstOrDefault(e => e.Thing.Id == id) ?? throw new DomainException($"primary weapon {id}: not among the pawn's equipment");
        var def = tables.Entity(item.Thing)?.DefName ?? throw new DomainException($"primary weapon {id}: the frame holds no row for it");
        var (quality, qualityless) = QualityOf(item.Quality);
        return new GearOption
        {
            ID = id,
            Definition = new Resource(def),
            Stuff = new Resource(""),
            Quality = quality,
            Qualityless = qualityless,
            Slot = GearSlot.Primary,
            Source = GearSource.Worn,
            Condition = item.ConditionFraction,
            Ranged = catalog.WeaponOf(def).Ranged,
        };
    }

    /// <summary>
    /// An item's quality as the loadout model rates it: its QualityCategory index, and a qualityless item (no CompQuality)
    /// rated at Normal and flagged, since it counts toward no royal title's minimum apparel quality (#3841).
    /// </summary>
    private static (int Quality, bool Qualityless) QualityOf(O.ItemQuality quality) =>
        quality is O.ItemQuality.Graded graded ? ((int)graded.Category, false) : ((int)D.QualityCategory.Normal, true);

    /// <summary>
    /// Maps one native option against the catalog: its layers and groups from the def's apparel row, its stats from
    /// the stat table for its (def, stuff), its move speed, psychic and shield facts from the row, a crafted bill's quality odds
    /// from its crafters. Null for a garment the model's slots do not name; throws for a def the catalog lacks or does not know
    /// as apparel, a pair without stat values, or a bill with a quality and no crafter.
    /// </summary>
    private static GearOption? GearLoadoutOption(O.GearLoadoutOption x, DefinitionCatalog catalog)
    {
        var def = x.DefName;
        var stuff = x.Stuff ?? "";
        if (ApparelShapeOf(catalog, def, x.Id) is not { } shape)
        {
            return null;
        }
        var source = x.Source;
        var (quality, qualityless) = QualityOf(x.Quality);
        EquatableArray<string> research = default;
        EquatableArray<Amount> ingredients = default;
        if (source == GearSource.Bill)
        {
            if (catalog.ApparelBill(def, stuff) is not var (needs, costs))
            {
                throw new DomainException($"gear option {x.Id}: no recipe makes {def}");
            }
            var amounts = new Amount[costs.Count];
            for (var i = 0; i < amounts.Length; i++)
            {
                amounts[i] = new Amount(new Resource(costs[i].Def), costs[i].Units);
            }
            research = EquatableArray.From(needs);
            ingredients = EquatableArray.From(amounts);
        }
        return new GearOption
        {
            ID = x.Id,
            Definition = new Resource(def),
            Stuff = new Resource(stuff),
            Quality = quality,
            Qualityless = qualityless,
            // A crafted garment comes out at its best crafter's quality odds (#3850); a bill names at least one crafter.
            QualityOdds = source == GearSource.Bill && !qualityless ? EquatableArray.From(CraftedQualityRules.BestOdds(x.Crafters)) : [],
            Slot = shape.Slot,
            Layers = shape.Layers,
            Groups = shape.Groups,
            Source = source,
            Condition = x.Condition,
            WearPerDay = shape.WearPerDay,
            MoveSpeed = shape.MoveSpeed,
            Tainted = x.Tainted,
            SlaveOnly = shape.SlaveOnly,
            Locked = x.Locked,
            Shield = shape.Shield,
            Psychic = shape.Psychic,
            Smokepop = shape.Smokepop,
            CareIfDamaged = shape.CareIfDamaged,
            CountsAsClothingForNudity = shape.CountsAsClothing,
            Research = research,
            Ingredients = ingredients,
            Sharp = GearDefinitionsReads.OptionStat(catalog, def, stuff, CatalogStats.ArmorSharp, false),
            Blunt = GearDefinitionsReads.OptionStat(catalog, def, stuff, CatalogStats.ArmorBlunt, false),
            ArmorHeat = GearDefinitionsReads.OptionStat(catalog, def, stuff, CatalogStats.ArmorHeat, true),
            Cold = GearDefinitionsReads.OptionStat(catalog, def, stuff, CatalogStats.InsulationCold, false),
            Heat = GearDefinitionsReads.OptionStat(catalog, def, stuff, CatalogStats.InsulationHeat, false),
            Cost = GearDefinitionsReads.OptionStat(catalog, def, stuff, CatalogStats.MarketValue, true),
            MaxHitPoints = GearDefinitionsReads.OptionStat(catalog, def, stuff, CatalogStats.MaxHitPoints, true),
        };
    }

    /// <summary>The stuff-independent facts of an apparel def an option carries; null for a def no model slot names.</summary>
    private sealed record ApparelShape(GearSlot Slot, EquatableArray<string> Layers, EquatableArray<string> Groups, double WearPerDay, double MoveSpeed, bool SlaveOnly, bool Shield, bool Psychic, bool Smokepop, bool CareIfDamaged, bool CountsAsClothing);

    // Every observation reads every option of every pawn, and the catalog's defs do not change under it, so each
    // def's shape is worked out once per catalog.
    private static readonly ConditionalWeakTable<DefinitionCatalog, ConcurrentDictionary<string, ApparelShape?>> Shapes = new();

    private static ApparelShape? ApparelShapeOf(DefinitionCatalog catalog, string def, string id)
    {
        var shapes = Shapes.GetValue(catalog, static _ => new ConcurrentDictionary<string, ApparelShape?>(StringComparer.Ordinal));
        if (shapes.TryGetValue(def, out var shape))
        {
            return shape;
        }
        var (row, apparel) = GearDefinitionsReads.ApparelRow(catalog, def);
        if (apparel is null)
        {
            throw new DomainException($"gear option {id}: no apparel row for def {def}");
        }
        var layers = GearDefinitionsReads.Distinct((apparel.layers ?? []).Select(l => l.defName));
        var groups = GearDefinitionsReads.Distinct((apparel.bodyPartGroups ?? []).Select(g => g.defName));
        shape = GearSlotOf(layers, groups, out var slot)
            ? new ApparelShape(slot, layers, groups, apparel.wearPerDay, GearDefinitionsReads.StatOffset(row, GearDefinitionsReads.StatMoveSpeed), apparel.slaveApparel, GearDefinitionsReads.HasShield(row), GearDefinitionsReads.StatOffset(row, GearDefinitionsReads.StatPsychic) < 0, catalog.SmokePop(def),
                row?.useHitPoints == true && apparel.careIfDamaged, apparel.countsAsClothingForNudity)
            : null;
        shapes.TryAdd(def, shape);
        return shape;
    }

    /// <summary>
    /// The model slot a garment's native apparel layers and body-part groups fill: belt, headgear (overhead or eye
    /// cover), outer (any shell layer, so plate armour's middle+shell is outer), skin torso or legs, then middle
    /// torso (a flak vest).
    /// </summary>
    private static bool GearSlotOf(EquatableArray<string> layers, EquatableArray<string> groups, out GearSlot slot)
    {
        if (layers.Contains("Belt"))
        {
            slot = GearSlot.Belt;
        }
        else if (layers.Contains("Overhead") || layers.Contains("EyeCover"))
        {
            slot = GearSlot.Headgear;
        }
        else if (layers.Contains("Shell"))
        {
            slot = GearSlot.Outer;
        }
        else if (layers.Contains("OnSkin") && groups.Contains("Torso"))
        {
            slot = GearSlot.SkinTorso;
        }
        else if (layers.Contains("OnSkin") && groups.Contains("Legs"))
        {
            slot = GearSlot.SkinLegs;
        }
        else if (layers.Contains("Middle"))
        {
            slot = GearSlot.MiddleTorso;
        }
        else
        {
            slot = GearSlot.Unset;
            return false;
        }
        return true;
    }

    /// <summary>
    /// The stored apparel stock. A qualityless garment is stocked as a Normal one: it meets the stores' quality floor
    /// (<see cref="GearStock.Serviceable"/>) the way the loadout model rates it at Normal.
    /// </summary>
    public static EquatableArray<GearStock> GearStorageFacts(EquatableArray<O.GearStock> stored)
    {
        var rows = new List<GearStock>(stored.Count);
        foreach (var row in stored)
        {
            rows.Add(new GearStock { Definition = new Resource(row.DefName), Stuff = new Resource(row.Stuff ?? ""), Quality = QualityOf(row.Quality).Quality, HPBand = row.HpBand, Count = row.Count });
        }
        return EquatableArray.From(rows);
    }

    /// <summary>
    /// Decodes one loadout's loose replacement candidates: the apparel a wear order (gear_replace, native
    /// GiveJobIntent Wear) can target. A def the catalog does not know as apparel is skipped: the wear operation
    /// looks its target up among loose apparel only, so a plan proposing one was refused on every attempt and held
    /// its development slot for the run. A candidate whose things table row the frame lacks, or whose def the
    /// catalog lacks, raises <see cref="RequiredFactException"/>.
    /// </summary>
    public static EquatableArray<GearCandidate> GearCandidateFacts(O.GearLoadout p, Tables tables, DefinitionCatalog catalog)
    {
        var candidates = new List<GearCandidate>();
        foreach (var c in p.Candidates)
        {
            var def = GearRowDef(tables, catalog, c.Item.Thing);
            if (GearDefinitionsReads.ApparelRow(catalog, def).Apparel is null)
            {
                continue;
            }
            candidates.Add(new GearCandidate(c.Item.Thing.Id, c.Gain, new Resource(def)));
        }
        return EquatableArray.From(candidates);
    }

    /// <summary>
    /// Decodes one pawn's worn apparel from the loadout's equipment read: each garment's definition, condition fraction
    /// (1 without hit points) and body-part groups. A garment whose things table row the frame lacks, or whose def the
    /// catalog does not know as apparel, raises <see cref="RequiredFactException"/>.
    /// </summary>
    public static EquatableArray<GearApparel> GearApparelFacts(O.PawnEquipment equipment, Tables tables, DefinitionCatalog catalog)
    {
        var apparel = new List<GearApparel>(equipment.Apparel.Count);
        foreach (var item in equipment.Apparel)
        {
            var def = GearRowDef(tables, catalog, item.Thing);
            var worn = GearDefinitionsReads.ApparelRow(catalog, def).Apparel ?? throw new RequiredFactException($"apparel row of worn {def}");
            apparel.Add(new GearApparel { Definition = new Resource(def), Condition = item.ConditionFraction, Groups = GearDefinitionsReads.Distinct((worn.bodyPartGroups ?? []).Select(g => g.defName)) });
        }
        return EquatableArray.From(apparel);
    }

    /// <summary>The def of a gear item by its frame row, which the catalog must know.</summary>
    private static string GearRowDef(Tables tables, DefinitionCatalog catalog, C.Ref thing)
    {
        var def = tables.Entity(thing)?.DefName ?? throw new RequiredFactException($"frame row of gear item {thing.Id}");
        return catalog.FindThing(def) is null ? throw new RequiredFactException($"catalog row of gear def {def}") : def;
    }

    internal static Fact<GearObservation> RoundsGear(Fact<GearObservation> v, EmergencyFacts emergency)
    {
        if (!v.TryGetValue(out var gear) || !emergency.AllSeen || gear.Pawns.Count != emergency.Colonists.Count)
        {
            return Unknown<GearObservation>();
        }
        var ids = new HashSet<PawnId>();
        foreach (var p in emergency.Colonists)
        {
            if (!ids.Add(p.Id))
            {
                return Unknown<GearObservation>();
            }
        }
        foreach (var p in gear.Pawns)
        {
            if (!ids.Remove(p.Pawn))
            {
                return Unknown<GearObservation>();
            }
        }
        return v;
    }

    /// <summary>Every outfit policy's load id.</summary>
    public static EquatableArray<string> OutfitIds(O.Policies p)
    {
        var ids = new List<string>();
        foreach (var row in p.Outfit)
        {
            ids.Add(row.Id);
        }
        return EquatableArray.From(ids);
    }

    internal static ColonyProjection StampGearCreepjoinersOn(ColonyProjection p, O.PawnSnapshot? pawns, CreepJoinerDownsides downsides) =>
        p.Facts.Gear.TryGetValue(out var gear) ? p with { Facts = p.Facts with { Gear = Known(StampGearCreepjoiners(gear, pawns, downsides)) } } : p;

    /// <summary>
    /// Flags each census colonist that is a creepjoiner with an unrevealed downside
    /// (GearRoleInput.UnrevealedCreepjoiner), from pawn rows, on both the apparel policy state and the loadout model
    /// so every reader derives the same constrained role. A pawn with no row is left unflagged; the census is
    /// copied, never mutated in place.
    /// </summary>
    public static GearObservation StampGearCreepjoiners(GearObservation gear, O.PawnSnapshot? pawns, CreepJoinerDownsides downsides)
    {
        if (pawns is null)
        {
            return gear;
        }
        var rows = new Dictionary<string, O.PawnState>(pawns.Pawns.Count, StringComparer.Ordinal);
        foreach (var row in pawns.Pawns)
        {
            rows[row.Pawn.Id ?? ""] = row;
        }
        var stamped = new List<GearPawn>(gear.Pawns);
        for (var i = 0; i < stamped.Count; i++)
        {
            var g = stamped[i];
            if (!rows.TryGetValue(g.Pawn.Value, out var row) || !downsides.Unrevealed(CreepJoinerFacts.CreepJoinerPawn(row)))
            {
                continue;
            }
            if (g.Policy is { } state)
            {
                g = g with { Policy = state with { Role = state.Role with { UnrevealedCreepjoiner = true } } };
            }
            if (g.LoadoutModel.TryGetValue(out var model))
            {
                g = g with { LoadoutModel = Known(model with { Role = model.Role with { UnrevealedCreepjoiner = true } }) };
            }
            stamped[i] = g;
        }
        return gear with { Pawns = stamped };
    }

    /// <summary>
    /// The pawn's outfit census; null for a pawn without an outfit.
    /// </summary>
    public static ApparelPolicyState? ApparelPolicyFacts(O.GearLoadout p, DefinitionCatalog catalog)
    {
        var v = p.ApparelPolicy;
        if (v is null)
        {
            return null;
        }
        var s = new ApparelPolicyState
        {
            Token = v.Token,
            PawnName = v.PawnName,
            PolicyID = v.PolicyId,
            Required = v.RequiredDefs,
            Nude = v.Nude,
            ExcludesTainted = v.ExcludesTainted,
            Role = new GearRoleInput { Child = v.Child, Slave = v.Slave, IncapableOfViolence = v.IncapableOfViolence, DraftedSquad = v.Drafted },
            Current = new ApparelPolicySpec(default, "", v.Name, v.AllowedDefs, v.MinHitPoints, v.MaxHitPoints, v.MinQuality, v.MaxQuality),
        };
        s = s with { Definitions = GearDefinitionsReads.ApparelDefinitions(catalog, GearDefinitionsReads.WearerOf(p)) };
        foreach (var a in p.Equipment.Apparel)
        {
            s = s with { Overrides = s.Overrides || a.Forced || a.Locked };
        }
        var priorities = new List<WorkPriority>();
        foreach (var w in v.Work)
        {
            if (ColonyDecodeReads.WireParseOpen(w.DefName, WorkType.Unrecognized) == WorkType.Unrecognized)
            {
                continue;
            }
            priorities.Add(new WorkPriority { Work = ColonyDecodeReads.WireParse<WorkType>(w.DefName), Priority = (int)w.Priority, Disabled = w.Disabled });
        }
        var work = s.Role.Work with { Work = Known(EquatableArray.From(priorities)) };
        // An older producer omits skills; the role then splits on priorities alone.
        if (v.Skills.Count > 0)
        {
            var skills = new List<WorkSkill>();
            foreach (var sk in v.Skills)
            {
                skills.Add(new WorkSkill { Learning = sk.Learning, Name = sk.DefName, Level = (int)sk.Level, Passion = RoundsWorkReads.SkillPassion(sk.Passion), Disabled = sk.Disabled });
            }
            work = work with { Skills = Known(EquatableArray.From(skills)) };
        }
        return s with { Role = s.Role with { Work = work } };
    }

    /// <summary>
    /// The garments the colonists' outfits allow that cover a core body-part group, each with its first available
    /// recipe's ingredient slots (PlanClothingRunway prices the replacement from them). A definition without a known
    /// available recipe is left out; an unread census or catalog yields none.
    /// </summary>
    public static IReadOnlyList<ClothingGarment> ClothingGarments(GearDefinitions defs, Fact<GearObservation> gear)
    {
        if (!gear.TryGetValue(out var observed) || !defs.Finished.TryGetValue(out var finished) || defs.Catalog is not { } catalog)
        {
            return [];
        }
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in observed.Pawns)
        {
            if (p.Policy is { } state)
            {
                foreach (var name in ApparelPolicyRules.RoleApparelDefinitions(GearLoadoutRules.DeriveGearRole(state.Role), state))
                {
                    allowed.Add(name);
                }
            }
        }
        var output = new List<ClothingGarment>();
        foreach (var name in allowed)
        {
            var apparel = GearDefinitionsReads.ApparelRow(catalog, name).Apparel;
            var groups = (apparel?.bodyPartGroups ?? []).Select(group => group.defName).ToList();
            if (!GearRules.GearCoreGroups.Any(groups.Contains))
            {
                continue;
            }
            IReadOnlyList<RecipeHost> hosts;
            try
            {
                hosts = catalog.RecipeHosts(name, finished);
            }
            catch (DomainException)
            {
                return [];
            }
            foreach (var host in hosts)
            {
                if (host.Available)
                {
                    output.Add(new ClothingGarment { Definition = new Resource(name), Groups = EquatableArray.From(groups), Slots = host.Ingredients });
                    break;
                }
            }
        }
        output.Sort((a, b) => string.CompareOrdinal(a.Definition.Value, b.Definition.Value));
        return output;
    }
}
