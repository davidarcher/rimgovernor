using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// The staging helpers of the retirement and climate cases (Go <c>shelter/stage.go</c>): both read the layout plan the
/// running controller recorded, stop the service, raise finished rooms on the planned rectangles
/// (<c>test/layout_rooms_stage</c>) and restart it on the same journal. Staging on the recorded plan, not on a plan
/// derived offline, keeps the rooms on the interiors the controller actually holds: a demand-grown room (the
/// laboratory) is sited by a scored search the offline derivation does not reproduce.
/// </summary>
internal static class ShelterStage
{
    internal const string RoomsOp = "test/layout_rooms_stage";

    /// <summary>The wall-clock ceiling for the first review that records the plan a case stages on.</summary>
    internal static readonly TimeSpan PlanWait = TimeSpan.FromMinutes(6);

    /// <summary>The layout plan a review recorded and the tick it was recorded at (Go <c>store.LayoutPlanRecord</c>).</summary>
    internal sealed record PlanRecord(long Tick, LayoutPlan Plan);

    /// <summary>
    /// Waits for a review whose latest recorded layout plan satisfies <paramref name="ready"/> and returns the record. The
    /// journal is opened for the wait and closed again.
    /// </summary>
    internal static async Task<PlanRecord> WaitPlanAsync(
        ISession s,
        ServiceProcess service,
        TimeSpan ceiling,
        Func<LayoutPlan, bool> ready,
        CancellationToken cancellationToken)
    {
        using var journal = await ServiceJournal.OpenWithRetryAsync(ServiceJournal.PathOf(s.Config), cancellationToken).ConfigureAwait(false);
        PlanRecord? found = null;
        _ = await ServiceJournal.WaitReviewAsync(
            journal,
            new Wait { Ceiling = ceiling, Terminal = service.Exited },
            r =>
            {
                if (r.Revision == 0 || !r.Snapshot.IsValid)
                {
                    return false;
                }

                try
                {
                    if (journal.LayoutPlan(r.Snapshot, r.Tick, cancellationToken) is not { Plan: { } plan } record || !ready(plan))
                    {
                        return false;
                    }

                    found = new PlanRecord(record.Tick.Value, plan);
                    return true;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    _ = e;
                    return false;
                }
            },
            cancellationToken).ConfigureAwait(false);
        return found!;
    }

    /// <summary>The plan's rooms of <paramref name="role"/>.</summary>
    internal static List<PlannedRoom> RoomsOf(LayoutPlan plan, PlannedRole role) => [.. plan.Rooms.Where(r => r.Role == role)];

    /// <summary>Raises each room finished, roofed and walled on its planned interior and door.</summary>
    internal static async Task<JsonObject> StageRoomsAsync(Harness h, string label, IReadOnlyList<PlannedRoom> rooms, CancellationToken cancellationToken)
    {
        var parts = new List<string>(rooms.Count);
        foreach (var r in rooms)
        {
            if (r.Dug)
            {
                throw new AcceptanceException($"planned {Wire.Name(r.Role)} room {Describe(r.Region.Bounds())} is dug from rock: the stage raises open-ground rooms only");
            }

            var inner = r.Region.Bounds();
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{inner.X},{inner.Z},{inner.Width},{inner.Height},{r.Door.X},{r.Door.Z}"));
        }

        var reply = await h.CallAsync(label, RoomsOp, new JsonObject { ["rooms"] = string.Join(';', parts) }, cancellationToken).ConfigureAwait(false);
        if (reply["success"].AsBool() != true)
        {
            throw new AcceptanceException($"{RoomsOp} did not leave every room enclosed and roofed: {reply.ToJson()}");
        }

        return reply;
    }

    /// <summary>Whether <paramref name="c"/> lies in <paramref name="r"/>.</summary>
    internal static bool Inside(Rectangle r, Cell c) => c.X >= r.X && c.X < r.X + r.Width && c.Z >= r.Z && c.Z < r.Z + r.Height;

    /// <summary>The cells the building <paramref name="id"/> occupies; <c>Found</c> is false when no such building stands.</summary>
    internal static async Task<(IReadOnlyList<Cell> Cells, bool Found)> BuildingCellsAsync(
        Harness h,
        JsonObject identity,
        string label,
        string id,
        CancellationToken cancellationToken)
    {
        var reply = await h.WireAsync(
            label,
            "observations_list_buildings",
            new JsonObject { ["scope"] = new JsonObject { ["expectedIdentity"] = identity.DeepClone() }, ["ids"] = new JsonArray(id) },
            cancellationToken).ConfigureAwait(false);
        var (_, observed) = Harness.Outcome(reply, "observed");
        var rows = observed["buildings"].AsSlice();
        if (rows.Count == 0)
        {
            return ([], false);
        }

        var row = rows[0].AsMap() ?? [];
        return (CellReads.RectCells(row["occupied"]), true);
    }

    /// <summary>A rectangle as report JSON.</summary>
    internal static JsonObject RectJson(Rectangle r) => new() { ["x"] = r.X, ["z"] = r.Z, ["width"] = r.Width, ["height"] = r.Height };

    /// <summary>A rectangle as message text.</summary>
    internal static string Describe(Rectangle r) => string.Create(CultureInfo.InvariantCulture, $"{{X:{r.X} Z:{r.Z} Width:{r.Width} Height:{r.Height}}}");

    /// <summary>Cells as a report array of <c>{x, z}</c> objects.</summary>
    internal static JsonArray CellsJson(IEnumerable<Cell> cells) => new([.. cells.Select(c => (JsonNode?)CaseSupport.Cell(c.X, c.Z))]);

    /// <summary>A cell as message text.</summary>
    internal static string Describe(Cell c) => string.Create(CultureInfo.InvariantCulture, $"{{{c.X} {c.Z}}}");

    /// <summary>The journal opened on the run's service journal.</summary>
    internal static Task<Journal> OpenJournalAsync(ISession s, CancellationToken cancellationToken) =>
        ServiceJournal.OpenWithRetryAsync(ServiceJournal.PathOf(s.Config), cancellationToken);
}
