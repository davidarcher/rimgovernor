using System;
using System.Collections.Generic;
using System.Linq;
using RimGovernor.Brain.Domain;
using RimWorld;
using Verse;

namespace RimGovernor.Native;

internal static class NativeDependentPatients
{
    internal static IReadOnlyList<(Pawn Pawn, DependentPatientGroup Group, bool Rescue, bool Tend)> Read(Map map)
    {
        var player = Faction.OfPlayerSilentFail ?? throw new InvalidOperationException("Player faction missing.");
        var refugees = Find.QuestManager.QuestsListForReading.SelectMany(NativeQuestRefugees.Read)
            .Where(o => o.Active).SelectMany(o => o.PawnIds).Select(p => p.Value).ToHashSet(StringComparer.Ordinal);
        var result = new List<(Pawn, DependentPatientGroup, bool, bool)>();
        foreach (var pawn in map.mapPawns.AllPawnsSpawned.OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal))
        {
            if (pawn.Dead || pawn.IsFreeColonist) continue;
            var held = pawn.TryGetComp<CompHoldingPlatformTarget>();
            if (held?.CurrentlyHeldOnPlatform == true && held.HeldPlatform?.Faction == player)
            {
                result.Add((pawn, DependentPatientGroup.HeldEntity, false, true));
                continue;
            }
            if (!pawn.RaceProps.Humanlike || pawn.IsSlave) continue;
            var hosted = pawn.HostFaction == player;
            if (refugees.Contains(pawn.GetUniqueLoadID()) && (hosted || !pawn.HostileTo(player)))
            {
                result.Add((pawn, DependentPatientGroup.RescueObjective, !pawn.IsPrisoner && !pawn.HostileTo(player), hosted));
                continue;
            }
            if (hosted && !pawn.IsPrisoner && !pawn.HostileTo(player))
                result.Add((pawn, DependentPatientGroup.HostedGuest, true, true));
        }
        return result;
    }

    internal static RimGovernor.Brain.GameFacts.Observations.DependentPatientCensus Observe(Map map) => new()
    {
        Patients = EquatableArray.From(Read(map).Select(p => new RimGovernor.Brain.GameFacts.Observations.DependentPatientObservation
        { Pawn = NativePawnObservationTools.Ref(p.Pawn), Group = p.Group, Rescue = p.Rescue, Tend = p.Tend }))
    };
}
