using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>defense/site-stuff</c> (Go <c>sitestuff.go</c>): proves the defense-site census's edifice stuff: every colonist wall
/// the lighting fixture builds reads back from observations_read_defense_site with the stuff the building census gives
/// it. The wait hardening tells a wooden door from a plasteel one by this field. Read-only: no orders, no clock.
/// </summary>
internal static class DefenseSiteStuff
{
    /// <summary>Bounds the walls checked, one census cell each.</summary>
    private const int SiteStuffWalls = 16;

    internal static AcceptanceCase Case() => new()
    {
        Name = "defense/site-stuff",
        Scope = "The defense-site census carries each colonist wall's stuff def, equal to the building census's (#1065); read-only.",
        Start = new FixtureStart { Op = "test/lighting_prepare", On = new LabStart() },
        Budget = TimeSpan.FromMinutes(3),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var report = s.Report;
        var h = s.Harness;
        var identityReply = await h.WireAsync("identity", "lifecycle_read_identity", new JsonObject(), cancellationToken).ConfigureAwait(false);
        var loaded = Harness.Outcome(identityReply, "loaded").Value;
        var context = loaded["context"].Obj();
        JsonObject Scope() => new() { ["expectedIdentity"] = context["identity"]?.DeepClone() };
        var reply = await h.WireAsync("walls", "observations_list_buildings", new JsonObject
        {
            ["scope"] = Scope(),
            ["defNames"] = new JsonArray("Wall"),
            ["statuses"] = new JsonArray("built"),
            ["playerOnly"] = true,
        }, cancellationToken).ConfigureAwait(false);
        var observed = Harness.Outcome(reply, "observed").Value;
        var checkedWalls = 0;
        foreach (var raw in observed["buildings"].AsSlice())
        {
            if (checkedWalls == SiteStuffWalls)
            {
                break;
            }

            var row = raw.Obj();
            var stuff = row["stuff"].AsString();
            var occupied = CellReads.RectCells(row["occupied"]);
            if (stuff.Length == 0 || occupied.Count != 1)
            {
                continue;
            }

            JsonObject At() => new() { ["x"] = (double)occupied[0].X, ["z"] = (double)occupied[0].Z };
            var siteReply = await h.WireAsync(string.Create(CultureInfo.InvariantCulture, $"site-{checkedWalls}"), "observations_read_defense_site", new JsonObject
            {
                ["scope"] = Scope(),
                ["region"] = new JsonObject { ["minimum"] = At(), ["maximum"] = At() },
            }, cancellationToken).ConfigureAwait(false);
            var site = Harness.Outcome(siteReply, "observed").Value;
            var cells = site["cells"].AsSlice();
            var at = At().ToJson();
            if (cells.Count != 1)
            {
                throw new AcceptanceException($"wall at {at}: {cells.Count} census cells");
            }

            var got = cells[0].Obj();
            if (got["edificeDefName"].AsString() != "Wall" || got["edificeStuffDefName"].AsString() != stuff)
            {
                throw new AcceptanceException($"wall at {at} of {stuff}: census reads {got["edificeDefName"].ToJson()}/{got["edificeStuffDefName"].ToJson()}");
            }

            checkedWalls++;
        }

        if (checkedWalls == 0)
        {
            throw new AcceptanceException("no single-cell colonist wall with stuff; the assertion would be vacuous");
        }

        report["walls_checked"] = checkedWalls;
    }
}
