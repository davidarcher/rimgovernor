namespace RimGovernor.Brain.GameAccess;

/// <summary>Fresh pawn positions and installed jobs used to hold dependent dispatch.</summary>
public interface IArrivalPawns
{
    Task<GameFacts.Observations.PawnSnapshot> ReadPawnsAsync(GameFacts.Common.Identity identity,
        IReadOnlyList<string> pawnIds, CancellationToken ct);
}

/// <summary>Sends selected native intents through Actions/Apply.</summary>
public interface IActionsWriter
{
    Task<GameFacts.Operations.ApplyReply> ApplyAsync(GameFacts.Common.Identity identity,
        IReadOnlyList<GameOrders.Operations.Action> actions, CancellationToken ct);
}
