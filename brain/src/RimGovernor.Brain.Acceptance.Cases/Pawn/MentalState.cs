using System.Text.Json.Nodes;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>pawn/mental-state</c> (Go <c>mental_state.go</c>).</summary>
internal static class PawnMentalState
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "pawn/mental-state",
        Scope = "Induced Berserk is read as its native defName, aggression and zero initial age through pawn and colonist status reads, including the Go Fact projection.",
        Start = FixtureStage.Start(new Fx.StageSpec { Paused = true, Pawns = [new Fx.StagePawn { Name = "pawn", Colonist = 0, MentalState = "Berserk" }] }),
        Budget = TimeSpan.FromMinutes(1),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    /// <summary>
    /// The row's active mental state; null when the pawn has none. False and zero members are omitted on the wire.
    /// </summary>
    private static (string DefName, bool IsAggro, double Ticks)? Known(JsonObject row) =>
        row["mentalState"] is not JsonObject state
            ? null
            : (state["defName"].AsString(), state["isAggro"].AsBool() == true, state["ticks"] is null ? 0 : state["ticks"].AsNumber());

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var pawnId = FixtureStage.Prepared(s).Pawns["pawn"].Id;
        var h = s.Harness;
        var row = await PawnSupport.ReadPawnAsync(h, s, "mental-state", pawnId, false, cancellationToken).ConfigureAwait(false);
        if (Known(row) is not { } state || state.DefName != "Berserk" || !state.IsAggro || state.Ticks != 0)
        {
            throw new AcceptanceException($"berserk readback: known={Known(row) is not null} state={row["mentalState"].ToJson()}");
        }

        s.Report["pawn_mental_state"] = new JsonObject { ["DefName"] = state.DefName, ["IsAggro"] = state.IsAggro, ["TicksInState"] = state.Ticks };

        // The colonist status read lists the pawn by reference; the Go emergency projection joins each reference against
        // the frame's pawn table (the colonist pawn read), so the state is compared on that joined row.
        var request = new JsonObject { ["scope"] = PawnSupport.Scope(s), ["colonists"] = true, ["threats"] = true };
        var reply = await h.WireAsync("mental-state-status", "observations_read_status", request, cancellationToken).ConfigureAwait(false);
        var status = WallCensus.Observed("mental-state-status", reply);
        if (!status["colonists"].AsSlice().Any(colonist => colonist.Member("id").AsString() == pawnId))
        {
            throw new AcceptanceException("berserk colonist missing from status");
        }

        var table = new JsonObject
        {
            ["scope"] = PawnSupport.Scope(s),
            ["filter"] = new JsonObject { ["colonist"] = true },
            ["details"] = new JsonObject { ["needs"] = false, ["health"] = false, ["equipment"] = false, ["biography"] = false, ["settings"] = false, ["social"] = false, ["animals"] = false },
        };
        var tableReply = await h.WireAsync("mental-state-table", "observations_list_pawns", table, cancellationToken).ConfigureAwait(false);
        var colonistRow = WallCensus.Observed("mental-state-table", tableReply)["pawns"].AsSlice()
            .Select(raw => raw.AsMap() ?? [])
            .FirstOrDefault(row => row["pawn"].Member("id").AsString() == pawnId);
        if (colonistRow is null || Known(colonistRow) is not { } observed || observed != state)
        {
            throw new AcceptanceException($"colonist mental state disagrees: state={colonistRow.ToJson()}");
        }

        s.Report["colonist_mental_state"] = new JsonObject { ["DefName"] = observed.DefName, ["IsAggro"] = observed.IsAggro, ["TicksInState"] = observed.Ticks };
    }
}
