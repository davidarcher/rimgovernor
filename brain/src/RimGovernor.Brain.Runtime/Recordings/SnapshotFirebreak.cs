using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Facts;
using RimGovernor.Brain.Policy;
using Cell = RimGovernor.Brain.Domain.Cell;
namespace RimGovernor.Brain.Runtime.Recordings;

/// <summary>
/// One firebreak ring review (the layout/firebreak Need's read) as recorded: the ring request it planned from, the dwell clock it carried in, and
/// the plant cut census and open wooden ruins it read over the plan. Request.Ground is cell-keyed, which a
/// recording cannot carry as a map, so it is recorded as <see cref="Ground"/>.
/// </summary>
public sealed class SnapshotFirebreak
{
    public string Recorded { get; set; } = "";

    public GenerationSnapshot Snapshot { get; set; }

    public Tick Tick { get; set; }

    public required FirebreakRequest Request { get; set; }

    public List<FirebreakGroundCell> Ground { get; set; } = [];

    public List<FirebreakDwell> Dwell { get; set; } = [];

    /// <summary>The cut cells the plant cut census reported a plant on.</summary>
    public List<Cell> Standing { get; set; } = [];

    /// <summary>The planned wooden ruins with no deconstruction designated.</summary>
    public List<Cell> Open { get; set; } = [];

    /// <summary>Plans the ring from the recorded request and dwell and returns the plan and the work the recorded census leaves owed.</summary>
    public (Policy.FirebreakPlan Plan, FirebreakWork Work) Replay()
    {
        var ground = new Dictionary<Cell, FirebreakGround>();
        foreach (var g in Ground)
        {
            ground[g.Cell] = g.Ground;
        }
        var r = Request with { Ground = ground };
        var dwell = new Dictionary<Cell, Tick>();
        foreach (var d in Dwell)
        {
            dwell[d.Cell] = d.Since;
        }
        var (plan, _) = FirebreakRules.PlanFirebreak(r, dwell);
        return (plan, FirebreakConcernRules.FirebreakOwed(plan, new HashSet<Cell>(Standing), new HashSet<Cell>(Open)));
    }
}

public readonly record struct FirebreakGroundCell(Cell Cell, FirebreakGround Ground);

public readonly record struct FirebreakDwell(Cell Cell, Tick Since);

public static class SnapshotFirebreakReplay
{
    /// <summary>Records request, dwell and the census over the plan.</summary>
    public static SnapshotFirebreak NewFirebreak(FirebreakRequest request, IReadOnlyDictionary<Cell, Tick> dwell, IEnumerable<Cell> standing, IEnumerable<Cell> open)
    {
        var f = new SnapshotFirebreak { Tick = request.Now, Standing = [.. standing], Open = [.. open], Request = request with { Ground = new Dictionary<Cell, FirebreakGround>() } };
        // Dictionary order is not defined: record cells in row-major order so a recording is stable.
        foreach (var c in request.Ground.Keys.OrderBy(c => c.Z).ThenBy(c => c.X))
        {
            f.Ground.Add(new FirebreakGroundCell(c, request.Ground[c]));
        }
        foreach (var c in dwell.Keys.OrderBy(c => c.Z).ThenBy(c => c.X))
        {
            f.Dwell.Add(new FirebreakDwell(c, dwell[c]));
        }
        return f;
    }

    /// <summary>Writes f into dir as firebreak-&lt;tick&gt;.</summary>
    public static void RecordFirebreak(string dir, SnapshotFirebreak f) => SnapshotRecorder.Write(dir, "firebreak", $"firebreak-{f.Tick.Value}", f);
}
