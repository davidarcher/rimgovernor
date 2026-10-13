namespace RimGovernor.Brain.Policy;

// Go: combat_anomaly.go
//
// Anomaly threats in defense tactics. Every fact here is a native def or object read on the pawn row
// (PawnAnomaly); unknown is never one of the cases below, so a pawn the native read could not classify keeps
// the tactic it had before.

public static partial class PolicyFunctions
{
    /// <summary>A hostile that is an Anomaly entity or a mutant (a shambler is a humanlike mutant, so Humanlike alone cannot place it).</summary>
    private static bool Anomalous(SquadThreatFacts t) => t.Anomaly.TryGetValue(out var a) && (Positive(a.Entity) || Positive(a.Mutant));

    /// <summary>
    /// An entity or mutant whose attack is melee with no offensive ability. It charges in, so a choke blocks
    /// it and a slow one is kited, as a manhunter pack is (manhunterFormation); one with a ranged or
    /// offensive-ability attack shoots over the block and is not.
    /// </summary>
    public static bool MeleeEntity(SquadThreatFacts t) =>
        t.Anomaly.TryGetValue(out var a) && (Positive(a.Entity) || Positive(a.Mutant)) && Positive(a.MeleeOnly);

    /// <summary>
    /// The pawn casting its lord's psychic ritual. A cultist psychic ritual siege only needs its caster to
    /// finish the ritual, and damage or interruption to the caster makes every cultist attack directly, so the
    /// caster is the one to hurt: https://rimworldwiki.com/wiki/Psychic_ritual_siege
    /// </summary>
    private static bool RitualCaster(SquadThreatFacts t) => t.Anomaly.TryGetValue(out var a) && Positive(a.PsychicRitualInvoker);

    /// <summary>
    /// A pawn the player cannot see or target (an unrevealed sightstealer): an attack order naming it is
    /// refused, so it is never a target until it shows itself.
    /// </summary>
    private static bool HiddenFromPlayer(SquadThreatFacts t) => t.Anomaly.TryGetValue(out var a) && Positive(a.HiddenFromPlayer);

    /// <summary>
    /// The live ritual casters' known cells, not within MortarSafeRadius of a colonist: shelled while they
    /// cast, before a colonist is near enough for the scatter to land on it.
    /// </summary>
    private static List<Cell> RitualCasterCells(CombatView view)
    {
        var caster = new Dictionary<PawnId, bool>();
        foreach (var t in view.Threats.Items)
        {
            caster[ThreatPawn(t)] = !t.Building && RitualCaster(t) && !Positive(t.Dead) && !Positive(t.Downed);
        }

        var ours = ColonistCells(view);
        var output = new List<Cell>();
        foreach (var p in view.Pawns.Items)
        {
            if (p.Cell.TryGetValue(out var c) && caster.GetValueOrDefault(p.Id) && !p.Dead && !p.Downed && !NearAny(ours, c))
            {
                output.Add(c);
            }
        }

        return output;
    }
}
