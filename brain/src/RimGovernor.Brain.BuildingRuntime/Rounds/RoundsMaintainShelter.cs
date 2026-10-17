using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.BuildingRuntime;

internal sealed class OwnedArea
{
    public IReadOnlyList<Cell> Current { get; set; } = [];

    public bool Known { get; set; }

    public IReadOnlyList<Cell> Want { get; set; } = [];
}

/// <summary>
/// Remembers the bot-owned allowed areas so each review plans only the edits that differ. The shelter family's
/// safe-area Need (shelter/safe-area, #3257) reads <see cref="Owed"/> and commits <see cref="Take"/>'s edits.
/// </summary>
public sealed partial class SafeAreaMemory
{
    private readonly object mu = new();
    private string world = "";
    private Dictionary<string, OwnedArea>? areas;
    private List<Area> edits = [];
    private Tick last;
    private bool lastKnown;
    private bool reviewed;
    private HashSet<Cell>? danger;

    private void Enter(string newWorld)
    {
        if (world != newWorld || areas is null)
        {
            world = newWorld;
            areas = [];
            edits = [];
            lastKnown = false;
            reviewed = false;
            danger = null;
        }
    }

    private void Plan(string key, IReadOnlyList<Cell> want)
    {
        if (!areas!.TryGetValue(key, out var a))
        {
            a = new OwnedArea();
            areas[key] = a;
        }

        var planned = SafeAreaRules.PlanBotArea(key, want, a.Current, a.Known);
        a.Want = want;
        edits.AddRange(planned);
    }

    public Fact<bool> Review(string newWorld, ColonyProjection projection)
    {
        if (!projection.Rooms.TryGetValue(out var rooms))
        {
            return Unknown<bool>();
        }

        IReadOnlyList<Cell> killbox = [], hospital = [], isolation = [];
        if (projection.LayoutPlan.TryGetValue(out var plan))
        {
            killbox = plan.KillboxCells();
            hospital = plan.HospitalCells();
            isolation = plan.IsolationRoomCells();
        }

        lock (mu)
        {
            Enter(newWorld);
            edits = [];
            Plan(SafeAreaRules.SafeAreaKey, SafeAreaRules.SafeAreaCells(rooms, [.. killbox, .. isolation]));
            if (hospital.Count > 0)
            {
                Plan(HerdWardRules.HospitalAreaKey, hospital);
            }

            if (isolation.Count > 0)
            {
                Plan(IsolationRoomRules.IsolationAreaKey, isolation);
            }

            if (projection.Facts.HomeCoverage.TryGetValue(out var census) && census.Home.TryGetValue(out var home) && home.Count > 0)
            {
                Plan(ShelterRules.NoDangerAreaKey, ShelterRules.NoDangerCells(home, killbox, SafeAreaMemory.SortedSeeds(danger)));
            }

            reviewed = true;
            return Known(edits.Count > 0);
        }
    }

    /// <summary>Whether the last review in <paramref name="newWorld"/> left edits to commit; unknown before a review there.</summary>
    public Fact<bool> Owed(string newWorld)
    {
        lock (mu)
        {
            if (world != newWorld || areas is null || !reviewed)
            {
                return Unknown<bool>();
            }
            return Known(edits.Count > 0);
        }
    }

    public IReadOnlyList<Area> Take(string newWorld)
    {
        lock (mu)
        {
            Enter(newWorld);
            return edits;
        }
    }

    public void Commit(string newWorld)
    {
        lock (mu)
        {
            Enter(newWorld);
            foreach (var a in areas!.Values)
            {
                a.Current = a.Want;
                a.Known = true;
            }

            edits = [];
        }
    }

    public Fact<bool> DangerWindow(string newWorld, Fact<long> hostiles, IReadOnlyList<Cell> seeds, Tick tick)
    {
        lock (mu)
        {
            Enter(newWorld);
            if (hostiles.TryGetValue(out var n) && n > 0)
            {
                last = tick;
                lastKnown = true;
            }

            var window = ShelterRules.DangerWindowOf(hostiles, last, lastKnown, tick);
            if (window.TryGetValue(out var open) && open)
            {
                danger ??= [];
                foreach (var c in seeds)
                {
                    danger.Add(c);
                }
            }
            else if (window.IsKnown)
            {
                danger = null;
            }

            return window;
        }
    }
}

partial class SafeAreaMemory
{
    internal static Fact<EquatableArray<Cell>> DangerSeedFact(Fact<long> hostiles, IReadOnlyList<EmergencyThreat> threats) =>
        hostiles.IsKnown ? Known<EquatableArray<Cell>>([.. ShelterRules.DangerSeeds(threats)]) : Unknown<EquatableArray<Cell>>();

    internal static IReadOnlyList<Cell> SortedSeeds(HashSet<Cell>? set) =>
        set is null ? [] : [.. set.OrderBy(c => c.Z).ThenBy(c => c.X)];
}
