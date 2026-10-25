using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RimGovernor.Native;

/// <summary>
/// The part-loss facts of the harm model's raid forecast (#3847), read without changing game state: a loss is scored on a copy of
/// the pawn's hediff set holding missing-part hediffs made without HediffMaker (which draws a hediff load id) or PostMake (which
/// draws a random number), never on the pawn's own set; so the death check is Pawn_HealthTracker.ShouldBeDead's capacity and core
/// part tests over that copy rather than WouldDieAfterAddingHediff, which adds to the pawn's own set and draws an id.
/// </summary>
internal static class NativePartLossFacts
{
    private static readonly PawnCapacityDef[] Levels =
    [
        PawnCapacityDefOf.Consciousness, PawnCapacityDefOf.Moving, PawnCapacityDefOf.Manipulation, PawnCapacityDefOf.Sight, PawnCapacityDefOf.Talking,
    ];

    internal static RimGovernor.Brain.GameFacts.Observations.PawnPartLossFacts? ReadPawn(Pawn pawn)
    {
        if (pawn.health?.hediffSet is not { } set) return null;
        var body = pawn.RaceProps.body;
        var rows = new List<RimGovernor.Brain.GameFacts.Observations.BodyPartLossRead>();
        var index = new Dictionary<BodyPartRecord, int>();
        var replaceable = new List<BodyPartRecord>();
        foreach (var part in body.AllParts)
        {
            if (set.PartIsMissing(part) || !(part.coverageAbs > 0f)) continue;
            var lost = Without(pawn, set, part);
            var levels = Levels.Select(c => PawnCapacityUtility.CalculateCapacityLevel(lost, c)).ToArray();
            var core = part == body.corePart;
            var kills = !core && Kills(pawn, set, lost, body);
            index[part] = rows.Count;
            rows.Add(new RimGovernor.Brain.GameFacts.Observations.BodyPartLossRead(part.def.defName, part.coverageAbs) {
                Groups = [.. (part.groups ?? new List<BodyPartGroupDef>()).Select(g => g.defName).Distinct().OrderBy(g => g, System.StringComparer.Ordinal)],
                Outside = part.depth == BodyPartDepth.Outside, Core = core,
                Parent = part.parent != null && index.TryGetValue(part.parent, out var parent) ? parent : -1,
                Health = set.GetPartHealth(part), MaxHealth = part.def.GetMaxHealth(pawn), Destroyable = part.def.destroyableByDamage, Kills = kills,
                Consciousness = levels[0], Moving = levels[1], Manipulation = levels[2], Sight = levels[3], Talking = levels[4],
            });
            if (!core && !kills && part.def.destroyableByDamage && replaceable.All(r => r.def != part.def)) replaceable.Add(part);
        }

        return new RimGovernor.Brain.GameFacts.Observations.PawnPartLossFacts {
            InstantKillChance = pawn.IsColonist ? Find.Storyteller.difficulty.allowInstantKillChance : 1f,
            Parts = [.. rows],
            Replacements = [.. Replacements(pawn, replaceable)],
        };
    }

    // A copy of the pawn's hediff set with the part and every part under it missing (Pawn_HealthTracker.CalculateMissingPartHediffsFromInjury),
    // the missing parts not fresh (isFreshInt left false: no fresh-loss pain).
    private static HediffSet Without(Pawn pawn, HediffSet set, BodyPartRecord part)
    {
        var copy = new HediffSet(pawn) { hediffs = new List<Hediff>(set.hediffs) };
        var stack = new Stack<BodyPartRecord>();
        stack.Push(part);
        while (stack.Count > 0)
        {
            var at = stack.Pop();
            var missing = System.Activator.CreateInstance(HediffDefOf.MissingBodyPart.hediffClass) as Hediff_MissingPart
                ?? throw new System.InvalidOperationException("MissingBodyPart hediffClass is no Hediff_MissingPart");
            missing.def = HediffDefOf.MissingBodyPart;
            missing.pawn = pawn;
            missing.comps = new List<HediffComp>();
            missing.Part = at;
            copy.hediffs.Add(missing);
            foreach (var child in at.parts) stack.Push(child);
        }

        return copy;
    }

    // Pawn_HealthTracker.ShouldBeDead over the copy: a lethal capacity (lethalFlesh, lethalMechanoids for a mechanoid) no longer capable
    // (PawnCapacitiesHandler.CapableOf: level above minForCapable), or the core part's efficiency at 0.0001 or under; never with a
    // death-preventing hediff. Missing parts carry no CauseDeathNow and no injury severity, so the other tests stay as they are.
    private static bool Kills(Pawn pawn, HediffSet set, HediffSet lost, BodyDef body)
    {
        if (set.HasPreventsDeath) return false;
        foreach (var capacity in DefDatabase<PawnCapacityDef>.AllDefsListForReading)
        {
            if ((pawn.RaceProps.IsFlesh ? capacity.lethalFlesh : capacity.lethalMechanoids)
                && !(PawnCapacityUtility.CalculateCapacityLevel(lost, capacity) > capacity.minForCapable)) return true;
        }

        return PawnCapacityUtility.CalculatePartEfficiency(lost, body.corePart) <= 0.0001f;
    }

    // Per destroyable part def whose loss does not kill, its first part record taken as missing, each install recipe that would put it
    // back (the illness's gates, NativeDiseaseFacts.Replacements).
    private static IEnumerable<RimGovernor.Brain.GameFacts.Observations.OrganReplacementRead> Replacements(Pawn pawn, List<BodyPartRecord> parts)
    {
        var map = pawn.MapHeld;
        if (pawn.Dead || map == null) yield break;
        var facts = NativePawnDetails.SurgeryScope.Facts(map);
        foreach (var part in parts.OrderBy(p => p.def.defName, System.StringComparer.Ordinal))
        {
            foreach (var recipe in pawn.def.AllRecipes.Where(r => r.AvailableNow && r.Worker is Recipe_InstallNaturalBodyPart or Recipe_InstallArtificialBodyPart
                && r.appliedOnFixedBodyParts != null && r.appliedOnFixedBodyParts.Contains(part.def) && r.Worker.AvailableReport(pawn).Accepted && r.AvailableOnNow(pawn, part))
                .OrderBy(r => r.defName, System.StringComparer.Ordinal))
                yield return NativePawnDetails.Replacement(pawn, recipe, part, facts, map);
        }
    }
}
