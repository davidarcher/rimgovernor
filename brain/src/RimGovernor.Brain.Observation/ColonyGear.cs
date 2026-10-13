namespace RimGovernor.Brain.Observation;

public static partial class ObservationPackage
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
        if (gear is null || defs.Catalog is null || !defs.Finished.IsKnown || !v.HasOutdoorTemperatureC || !v.HasColonistCount || (uint)gear.Pawns.Count != v.ColonistCount)
        {
            return Unknown<GearObservation>();
        }
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
                row = row with { LoadoutModel = GearLoadoutModelFacts(defs, v.OutdoorTemperatureC, p, policy) };
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
            Outfits = OutfitIds(ColonyPolicies(v.Policies)),
        };
        return Known(result);
    }

    /// <summary>
    /// Maps one pawn's loadout-model inputs. The role is the apparel-policy role (with the model's traits), and
    /// unworn options are narrowed to the definitions that role's apparel policy permits, so the model never plans a
    /// garment DesiredApparelPolicy would forbid. A pawn with no apparel policy cannot wear apparel: unknown with no
    /// error. Any other pawn the model cannot be built for (no model, role or temperatures, a garment the catalog
    /// lacks a row or stat values for, or a model outside PlanGearLoadout's bounds such as two worn garments on one
    /// slot) throws a <see cref="DomainException"/> naming the cause.
    /// </summary>
    public static Fact<GearLoadoutInput> GearLoadoutModelFacts(GearDefinitions defs, double outdoor, O.GearLoadout p, Fact<ApparelPolicyState> state)
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
        if (!p.HasGender || !p.HasComfortableMinC || !p.HasComfortableMaxC)
        {
            throw new DomainException("missing gender, comfortable temperatures or outdoor temperature");
        }
        var research = new List<string>(finished ?? new HashSet<string>());
        research.Sort(StringComparer.Ordinal);
        var input = new GearLoadoutInput
        {
            Role = role.Role,
            Female = p.Gender == D.Gender.Female,
            Research = EquatableArray.From(research),
            Ambient = outdoor,
            ComfortableMin = p.ComfortableMinC,
            ComfortableMax = p.ComfortableMaxC,
        };
        var traits = new List<PawnTrait>();
        foreach (var t in m.Traits)
        {
            traits.Add(ResolveTrait(catalog, new PawnTrait { Name = t.DefName, Degree = (int)t.Degree }));
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
            allowed = new HashSet<string>(RoleApparelDefinitions(DeriveGearRole(role.Role), role), StringComparer.Ordinal);
        }
        var worn = new List<GearOption>();
        foreach (var x in m.Worn)
        {
            if (GearLoadoutOption(x, catalog) is { } option)
            {
                worn.Add(option);
            }
        }
        var options = new List<GearOption>();
        foreach (var x in m.Options)
        {
            if (GearLoadoutOption(x, catalog) is { } option && (allowed is null || allowed.Contains(x.DefName)))
            {
                options.Add(option);
            }
        }
        input = input with { Worn = EquatableArray.From(worn), Options = EquatableArray.From(options) };
        input.Validate();
        return Known(input);
    }

    /// <summary>
    /// Maps one native option against the catalog: its layers and groups from the def's apparel row, its stats from
    /// the stat table for its (def, stuff), its move speed, psychic and shield facts from the row. Null for a
    /// garment the model's slots do not name; throws for a def the catalog lacks or does not know as apparel, or a
    /// pair without stat values.
    /// </summary>
    private static GearOption? GearLoadoutOption(O.GearLoadoutOption x, DefinitionCatalog catalog)
    {
        var (row, apparel) = ApparelRow(catalog, x.DefName);
        if (apparel is null)
        {
            throw new DomainException($"gear option {x.Id}: no apparel row for def {x.DefName}");
        }
        var layers = Distinct((apparel.layers ?? []).Select(l => l.defName));
        var groups = Distinct((apparel.bodyPartGroups ?? []).Select(g => g.defName));
        if (!GearSlotOf(layers, groups, out var slot))
        {
            return null;
        }
        var option = new GearOption
        {
            ID = x.Id,
            Definition = new Resource(x.DefName),
            Stuff = new Resource(x.Stuff),
            Quality = (int)x.Quality,
            Slot = slot,
            Layers = layers,
            Groups = groups,
            Source = WireParse<GearSource>(x.Source),
            Condition = x.Condition,
            WearPerDay = apparel.wearPerDay,
            MoveSpeed = StatOffset(row, StatMoveSpeed),
            Tainted = x.Tainted,
            SlaveOnly = apparel.slaveApparel,
            Locked = x.Locked,
            Shield = HasShield(row),
            Psychic = StatOffset(row, StatPsychic) < 0,
            Smokepop = catalog.SmokePop(x.DefName),
        };
        if (option.Source == GearSource.Bill)
        {
            if (catalog.ApparelBill(x.DefName, x.Stuff) is not var (research, ingredients))
            {
                throw new DomainException($"gear option {x.Id}: no recipe makes {x.DefName}");
            }
            var amounts = new List<Amount>();
            foreach (var cost in ingredients)
            {
                amounts.Add(new Amount(new Resource(cost.Def), cost.Units));
            }
            option = option with { Research = EquatableArray.From(research), Ingredients = EquatableArray.From(amounts) };
        }
        option = option with
        {
            Sharp = OptionStat(catalog, x.DefName, x.Stuff, StatArmorSharp, false),
            Blunt = OptionStat(catalog, x.DefName, x.Stuff, StatArmorBlunt, false),
            Cold = OptionStat(catalog, x.DefName, x.Stuff, StatInsulationCold, false),
            Heat = OptionStat(catalog, x.DefName, x.Stuff, StatInsulationHeat, false),
            Cost = OptionStat(catalog, x.DefName, x.Stuff, StatMarketValue, true),
            MaxHitPoints = OptionStat(catalog, x.DefName, x.Stuff, StatMaxHitPoints, true),
        };
        return option;
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
            rows.Add(new GearStock { Definition = new Resource(row.DefName), Stuff = new Resource(row.Stuff), Quality = (int)row.Quality, HPBand = (int)row.HpBand, Count = (int)row.Count });
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
            CurrentTwelfth = (int)gear.CurrentTwelfth,
            TicksToNextTwelfth = (long)gear.TicksToNextTwelfth,
            Temperatures = EquatableArray.From(gear.OutdoorTemperatureByTwelfthC.Select(n => (double)n)),
        };
        if (gear.ActiveWeather is { } w)
        {
            climate = climate with { Weather = new GearWeather { Definition = w.DefName, RemainingTicks = w.RemainingTicks, TemperatureOffset = w.TemperatureOffsetC } };
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
            if (head is null || catalog.FindThing(head.DefName) is null)
            {
                return Unknown<EquatableArray<GearCandidate>>();
            }
            if (ApparelRow(catalog, head.DefName).Apparel is null)
            {
                continue;
            }
            candidates.Add(new GearCandidate(item?.Thing?.Id ?? "", c.Gain, new Resource(head.DefName)));
        }
        return Known(EquatableArray.From(candidates));
    }

    /// <summary>
    /// Decodes one pawn's worn apparel from the loadout's equipment read: unknown when the native apparel tracker
    /// could not be read (the equipment carries an "apparel" issue), otherwise each garment's definition, condition
    /// fraction (1 without hit points) and body-part groups.
    /// </summary>
    public static Fact<EquatableArray<GearApparel>> GearApparelFacts(O.PawnEquipment? equipment, Tables tables, DefinitionCatalog catalog)
    {
        if (equipment is null || !Headed(tables, equipment.Apparel, item => item.Thing))
        {
            return Unknown<EquatableArray<GearApparel>>();
        }
        if (HasIssue(equipment.Issues, "apparel"))
        {
            return Unknown<EquatableArray<GearApparel>>();
        }
        var apparel = new List<GearApparel>();
        foreach (var item in equipment.Apparel)
        {
            var def = tables.Entity(item.Thing)?.DefName ?? "";
            var worn = ApparelRow(catalog, def).Apparel;
            if (worn is null)
            {
                return Unknown<EquatableArray<GearApparel>>();
            }
            apparel.Add(new GearApparel { Definition = new Resource(def), Condition = item.HasConditionFraction ? item.ConditionFraction : 1, Groups = Distinct((worn.bodyPartGroups ?? []).Select(g => g.defName)) });
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
            rows[row.Pawn?.Id ?? ""] = row;
        }
        var stamped = new List<GearPawn>(gear.Pawns);
        for (var i = 0; i < stamped.Count; i++)
        {
            var g = stamped[i];
            if (!rows.TryGetValue(g.Pawn.Value, out var row) || !downsides.Unrevealed(CreepJoinerPawn(row)))
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
    /// Puts each pawn's personal share on its loadout model input, so PlanGearLoadout drops an upgrade the
    /// colonist cannot afford. It runs after PersonalShares, which reads the same models' worn gear; the held
    /// census is copied, never mutated in place.
    /// </summary>
    internal static ColonyProjection StampGearShares(ColonyProjection p)
    {
        if (!p.Facts.Gear.TryGetValue(out var gear))
        {
            return p;
        }
        var stamped = new List<GearPawn>(gear.Pawns);
        for (var i = 0; i < stamped.Count; i++)
        {
            var g = stamped[i];
            if (g.LoadoutModel.TryGetValue(out var model))
            {
                stamped[i] = g with { LoadoutModel = Known(model with { Share = p.PersonalShareOf(g.Pawn) }) };
            }
        }
        return p with { Facts = p.Facts with { Gear = Known(gear with { Pawns = stamped }) } };
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
            Token = v.Token,
            PawnName = v.PawnName,
            PolicyID = v.PolicyId,
            Required = EquatableArray.From(v.RequiredDefs),
            Nude = v.Nude,
            ExcludesTainted = v.ExcludesTainted,
            Role = new GearRoleInput { Child = v.Child, Slave = v.Slave, IncapableOfViolence = v.IncapableOfViolence, DraftedSquad = v.Drafted },
            Current = new ApparelPolicySpec(default, "", v.Name, EquatableArray.From(v.AllowedDefs), v.MinHitPoints, v.MaxHitPoints, (int)v.MinQuality, (int)v.MaxQuality),
        };
        var wearer = WearerOf(p);
        if (wearer is null || catalog is null)
        {
            return Unknown<ApparelPolicyState>();
        }
        s = s with { Definitions = ApparelDefinitions(catalog, wearer) };
        foreach (var a in p.Equipment?.Apparel ?? [])
        {
            s = s with { Overrides = s.Overrides || a.Forced || a.Locked };
        }
        var priorities = new List<WorkPriority>();
        foreach (var w in v.Work)
        {
            priorities.Add(new WorkPriority { Work = WireParse<WorkType>(w.DefName), Priority = (int)w.Priority, Disabled = w.Disabled });
        }
        var work = s.Role.Work with { Work = Known(EquatableArray.From(priorities)) };
        // An older producer omits skills; the role then splits on priorities alone.
        if (v.Skills.Count > 0)
        {
            var skills = new List<WorkSkill>();
            foreach (var sk in v.Skills)
            {
                skills.Add(new WorkSkill { Name = sk.DefName, Level = (int)sk.Level, Passion = SkillPassion(sk.Passion), Disabled = sk.Disabled });
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
                foreach (var name in RoleApparelDefinitions(DeriveGearRole(state.Role), state))
                {
                    allowed.Add(name);
                }
            }
        }
        var output = new List<ClothingGarment>();
        foreach (var name in allowed)
        {
            var apparel = ApparelRow(catalog, name).Apparel;
            var groups = (apparel?.bodyPartGroups ?? []).Select(group => group.defName).ToList();
            if (!GearCoreGroups.Any(groups.Contains))
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
