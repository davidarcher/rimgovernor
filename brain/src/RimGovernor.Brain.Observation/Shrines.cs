namespace RimGovernor.Brain.Observation;

public interface IShrineSource
{
    Task<(O.AncientShrinesReply Reply, Result Receipt)> ReadAncientShrinesAsync(C.Identity identity, CancellationToken cancellationToken);
}

public static partial class ObservationPackage
{
    /// <summary>
    /// Tolerates an explicit unavailable native stub as unknown, distinct from a complete census with no
    /// shrines. Transport, malformed-contract and identity errors remain errors.
    /// </summary>
    public static async Task<Fact<EquatableArray<AncientShrine>>> ObserveShrines(IShrineSource? source, Identity expected, CancellationToken cancellationToken)
    {
        var unknown = Unknown<EquatableArray<AncientShrine>>();
        if (source is null || !SameColonyContext(expected, expected))
        {
            throw new ObservationContractException("missing shrine source or expected identity");
        }
        expected.Validate();
        var id = new C.Identity { ColonyId = expected.Colony.Value, LoadToken = expected.Load.Value, MapId = expected.Map.Value };
        O.AncientShrinesReply? reply;
        try
        {
            (reply, _) = await source.ReadAncientShrinesAsync(id, cancellationToken).ConfigureAwait(false);
        }
        catch (BridgeUnavailableException)
        {
            return unknown;
        }
        if (reply is null)
        {
            throw new ObservationContractException("missing ancient shrines reply");
        }
        if (reply.Outcome?.Unavailable is { } u)
        {
            if (u.Reason.GetValueOrDefault() == C.UnavailableReason.Unspecified || !Enum.IsDefined(u.Reason.GetValueOrDefault()))
            {
                throw new ObservationContractException("unavailable reply without a reason");
            }
            return unknown;
        }
        var v = reply.Outcome?.Observed ?? throw new ObservationContractException("missing ancient shrines");
        var actual = ContextIdentity(v.Context);
        if (!SameColonyContext(actual, expected))
        {
            throw new ObservationChangedException();
        }
        var rows = new List<AncientShrine>(v.Shrines.Count);
        foreach (var row in v.Shrines)
        {
            var caskets = new List<ShrineCasket>(row.Caskets.Count);
            foreach (var casket in row.Caskets)
            {
                caskets.Add(new ShrineCasket
                {
                    EntityId = casket.EntityId ?? "",
                    Cell = CellOf(casket.Cell),
                    InteractionCell = CellOf(casket.InteractionCell),
                    HitPoints = casket.HitPoints.GetValueOrDefault(),
                    HasContents = casket.HasContents.GetValueOrDefault(),
                    PlayerClaimed = casket.PlayerClaimed.GetValueOrDefault(),
                });
            }
            var guards = new List<ShrineGuard>(row.Guards.Count);
            foreach (var guard in row.Guards)
            {
                guards.Add(new ShrineGuard { EntityId = guard.EntityId ?? "", Kind = GuardKind(guard.Kind), Downed = guard.Downed.GetValueOrDefault(), Dead = guard.Dead.GetValueOrDefault() });
            }
            var walls = new List<ShrineBreachWall>(row.BreachWalls.Count);
            foreach (var wall in row.BreachWalls)
            {
                walls.Add(new ShrineBreachWall { EntityId = wall.EntityId ?? "", DefName = wall.DefName ?? "", Cell = CellOf(wall.Cell), Outside = CellOf(wall.Outside) });
            }
            var occupants = new List<ShrineOccupant>(row.Occupants.Count);
            foreach (var occupant in row.Occupants)
            {
                occupants.Add(new ShrineOccupant
                {
                    EntityId = occupant.EntityId ?? "",
                    Hostile = occupant.Hostile.GetValueOrDefault(),
                    Downed = occupant.Downed.GetValueOrDefault(),
                    Dead = occupant.Dead.GetValueOrDefault(),
                    Prisoner = occupant.Prisoner.GetValueOrDefault(),
                    Faction = occupant.Faction ?? "",
                });
            }
            rows.Add(new AncientShrine
            {
                Id = row.ShrineId ?? "",
                Minimum = CellOf(row.Room?.Minimum),
                Maximum = CellOf(row.Room?.Maximum),
                Sealed = row.Sealed.GetValueOrDefault(),
                InHome = row.InHome.GetValueOrDefault(),
                GuardsKnown = row.GuardsKnown.GetValueOrDefault(),
                Caskets = EquatableArray.From(caskets),
                Guards = EquatableArray.From(guards),
                BreachWalls = EquatableArray.From(walls),
                Occupants = EquatableArray.From(occupants),
            });
        }
        cancellationToken.ThrowIfCancellationRequested();
        return Known(EquatableArray.From(rows));
    }

    private static string GuardKind(O.ShrineGuardKind kind) => kind switch
    {
        O.ShrineGuardKind.Mechanoid => PolicyFunctions.ShrineGuardMechanoid,
        O.ShrineGuardKind.Insectoid => PolicyFunctions.ShrineGuardInsectoid,
        O.ShrineGuardKind.Fleshbeast => PolicyFunctions.ShrineGuardFleshbeast,
        O.ShrineGuardKind.Human => PolicyFunctions.ShrineGuardHuman,
        O.ShrineGuardKind.Hive => PolicyFunctions.ShrineGuardHive,
        O.ShrineGuardKind.Other => PolicyFunctions.ShrineGuardOther,
        _ => "",
    };
}
