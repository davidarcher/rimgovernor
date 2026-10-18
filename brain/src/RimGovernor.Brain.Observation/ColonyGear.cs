using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.Observation;

public static class ColonyGearReads
{
    /// <summary>
    /// Decodes the gear census against the frame's definitions: the catalog's apparel rows and stat table give every
    /// garment's layers, groups and stats, and the finished research its bill options need. A frame without a
    /// catalog, finished research or outdoor temperature leaves the census unknown; a pawn the loadout model
    /// refuses carries the cause in GearPawn.ModelRefusal.
    /// </summary>
    public static Fact<GearObservation> GearFacts(O.ColonyFactsSnapshot v, Tables tables, GearDefinitions defs)
    {
        var gear = v.Planning?.Observed?.Gear;
        if (gear is null || defs.Catalog is null || !defs.Finished.IsKnown || (uint)gear.Pawns.Count != v.ColonistCount)
        {
            return Unknown<GearObservation>();
        }
        var threat = RaidThreatFacts(v.Threat?.Observed, defs.Catalog);
        var pawns = new List<GearPawn>(gear.Pawns.Count);
        foreach (var p in gear.Pawns)
        {
            var policy = ApparelPolicyFacts(p, defs.Catalog);
            var row = new GearPawn
            {
                Pawn = new PawnId(p.Pawn?.Id ?? ""),
                Loadout = p.Snapshot?.Token ?? "",
                Blocked = p.Blocker is not null,
                Candidates = GearCandidateFacts(p, tables, defs.Catalog),
                Apparel = GearApparelFacts(p.Equipment, tables, defs.Catalog),
                Policy = policy,
                Climate = GearClimateFacts(gear),
            };
            try
            {
                row = row with { LoadoutModel = GearLoadoutModelFacts(defs, tables, v.OutdoorTemperatureC, p, policy, threat) };
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
            Stored = GearStorageFacts(gear),
            Outfits = OutfitIds(PoliciesReads.ColonyPolicies(v.Policies)),
        };
        return Known(result);
    }

    /// <summary>
    /// The expected raid the gear planner values armor against (#3545): the composition of the observed raid points from the
    /// hostile factions, with the catalog's body coverage and the race's natural armor. Unknown without a catalog, raid points
    /// or hostile factions, or when the composition cannot be decoded.
    /// </summary>
    internal static Fact<RaidThreat> RaidThreatFacts(O.ThreatFacts? threat, DefinitionCatalog? catalog)
    {
        if (catalog is null || threat?.RaidPoints is not { } points || threat.HostileFactions.Count == 0)
        {
            return Unknown<RaidThreat>();
        }
        var composition = RaidCompositionRules.Of(points, [.. threat.HostileFactions], catalog.RaidFactions());
        return composition.Pawns.IsKnown
            ? Known(new RaidThreat { Bins = composition.Bins, Coverage = catalog.BodyCoverage(), Natural = catalog.NaturalArmor() })
            : Unknown<RaidThreat>();
    }

    /// <summary>
    /// Maps one pawn's loadout-model inputs. The role is the apparel-policy role (with the model's traits), and
    /// unworn options are narrowed to the definitions that role's apparel policy permits, so the model never plans a
    /// garment DesiredApparelPolicy would forbid. A pawn with no apparel policy cannot wear apparel: unknown with no
    /// error. Any other pawn the model cannot be built for (no model, role or temperatures, a garment the catalog
    /// lacks a row or stat values for, or a model outside PlanGearLoadout's bounds such as two worn garments on one
    /// slot) throws a <see cref="DomainException"/> naming the cause.
    /// </summary>
    public static Fact<GearLoadoutInput> GearLoadoutModelFacts(GearDefinitions defs, Tables tables, double outdoor, O.GearLoadout p, Fact<ApparelPolicyState> state, Fact<RaidThreat> threat)
    {
        var unknown = Unknown<GearLoadoutInput>();
        var m = p.LoadoutModel;
        if (p.ApparelPolicy is null)
        {
            return unknown;
        }
        var known = state.TryGetValue(out var role);
        defs.Finished.TryGetValue(out var finished);
        var catalog = defs.Catalog ?? throw new DomainException("gear loadout model needs a definition catalog");
        if (m is null)
        {
            throw new DomainException("the producer sent no loadout model");
        }
        if (!known || role is null)
        {
            throw new DomainException("apparel policy facts unavailable (wearer inputs or definition catalog)");
        }
        if (p.Gender is null || p.ComfortableMinC is null || p.ComfortableMaxC is null)
        {
            throw new DomainException("missing gender, comfortable temperatures or outdoor temperature");
        }
        var research = new List<string>(finished ?? new HashSet<string>());
        research.Sort(StringComparer.Ordinal);
        var input = new GearLoadoutInput
        {
            Role = role.Role,
            Female = p.Gender.GetValueOrDefault() == D.Gender.Female,
            Research = EquatableArray.From(research),
            Threat = threat,
            Ambient = outdoor,
            ComfortableMin = p.ComfortableMinC.GetValueOrDefault(),
            ComfortableMax = p.ComfortableMaxC.GetValueOrDefault(),
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
            if (GearLoadoutOption(x, catalog) is { } option && (allowed is null || allowed.Contains((x.DefName ?? ""))))
            {
                options.Add(option);
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
    private static GearOption? PrimaryWeaponOption(O.PawnEquipment? equipment, Tables tables, DefinitionCatalog catalog)
    {
        var id = equipment?.PrimaryId ?? "";
        if (id.Length == 0)
        {
            return null;
        }
        var item = equipment!.Equipped.FirstOrDefault(e => e.Thing?.Id == id);
        var def = tables.Entity(item?.Thing)?.DefName ?? throw new DomainException($"primary weapon {id}: the frame holds no row for it");
        return new GearOption
        {
            ID = id,
            Definition = new Resource(def),
            Stuff = new Resource(""),
            Quality = (int)item!.Quality.GetValueOrDefault(D.QualityCategory.Normal),
            Slot = GearSlot.Primary,
            Source = GearSource.Worn,
            Condition = item.ConditionFraction ?? 1,
            Ranged = catalog.WeaponOf(def).Ranged,
        };
    }

    /// <summary>
    /// Maps one native option against the catalog: its layers and groups from the def's apparel row, its stats from
    /// the stat table for its (def, stuff), its move speed, psychic and shield facts from the row. Null for a
    /// garment the model's slots do not name; throws for a def the catalog lacks or does not know as apparel, or a
    /// pair without stat values.
    /// </summary>
    private static GearOption? GearLoadoutOption(O.GearLoadoutOption x, DefinitionCatalog catalog)
    {
        var def = x.DefName ?? "";
        var stuff = x.Stuff ?? "";
        if (ApparelShapeOf(catalog, def, x.Id ?? "") is not { } shape)
        {
            return null;
        }
        var source = ColonyDecodeReads.WireParse<GearSource>(x.Source);
        EquatableArray<string> research = default;
        EquatableArray<Amount> ingredients = default;
        if (source == GearSource.Bill)
        {
            if (catalog.ApparelBill(def, stuff) is not var (needs, costs))
            {
                throw new DomainException($"gear option {(x.Id ?? "")}: no recipe makes {def}");
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
            ID = x.Id ?? "",
            Definition = new Resource(def),
            Stuff = new Resource(stuff),
            Quality = (int)x.Quality.GetValueOrDefault(),
            Slot = shape.Slot,
            Layers = shape.Layers,
            Groups = shape.Groups,
            Source = source,
            Condition = x.Condition.GetValueOrDefault(),
            WearPerDay = shape.WearPerDay,
            MoveSpeed = shape.MoveSpeed,
            Tainted = x.Tainted.GetValueOrDefault(),
            SlaveOnly = shape.SlaveOnly,
            Locked = x.Locked.GetValueOrDefault(),
            Shield = shape.Shield,
            Psychic = shape.Psychic,
            Smokepop = shape.Smokepop,
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
    private sealed record ApparelShape(GearSlot Slot, EquatableArray<string> Layers, EquatableArray<string> Groups, double WearPerDay, double MoveSpeed, bool SlaveOnly, bool Shield, bool Psychic, bool Smokepop);

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
            ? new ApparelShape(slot, layers, groups, apparel.wearPerDay, GearDefinitionsReads.StatOffset(row, GearDefinitionsReads.StatMoveSpeed), apparel.slaveApparel, GearDefinitionsReads.HasShield(row), GearDefinitionsReads.StatOffset(row, GearDefinitionsReads.StatPsychic) < 0, catalog.SmokePop(def))
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

    /// <summary>The stored apparel stock; unknown when the producer sent none.</summary>
    public static Fact<EquatableArray<GearStock>> GearStorageFacts(O.GearSnapshot gear)
    {
        if (gear.StoredApparel is null)
        {
            return Unknown<EquatableArray<GearStock>>();
        }
        var rows = new List<GearStock>();
        foreach (var row in gear.StoredApparel.Rows)
        {
            rows.Add(new GearStock { Definition = new Resource((row.DefName ?? "")), Stuff = new Resource((row.Stuff ?? "")), Quality = (int)row.Quality.GetValueOrDefault(), HPBand = (int)row.HpBand.GetValueOrDefault(), Count = (int)row.Count.GetValueOrDefault() });
        }
        return Known(EquatableArray.From(rows));
    }

    /// <summary>Maps a validated optional seasonal observation. Older producers omit the entire group, preserving ambient-only policy.</summary>
    public static GearClimate? GearClimateFacts(O.GearSnapshot? gear)
    {
        if (gear is null || gear.OutdoorTemperatureByTwelfthC.Count == 0)
        {
            return null;
        }
        var climate = new GearClimate
        {
            CurrentTwelfth = (int)gear.CurrentTwelfth.GetValueOrDefault(),
            TicksToNextTwelfth = (long)gear.TicksToNextTwelfth.GetValueOrDefault(),
            Temperatures = EquatableArray.From(gear.OutdoorTemperatureByTwelfthC.Select(n => (double)n)),
            DaylightHours = EquatableArray.From(gear.DaylightHoursByTwelfth.Select(n => (double)n)),
        };
        if (gear.ActiveWeather is { } w)
        {
            climate = climate with { Weather = new GearWeather { Definition = w.DefName ?? "", RemainingTicks = w.RemainingTicks.GetValueOrDefault(), TemperatureOffset = w.TemperatureOffsetC.GetValueOrDefault() } };
        }
        return climate;
    }

    /// <summary>
    /// Decodes one loadout's loose replacement candidates: the apparel a wear order (gear_replace, native
    /// GiveJobIntent Wear) can target. A def the catalog does not know as apparel is skipped: the wear operation
    /// looks its target up among loose apparel only, so a plan proposing one was refused on every attempt and held
    /// its development slot for the run. A candidate whose things table row the frame lacks, or whose def the
    /// catalog lacks, leaves them unknown.
    /// </summary>
    public static Fact<EquatableArray<GearCandidate>> GearCandidateFacts(O.GearLoadout p, Tables tables, DefinitionCatalog catalog)
    {
        var candidates = new List<GearCandidate>();
        foreach (var c in p.Candidates)
        {
            var item = c.Item;
            var head = tables.Entity(item?.Thing);
            if (head is null || catalog.FindThing((head.DefName ?? "")) is null)
            {
                return Unknown<EquatableArray<GearCandidate>>();
            }
            if (GearDefinitionsReads.ApparelRow(catalog, (head.DefName ?? "")).Apparel is null)
            {
                continue;
            }
            candidates.Add(new GearCandidate(item?.Thing?.Id ?? "", c.Gain.GetValueOrDefault(), new Resource((head.DefName ?? ""))));
        }
        return Known(EquatableArray.From(candidates));
    }

    /// <summary>
    /// Decodes one pawn's worn apparel from the loadout's equipment read: unknown while a garment's row or catalog
    /// apparel is missing, otherwise each garment's definition, condition fraction (1 without hit points) and
    /// body-part groups.
    /// </summary>
    public static Fact<EquatableArray<GearApparel>> GearApparelFacts(O.PawnEquipment? equipment, Tables tables, DefinitionCatalog catalog)
    {
        if (equipment is null || !ColonyDecodeReads.Headed(tables, equipment.Apparel, item => item.Thing))
        {
            return Unknown<EquatableArray<GearApparel>>();
        }
        var apparel = new List<GearApparel>();
        foreach (var item in equipment.Apparel)
        {
            var def = tables.Entity(item.Thing)?.DefName ?? "";
            var worn = GearDefinitionsReads.ApparelRow(catalog, def).Apparel;
            if (worn is null)
            {
                return Unknown<EquatableArray<GearApparel>>();
            }
            apparel.Add(new GearApparel { Definition = new Resource(def), Condition = item.ConditionFraction is not null ? item.ConditionFraction.GetValueOrDefault() : 1, Groups = GearDefinitionsReads.Distinct((worn.bodyPartGroups ?? []).Select(g => g.defName)) });
        }
        return Known(EquatableArray.From(apparel));
    }

    internal static Fact<GearObservation> RoundsGear(Fact<GearObservation> v, EmergencyFacts emergency)
    {
        if (!v.TryGetValue(out var gear) || !emergency.ColonistsComplete.TryGetValue(out var complete) || !complete || gear.Pawns.Count != emergency.Colonists.Count)
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

    /// <summary>Every outfit policy's load id; unknown with the policy section.</summary>
    public static Fact<EquatableArray<string>> OutfitIds(Fact<Policies> f)
    {
        if (!f.TryGetValue(out var p))
        {
            return Unknown<EquatableArray<string>>();
        }
        var ids = new List<string>();
        foreach (var row in p.Outfit)
        {
            ids.Add(row.ID);
        }
        return Known(EquatableArray.From(ids));
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
            if (g.Policy.TryGetValue(out var state))
            {
                g = g with { Policy = Known(state with { Role = state.Role with { UnrevealedCreepjoiner = true } }) };
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
    /// The pawn's outfit census: unknown without an apparel policy, wearer inputs or catalog.
    /// </summary>
    public static Fact<ApparelPolicyState> ApparelPolicyFacts(O.GearLoadout p, DefinitionCatalog? catalog)
    {
        var v = p.ApparelPolicy;
        if (v is null)
        {
            return Unknown<ApparelPolicyState>();
        }
        var s = new ApparelPolicyState
        {
            Token = v.Token ?? "",
            PawnName = v.PawnName ?? "",
            PolicyID = v.PolicyId ?? "",
            Required = EquatableArray.From(v.RequiredDefs),
            Nude = v.Nude.GetValueOrDefault(),
            ExcludesTainted = v.ExcludesTainted.GetValueOrDefault(),
            Role = new GearRoleInput { Child = v.Child.GetValueOrDefault(), Slave = v.Slave.GetValueOrDefault(), IncapableOfViolence = v.IncapableOfViolence.GetValueOrDefault(), DraftedSquad = v.Drafted.GetValueOrDefault() },
            Current = new ApparelPolicySpec(default, "", (v.Name ?? ""), EquatableArray.From(v.AllowedDefs), v.MinHitPoints.GetValueOrDefault(), v.MaxHitPoints.GetValueOrDefault(), (int)v.MinQuality.GetValueOrDefault(), (int)v.MaxQuality.GetValueOrDefault()),
        };
        var wearer = GearDefinitionsReads.WearerOf(p);
        if (wearer is null || catalog is null)
        {
            return Unknown<ApparelPolicyState>();
        }
        s = s with { Definitions = GearDefinitionsReads.ApparelDefinitions(catalog, wearer) };
        foreach (var a in p.Equipment?.Apparel ?? [])
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
        return Known(s with { Role = s.Role with { Work = work } });
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
            if (p.Policy.TryGetValue(out var state))
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
                if (host.Ingredients.TryGetValue(out var slots) && host.Available)
                {
                    output.Add(new ClothingGarment { Definition = new Resource(name), Groups = EquatableArray.From(groups), Slots = slots });
                    break;
                }
            }
        }
        output.Sort((a, b) => string.CompareOrdinal(a.Definition.Value, b.Definition.Value));
        return output;
    }
}
