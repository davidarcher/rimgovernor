namespace RimGovernor.Brain.Observation;

public interface IClearanceSource
{
    Task<(O.ClearanceTargetsReply Reply, Result Receipt)> ReadClearanceTargetsAsync(C.Identity identity, bool includeSalvage, CancellationToken cancellationToken);
}

/// <summary>Widens the census to planned ground.</summary>
public interface IGroundClearanceSource : IClearanceSource
{
    Task<(O.ClearanceTargetsReply Reply, Result Receipt)> ReadClearanceTargetsOnGroundAsync(C.Identity identity, bool includeSalvage, IReadOnlyList<O.Rectangle> ground, CancellationToken cancellationToken);
}

public static partial class ObservationPackage
{
    // The clearance classes a target carries.
    public const string ClearanceAncientWallDoor = "ancient_wall_door";
    public const string ClearanceShipChunk = "ship_chunk";
    public const string ClearanceAncientCasket = "ancient_casket";
    public const string ClearanceOther = "other";

    /// <summary>The units one hauler carries per trip when pricing a salvage yield; policy, not a native fact.</summary>
    internal const long SalvageUnitsPerTrip = 75;

    /// <summary>
    /// Also reads the player's buildings (Player rows) and constructed floors on planned ground. A source without
    /// the planned-ground read is a contract error when ground is requested.
    /// </summary>
    public static async Task<Fact<ClearanceCensus>> ObserveClearanceCensusOnGround(
        IClearanceSource? source, Identity expected, bool includeSalvage, IReadOnlyList<Rectangle> ground, CancellationToken cancellationToken)
    {
        var unknown = Unknown<ClearanceCensus>();
        if (source is null)
        {
            throw new ObservationContractException("missing clearance source");
        }
        expected.Validate();
        if (!SameColonyContext(expected, expected))
        {
            throw new ObservationContractException("expected identity lacks a native generation");
        }
        var id = new C.Identity { ColonyId = expected.Colony.Value, LoadToken = expected.Load.Value, MapId = expected.Map.Value };
        O.ClearanceTargetsReply? reply;
        try
        {
            if (ground.Count > 0)
            {
                if (source is not IGroundClearanceSource widened)
                {
                    throw new ObservationContractException("clearance source lacks the planned-ground read");
                }
                var rects = new List<O.Rectangle>(ground.Count);
                foreach (var g in ground)
                {
                    rects.Add(new O.Rectangle
                    {
                        Minimum = new C.Cell { X = g.X, Z = g.Z },
                        Maximum = new C.Cell { X = g.X + g.Width - 1, Z = g.Z + g.Height - 1 },
                    });
                }
                (reply, _) = await widened.ReadClearanceTargetsOnGroundAsync(id, includeSalvage, rects, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                (reply, _) = await source.ReadClearanceTargetsAsync(id, includeSalvage, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (BridgeUnavailableException)
        {
            return unknown;
        }
        if (reply is null)
        {
            throw new ObservationContractException("missing clearance targets reply");
        }
        // Test and staged native sources can return the typed stub without an error.
        if (reply.Outcome?.Unavailable is { } u)
        {
            if (u.Reason.GetValueOrDefault() == C.UnavailableReason.Unspecified || !Enum.IsDefined(u.Reason.GetValueOrDefault()))
            {
                throw new ObservationContractException("unavailable reply without a reason");
            }
            return unknown;
        }
        var v = reply.Outcome?.Observed ?? throw new ObservationContractException("missing clearance targets");
        var actual = ContextIdentity(v.Context);
        if (!SameColonyContext(actual, expected))
        {
            throw new ObservationChangedException();
        }
        var rows = new List<ClearanceTarget>(v.Targets.Count);
        foreach (var row in v.Targets)
        {
            var target = new ClearanceTarget
            {
                EntityId = row.EntityId ?? "",
                DefName = row.DefName ?? "",
                Class = ClearanceClassName(row.Class),
                Minimum = CellOf(row.Occupied?.Minimum),
                Maximum = CellOf(row.Occupied?.Maximum),
                Deconstructible = row.Deconstructible.GetValueOrDefault(),
                InHome = row.InHome.GetValueOrDefault(),
                AncientDanger = row.AncientDanger.GetValueOrDefault(),
                RoofBlocker = row.RoofBlocker ?? "",
                Designated = row.Designated.GetValueOrDefault(),
                Player = row.EnclosesRoom is not null,
                EnclosesRoom = row.EnclosesRoom.GetValueOrDefault(),
                SalvageSkipped = row.SalvageSkipped.GetValueOrDefault(),
            };
            if (row.Salvage is { } s)
            {
                var yields = new List<CandidateYield>(s.Yields.Count);
                foreach (var y in s.Yields)
                {
                    yields.Add(PolicyFunctions.SourceYield(new ResourceKey { Def = new Resource(y.DefName) }, y.Count, y.UnitValue, Known((long)y.StorageHeadroom)));
                }
                var candidate = PolicyFunctions.SourceCandidate(CandidateKind.Salvage, (row.EntityId ?? ""), Known(s.Labor), Known(s.PathLength), true, SalvageUnitsPerTrip, [.. yields]);
                target = target with { Salvage = new SalvageEvidence { Safe = Known(s.Safe), Candidate = candidate, AgeTicks = (int)s.AgeTicks } };
            }
            rows.Add(target);
        }
        var chunks = new List<ClearanceChunk>(v.Chunks.Count);
        foreach (var row in v.Chunks)
        {
            chunks.Add(new ClearanceChunk
            {
                EntityId = row.EntityId ?? "",
                DefName = row.DefName ?? "",
                Cell = CellOf(row.Cell),
                Forbidden = row.Forbidden.GetValueOrDefault(),
                Stored = row.Stored.GetValueOrDefault(),
                Destination = row.Destination.GetValueOrDefault(),
            });
        }
        var floors = new List<ClearanceFloor>(v.Floors.Count);
        foreach (var row in v.Floors)
        {
            floors.Add(new ClearanceFloor { Cell = CellOf(row.Cell), DefName = row.DefName ?? "", Designated = row.Designated.GetValueOrDefault() });
        }
        cancellationToken.ThrowIfCancellationRequested();
        return Known(new ClearanceCensus { Targets = EquatableArray.From(rows), Chunks = EquatableArray.From(chunks), Floors = EquatableArray.From(floors) });
    }

    private static string ClearanceClassName(O.ClearanceClass c) => c switch
    {
        O.ClearanceClass.AncientWallDoor => ClearanceAncientWallDoor,
        O.ClearanceClass.ShipChunk => ClearanceShipChunk,
        O.ClearanceClass.AncientCasket => ClearanceAncientCasket,
        O.ClearanceClass.Other => ClearanceOther,
        _ => "",
    };
}
