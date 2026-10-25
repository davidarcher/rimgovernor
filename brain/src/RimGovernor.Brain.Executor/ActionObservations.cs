using RimGovernor.Brain.Domain;
using RimGovernor.Brain.GameFacts.Common;
using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.GameFacts.Observations;

namespace RimGovernor.Brain.Executor;

/// <summary>World identity and native authority checks shared by execution observations.</summary>
public static class ActionObservations
{
    /// <summary>Returns only pawn rows read under the requested world and native generation.</summary>
    public static async Task<(Dictionary<string, PawnState> Rows, long Tick)> ReadPawnsAsync(
        IArrivalPawns? source, GenerationSnapshot current, IReadOnlyList<string> ids, CancellationToken ct)
    {
        var rows = new Dictionary<string, PawnState>(StringComparer.Ordinal);
        if (source is null) return (rows, 0);
        PawnSnapshot observed;
        try
        {
            observed = await source.ReadPawnsAsync(Identity(current), ids, ct).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return (rows, 0);
        }
        try
        {
            _ = Context(observed.Context, current);
        }
        catch (DomainException)
        {
            return (rows, 0);
        }
        foreach (var row in observed.Pawns) rows[row.Pawn.Id ?? ""] = row;
        return (rows, observed.Context?.Tick ?? 0);
    }

    public static Identity Identity(GenerationSnapshot scope) =>
        new() { ColonyId = scope.Colony.Value, LoadToken = scope.Load.Value, MapId = scope.Map.Value };

    public static GenerationSnapshot Context(ObservationContext? observed, GenerationSnapshot expected)
    {
        if (observed?.Identity is not { } identity || !observed.NativeGeneration.IsAt(expected.Native.Value))
            throw new AuthorityException();
        var actual = expected with
        {
            Colony = new ColonyId(identity.ColonyId ?? ""),
            Load = new LoadId(identity.LoadToken ?? ""),
            Map = new MapId(identity.MapId),
        };
        if (!actual.SameWorld(expected)) throw new AuthorityException();
        return actual;
    }
}
