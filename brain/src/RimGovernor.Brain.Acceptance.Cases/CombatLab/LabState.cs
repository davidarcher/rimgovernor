using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>Every pawn's job and fire mode and every door's hold-open, as one lab tick read them (Go <c>labState</c>).</summary>
internal sealed class LabState
{
    internal Dictionary<string, JsonObject> Pawns { get; } = new(StringComparer.Ordinal);

    internal IReadOnlyList<JsonNode?> Doors { get; init; } = [];

    /// <summary>The pawn's row, or null when the lab did not list it (Go map lookup yields nil).</summary>
    internal JsonObject? Pawn(string id) => Pawns.GetValueOrDefault(id);

    /// <summary>The pawn's row, empty when the lab did not list it (Go reads of a nil map).</summary>
    internal JsonObject Row(string id) => Pawns.GetValueOrDefault(id) ?? [];

    internal bool Door(int x, int z) => DoorField(x, z, "holdOpen");

    internal bool PhysicalDoor(int x, int z) => DoorField(x, z, "open");

    internal bool DoorField(int x, int z, string field)
    {
        foreach (var d in Doors)
        {
            var row = d.AsMap() ?? [];
            if ((int)row["x"].AsNumber() == x && (int)row["z"].AsNumber() == z)
            {
                return row[field].AsBool() is { } open
                    ? open
                    : throw new AcceptanceException($"door {field} unknown: {row.ToJson()}");
            }
        }

        throw new AcceptanceException($"no door at {x},{z} in {new JsonArray([.. Doors.Select(d => d?.DeepClone())]).ToJson()}");
    }
}

/// <summary>The orders, draft and tick-read helpers the combat lab cases share (Go <c>orders.go</c>).</summary>
internal static class LabOps
{
    /// <summary>A pawn entity target <c>{entityId}</c>.</summary>
    internal static JsonObject PawnTarget(string id) => new() { ["entityId"] = id };

    /// <summary>Sends one combat_orders batch through Actions/Apply and returns its per-order results.</summary>
    internal static Task<IReadOnlyList<JsonObject>> IssueAsync(Harness h, JsonObject identity, string action, JsonArray orders, CancellationToken cancellationToken) =>
        ApplyIntents.ApplyCombatOrdersAsync(h, action, identity, action, orders, cancellationToken);

    /// <summary>Drafts every pawn through a DraftIntent.</summary>
    internal static async Task DraftAllAsync(Harness h, JsonObject identity, string label, IReadOnlyList<string> pawns, CancellationToken cancellationToken)
    {
        for (var i = 0; i < pawns.Count; i++)
        {
            var id = pawns[i];
            try
            {
                _ = await ApplyIntents.ApplyDraftAsync(h, $"{label}-{CaseSupport.Tag(i)}", identity, $"{label}-{id}", id, true, cancellationToken).ConfigureAwait(false);
            }
            catch (AcceptanceException e)
            {
                throw new AcceptanceException($"draft {id}: {e.Message}", e);
            }
        }
    }

    /// <summary>Runs one game tick on the paused lab and reads every pawn's job and fire mode and every door's hold-open.</summary>
    internal static Task<LabState> TickReadAsync(Harness h, CancellationToken cancellationToken) => TickReadNAsync(h, 1, cancellationToken);

    /// <summary><see cref="TickReadAsync"/> over <paramref name="n"/> ticks.</summary>
    internal static async Task<LabState> TickReadNAsync(Harness h, int n, CancellationToken cancellationToken)
    {
        var reply = await h.CallAsync("combat-orders-tick", CombatFixtures.StageTool, new JsonObject { ["action"] = "tick", ["ticks"] = n }, cancellationToken).ConfigureAwait(false);
        if (reply["success"].AsBool() != true)
        {
            throw new AcceptanceException($"{CombatFixtures.StageTool} tick refused: {reply.ToJson()}");
        }

        var output = new LabState { Doors = reply["doors"].AsSlice() };
        foreach (var r in reply["pawns"].AsSlice())
        {
            var row = r.AsMap() ?? [];
            output.Pawns[row["id"].AsString()] = row;
        }

        return output;
    }
}
