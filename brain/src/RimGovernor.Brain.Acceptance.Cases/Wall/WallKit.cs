using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;
using Operations = RimGovernor.Contracts.Wire.Operations;
using Receipts = RimGovernor.Contracts.Wire.Receipts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The staging, reads and intents the typed <c>wall/*</c> cases share.</summary>
internal static class WallKit
{
    /// <summary>The fixture op that lays a player's own Deconstruct designation on a thing (the supervisor may adopt it).</summary>
    internal const string PlayerDesignateTool = "test/player_designate";

    /// <summary>Lays a player Deconstruct designation on one thing by id.</summary>
    internal static readonly FixtureOp<string, Fx.PlayerDesignateReply> PlayerDesignate = new(
        PlayerDesignateTool,
        target => Args(("target", target)),
        (label, reply) => reply is not null && Fx.FixtureJson.Deserialize<Fx.PlayerDesignateReply>(reply.ToJsonString()) is { Success: true, Designated: true } read
            ? read
            : throw new AcceptanceException($"{label}: {PlayerDesignateTool} refused: {reply?.ToJsonString()}"));

    /// <summary>A bespoke fixture op's JSON arguments from name/value pairs.</summary>
    internal static JsonObject Args(params (string Name, JsonNode? Value)[] pairs) =>
        new(pairs.Select(p => new KeyValuePair<string, JsonNode?>(p.Name, p.Value)));

    /// <summary>The lab crew as builders: Construction first and every hour work, so vanilla takes the designated jobs.</summary>
    internal static IReadOnlyList<Fx.StagePawn> Builders { get; } =
    [
        .. Enumerable.Range(0, 3).Select(i => new Fx.StagePawn
        {
            Name = "builder-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Colonist = i,
            Work = new Dictionary<string, int> { ["Construction"] = 1 },
            Timetable = Fx.StageTimeAssignment.Work,
        }),
    ];

    /// <summary>A colonist-anchored open, unroofed, unzoned site of heavy ground of the given size.</summary>
    internal static Fx.StageSite Site(int width, int height) => new()
    {
        Width = width,
        Height = height,
        Near = Fx.StageAnchor.Colonist,
        OffsetX = 6,
        Radius = 30,
        Require = [Fx.StageGround.Standable, Fx.StageGround.Empty, Fx.StageGround.Unfogged, Fx.StageGround.Unroofed, Fx.StageGround.Unzoned, Fx.StageGround.Heavy],
    };

    /// <summary>Clears things, pawns, roofs and zones over the whole site.</summary>
    internal static Fx.StageClear Clear(int width, int height) => new()
    {
        Rect = new Fx.StageRect(0, 0, width, height),
        Kinds = [Fx.StageClearKind.Things, Fx.StageClearKind.Pawns, Fx.StageClearKind.Roofs, Fx.StageClearKind.Zones],
    };

    /// <summary>The absolute cell of a site-relative staged cell.</summary>
    internal static Cell At(Fx.StageRect site, Fx.StageCell c) => new(site.X + c.X, site.Z + c.Z);

    /// <summary>The absolute cells of a site-relative rectangle.</summary>
    internal static List<Cell> Cells(Fx.StageRect site, Fx.StageRect rect) =>
        [.. from z in Enumerable.Range(rect.Z, rect.Height) from x in Enumerable.Range(rect.X, rect.Width) select new Cell(site.X + x, site.Z + z)];

    /// <summary>The wire cell of a domain cell.</summary>
    internal static RimGovernor.Contracts.Wire.Common.Cell Wire(Cell c) => new() { X = c.X, Z = c.Z };

    /// <summary>The ids among <paramref name="ids"/> the building census still lists, any faction.</summary>
    internal static async Task<HashSet<string>> PresentAsync(TypedSession s, string label, IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        var rows = await s.BuildingsAsync(label, new Observations.ListBuildingsRequest { Ids = [.. ids], AnyFaction = true }, cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(r => r.Building.Id).Where(ids.Contains)];
    }

    /// <summary>How many of <paramref name="cells"/> one cells read over the site reports roofed.</summary>
    internal static int Roofed(MapCellGrid grid, IEnumerable<Cell> cells) => cells.Count(c => grid.At(c) is { Roofed: true });

    /// <summary>How many of <paramref name="cells"/> one cells read over the site reports with terrain <paramref name="terrain"/>.</summary>
    internal static int WithTerrain(MapCellGrid grid, IEnumerable<Cell> cells, string terrain) => cells.Count(c => grid.At(c) is { } g && g.Terrain == terrain);

    /// <summary>The enclosure-guarded DECONSTRUCT Designate of one building.</summary>
    internal static Operations.DesignateIntent Deconstruct(string id, bool replaceWithWall = false) => new()
    {
        Designation = Operations.ThingDesignation.Deconstruct,
        Target = new() { Id = id },
        Guard = Operations.DesignationGuard.Enclosure,
        ReplaceWithWall = replaceWithWall ? true : null,
    };

    /// <summary>The observed effect of an applied receipt, or null.</summary>
    internal static Receipts.EffectEvidenceEffect? Effect(this Operations.ActionResultOutcome outcome) =>
        outcome.Applied?.Outcome?.Applied?.Observed?.Effect;
}
