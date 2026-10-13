using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Acceptance;

/// <summary>The layout plan derived from the loaded game's map survey, and the map's size (Go <c>startersite.args</c>'s plan and <c>facts.Bounds</c>).</summary>
public sealed record StarterSitePlan(P.LayoutPlan Plan, P.Bounds Bounds);

/// <summary>
/// Derives the first layout plan of the loaded game (Go <c>startersite.Survey</c> and the derivation in <c>args</c>:
/// identity, definition catalog, the colony read with the planning window, the map survey, the climate flags and
/// <c>DeriveLayoutPlan</c> over the first-plan facts). The Go harness did this over its own bridge client; the C#
/// harness has no typed native client, so the host supplies the planner (the in-process brain's observation and
/// building-runtime layers own the inputs).
/// </summary>
public interface IStarterSitePlanner
{
    /// <summary>Reads the loaded game and derives its first layout plan; throws when the map holds no plan.</summary>
    Task<StarterSitePlan> PlanAsync(Harness harness, CancellationToken cancellationToken);
}

/// <summary>
/// Where the fixture hut stands (Go package <c>startersite</c>): the south-west corner and door the layout plan puts
/// on the shelter room, as the <c>siteX/siteZ/doorX/doorZ</c> arguments the hut fixture takes.
/// </summary>
public sealed class StarterSite
{
    private readonly IStarterSitePlanner _planner;

    public StarterSite(IStarterSitePlanner planner)
    {
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
    }

    /// <summary>Args for the 9x9 fixture hut's site.</summary>
    public Task<JsonObject?> ArgsAsync(Harness harness, CancellationToken cancellationToken) => ArgsCoreAsync(harness, 9, bedrooms: false, cancellationToken);

    /// <summary>
    /// A fixture start's <see cref="FixtureArgsFrom"/> for a <paramref name="size"/> x <paramref name="size"/> fixture
    /// hut: the south-west corner and door the planned site puts on the layout plan derived from the loaded game's map
    /// survey; it fails when the plan holds no shelter.
    /// </summary>
    public FixtureArgsFrom ArgsFor(int size) => (harness, cancellationToken) => ArgsCoreAsync(harness, size, bedrooms: false, cancellationToken);

    /// <summary>Args plus the layout plan's bedroom-wing rooms as the bedrooms argument, for a start that builds the planned bedrooms itself.</summary>
    public Task<JsonObject?> BedroomArgsAsync(Harness harness, CancellationToken cancellationToken) => ArgsCoreAsync(harness, 9, bedrooms: true, cancellationToken);

    /// <summary>
    /// The size x size fixture hut on the layout plan's shelter room: the square shares the shelter ring's south-west
    /// corner, pulled back inside the map, and keeps the planned door when it lands on the square's ring off a corner
    /// (else the hut's mid east wall). Null when the plan holds no shelter.
    /// </summary>
    public static (Cell Site, Cell Door)? PlannedSite(P.LayoutPlan plan, P.Bounds bounds, int size)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var shells = plan.PlannedShells(P.RoomRole.Shelter);
        if (shells.Count == 0 || bounds.Width < size || bounds.Height < size)
        {
            return null;
        }

        var b = shells[0].Bounds;
        var site = new Cell(Math.Min(Math.Max(b.X, 0), bounds.Width - size), Math.Min(Math.Max(b.Z, 0), bounds.Height - size));
        var door = shells[0].Door;
        int minX = site.X, minZ = site.Z, maxX = site.X + size - 1, maxZ = site.Z + size - 1;
        var edgeX = door.X == minX || door.X == maxX;
        var edgeZ = door.Z == minZ || door.Z == maxZ;
        var inside = door.X >= minX && door.X <= maxX && door.Z >= minZ && door.Z <= maxZ;
        if (!inside || edgeX == edgeZ)
        {
            door = new Cell(maxX, site.Z + (size / 2));
        }

        return (site, door);
    }

    /// <summary>
    /// The plan's bedroom-wing rooms as the fixture's bedrooms argument: "x,z,width,height,doorX,doorZ" per room (its
    /// interior and door cell), joined by ';'.
    /// </summary>
    public static string PlannedBedrooms(P.LayoutPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var rooms = new List<string>();
        foreach (var room in plan.Rooms)
        {
            if (room.Role != P.PolicyFunctions.PlannedBedroom)
            {
                continue;
            }

            var interior = room.Region.Bounds();
            rooms.Add(string.Create(CultureInfo.InvariantCulture, $"{interior.X},{interior.Z},{interior.Width},{interior.Height},{room.Door.X},{room.Door.Z}"));
        }

        return string.Join(';', rooms);
    }

    private async Task<JsonObject?> ArgsCoreAsync(Harness harness, int size, bool bedrooms, CancellationToken cancellationToken)
    {
        // The first plan is the seed rooms and the outskirts, which hold the shelter the fixture hut stands on (no
        // ledger and no stock are read). Go also dumped the survey to RIMGOVERNOR_SURVEY_DUMP for the policy layout
        // benchmarks; the survey is the planner's input here and the benchmark capture is not ported.
        var planned = await _planner.PlanAsync(harness, cancellationToken).ConfigureAwait(false);
        if (PlannedSite(planned.Plan, planned.Bounds, size) is not { } placed)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"no planned shelter for a {size}x{size} fixture hut on this map"));
        }

        var args = new JsonObject
        {
            ["siteX"] = placed.Site.X,
            ["siteZ"] = placed.Site.Z,
            ["doorX"] = placed.Door.X,
            ["doorZ"] = placed.Door.Z,
        };
        if (bedrooms)
        {
            args["bedrooms"] = PlannedBedrooms(planned.Plan);
        }

        return args;
    }
}
