using System;
using System.Collections.Generic;
using System.Linq;
using Verse;
using Verse.AI;
using RimGovernor.Brain.GameAccess;
using DomainCell = RimGovernor.Brain.Domain.Cell;
using Facts = RimGovernor.Brain.GameFacts.Observations;
using Orders = RimGovernor.Brain.GameOrders.Observations;

namespace RimGovernor.Native;

/// <summary>
/// The relocation haul probe (RG-HABITAT-010, task 8.1): each requested worker's path from a standing piece to its planned
/// slot, read by the game's own pathfinder on the map as it stands and priced at that pawn's move ticks. The legs are
/// searched together on the path job workers and kept across reads (NativePathLegs.Batched), like the servicing read's. A
/// pure observation: nothing is reserved, placed or simulated.
/// </summary>
internal static class NativeHaulProbe
{
    internal static Facts.HaulProbeSnapshot HandleRead(Orders.HaulProbeRequest request)
    {
        if (!Validate(request, out var invalid)) throw NativeFaults.Refused(invalid);
        if (!GameThread.ValidateIdentity(request.Scope.ExpectedIdentity, out var map, out var context, out var failure))
            throw NativeFaults.Refused(failure);
        if (request.Legs.Any(l => !Native(l.From).InBounds(map) || !Native(l.To).InBounds(map)))
            throw NativeFaults.Refused(GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Cell is outside the current map."));
        try { return Read(map, request, context); }
        catch (Exception error)
        {
            throw NativeFaults.Unavailable(new RimGovernor.Brain.GameFacts.Common.Unavailable { Reason = RimGovernor.Brain.GameFacts.Common.UnavailableReason.ReadFailed,
                Detail = "Native haul probe could not be read completely: " + error.GetType().Name + ": " + error.Message });
        }
    }

    internal static bool Validate(Orders.HaulProbeRequest request, out RimGovernor.Brain.GameFacts.Common.Failure failure)
    {
        failure = GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest,
            "Valid identity scope and uniquely identified legs, each naming its worker, are required.");
        if (request?.Scope?.ExpectedIdentity == null) return false;
        if (request.Legs.Select(l => l.Id).Distinct(StringComparer.Ordinal).Count() != request.Legs.Count) return false;
        return request.Legs.All(l => !string.IsNullOrEmpty(l.Id) && !string.IsNullOrEmpty(l.Pawn));
    }

    private static IntVec3 Native(DomainCell cell) => new(cell.X, 0, cell.Z);

    private static Facts.HaulProbeSnapshot Read(Map map, Orders.HaulProbeRequest request, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
    {
        var pawns = new Dictionary<string, Pawn>(StringComparer.Ordinal);
        foreach (var pawn in map.mapPawns.AllPawnsSpawned) pawns[pawn.GetUniqueLoadID()] = pawn;
        var legs = new NativePathLegs(map);
        var answers = legs.Batched(() => request.Legs.Select(l => new Facts.HaulProbeAnswer(l.Id, Leg(legs, pawns, l))).ToList());
        return new Facts.HaulProbeSnapshot { Context = context, Legs = [.. answers] };
    }

    // The worker's walk from the piece to the slot (PathEndMode.OnCell: the minified thing is carried onto its blueprint cell).
    private static Facts.TransitionLeg Leg(NativePathLegs legs, Dictionary<string, Pawn> pawns, Orders.HaulProbeLeg leg) =>
        !pawns.TryGetValue(leg.Pawn, out var pawn) ? new Facts.TransitionLeg.Unread("pawn_unspawned")
        : legs.Cost(pawn, Native(leg.From), Native(leg.To), PathEndMode.OnCell) is { } ticks ? new Facts.TransitionLeg.Walked(ticks)
        : new Facts.TransitionLeg.Unread("no_path");
}
