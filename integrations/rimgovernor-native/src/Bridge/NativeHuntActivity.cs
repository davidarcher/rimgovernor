#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using RimGovernor.Brain.Domain;
using RimWorld;
using Verse;
using Verse.AI;
using Obs = RimGovernor.Contracts.Wire.Observations;
namespace HomeBridge.BridgeTools;

/// <summary>
/// Hunt estimate inputs: the colonists who could join a hunt and the huntable animals, each with the game's own combat
/// inputs (the gun's shot, the body damage meets, the prey's melee and its revenge odds against each hunter), plus the
/// weather's accuracy factor. Every hunter and every prey are read (#3341). Observation only: no roster, target or estimate.
/// </summary>
internal static class NativeHuntActivity
{
    /// <summary>The band edges ShotReport lerps accuracy between: touch, short, medium and long range.</summary>
    const float Touch = 3f, Short = 12f, Medium = 25f, Long = 40f;

    internal static Obs.HuntActivityRead Read(Map map)
    {
        var hunters = map.mapPawns.FreeColonistsSpawned.Where(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Hunting))
            .OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).ToList();
        var center = hunters.Count == 0 ? map.Center : hunters[0].Position;
        // Wild edible animals, nearest to the first hunter; a roster is built from what is here, never from a radius.
        var prey = map.mapPawns.AllPawnsSpawned.Where(p => p.Faction == null && p.RaceProps.Animal && !p.Dead
            && p.RaceProps.meatDef?.IsNutritionGivingIngestible == true && !p.Position.Fogged(map))
            .OrderBy(p => p.Position.DistanceToSquared(center)).ThenBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).ToList();
        var hunterReads = hunters.Select(h => Actor(map, h, true, prey, hunters)).ToArray();
        var preyReads = prey.Select(p => Actor(map, p, false, prey, hunters)).ToArray();
        return new()
        {
            Hunters = EquatableArray.From(hunterReads), Prey = EquatableArray.From(preyReads),
            WeatherAccuracy = map.weatherManager.CurWeatherAccuracyMultiplier,
            Findings = ["needs_genes_and_effects_are_not_observed_here"],
        };
    }

    static Obs.HuntActorRead Actor(Map map, Pawn p, bool hunter, List<Pawn> prey, List<Pawn> hunters)
    {
        var weapon = p.equipment?.Primary;
        var verb = weapon?.GetComp<CompEquippable>()?.AllVerbs.Where(v => !v.verbProps.IsMeleeAttack).OrderByDescending(v => v.verbProps.range).FirstOrDefault();
        var herdSize = !hunter ? prey.Count(q => q.def == p.def && q.Position.DistanceToSquared(p.Position) <= 24 * 24) : 0;
        return new Obs.HuntActorRead(p.GetUniqueLoadID(), p.kindDef.defName, new(p.Position.x, p.Position.z), p.ageTracker.AgeBiologicalYearsFloat)
        {
            Xenotype = p.genes?.Xenotype?.defName ?? "",
            Weapon = Gear(weapon),
            Gun = hunter && verb != null && weapon != null ? Gun(p, weapon, verb) : null,
            Body = Body(p),
            Melee = hunter ? null : Melee(p),
            Revenge = hunter ? [] : Revenge(p, hunters),
            Apparel = EquatableArray.From((p.apparel?.WornApparel ?? []).Select(a => Gear(a)!).Where(g => g != null)),
            Skills = EquatableArray.From((p.skills?.skills ?? []).Select(s => new Obs.HuntSkillRead(s.def.defName, s.Level))),
            Traits = EquatableArray.From((p.story?.traits.allTraits ?? []).Select(t => new Obs.HuntTraitRead(t.def.defName, t.Degree))),
            Health = EquatableArray.From(p.health.hediffSet.hediffs.Where(h => h is not Hediff_MissingPart)
                .Select(h => new Obs.HuntHediffRead(h.def.defName, h.Part?.Label ?? ""))),
            MissingParts = EquatableArray.From(p.health.hediffSet.hediffs.OfType<Hediff_MissingPart>().Select(h => h.Part?.Label ?? "")),
            Available = hunter && !p.Downed && !p.InMentalState && !p.Drafted,
            Hauls = hunter && p.workSettings?.WorkIsActive(WorkTypeDefOf.Hauling) == true,
            CorpseStore = CorpseStore(map, p),
            Downed = p.Downed, Mental = p.InMentalState,
            Herd = herdSize > 1 ? "wild-herd" : "",
            Meat = Math.Max(0d, p.GetStatValue(StatDefOf.MeatAmount)),
            // A race without meat (a mechanoid) gives no nutrition when butchered.
            MeatNutrition = p.RaceProps.meatDef?.GetStatValueAbstract(StatDefOf.Nutrition) ?? 0,
        };
    }

    /// <summary>The verb's shot as ShotReport.HitReportFor and Verb.TryStartCastOn resolve it; null when it launches no damaging projectile.</summary>
    static Obs.HuntGunRead? Gun(Pawn p, ThingWithComps weapon, Verb verb)
    {
        var projectile = verb.GetProjectile()?.projectile;
        if (projectile?.damageDef == null) return null;
        var unique = weapon.TryGetComp<CompUniqueWeapon>(out var comp) && comp.IgnoreAccuracyMaluses;
        var props = verb.verbProps;
        return new(props.range, p.GetStatValue(StatDefOf.ShootingAccuracyPawn),
            p.GetStatValue(StatDefOf.ShootingAccuracyFactor_Touch), p.GetStatValue(StatDefOf.ShootingAccuracyFactor_Short),
            p.GetStatValue(StatDefOf.ShootingAccuracyFactor_Medium), p.GetStatValue(StatDefOf.ShootingAccuracyFactor_Long),
            props.GetHitChanceFactor(weapon, Touch), props.GetHitChanceFactor(weapon, Short), props.GetHitChanceFactor(weapon, Medium), props.GetHitChanceFactor(weapon, Long),
            props.canGoWild, unique,
            (verb.WarmupTime * p.GetStatValue(StatDefOf.AimingDelayFactor)).SecondsToTicks(), props.AdjustedCooldownTicks(verb, p), verb.BurstShotCount, verb.TicksBetweenBurstShots,
            projectile.GetDamageAmount(weapon), projectile.GetArmorPenetration(weapon), projectile.damageDef.armorCategory == DamageArmorCategoryDefOf.Sharp,
            projectile.damageDef.hediff?.injuryProps?.painPerSeverity ?? 0);
    }

    static Obs.HuntBodyRead? Body(Pawn p)
    {
        if (p.health?.hediffSet is not { } set) return null;
        return new(p.BodySize, p.HealthScale, p.GetStatValue(StatDefOf.PainShockThreshold), p.kindDef.ignoresPainShock, set.PainTotal,
            set.hediffs.OfType<Hediff_Injury>().Sum(h => (double)h.Severity), OverallArmor(p, StatDefOf.ArmorRating_Sharp), OverallArmor(p, StatDefOf.ArmorRating_Blunt),
            p.TicksPerMoveCardinal, p.GetStatValue(StatDefOf.MeleeDodgeChance));
    }

    /// <summary>The overall armour the gear tab shows (ITab_Pawn_Gear.TryDrawOverallArmor): body-part coverage of the natural and worn ratings.</summary>
    static double OverallArmor(Pawn p, StatDef stat)
    {
        var natural = Math.Clamp(p.GetStatValue(stat) / 2f, 0f, 1f);
        var worn = p.apparel?.WornApparel ?? [];
        var total = 0f;
        foreach (var part in p.RaceProps.body.AllParts)
        {
            var through = 1f - natural;
            foreach (var a in worn)
                if (a.def.apparel.CoversBodyPart(part)) through *= 1f - Math.Clamp(a.GetStatValue(stat) / 2f, 0f, 1f);
            total += part.coverageAbs * (1f - through);
        }
        return Math.Clamp(total * 2f, 0f, 2f);
    }

    /// <summary>The prey's melee attack averaged by selection weight over its available melee verbs that deal damage (StatWorker_MeleeDPS); null without one.</summary>
    static Obs.HuntMeleeRead? Melee(Pawn p)
    {
        var verbs = (p.meleeVerbs?.GetUpdatedAvailableVerbsList(false) ?? []).Where(v => v.IsMeleeAttack && v.verb.verbProps.meleeDamageDef != null).ToList();
        var weight = verbs.Sum(v => v.GetSelectionWeight(null));
        if (weight <= 0) return null;
        double Mean(Func<Verb, double> of) => verbs.Sum(v => v.GetSelectionWeight(null) / weight * of(v.verb));
        return new(Mean(v => v.verbProps.AdjustedMeleeDamageAmount(v, p)), Mean(v => v.verbProps.AdjustedCooldownTicks(v, p)), p.GetStatValue(StatDefOf.MeleeHitChance),
            Mean(v => v.verbProps.AdjustedArmorPenetration(v, p)), Mean(v => v.verbProps.meleeDamageDef.armorCategory == DamageArmorCategoryDefOf.Sharp ? 1 : 0),
            Mean(v => v.verbProps.meleeDamageDef.hediff?.injuryProps?.painPerSeverity ?? 0));
    }

    /// <summary>Per hunter, the game's revenge chance for one damage instance he deals and the pack's chance to join (Pawn_MindState.StartManhunterBecauseOfPawnAction).</summary>
    static EquatableArray<Obs.HuntRevengeRead> Revenge(Pawn prey, List<Pawn> hunters) => EquatableArray.From(hunters.Select(h => new Obs.HuntRevengeRead(h.GetUniqueLoadID(),
        PawnUtility.GetManhunterOnDamageChance(prey, h), Find.Storyteller.difficulty.allowBigThreats ? 0.5 * PawnUtility.GetManhunterChanceFactorForInstigator(h) : 0)));

    /// <summary>
    /// Where native would haul the pawn's corpse: the store of highest priority whose settings accept its corpse def,
    /// at its cell nearest the pawn (StoreUtility's own order: priority, then distance from the corpse).
    /// </summary>
    static Obs.HuntCorpseStore CorpseStore(Map map, Pawn pawn)
    {
        var corpse = pawn.RaceProps.corpseDef;
        if (corpse == null) return new Obs.HuntCorpseStore.NoStore();
        var groups = map.haulDestinationManager.AllGroupsListInPriorityOrder
            .Where(g => (g.parent is not Thing parent || parent.Faction == Faction.OfPlayer) && g.parent.HaulDestinationEnabled && g.Settings.AllowedToAccept(corpse) && g.CellsList.Count > 0)
            .ToList();
        if (groups.Count == 0) return new Obs.HuntCorpseStore.NoStore();
        var best = groups[0].Settings.Priority;
        var cell = groups.Where(g => g.Settings.Priority == best).SelectMany(g => g.CellsList)
            .OrderBy(c => c.DistanceToSquared(pawn.Position)).ThenBy(c => c.x).ThenBy(c => c.z).First();
        return new Obs.HuntCorpseStore.Store(new RimGovernor.Brain.Domain.Cell(cell.x, cell.z));
    }

    static Obs.HuntGearRead? Gear(ThingWithComps? thing)
    {
        if (thing == null) return null;
        thing.TryGetQuality(out var quality);
        return new(thing.def.defName, thing.Stuff?.defName ?? "", quality.ToString());
    }
}
