namespace RimGovernor.Brain.Policy;

/// <summary>
/// One step of an armor family's ladder: the definition and the lowest armory tier that may craft it. Rungs run
/// from the cheapest to the best; a family's need falls back down its rungs when the tier cannot reach the higher
/// one. (Go <c>armoryArmorRung</c>.)
/// </summary>
internal readonly record struct ArmoryArmorRung(Resource Definition, ArmoryTier Tier);

public static partial class PolicyFunctions
{
    /// <summary>
    /// The armor ladder a colony with a soldier walks right after Electricity: the simple helmet's smithy, the
    /// tailoring bench flak cloth needs, then flak armor itself. It stays a judgment table (DefTables): the
    /// recipes' prerequisites give Smithing and FlakArmor, but when a colony wants them is a choice and
    /// ComplexClothing is no prerequisite of any armor recipe. It once also named "Shields", which is no project
    /// (the belt's is ShieldBelt), so the rung never matched.
    /// </summary>
    public static readonly IReadOnlyList<string> ArmorResearchRungs = ["Smithing", "ComplexClothing", "FlakArmor"];

    /// <summary>
    /// The ladder with ArmorResearchRungs spliced in directly after Electricity (at the front when the ladder has
    /// no Electricity rung) while a soldier role exists; rungs the ladder already names move up rather than
    /// repeat. Without a soldier the ladder is returned as is.
    /// </summary>
    public static IReadOnlyList<string> ArmorResearchLadder(IReadOnlyList<string> ladder, bool soldier)
    {
        if (!soldier)
        {
            return ladder;
        }
        var armor = new HashSet<string>(ArmorResearchRungs, StringComparer.Ordinal);
        var @out = new List<string>();
        var inserted = false;
        foreach (var rung in ladder)
        {
            if (armor.Contains(rung))
            {
                continue;
            }
            @out.Add(rung);
            if (rung == "Electricity" && !inserted)
            {
                @out.AddRange(ArmorResearchRungs);
                inserted = true;
            }
        }
        if (!inserted)
        {
            @out.InsertRange(0, ArmorResearchRungs);
        }
        return @out;
    }

    /// <summary>The policy with its ResearchLadder extended by ArmorResearchLadder; an empty ladder (roadmap disabled) stays empty.</summary>
    public static RoundsPolicy ArmorResearchPolicy(RoundsPolicy p, bool soldier)
    {
        if (p.ResearchLadder.Count == 0)
        {
            return p;
        }
        return p with { ResearchLadder = EquatableArray.From(ArmorResearchLadder(p.ResearchLadder, soldier)) };
    }

    /// <summary>
    /// Reports whether the gear census derives a soldier role for any pawn (its apparel-policy role, or its
    /// loadout model's when one is supplied). Unknown census: no soldier.
    /// </summary>
    public static bool GearSoldierPresent(Fact<GearObservation> gear)
    {
        if (!gear.TryGetValue(out var v))
        {
            return false;
        }
        foreach (var p in v.Pawns)
        {
            if (p.LoadoutModel.TryGetValue(out var model) && DeriveGearRole(model.Role) == GearRole.Soldier)
            {
                return true;
            }
            if (p.Policy.TryGetValue(out var state) && DeriveGearRole(state.Role) == GearRole.Soldier)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The body-armor and helmet ladder the armory owns: flak, recon, marine, with the simple helmet at smithing.</summary>
    private static readonly IReadOnlyList<IReadOnlyList<ArmoryArmorRung>> ArmoryArmorFamilies =
    [
        [
            new(new Resource("Apparel_FlakVest"), ArmoryTier.Machining),
            new(new Resource("Apparel_FlakJacket"), ArmoryTier.Machining),
            new(new Resource("Apparel_ArmorRecon"), ArmoryTier.Fabrication),
            new(new Resource("Apparel_PowerArmor"), ArmoryTier.Fabrication),
        ],
        [new(new Resource("Apparel_FlakPants"), ArmoryTier.Machining)],
        [
            new(new Resource("Apparel_SimpleHelmet"), ArmoryTier.Smithing),
            new(new Resource("Apparel_AdvancedHelmet"), ArmoryTier.Machining),
            new(new Resource("Apparel_ArmorHelmetRecon"), ArmoryTier.Fabrication),
            new(new Resource("Apparel_PowerArmorHelmet"), ArmoryTier.Fabrication),
        ],
    ];

    /// <summary>
    /// The rungs a need for definition may be crafted as under tier, best first: the need's own rung and every
    /// lower one the tier allows. Not ok for a definition outside the armory's ladder.
    /// </summary>
    internal static (IReadOnlyList<Resource> Rungs, bool Ok) ArmoryArmorRungs(Resource definition, ArmoryTier tier)
    {
        foreach (var family in ArmoryArmorFamilies)
        {
            for (var i = 0; i < family.Count; i++)
            {
                if (family[i].Definition != definition)
                {
                    continue;
                }
                var rungs = new List<Resource>();
                for (var j = i; j >= 0; j--)
                {
                    if (family[j].Tier <= tier)
                    {
                        rungs.Add(family[j].Definition);
                    }
                }
                return (rungs, true);
            }
        }
        return ([], false);
    }

    /// <summary>Reports whether definition is body armor or a helmet the armory crafts; the gear planner leaves its bills to the armory.</summary>
    public static bool ArmoryArmor(Resource definition)
    {
        var (_, ok) = ArmoryArmorRungs(definition, ArmoryTier.Fabrication);
        return ok;
    }
}
