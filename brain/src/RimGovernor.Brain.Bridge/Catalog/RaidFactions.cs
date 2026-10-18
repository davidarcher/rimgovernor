using RimWorld;
using Verse;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Bridge;

// What the faction defs say of the raids they send: the Combat pawn group makers, their pawn kinds and the weapons those kinds
// carry (#3542 of #3471). The expected composition itself is policy (RaidCompositionRules).

public sealed partial class DefinitionCatalog
{
    /// <summary>
    /// Every faction def with a <c>PawnGroupKindDefOf.Combat</c> group maker, as the raid composition reads it: each maker's
    /// commonality and point ceiling, its <c>options</c> (pawn kind, <c>selectionWeight</c>, <c>combatPower</c>), the faction's
    /// <c>maxPawnCostPerTotalPointsCurve</c>, and for each kind the weapon defs its <c>weaponTags</c> select
    /// (<c>ThingDef.weaponTags</c>, read through <see cref="WeaponOf"/>). A kind with no weapon tags carries its race's own
    /// attacks as its one weapon (a mechanoid's verbs). A tag no weapon def carries is the kind's <c>MissingTags</c>, not an
    /// error. <c>weaponMoney</c> is not decoded: v1 weighs the tagged weapons by commonality alone.
    /// </summary>
    public P.RaidTable RaidFactions() => _raidFactions.Value;

    private P.RaidTable BuildRaidFactions()
    {
        var byTag = new Dictionary<string, P.RaidWeapon[]>(StringComparer.Ordinal);
        var weapons = new Dictionary<string, P.RaidWeapon>(StringComparer.Ordinal);
        var factions = new List<P.RaidFaction>();
        foreach (var faction in Rows<FactionDef>().Values.OrderBy(f => f.defName, StringComparer.Ordinal))
        {
            var groups = faction.pawnGroupMakers.Seq()
                .Where(maker => maker.kindDef?.defName == nameof(PawnGroupKindDefOf.Combat))
                .Select(maker => new P.RaidPawnGroup
                {
                    Commonality = maker.commonality,
                    MaxTotalPoints = maker.maxTotalPoints,
                    Kinds = Domain.EquatableArray.From(maker.options.Seq().Select(option => RaidKindOf(option, byTag, weapons))),
                })
                .ToList();
            if (groups.Count == 0)
            {
                continue;
            }

            factions.Add(new P.RaidFaction
            {
                Def = faction.defName,
                CostCurve = Domain.EquatableArray.From(faction.maxPawnCostPerTotalPointsCurve?.Points.Select(point => new P.CurvePoint(point.x, point.y)) ?? []),
                Groups = Domain.EquatableArray.From(groups),
            });
        }

        return new P.RaidTable { Factions = Domain.EquatableArray.From(factions) };
    }

    private P.RaidKind RaidKindOf(PawnGenOption option, Dictionary<string, P.RaidWeapon[]> byTag, Dictionary<string, P.RaidWeapon> weapons)
    {
        var kind = option.kind;
        var tags = kind.weaponTags.Seq().Distinct().Order(StringComparer.Ordinal).ToList();
        var missing = new List<string>();
        var carried = new Dictionary<string, P.RaidWeapon>(StringComparer.Ordinal);
        foreach (var tag in tags)
        {
            if (!byTag.TryGetValue(tag, out var tagged))
            {
                byTag[tag] = tagged = [.. Rows<ThingDef>().Values
                    .Where(def => def.weaponTags.Seq().Contains(tag))
                    .OrderBy(def => def.defName, StringComparer.Ordinal)
                    .Select(def => RaidWeaponOf(def, weapons))];
            }

            if (tagged.Length == 0)
            {
                missing.Add(tag);
            }

            foreach (var weapon in tagged)
            {
                carried[weapon.Def] = weapon;
            }
        }

        if (tags.Count == 0)
        {
            carried[kind.race.defName] = RaidWeaponOf(kind.race, weapons);
        }

        return new P.RaidKind
        {
            Def = kind.defName,
            Weight = option.selectionWeight,
            CombatPower = kind.combatPower,
            Weapons = Domain.EquatableArray.From(carried.Values.OrderBy(w => w.Def, StringComparer.Ordinal)),
            MissingTags = Domain.EquatableArray.From(missing),
        };
    }

    private P.RaidWeapon RaidWeaponOf(ThingDef def, Dictionary<string, P.RaidWeapon> weapons)
    {
        if (!weapons.TryGetValue(def.defName, out var weapon))
        {
            weapons[def.defName] = weapon = new P.RaidWeapon { Def = def.defName, Commonality = def.generateCommonality, Weapon = WeaponOf(def.defName) };
        }

        return weapon;
    }
}
