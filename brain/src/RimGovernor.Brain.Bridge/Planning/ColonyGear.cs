using RimGovernor.Brain.Facts;
using RimGovernor.Brain.GameAccess;
using DefinitionCatalog = RimGovernor.Brain.Bridge.DefinitionCatalog;
using CatalogStats = RimGovernor.Brain.Bridge.CatalogStats;
using PawnId = RimGovernor.Brain.Domain.PawnId;
using GearSource = RimGovernor.Brain.GameFacts.Observations.GearSource;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.Bridge;

public static class ColonyGearReads
{
    /// <summary>
    /// Decodes the gear census against the frame's definitions: the catalog's apparel rows and stat table give every
    /// garment's layers, groups and stats, and the finished research its bill options need. Each bill option carries the
    /// arrival of its stuff (<see cref="StuffArrivalFacts"/>), counting the complete growing <paramref name="fields"/>.
    /// A census whose rows are not the frame's colonists raises
    /// <see cref="RequiredFactException"/>. A pawn the loadout model refuses is <see cref="GearModel.Refused"/> with the cause.
    /// </summary>
    public static GearObservation GearFacts(RimGovernor.Brain.GameFacts.Observations.ColonyFactsSnapshot v, Tables tables, GearDefinitions defs, IReadOnlyList<FoodField> fields)
    {
        var gear = v.Gear ?? throw new BridgeContractException("missing total gear census");
        var catalog = defs.Catalog;
        var finished = defs.Finished;
        var stock = ResourceStockRules.Count(v.Resources);
        if ((uint)gear.Pawns.Count != v.ColonistCount)
        {
            throw new RequiredFactException("gear census of every colonist");
        }
        var emergency = tables.Emergency ?? throw new BridgeContractException("missing total emergency census for gear");
        if (!emergency.AllSeen || gear.Pawns.Count != emergency.Colonists.Count)
        {
            throw new RequiredFactException("gear census of every emergency colonist");
        }
        var identities = new HashSet<PawnId>();
        foreach (var pawn in emergency.Colonists)
        {
            if (!identities.Add(pawn.Id)) throw new RequiredFactException("unique emergency colonists");
        }
        foreach (var pawn in gear.Pawns)
        {
            if (!identities.Remove(pawn.Pawn)) throw new RequiredFactException("gear census of every emergency colonist");
        }
        var arrival = StuffArrivalFacts(v, tables, catalog, stock, fields);
        var threat = RaidThreatFacts(v.Threat.Observed, catalog);
        var pawns = new List<GearPawn>(gear.Pawns.Count);
        foreach (var p in gear.Pawns)
        {
            if (!tables.Pawns.TryGetValue(p.Pawn.Value, out var pawnRow))
            {
                throw new RequiredFactException($"pawn row for gear role: {p.Pawn.Value}");
            }
            var policy = ApparelPolicyFacts(p, catalog, pawnRow);
            GearModel model;
            try
            {
                model = GearLoadoutModelFacts(defs, tables, v.OutdoorTemperatureC, p, policy, threat, arrival);
            }
            catch (DomainException e)
            {
                model = new GearModel.Refused(e.Message);
            }
            pawns.Add(new GearPawn
            {
                Observed = p,
                Candidates = GearCandidateFacts(p, tables, catalog),
                Apparel = GearApparelFacts(pawnRow.Equipment, tables, catalog),
                Policy = policy,
                LoadoutModel = model,
            });
        }
        var result = new GearObservation
        {
            Pawns = pawns,
            Climate = gear.Climate,
            Threat = threat,
            Stored = gear.StoredApparel,
            Outfits = OutfitIds(v.Policies),
            KnowledgeBills = KnowledgeBillOptions(catalog, finished, gear.LockedBills),
        };
        return result;
    }

    /// <summary>
    /// The bill options of apparel that only unfinished knowledge projects (Anomaly) keep from the colony (#3848): every apparel
    /// recipe of the catalog (<see cref="DefinitionCatalog.ApparelBills"/>) whose unfinished research is all knowledge projects, one
    /// option per stuff a bill of it takes, as the census writes a bill option (whole, at its best crafter's quality odds, #3850).
    /// The census lists only recipes available now, so these are what finishing those projects adds. A def no free colonist could
    /// craft offers nothing; one the <paramref name="locked"/> bills lack raises <see cref="RequiredFactException"/>.
    /// </summary>
    public static EquatableArray<GearOption> KnowledgeBillOptions(DefinitionCatalog catalog, IReadOnlySet<string> finished, IEnumerable<RimGovernor.Brain.GameFacts.Observations.GearLockedBill> locked)
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
                var origin = new GearOptionOrigin.Catalog(bill, stuff, bill.Crafters.Count == 0);
                if (GearLoadoutOption(origin, catalog) is { } option)
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
    /// cells times the plant's harvest yield) at its harvest lead. A fallow or dormant field offers no harvest arrival.
    /// </summary>
    internal static StuffArrival StuffArrivalFacts(RimGovernor.Brain.GameFacts.Observations.ColonyFactsSnapshot v, Tables tables, DefinitionCatalog catalog, IReadOnlyDictionary<Resource, long> stock,
        IReadOnlyList<FoodField> fields)
    {
        var supply = new List<SupplyCandidate>();
        foreach (var s in AcquisitionReads.ColonyAcquisition(v, tables, catalog))
        {
            if (ClothingDemandRules.HuntYieldCandidate(s) is { } hunt)
            {
                supply.Add(hunt);
            }
        }
        var butcher = catalog.ButcherWork();
        var races = catalog.AnimalRaces();
        foreach (var held in v.FoodSupply.Stocks)
        {
            if (tables.Things.TryRow(new RimGovernor.Brain.GameFacts.Common.Ref { Id = held.Id }, out var row) && row.Corpse && !row.Forbidden && !catalog.HumanlikeCorpse(row.Entity.DefName)
                && races.Race(new Resource(catalog.CorpseRace(row.Entity.DefName)), out var race)
                && ClothingDemandRules.CorpseYieldCandidate(row.Entity.Id, race.Butchery, butcher / GameTime.TicksPerDay) is { } corpse)
            {
                supply.Add(corpse);
            }
        }
        foreach (var f in fields)
        {
            var crop = f.Plan.Crop;
            var product = crop.Harvests;
            var yield = crop.UnitsPerCell;
            if (ClothingDemandRules.GrowingFieldCandidate(product, f.Id, f.Plan.Sites.Cells * yield, f.RemainingGrowDays) is { } field)
            {
                supply.Add(field);
            }
        }
        return new StuffArrival(stock, supply);
    }

    /// <summary>
    /// The expected raid the gear planner values armor against (#3545): the composition of the observed raid points from the
    /// hostile factions, with the catalog's body coverage and the race's natural armor. Uncomposed when no hostile faction
    /// raids or the faction defs support no composition.
    /// </summary>
    internal static RaidThreat RaidThreatFacts(RimGovernor.Brain.GameFacts.Observations.ThreatFacts threat, DefinitionCatalog catalog)
    {
        var composition = RaidCompositionRules.Of(threat.RaidPoints, [.. threat.HostileFactions], catalog.RaidFactions());
        return composition is RaidComposition.Expected expected
            ? new RaidThreat.Composed(expected.Bins, catalog.BodyCoverage(), catalog.NaturalArmor())
            : new RaidThreat.Uncomposed();
    }

    /// <summary>
    /// Maps one pawn's loadout-model inputs. The role is the apparel-policy role (with the model's traits), and
    /// unworn options are narrowed to the definitions that role's apparel policy permits, so the model never plans a
    /// garment DesiredApparelPolicy would forbid. A pawn with no apparel policy cannot wear apparel: <see cref="GearModel.NoOutfit"/>. Any other pawn the model cannot be built for (no catalog or finished research, a garment the catalog
    /// lacks a row or stat values for, or a model outside PlanGearLoadout's bounds such as two worn garments on one
    /// slot) throws a <see cref="DomainException"/> naming the cause. Each option carries its <see cref="GearOption.Arrival"/>
    /// by <paramref name="arrival"/>.
    /// </summary>
    public static GearModel GearLoadoutModelFacts(GearDefinitions defs, Tables tables, double outdoor, RimGovernor.Brain.GameFacts.Observations.GearLoadout p, ApparelPolicyState? role, RaidThreat threat, StuffArrival arrival)
    {
        var m = p.LoadoutModel;
        if (role is null)
        {
            return new GearModel.NoOutfit();
        }
        var catalog = defs.Catalog;
        var research = new List<string>(defs.Finished);
        research.Sort(StringComparer.Ordinal);
        var input = new GearLoadoutInput
        {
            Role = role.Role,
            Observed = p,
            Body = role.Role.Pawn.Settings.Body,
            Research = EquatableArray.From(research),
            Threat = threat,
            Ambient = outdoor,
        };
        var traits = new List<PawnTrait>();
        foreach (var t in role.Role.Pawn.Biography.Traits)
        {
            traits.Add(WorkReads.ResolveTrait(catalog, t));
        }
        input = input with { Role = input.Role with { Traits = EquatableArray.From(traits), Work = EquatableArray.From(input.Role.Work.Select(catalog.ResolveWorkRow)) } };
        HashSet<string>? allowed = null;
        if (role.Definitions.Count > 0)
        {
            allowed = new HashSet<string>(ApparelPolicyRules.RoleApparelDefinitions(GearLoadoutRules.DeriveGearRole(role.Role), role), StringComparer.Ordinal);
        }
        var worn = new List<GearOption>();
        foreach (var x in m.Worn)
        {
            if (GearLoadoutOption(new GearOptionOrigin.Observed(x), catalog) is { } option)
            {
                worn.Add(option);
            }
        }
        if (PrimaryWeaponOption(role.Role.Pawn.Equipment, tables, catalog) is { } primary)
        {
            worn.Add(primary);
        }
        var options = new List<GearOption>();
        foreach (var x in m.Options)
        {
            if (GearLoadoutOption(new GearOptionOrigin.Observed(x), catalog) is { } option && (allowed is null || allowed.Contains(x.DefName)))
            {
                options.Add(option with { Arrival = arrival.Of(option) });
            }
        }
        input = input with { Worn = EquatableArray.From(worn), Options = EquatableArray.From(options) };
        input.Validate();
        return new GearModel.Modeled(input);
    }

    /// <summary>
    /// The pawn's equipped primary weapon as a worn <see cref="GearSlot.Primary"/> option (#3562), read from the equipment the
    /// census already carries: the exposure role is its only consumer (<c>HarmLedger.RoleOf</c>), so it carries the def and
    /// whether the def's verb is ranged. Null for an unarmed pawn; throws for a primary the frame's tables or the catalog cannot resolve.
    /// </summary>
    private static GearOption? PrimaryWeaponOption(RimGovernor.Brain.GameFacts.Observations.PawnEquipment equipment, Tables tables, DefinitionCatalog catalog)
    {
        if (equipment.PrimaryId is not { } id)
        {
            return null;
        }
        var item = equipment.Equipped.FirstOrDefault(e => e.Thing.Id == id) ?? throw new DomainException($"primary weapon {id}: not among the pawn's equipment");
        var entity = tables.Entity(item.Thing) ?? throw new DomainException($"primary weapon {id}: the frame holds no row for it");
        var def = entity.DefName;
        var weapon = catalog.WeaponOf(def);
        return new GearOption
        {
            Origin = new GearOptionOrigin.Equipped(item, entity),
            Slot = GearSlot.Primary,
            Ranged = weapon.Ranged,
            Weapon = weapon,
        };
    }

    /// <summary>
    /// Maps one native option against the catalog: its layers and groups from the def's apparel row, its stats from
    /// the stat table for its (def, stuff), its move speed, psychic and shield facts from the row, a crafted bill's quality odds
    /// from its crafters. Null for a garment the model's slots do not name; throws for a def the catalog lacks or does not know
    /// as apparel, a pair without stat values, or a bill with a quality and no crafter.
    /// </summary>
    private static GearOption? GearLoadoutOption(GearOptionOrigin origin, DefinitionCatalog catalog)
    {
        var item = new GearOption { Origin = origin };
        var def = item.Definition.Value;
        var stuff = item.Stuff.Value;
        if (ApparelShapeOf(catalog, def, item.ID) is not { } shape)
        {
            return null;
        }
        var source = item.Source;
        var qualityless = item.Qualityless;
        var crafters = origin switch
        {
            GearOptionOrigin.Observed o => o.Value.Crafters,
            GearOptionOrigin.Catalog c => c.Bill.Crafters,
            GearOptionOrigin.Equipped => [],
            GearOptionOrigin invalid => throw new DomainException($"invalid gear origin: {invalid}"),
        };
        EquatableArray<string> research = default;
        EquatableArray<Amount> ingredients = default;
        if (source == GearSource.Bill)
        {
            if (catalog.ApparelBill(def, stuff) is not var (needs, costs))
            {
                throw new DomainException($"gear option {item.ID}: no recipe makes {def}");
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
            Origin = origin,
            // A crafted garment comes out at its best crafter's quality odds (#3850); a bill names at least one crafter.
            QualityOdds = source == GearSource.Bill && !qualityless ? EquatableArray.From(CraftedQualityRules.BestOdds(crafters)) : [],
            Slot = shape.Slot,
            Layers = shape.Layers,
            Groups = shape.Groups,
            WearPerDay = shape.WearPerDay,
            MoveSpeed = shape.MoveSpeed,
            SlaveOnly = shape.SlaveOnly,
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
    /// Decodes one loadout's loose replacement candidates: the apparel a wear order (gear_replace, native
    /// GiveJobIntent Wear) can target. A def the catalog does not know as apparel is skipped: the wear operation
    /// looks its target up among loose apparel only, so a plan proposing one was refused on every attempt and held
    /// its development slot for the run. A candidate whose things table row the frame lacks, or whose def the
    /// catalog lacks, raises <see cref="RequiredFactException"/>.
    /// </summary>
    public static EquatableArray<GearCandidate> GearCandidateFacts(RimGovernor.Brain.GameFacts.Observations.GearLoadout p, Tables tables, DefinitionCatalog catalog)
    {
        var candidates = new List<GearCandidate>();
        foreach (var c in p.Candidates)
        {
            var def = GearRowDef(tables, catalog, c.Item.Thing);
            if (GearDefinitionsReads.ApparelRow(catalog, def).Apparel is null)
            {
                continue;
            }
            candidates.Add(new GearCandidate(c, new Resource(def)));
        }
        return EquatableArray.From(candidates);
    }

    /// <summary>
    /// Decodes one pawn's worn apparel from the loadout's equipment read: each garment's definition, condition fraction
    /// (1 without hit points) and body-part groups. A garment whose things table row the frame lacks, or whose def the
    /// catalog does not know as apparel, raises <see cref="RequiredFactException"/>.
    /// </summary>
    public static EquatableArray<GearApparel> GearApparelFacts(RimGovernor.Brain.GameFacts.Observations.PawnEquipment equipment, Tables tables, DefinitionCatalog catalog)
    {
        var apparel = new List<GearApparel>(equipment.Apparel.Count);
        foreach (var item in equipment.Apparel)
        {
            var def = GearRowDef(tables, catalog, item.Thing);
            var worn = GearDefinitionsReads.ApparelRow(catalog, def).Apparel ?? throw new RequiredFactException($"apparel row of worn {def}");
            var entity = tables.Entity(item.Thing) ?? throw new RequiredFactException($"frame row of gear item {item.Thing.Id}");
            apparel.Add(new GearApparel { Observed = item, Entity = entity, Groups = GearDefinitionsReads.Distinct((worn.bodyPartGroups ?? []).Select(g => g.defName)) });
        }
        return EquatableArray.From(apparel);
    }

    /// <summary>The def of a gear item by its frame row, which the catalog must know.</summary>
    private static string GearRowDef(Tables tables, DefinitionCatalog catalog, RimGovernor.Brain.GameFacts.Common.Ref thing)
    {
        var def = tables.Entity(thing)?.DefName ?? throw new RequiredFactException($"frame row of gear item {thing.Id}");
        return catalog.FindThing(def) is null ? throw new RequiredFactException($"catalog row of gear def {def}") : def;
    }

    /// <summary>Every outfit policy's load id.</summary>
    public static EquatableArray<string> OutfitIds(RimGovernor.Brain.GameFacts.Observations.Policies p)
    {
        var ids = new List<string>();
        foreach (var row in p.Outfit)
        {
            ids.Add(row.Id);
        }
        return EquatableArray.From(ids);
    }

    /// <summary>
    /// Flags each census colonist that is a creepjoiner with an unrevealed downside
    /// (GearRoleInput.UnrevealedCreepjoiner), from pawn rows, on both the apparel policy state and the loadout model
    /// so every reader derives the same constrained role, and gives each the PreceptDef names of its pawn row's ideo
    /// (GearRoleInput.Precepts; the temperature cards read them). Every gear pawn must have a row in the total
    /// pawn census. The census is copied, never mutated in place.
    /// </summary>
    public static GearObservation StampGearCreepjoiners(GearObservation gear, RimGovernor.Brain.GameFacts.Observations.PawnSnapshot? pawns, CreepJoinerDownsides downsides)
    {
        if (pawns is null)
        {
            throw new BridgeContractException("missing total pawn census for gear roles");
        }
        var rows = new Dictionary<string, RimGovernor.Brain.GameFacts.Observations.PawnState>(pawns.Pawns.Count, StringComparer.Ordinal);
        foreach (var row in pawns.Pawns)
        {
            if (!rows.TryAdd(row.Pawn.Id, row))
            {
                throw new BridgeContractException($"duplicate pawn row for gear roles: {row.Pawn.Id}");
            }
        }
        var stamped = new List<GearPawn>(gear.Pawns);
        for (var i = 0; i < stamped.Count; i++)
        {
            var g = stamped[i];
            if (!rows.TryGetValue(g.Pawn.Value, out var row))
            {
                throw new BridgeContractException($"missing pawn row for gear roles: {g.Pawn.Value}");
            }
            var unrevealed = downsides.Unrevealed(row);
            GearRoleInput Stamp(GearRoleInput role) => role with { UnrevealedCreepjoiner = role.UnrevealedCreepjoiner || unrevealed, Pawn = row };
            if (g.Policy is { } state)
            {
                g = g with { Policy = state with { Role = Stamp(state.Role) } };
            }
            if (g.Model is { } model)
            {
                g = g with { LoadoutModel = new GearModel.Modeled(model with { Role = Stamp(model.Role) }) };
            }
            stamped[i] = g;
        }
        return gear with { Pawns = stamped };
    }

    /// <summary>
    /// The pawn's outfit census; null for a pawn without an outfit.
    /// </summary>
    public static ApparelPolicyState? ApparelPolicyFacts(RimGovernor.Brain.GameFacts.Observations.GearLoadout p, DefinitionCatalog catalog, RimGovernor.Brain.GameFacts.Observations.PawnState pawn)
    {
        var v = p.ApparelPolicy;
        if (v is null)
        {
            return null;
        }
        var s = new ApparelPolicyState
        {
            Observed = v,
            Role = new GearRoleInput { Observed = p, Pawn = pawn },
        };
        s = s with { Definitions = GearDefinitionsReads.ApparelDefinitions(catalog, GearDefinitionsReads.WearerOf(p)) };
        foreach (var a in pawn.Equipment.Apparel)
        {
            s = s with { Overrides = s.Overrides || a.Forced || a.Locked };
        }
        var priorities = new List<WorkPriority>();
        var work = pawn.Settings.Work switch
        {
            RimGovernor.Brain.GameFacts.Observations.PawnWork.Works working => working.Rows,
            RimGovernor.Brain.GameFacts.Observations.PawnWork.NeverWorks => [],
            RimGovernor.Brain.GameFacts.Observations.PawnWork.Uninitialized => throw new RequiredFactException("initialized gear role work settings"),
            null => throw new RequiredFactException("gear role work settings"),
            RimGovernor.Brain.GameFacts.Observations.PawnWork invalid => throw new DomainException($"invalid gear role work settings: {invalid}"),
        };
        foreach (var w in work)
        {
            if (WorkRules.WorkTypeOf(w.DefName) == WorkType.Unrecognized)
            {
                continue;
            }
            priorities.Add(new WorkPriority { Observed = w });
        }
        return s with { Role = s.Role with { Work = EquatableArray.From(priorities) } };
    }

    /// <summary>
    /// The garments the colonists' outfits allow that cover a core body-part group, each with its first available
    /// recipe's ingredient slots (PlanClothingRunway prices the replacement from them). A definition without a known
    /// available recipe is left out; an unread census yields none.
    /// </summary>
    public static IReadOnlyList<ClothingGarment> ClothingGarments(GearDefinitions defs, GearObservation gear)
    {
        var (catalog, finished) = (defs.Catalog, defs.Finished);
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in gear.Pawns)
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
