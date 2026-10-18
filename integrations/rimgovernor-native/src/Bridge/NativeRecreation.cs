#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools;

/// <summary>
/// The recreation buildings the game's own JoyGivers use (#3570): every JoyGiverDef whose worker is a
/// <see cref="JoyGiver_InteractBuilding"/> (horseshoes, hoopstone, chess, poker, Ur, billiards, televisions, telescope,
/// instruments and any modded giver of that family), read from its def, never a hard-coded building. One source for the
/// needs read, bind-time checks and the exact job, so an observed target is one Hands can start.
/// </summary>
internal static class NativeRecreation
{
    private static Dictionary<ThingDef, JoyGiverDef>? givers;

    /// <summary>The interact-building giver that uses this building, or null when the game has none for it.</summary>
    internal static JoyGiverDef? GiverFor(Thing thing)
    {
        givers ??= DefDatabase<JoyGiverDef>.AllDefsListForReading
            .Where(g => g.Worker is JoyGiver_InteractBuilding && g.jobDef != null && g.thingDefs != null)
            .SelectMany(g => g.thingDefs.Select(d => (d, g)))
            .GroupBy(p => p.d).ToDictionary(p => p.Key, p => p.First().g);
        return givers.TryGetValue(thing.def, out var giver) ? giver : null;
    }

    /// <summary>
    /// The giver's own legality for this pawn and building, as JoyGiver_InteractBuilding.FindBestGame tests a candidate:
    /// CanBeGivenTo (required capacities, joy kind, outdoors preference) then the worker's CanInteractWith (reservation at
    /// joyMaxParticipants, forbidden, fogged, social and political propriety, vacuum, power, unroofed-only).
    /// </summary>
    internal static bool Usable(Pawn pawn, Thing thing, JoyGiverDef giver) =>
        pawn.needs?.joy != null && giver.Worker.CanBeGivenTo(pawn) && Access.CanInteract(giver, pawn, thing);

    /// <summary>The worker's own play job on exactly this building (its seat, watch cell or interaction cell), or null; the caller checks <see cref="Usable"/> first.</summary>
    internal static Job? PlayJob(Pawn pawn, Thing thing, JoyGiverDef giver) => Access.PlayJob(giver, pawn, thing);

    /// <summary>
    /// JoyUtility.JoyTickCheckEnd's gain per tick as every vanilla interact-building driver calls it (joySource = the
    /// building, extra factor 1, decompiled 1.7): joyGainRate × 0.36 / 2500 × the building's JoyGainFactor × the pawn's
    /// tolerance factor for the job's joy kind, at the observed tolerance.
    /// </summary>
    internal static double GainPerTick(Pawn pawn, Thing thing, JobDef job) =>
        job.joyGainRate * 0.36 / RimGovernor.Brain.Domain.GameTime.TicksPerHour * thing.GetStatValue(StatDefOf.JoyGainFactor)
        * pawn.needs!.joy.tolerances.JoyFactorFromTolerance(job.joyKind);

    /// <summary>
    /// Vanilla drivers whose play is one stationary final Delay toil of joyDuration (the watch and sit-facing families),
    /// so ticksLeftThisToil is the session's remainder. Billiards re-walks between shots and is not one.
    /// </summary>
    internal static bool StationaryDriver(JobDriver? driver) => driver?.GetType() is { } type
        && (type == typeof(JobDriver_WatchBuilding) || type == typeof(JobDriver_PlayHorseshoes) || type == typeof(JobDriver_PlayHoopstone)
            || type == typeof(JobDriver_WatchTelevision) || type == typeof(JobDriver_SitFacingBuilding) || type == typeof(JobDriver_PlayPoker)
            || type == typeof(JobDriver_PlayMusicalInstrument));

    // The worker's protected members, invoked on the def's own worker so each giver's override runs (reflection invokes virtually).
    private abstract class Access : JoyGiver_InteractBuilding
    {
        private static readonly MethodInfo CanInteractWithMethod =
            typeof(JoyGiver_InteractBuilding).GetMethod(nameof(CanInteractWith), BindingFlags.Instance | BindingFlags.NonPublic)!;
        private static readonly MethodInfo TryGivePlayJobMethod =
            typeof(JoyGiver_InteractBuilding).GetMethod(nameof(TryGivePlayJob), BindingFlags.Instance | BindingFlags.NonPublic)!;
        internal static bool CanInteract(JoyGiverDef giver, Pawn pawn, Thing thing) =>
            (bool)CanInteractWithMethod.Invoke(giver.Worker, [pawn, thing, false])!;
        internal static Job? PlayJob(JoyGiverDef giver, Pawn pawn, Thing thing) =>
            (Job?)TryGivePlayJobMethod.Invoke(giver.Worker, [pawn, thing]);
    }
}
