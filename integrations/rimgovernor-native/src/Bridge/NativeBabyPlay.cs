using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;
using RimGovernor.Brain.Domain;
using F = RimGovernor.Brain.GameFacts.Observations;
using RequiredFactException = RimGovernor.Brain.Policy.RequiredFactException;

namespace RimGovernor.Native;

/// <summary>Read-only play mechanics; no jobs are made and no random destination is sampled.</summary>
internal static class NativeBabyPlay
{
    internal static F.BabyPlayMechanicsRead? Mechanics()
    {
        if (!ModsConfig.BiotechActive) return null;
        return new()
        {
            // Pawn_NeedsTracker invokes NeedInterval every 150 ticks; it exposes no public interval constant.
            FallPerTick = Need_Play.BaseFallPerInterval / 150d,
            GainPerTick = PlayPerTick(),
            Activities = EquatableArray.From(DefDatabase<BabyPlayDef>.AllDefsListForReading.Select(d => new F.BabyPlayActivityRead(d.defName, Kind(d)))),
            Buildings = EquatableArray.From(new[] { ThingDefOf.ToyBox, ThingDefOf.BabyDecoration }.SelectMany(d =>
                (d.MadeFromStuff ? GenStuff.AllowedStuffsFor(d) : new ThingDef[] { null! }).Select(s =>
                    new F.BabyPlayBuildingRead(d.defName, s?.defName ?? "", d == ThingDefOf.BabyDecoration, Positive(d.GetStatValueAbstract(StatDefOf.BabyPlayGainFactor, s)))))),
        };
    }

    internal static F.BabyPlayRead? Read(Pawn baby, Map map, Room? nursery)
    {
        if (baby.needs?.play == null) return null;
        bool Playing(Pawn p) => p.CurJob is { } job && job.targetA.Thing == baby
            && DefDatabase<BabyPlayDef>.AllDefsListForReading.Any(d => d.jobDef == job.def);
        var ongoing = map.mapPawns.FreeColonistsSpawned.Where(Playing).ToArray();
        if (ongoing.Length > 1) throw new RequiredFactException("baby play multiple active carers:" + baby.GetUniqueLoadID());
        return new()
        {
            OngoingCarer = ongoing.FirstOrDefault()?.GetUniqueLoadID() ?? "",
            RoomFactor = Positive(BabyPlayUtility.GetRoomPlayGainFactors(baby)),
            Carers = !ChildcareUtility.CanSuckle(baby, out _) ? [] : EquatableArray.From(map.mapPawns.FreeColonistsSpawned
                .Where(p => Playing(p) || !p.DevelopmentalStage.Juvenile() && !p.WorkTypeIsDisabled(WorkTypeDefOf.Childcare)
                    && p.workSettings != null && p.workSettings.WorkIsActive(WorkTypeDefOf.Childcare)
                    && (p.IsCarryingPawn(baby) || p.CanReserveAndReach(baby, PathEndMode.Touch, Danger.Some)))
                .OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).Select(p => Carer(p, baby, map, nursery))),
        };
    }

    private static F.BabyPlayCarerRead Carer(Pawn carer, Pawn baby, Map map, Room? nursery)
    {
        // A walking baby's Position remains its pickup cell while PositionHeld follows the carer. The room
        // multiplier tests the latter's room and the former's distance. Include every decoration in a reachable
        // room, plus 1 (no decoration); this bounds the native multi-leg walk without inventing a destination law.
        var factors = new List<double> { 1 };
        var without = new List<double> { 1 };
        foreach (var decoration in map.listerThings.ThingsOfDef(ThingDefOf.BabyDecoration))
        {
            var room = decoration.GetRoom();
            if (room == null || room.IsHuge || !room.Regions.SelectMany(r => r.Cells).Any(c => carer.CanReach(c, PathEndMode.OnCell, Danger.Deadly))) continue;
            var factor = Positive(decoration.GetStatValue(StatDefOf.BabyPlayGainFactor));
            factors.Add(factor);
            if (nursery == null || !nursery.ContainedAndAdjacentThings.Contains(decoration)) without.Add(factor);
        }
        return new(carer.GetUniqueLoadID(), Toy(carer, baby, map, null, factors.Min(), factors.Max()), Toy(carer, baby, map, nursery, without.Min(), without.Max()),
            factors.Min(), factors.Max(), without.Min(), without.Max());
    }

    private static F.BabyPlaySourceRead? Toy(Pawn carer, Pawn baby, Map map, Room? removed, double minFactor, double maxFactor)
    {
        // BabyPlayGiver_PlayToys.FindNearbyUseableToyBox has no public observation API. Reproduce its
        // typed IsValidToybox predicate and room-first/GenClosest ordering; TryGiveJob would consume job IDs.
        bool Valid(Thing t) => (removed == null || t.GetRoom() != removed) && !t.IsForbidden(carer) && !t.IsForbidden(baby) && !t.IsBurning()
            && carer.CanReserveAndReach(t, PathEndMode.Touch, Danger.None) && carer.Position.DistanceTo(t.Position) <= 15.9f;
        var room = baby.GetRoom();
        var toy = room?.ContainedThings(ThingDefOf.ToyBox).FirstOrDefault(Valid)
            ?? GenClosest.ClosestThingReachable(baby.PositionHeld, map, ThingRequest.ForDef(ThingDefOf.ToyBox), PathEndMode.OnCell,
                TraverseParms.For(carer), 15.9f, Valid);
        if (toy == null) return null;
        // PlayToys drops the baby adjacent to the source. Its room can have different decoration distances
        // at those possible drops; policy retains that exposure separately rather than a fixed drop cell.
        return new(Positive(toy.GetStatValue(StatDefOf.BabyPlayGainFactor)), minFactor, maxFactor);
    }

    private static F.BabyPlayKind Kind(BabyPlayDef def) => def.Worker switch
    {
        BabyPlayGiver_PlayStatic => F.BabyPlayKind.Static,
        BabyPlayGiver_PlayWalking => F.BabyPlayKind.Walking,
        BabyPlayGiver_PlayToys => F.BabyPlayKind.Toys,
        _ => throw new RequiredFactException("baby play giver:" + def.defName),
    };

    // BabyPlayUtility has no public rate member. A renamed private const is a broken read.
    private static double PlayPerTick() => BridgeCommon.PrivateStaticField(typeof(BabyPlayUtility), "PlayPerTick")?.GetRawConstantValue() is float value
        ? Positive(value) : throw new RequiredFactException("BabyPlayUtility.PlayPerTick");

    private static double Positive(double value) => double.IsFinite(value) && value > 0 ? value
        : throw new RequiredFactException("baby play factor:finite positive");
}
