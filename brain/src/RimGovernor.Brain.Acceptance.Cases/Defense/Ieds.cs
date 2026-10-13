using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>defense/ied-lane</c> (Go <c>ieds.go</c>): the perimeter campaign with the IED tier's gates open. Before the layout
/// is built the fixture finishes the IEDs research and drops high-explosive shells, so the layout planner extends the trap
/// lane outward from the killbox entry with IEDs whose blast keeps off doors, the safe lane, colonist routes and storage.
/// The case holds the built tier to the native IEDs standing on its cells, then the edge raid walks the approach and at
/// least one IED is gone (sprung) once it resolves. A layout that places no IED is a legitimate policy outcome but proves
/// nothing here, so the case fails naming it.
/// </summary>
internal static class DefenseIeds
{
    private static readonly DefenseVariant s_variant = new()
    {
        Strategy = "ImmediateAttack",
        Arrival = "EdgeWalkIn",
        Threat = "raid",
        InstantWalls = true,
        Gates = (fixture, siteX, siteZ) => fixture("ieds", new JsonObject { ["op"] = "ieds", ["x"] = siteX, ["z"] = siteZ }),
        LayoutBuilt = IedsBuilt,
        Raided = IedsSprung,
    };

    internal static AcceptanceCase Case() => new()
    {
        Name = "defense/ied-lane",
        Scope = "IEDs on the approach (#1209): with IEDs researched and HE shells stocked, the perimeter layout's ieds tier places IEDs outward from the "
            + "killbox entry and builds them natively (every tier IED stands on its cell); a real RaidEnemy edge assault walks the approach and at least "
            + "one IED is sprung by the time the raid resolves; the rest of the perimeter campaign (hold-the-line, release, repair) holds as in defense/perimeter. Native: vanilla raid pathing springing the traps.",
        Start = new SaveStart { Name = Baselines.BaselineName },
        Serve = new ServeSpec { Families = DefensePerimeter.PerimeterFamilies, Prefix = "defense-ieds" },
        Budget = TimeSpan.FromHours(3),
        Crew = new Crew { Size = 3 },
        Reason = "the IED tier is part of the perimeter's build, and only a real raid on the approach springs it",
        Run = (s, cancellationToken) => DefensePerimeter.RunVariantAsync(s, s_variant, cancellationToken),
    };

    /// <summary>Holds the layout's ieds tier to the native IEDs: at least one placed, the tier built, and every tier IED standing on its cell.</summary>
    private static void IedsBuilt(DefenseLayoutRecord layout, JsonObject inspect)
    {
        var (tier, _, found) = layout.Tier(PolicyFunctions.TierIEDs);
        if (!found || tier.Buildings.Count == 0)
        {
            throw new AcceptanceException($"the layout placed no IED on the approach (a door, the safe lane, a colonist route or storage covers every approach cell's blast); nothing to spring: {DefensePerimeter.TierJson(tier).ToJson()}");
        }

        if (!tier.Built)
        {
            throw new AcceptanceException($"ieds tier not built: {DefensePerimeter.TierJson(tier).ToJson()}");
        }

        var standing = new HashSet<(int X, int Z)>();
        foreach (var raw in inspect["ieds"].AsSlice())
        {
            var row = raw.Obj();
            _ = standing.Add(((int)row["x"].AsNumber(), (int)row["z"].AsNumber()));
        }

        foreach (var b in tier.Buildings)
        {
            if (!standing.Contains((b.Cell.X, b.Cell.Z)))
            {
                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"tier IED {b.Definition} at {b.Cell.X},{b.Cell.Z} not standing natively: {inspect["ieds"].ToJson()}"));
            }
        }
    }

    /// <summary>Wants at least one IED from the layout audit gone once the raid resolved: an IED explodes when sprung and is destroyed.</summary>
    private static void IedsSprung(JsonObject afterLayout, JsonObject afterRaid)
    {
        var now = new HashSet<string>(afterRaid["ieds"].AsSlice().Select(raw => raw.Obj()["id"].AsString()), StringComparer.Ordinal);
        foreach (var raw in afterLayout["ieds"].AsSlice())
        {
            if (!now.Contains(raw.Obj()["id"].AsString()))
            {
                return;
            }
        }

        throw new AcceptanceException($"no IED sprung during the edge raid: {afterLayout["ieds"].ToJson()} before, {afterRaid["ieds"].ToJson()} after");
    }
}
